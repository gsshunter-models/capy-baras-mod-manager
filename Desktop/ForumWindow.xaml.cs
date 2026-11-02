using Microsoft.Web.WebView2.Core;
using MXSimModManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MXSimModManager.Desktop;

public partial class ForumWindow : Window
{
    private readonly string stateRoot;
    private readonly Func<string, Window, Task<bool>> install;
    private readonly string initialUrl;
    private readonly Dictionary<ForumDownload, CoreWebView2DownloadOperation> operations = [];
    private readonly DirectDownloadClient directClient = new();
    private readonly Dictionary<ForumDownload, CancellationTokenSource> directOperations = [];
    private readonly HashSet<string> pendingLinks = new(StringComparer.Ordinal);
    private bool installing, closed, progressQueued;
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public ObservableCollection<ForumDownload> Downloads { get; } = [];
    public ForumWindow(string stateRoot, Func<string, Window, Task<bool>> install, string? initialUrl = null)
    {
        this.stateRoot = stateRoot; this.install = install; this.initialUrl = initialUrl ?? ForumDownloadPolicy.TracksUrl;
        InitializeComponent(); DataContext = this;
        if (Environment.GetCommandLineArgs().Contains("--background-test")) { ShowActivated = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -10000; Top = -10000; }
        LoadHistory();
        progressTimer.Tick += (_, _) => PollDownloads();
        progressTimer.Start();
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) => { closed = true; progressTimer.Stop(); foreach (var (item, cancellation) in directOperations) { item.State = "Cancelled"; cancellation.Cancel(); } SaveHistory(); directClient.Dispose(); Browser.Dispose(); };
    }
    private async Task InitializeBrowserAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(stateRoot, "forum-browser"));
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.NavigationStarting += (_, e) =>
            {
                if (!Navigable(e.Uri)) { e.Cancel = true; StatusText.Text = "Only web links are opened in this browser."; }
                else if (TryDirectLink(e.Uri)) e.Cancel = true;
                else StatusText.Text = "Loading page…";
            };
            core.NavigationCompleted += (_, e) =>
            {
                BackButton.IsEnabled = core.CanGoBack; ForwardButton.IsEnabled = core.CanGoForward;
                if (e.IsSuccess) StatusText.Text = "Click a public download link or use Paste download link. Choose Install selected when it finishes.";
                else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.ConnectionAborted) StatusText.Text = "The page could not load: " + e.WebErrorStatus + ". Try Reload or another link.";
            };
            core.SourceChanged += (_, _) => AddressBox.Text = core.Source;
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (e.IsUserInitiated && Navigable(e.Uri) && !TryDirectLink(e.Uri)) core.Navigate(e.Uri); };
            core.LaunchingExternalUriScheme += (_, e) => { e.Cancel = true; StatusText.Text = "External application links are not opened. Use a track archive download."; };
            core.DownloadStarting += DownloadStarting;
            Navigate(initialUrl);
        }
        catch (Exception ex) { StatusText.Text = "The browser could not start. Microsoft Edge WebView2 Runtime is required. " + ex.Message; }
    }
    private static bool Navigable(string value) => value == "about:blank" || value.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("blob:http://", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    private void Navigate(string value)
    {
        try { var uri = ForumDownloadPolicy.WebAddress(value); AddressBox.Text = uri.AbsoluteUri; Browser.CoreWebView2?.Navigate(uri.AbsoluteUri); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Go_Click(object sender, RoutedEventArgs e) => Navigate(AddressBox.Text);
    private void Address_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Return) { Navigate(AddressBox.Text); e.Handled = true; } }
    private void Search_Click(object sender, RoutedEventArgs e) => Navigate(string.IsNullOrWhiteSpace(SearchBox.Text) ? ForumDownloadPolicy.TracksUrl : ForumDownloadPolicy.SearchUrl(SearchBox.Text));
    private void AlternateSearch_Click(object sender, RoutedEventArgs e) => Navigate(string.IsNullOrWhiteSpace(SearchBox.Text) ? ForumDownloadPolicy.TracksUrl : ForumDownloadPolicy.AlternateSearchUrl(SearchBox.Text));
    private void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Return) { Search_Click(sender, e); e.Handled = true; } }
    private void Home_Click(object sender, RoutedEventArgs e) => Navigate(ForumDownloadPolicy.TracksUrl);
    private void Back_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoBack == true) Browser.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoForward == true) Browser.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();
    private int ActiveDownloadCount => operations.Keys.Count(item => item.State == "Downloading") + directOperations.Count;
    private bool TryDirectLink(string value)
    {
        if (DirectLinksBox.IsChecked != true || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !DirectDownloadClient.CanHandle(uri)) return false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => { if (!closed) await StartDirectDownloadAsync(uri); }));
        return true;
    }
    private async void PasteDownload_Click(object sender, RoutedEventArgs e)
    {
        var value = Dialogs.Prompt(this, "Download a mod link", "Paste the track author's public MediaFire, MEGA, Dropbox or archive link. For MEGA, include the complete link and # key.", "", 8192);
        if (string.IsNullOrWhiteSpace(value)) return;
        try { await StartDirectDownloadAsync(ForumDownloadPolicy.WebAddress(value)); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void OpenDownloadPage_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadList.SelectedItem is not ForumDownload { SourcePage: { } uri }) return;
        DirectLinksBox.IsChecked = false; Navigate(uri.AbsoluteUri);
        StatusText.Text = "Download page opened. Turn Skip download pages back on to resume direct downloads.";
    }
    private async Task StartDirectDownloadAsync(Uri uri)
    {
        if (closed) return;
        if (ActiveDownloadCount >= 3) { StatusText.Text = "Three downloads are already running. Wait for one to finish."; return; }
        if (!pendingLinks.Add(uri.AbsoluteUri)) { StatusText.Text = "That link is already being downloaded."; return; }
        var item = new ForumDownload { Id = Guid.NewGuid().ToString("N"), Name = "Resolving download link…", Host = uri.Host, SourcePage = uri, State = "Resolving link", CreatedUtc = DateTime.UtcNow };
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        directOperations[item] = cancellation; Downloads.Insert(0, item); DownloadList.SelectedItem = item; UpdateButtons();
        try
        {
            StatusText.Text = "Getting the public download from " + uri.Host + "…";
            using var batch = await directClient.ResolveAsync(uri, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (closed) return;
            cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            var file = batch.Files.Count == 1 ? batch.Files[0] : Dialogs.ChooseDownload(this, batch.Files);
            cancellation.Token.ThrowIfCancellationRequested(); if (file == null) { item.State = "Cancelled"; return; }
            item.Name = file.Name; item.Host = file.Host; item.TotalBytes = Math.Max(0, file.Size);
            var directory = Path.Combine(stateRoot, "downloads", item.Id); Directory.CreateDirectory(directory);
            item.LocalPath = ModInstaller.SafePath(directory, file.Name); item.State = "Downloading";
            SaveHistory(); UpdateButtons(); StatusText.Text = "Downloading " + file.Name + " directly…";
            var progress = new Progress<DownloadProgress>(value => { if (!closed) { item.ReceivedBytes = value.ReceivedBytes; item.TotalBytes = value.TotalBytes; } });
            await Task.Run(() => DirectDownloadClient.DownloadAsync(file, item.LocalPath, progress, cancellation.Token));
            item.State = "Ready to install";
            if (!closed) StatusText.Text = "Download ready. Select Install selected to review its contents.";
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        { item.State = "Cancelled"; if (!closed) StatusText.Text = "Download cancelled or link lookup timed out. You can retry or open the download page."; }
        catch (Exception ex)
        {
            item.State = "Download failed";
            // MEGA exceptions can contain the secret link key; never persist them in history.
            if (!closed) StatusText.Text = DirectDownloadClient.IsMega(uri)
                ? "MEGA could not complete this public download (" + ex.GetType().Name + "). The link may be unavailable or the host may require a password or a quota wait. Use Open download page for details."
                : ex.Message + " Use Open download page to continue in the browser.";
        }
        finally { pendingLinks.Remove(uri.AbsoluteUri); directOperations.Remove(item); if (!closed) { SaveHistory(); UpdateButtons(); } }
    }
    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (ActiveDownloadCount >= 3) throw new InvalidOperationException("Three downloads are already running. Wait for one to finish.");
            var operation = e.DownloadOperation;
            var name = ForumDownloadPolicy.DownloadName(e.ResultFilePath, operation.MimeType, operation.Uri);
            var item = new ForumDownload { Id = Guid.NewGuid().ToString("N"), Name = name, Host = Uri.TryCreate(operation.Uri, UriKind.Absolute, out var origin) ? origin.Host : "Download host", State = "Downloading", TotalBytes = ExpectedBytes(operation), CreatedUtc = DateTime.UtcNow };
            var directory = Path.Combine(stateRoot, "downloads", item.Id); Directory.CreateDirectory(directory);
            item.LocalPath = ModInstaller.SafePath(directory, name); e.ResultFilePath = item.LocalPath;
            Downloads.Insert(0, item); DownloadList.SelectedItem = item; operations[item] = operation;
            operation.BytesReceivedChanged += (_, _) => QueueProgress();
            operation.StateChanged += (_, _) => QueueProgress();
            SaveHistory(); UpdateButtons(); StatusText.Text = "Downloading " + name + "…"; QueueProgress();
        }
        catch (Exception ex) { e.Cancel = true; StatusText.Text = ex.Message; }
    }
    private static long ExpectedBytes(CoreWebView2DownloadOperation operation) => operation.TotalBytesToReceive is ulong value ? value > long.MaxValue ? long.MaxValue : (long)value : 0;
    private void QueueProgress()
    {
        if (closed || progressQueued) return;
        progressQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { progressQueued = false; if (!closed) PollDownloads(); });
    }
    private void PollDownloads()
    {
        foreach (var (item, operation) in operations)
        {
            if (item.State != "Downloading") continue;
            try
            {
                item.ReceivedBytes = operation.BytesReceived;
                item.TotalBytes = ExpectedBytes(operation);
                if (operation.State == CoreWebView2DownloadState.Completed) { item.State = "Ready to install"; StatusText.Text = "Download ready. Select Install selected to review its contents."; SaveHistory(); }
                else if (operation.State == CoreWebView2DownloadState.Interrupted) { item.State = "Interrupted: " + operation.InterruptReason; SaveHistory(); }
            }
            catch (InvalidOperationException) { item.State = "Interrupted: browser closed"; SaveHistory(); }
        }
        UpdateButtons();
    }
    private void DownloadList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void UpdateButtons()
    {
        if (InstallButton == null) return;
        var item = DownloadList.SelectedItem as ForumDownload;
        InstallButton.IsEnabled = !installing && item?.CanInstall == true;
        CancelButton.IsEnabled = item != null && (directOperations.ContainsKey(item) || item.State == "Downloading" && operations.ContainsKey(item));
        OpenPageButton.IsEnabled = item?.SourcePage != null;
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (installing || DownloadList.SelectedItem is not ForumDownload item || !item.CanInstall) return;
        installing = true; UpdateButtons(); StatusText.Text = "Preparing the installation preview…";
        try
        {
            if (await install(item.LocalPath, this)) { item.State = "Installed"; StatusText.Text = "Installed " + item.Name + ". Undo last install is available in the main window."; }
            else StatusText.Text = "The package was not installed. The download is still available.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; MessageBox.Show(this, ex.Message, "Forum installation", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { installing = false; SaveHistory(); UpdateButtons(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadList.SelectedItem is ForumDownload directItem && directOperations.TryGetValue(directItem, out var cancellation)) { cancellation.Cancel(); directItem.State = "Cancelling…"; UpdateButtons(); return; }
        if (DownloadList.SelectedItem is ForumDownload item && operations.TryGetValue(item, out var operation)) { item.State = "Cancelled"; operation.Cancel(); SaveHistory(); UpdateButtons(); }
    }
    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    { var path = Path.Combine(stateRoot, "downloads"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private void LoadHistory()
    {
        try
        {
            var history = Path.Combine(stateRoot, "downloads.json");
            if (!File.Exists(history) || new FileInfo(history).Length > 2 * 1024 * 1024) return;
            foreach (var item in (JsonSerializer.Deserialize<List<ForumDownload>>(File.ReadAllText(history)) ?? []).Take(100))
            {
                if (item == null || !Guid.TryParseExact(item.Id, "N", out _) || !ForumDownloadPolicy.IsPackage(item.Name)) continue;
                item.LocalPath = ModInstaller.SafePath(Path.Combine(stateRoot, "downloads", item.Id), item.Name);
                if (item.State is "Downloading" or "Resolving link" or "Cancelling…") item.State = "Interrupted: app closed";
                if (!File.Exists(item.LocalPath)) item.State = "File missing";
                Downloads.Add(item);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException) { StatusText.Text = "Previous downloads could not be read. " + ex.Message; }
    }
    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(stateRoot); var file = Path.Combine(stateRoot, "downloads.json");
            var temporary = file + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(Downloads.Take(100).ToList())); File.Move(temporary, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!closed) StatusText.Text = "Download history could not be saved. " + ex.Message; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (installing) { e.Cancel = true; StatusText.Text = "Wait for the installation to finish before closing."; return; }
        var active = operations.Where(pair => pair.Key.State == "Downloading").Select(pair => pair.Value).ToList();
        if (active.Count == 0 && directOperations.Count == 0) return;
        if (MessageBox.Show(this, "Cancel the running downloads and close the forum browser?", "Downloads in progress", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        foreach (var operation in active) operation.Cancel();
        foreach (var cancellation in directOperations.Values) cancellation.Cancel();
    }
}

public sealed class ForumDownload : INotifyPropertyChanged
{
    private string state = "", name = "", host = ""; private long received, total;
    public string Id { get; set; } = "";
    public string Name { get => name; set { name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
    public string Host { get => host; set { host = value; PropertyChanged?.Invoke(this, new(nameof(Host))); } }
    public DateTime CreatedUtc { get; set; }
    [JsonIgnore] public string LocalPath { get; set; } = "";
    [JsonIgnore] public Uri? SourcePage { get; set; }
    public string State { get => state; set { state = value; Changed(); } }
    public long ReceivedBytes { get => received; set { received = value; Changed(); } }
    public long TotalBytes { get => total; set { total = value; Changed(); } }
    [JsonIgnore] public string SizeText => ModPackage.FormatSize(ReceivedBytes) + (TotalBytes > 0 ? " / " + ModPackage.FormatSize(TotalBytes) : "");
    [JsonIgnore] public double Percent => State is "Ready to install" or "Installed" ? 100 : TotalBytes > 0 ? Math.Clamp(ReceivedBytes * 100d / TotalBytes, 0, 100) : 0;
    [JsonIgnore] public bool CanInstall => State is "Ready to install" or "Installed" && File.Exists(LocalPath);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() { foreach (var name in new[] { nameof(State), nameof(SizeText), nameof(Percent), nameof(CanInstall) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}
