using Microsoft.Win32;
using MXSimModManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MXSimModManager.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Window owner;
    private readonly string stateRoot;
    private readonly DispatcherTimer gameTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer scanTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private FileSystemWatcher? watcher;
    private Preferences prefs;
    private bool isBusy;
    private bool gameRunning;
    private string searchText = "", category = "All", statusText = "Reading your library…", activityText = "Capy Bara's • v1.4.5";
    private ModPackage? selectedPackage;
    private List<ModPackage> selection = [];
    private ModGroup? selectedGroup;
    private SortOption? selectedSort;
    private bool groupsHealthy = true;
    private readonly List<ModGroup> customGroups = [];
    public ObservableCollection<ModPackage> Packages { get; } = [];
    public ObservableCollection<ModGroup> Groups { get; } = [];
    public List<SortOption> SortOptions { get; } = [new("Name A–Z", "Name", ListSortDirection.Ascending), new("Name Z–A", "Name", ListSortDirection.Descending), new("Largest first", "SizeBytes", ListSortDirection.Descending), new("Smallest first", "SizeBytes", ListSortDirection.Ascending), new("Most tracks", "TrackCount", ListSortDirection.Descending), new("Recently modified", "ModifiedUtc", ListSortDirection.Descending)];
    public ICollectionView PackagesView { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand ScanCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand InstallFolderCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public ICommand RestartGameCommand { get; }
    public ICommand ReloadTrackCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CategoryCommand { get; }
    public ICommand ChooseLibraryCommand { get; }
    public ICommand NewGroupCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand DeleteGroupCommand { get; }
    public ICommand AssignGroupCommand { get; }
    public ICommand RemoveFromGroupCommand { get; }
    public ICommand SetupManagerCommand { get; }
    public ICommand AppearanceCommand { get; }
    public ICommand ForumCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public PreviewCatalog? PreviewCatalog { get; private set; }

    public MainViewModel(Window owner)
    {
        this.owner = owner;
        var args = Environment.GetCommandLineArgs();
        string? Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        stateRoot = Arg("--state") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MXSimModManager");
        Directory.CreateDirectory(stateRoot);
        try { prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(stateRoot, "settings.json"))) ?? new(); }
        catch { prefs = new(); }
        if (Arg("--library") is string library) prefs.PersonalFolder = Path.GetFullPath(library);
        PackagesView = CollectionViewSource.GetDefaultView(Packages);
        PackagesView.Filter = Matches;
        PackagesView.SortDescriptions.Add(new SortDescription(nameof(ModPackage.Name), ListSortDirection.Ascending));
        try { customGroups.AddRange(GroupStore.Load(stateRoot)); }
        catch (Exception ex) { groupsHealthy = false; StatusText = "Groups could not be loaded: " + ex.Message; }
        RefreshGroups(); selectedSort = SortOptions.FirstOrDefault(s => s.Name == prefs.SortName) ?? SortOptions[0]; ApplySort();
        ScanCommand = Command(_ => ScanAsync());
        InstallCommand = Command(async _ => { var dialog = new OpenFileDialog { Title = "Install MX Simulator mods", Filter = "Mod packages (*.zip;*.saf;*.rar;*.7z)|*.zip;*.saf;*.rar;*.7z", Multiselect = true }; if (dialog.ShowDialog(owner) == true) await InstallPathsAsync(dialog.FileNames); });
        InstallFolderCommand = Command(async _ => { var dialog = new OpenFolderDialog { Title = "Choose an extracted mod folder" }; if (dialog.ShowDialog(owner) == true) await InstallPathsAsync([dialog.FolderName]); });
        OpenLibraryCommand = Command(_ => { OpenFolder(PersonalFolder); return Task.CompletedTask; });
        LaunchGameCommand = Command(_ => { LaunchGame(); return Task.CompletedTask; });
        RestartGameCommand = Command(_ => RestartGameAsync());
        ReloadTrackCommand = Command(_ => { Clipboard.SetText("/forcereload"); MessageBox.Show(owner, "Copied /forcereload.\n\nIn MX Simulator, press T, paste the command, then restart the race. This reloads the current track.\n\nNew tracks, gear and bike packages may require closing and reopening the game. The library refresh button updates this manager's inventory.", "Reload current track", MessageBoxButton.OK, MessageBoxImage.Information); return Task.CompletedTask; });
        UndoCommand = Command(_ => UndoAsync());
        ToggleEnabledCommand = Command(_ => ToggleAsync(), () => SelectedPackage != null && !SelectedPackage.IsReadOnly && SelectedPackage.Scope != "Base game");
        RevealCommand = Command(_ => { Reveal(); return Task.CompletedTask; }, () => SelectedPackage != null);
        ExportCommand = Command(_ => ExportAsync());
        CategoryCommand = Command(arg => { category = arg?.ToString() ?? "All"; Raise(nameof(SelectedCategory)); PackagesView.Refresh(); SelectVisiblePackage(); ActivityText = category == "All" ? "All packages" : category; return Task.CompletedTask; });
        ChooseLibraryCommand = Command(async _ => { var dialog = new OpenFolderDialog { Title = "Choose your MX Simulator personal folder", InitialDirectory = Directory.Exists(PersonalFolder) ? PersonalFolder : "" }; if (dialog.ShowDialog(owner) == true) { prefs.PersonalFolder = dialog.FolderName; Save(); Raise(nameof(PersonalFolder)); WatchLibrary(); await ScanAsync(); } });
        NewGroupCommand = Command(_ => { CreateGroup(); return Task.CompletedTask; }, () => groupsHealthy);
        RenameGroupCommand = Command(_ => { RenameGroup(); return Task.CompletedTask; }, () => groupsHealthy && IsCustomGroup);
        DeleteGroupCommand = Command(_ => { DeleteGroup(); return Task.CompletedTask; }, () => groupsHealthy && IsCustomGroup);
        AssignGroupCommand = Command(_ => { AssignGroup(); return Task.CompletedTask; }, () => groupsHealthy && SelectedPackage != null);
        RemoveFromGroupCommand = Command(_ => { if (SelectedGroup != null) { GroupStore.Remove(SelectedGroup, CurrentSelection().Select(PackageKey)); SaveGroups(); PackagesView.Refresh(); SelectVisiblePackage(); } return Task.CompletedTask; }, () => groupsHealthy && IsCustomGroup && SelectedPackage != null);
        SetupManagerCommand = Command(_ => { new SetupManagerWindow(PersonalFolder, stateRoot, IsGameRunning) { Owner = owner }.ShowDialog(); return Task.CompletedTask; });
        AppearanceCommand = Command(_ => { new AppearanceWindow(PersonalFolder, prefs.InstallFolder, stateRoot, IsGameRunning) { Owner = owner }.ShowDialog(); return Task.CompletedTask; });
        ForumCommand = Command(_ => { new ForumWindow(stateRoot, async (file, dialogOwner) => await InstallPathsAsync([file], dialogOwner), Arg("--forum-url")) { Owner = owner }.ShowDialog(); return Task.CompletedTask; });
        PlayTrackCommand = Command(_ => PlaySelectedTrackAsync(), () => SelectedPackage is { IsDisabled: false } package && package.TrackIds.Count > 0);
        gameTimer.Tick += (_, _) => UpdateGameState();
        scanTimer.Tick += async (_, _) => { scanTimer.Stop(); if (!IsBusy) await ScanAsync(); else scanTimer.Start(); };
    }
    public string PersonalFolder => prefs.PersonalFolder;
    public string SelectedCategory => category;
    public ModGroup? SelectedGroup { get => selectedGroup; set { selectedGroup = value; Raise(); PackagesView.Refresh(); SelectVisiblePackage(); CommandManager.InvalidateRequerySuggested(); } }
    public SortOption? SelectedSortOption { get => selectedSort; set { selectedSort = value; Raise(); ApplySort(); prefs.SortName = value?.Name ?? ""; Save(); } }
    private bool IsCustomGroup => SelectedGroup != null && Guid.TryParseExact(SelectedGroup.Id, "N", out _);
    public bool IncludeBaseGame { get => prefs.IncludeBaseGame; set { if (prefs.IncludeBaseGame == value) return; prefs.IncludeBaseGame = value; Save(); Raise(); scanTimer.Stop(); scanTimer.Start(); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Raise(); CommandManager.InvalidateRequerySuggested(); } }
    public string SearchText { get => searchText; set { searchText = value; Raise(); PackagesView.Refresh(); SelectVisiblePackage(); } }
    public ModPackage? SelectedPackage { get => selectedPackage; set { selectedPackage = value; Raise(); CommandManager.InvalidateRequerySuggested(); } }
    public string StatusText { get => statusText; private set { statusText = value; Raise(); } }
    public string ActivityText { get => activityText; private set { activityText = value; Raise(); } }
    public string GameStatusText => gameRunning ? "Game is running" : "Game is closed";
    public string RestartNotice => prefs.PendingSinceUtc != null ? "Content changed. Close and relaunch MX Simulator to rebuild its file index." : gameRunning ? "You can install new files while playing. Replacements and enabling/disabling need the game closed." : "Ready to launch. The game reads your mod library at startup.";
    public int TotalCount { get; private set; }
    public int TrackCount { get; private set; }
    public int GearCount { get; private set; }
    public int BikeCount { get; private set; }
    public string LibrarySizeText { get; private set; } = "";

    public async Task InitializeAsync()
    {
        UpdateGameState(); WatchLibrary(); gameTimer.Start(); await ScanAsync();
        if (ModInstaller.History(stateRoot).Any(r => !r.Completed && !r.Undone && Path.GetFullPath(r.LibraryRoot).Equals(Path.GetFullPath(PersonalFolder), StringComparison.OrdinalIgnoreCase)))
            StatusText = "An interrupted installation needs recovery. Close the game and use Undo last install.";
    }
    private ICommand Command(Func<object?, Task> run, Func<bool>? can = null) => new AsyncCommand(async arg => { try { await run(arg); } catch (Exception ex) { ShowError(ex); } }, () => !IsBusy && (can?.Invoke() ?? true));
    private bool Matches(object value)
    {
        if (value is not ModPackage p) return false;
        var c = p.Categories;
        var key = PackageKey(p);
        if (SelectedGroup?.Id == "ungrouped" && customGroups.Any(g => g.Members.Contains(key, StringComparer.OrdinalIgnoreCase))) return false;
        if (IsCustomGroup && !SelectedGroup!.Members.Contains(key, StringComparer.OrdinalIgnoreCase)) return false;
        if (category == "Tracks" && !c.Contains("Track", StringComparison.OrdinalIgnoreCase)) return false;
        if (category == "Gear" && !c.Contains("Gear", StringComparison.OrdinalIgnoreCase)) return false;
        if (category == "Bikes" && !c.Contains("Bike", StringComparison.OrdinalIgnoreCase) && !c.Contains("Wheel", StringComparison.OrdinalIgnoreCase)) return false;
        if (category == "Other" && (c.Contains("Track", StringComparison.OrdinalIgnoreCase) || c.Contains("Gear", StringComparison.OrdinalIgnoreCase) || c.Contains("Bike", StringComparison.OrdinalIgnoreCase) || c.Contains("Wheel", StringComparison.OrdinalIgnoreCase))) return false;
        return string.IsNullOrWhiteSpace(SearchText) || (p.Name + " " + p.Categories + " " + p.Path + " " + p.Details + " " + string.Join(' ', p.TrackIds) + " " + string.Join(' ', p.TrackNames)).Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
    private void SelectVisiblePackage() => SelectedPackage = PackagesView.Cast<ModPackage>().FirstOrDefault();
    public void SetSelection(IEnumerable<ModPackage> items) { selection = items.ToList(); CommandManager.InvalidateRequerySuggested(); }
    private List<ModPackage> CurrentSelection() => selection.Count > 0 ? selection : SelectedPackage != null ? [SelectedPackage] : [];
    private string PackageKey(ModPackage package)
    {
        var path = package.Path;
        if (package.IsDisabled)
        {
            try
            {
                var parts = Path.GetRelativePath(Path.Combine(stateRoot, "disabled"), path).Split(Path.DirectorySeparatorChar);
                var metadata = Path.Combine(stateRoot, "disabled", parts[0], "disabled.json");
                var info = JsonSerializer.Deserialize<DisabledPackage>(File.ReadAllText(metadata)); if (info != null) path = info.OriginalPath;
            }
            catch { }
        }
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    }
    private void ApplySort()
    {
        if (selectedSort == null) return;
        using (PackagesView.DeferRefresh()) { PackagesView.SortDescriptions.Clear(); PackagesView.SortDescriptions.Add(new SortDescription(selectedSort.Member, selectedSort.Direction)); if (selectedSort.Member != "Name") PackagesView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending)); }
    }
    private void RefreshGroups(string? selectId = null)
    {
        var id = selectId ?? SelectedGroup?.Id ?? "all";
        Groups.Clear(); Groups.Add(new ModGroup { Id = "all", Name = "All groups" }); Groups.Add(new ModGroup { Id = "ungrouped", Name = "Ungrouped" });
        foreach (var group in customGroups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) Groups.Add(group);
        SelectedGroup = Groups.FirstOrDefault(g => g.Id == id) ?? Groups[0];
    }
    private void SaveGroups() => GroupStore.Save(stateRoot, customGroups);
    private void CreateGroup()
    {
        var name = Dialogs.Prompt(owner, "New group", "Name this group. Currently selected packages will be added.", ""); if (name == null) return;
        var keys = CurrentSelection().Select(PackageKey).ToList();
        var group = GroupStore.Create(customGroups, name); GroupStore.Add(group, keys); SaveGroups(); RefreshGroups(group.Id);
    }
    private void RenameGroup()
    {
        if (!IsCustomGroup) return;
        var group = SelectedGroup!; var name = Dialogs.Prompt(owner, "Rename group", "Choose a new name.", group.Name); if (name == null) return;
        GroupStore.Rename(customGroups, group, name); SaveGroups(); RefreshGroups(group.Id);
    }
    private void DeleteGroup()
    {
        if (!IsCustomGroup) return;
        if (MessageBox.Show(owner, $"Delete the group '{SelectedGroup!.Name}'?\n\nThe installed files will stay in place.", "Delete group", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        customGroups.Remove(SelectedGroup); SaveGroups(); RefreshGroups("all");
    }
    private void AssignGroup()
    {
        if (customGroups.Count == 0) { CreateGroup(); return; }
        var group = Dialogs.ChooseGroup(owner, customGroups); if (group == null) return;
        var selected = CurrentSelection(); GroupStore.Add(group, selected.Select(PackageKey)); SaveGroups(); PackagesView.Refresh();
        StatusText = $"Added {selected.Count} package(s) to {group.Name}.";
    }
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true; StatusText = "Scanning tracks, gear and bikes…";
        try
        {
            var personal = PersonalFolder; var install = prefs.InstallFolder; var include = IncludeBaseGame; var selectedPath = SelectedPackage?.Path;
            var result = await Task.Run(() => { var snapshot = LibraryScanner.Scan(personal, install, Path.Combine(stateRoot, "disabled"), include, collectPreviewFiles: true); return (snapshot, catalog: new PreviewCatalog(snapshot.PreviewFiles)); });
            var snapshot = result.snapshot; PreviewCatalog = result.catalog; Raise(nameof(PreviewCatalog));
            Packages.Clear(); foreach (var item in snapshot.Packages) Packages.Add(item);
            TotalCount = Packages.Count; TrackCount = snapshot.UniqueTrackCount; GearCount = snapshot.GearPackageCount; BikeCount = snapshot.BikePackageCount;
            LibrarySizeText = ModPackage.FormatSize(snapshot.TotalBytes) + " indexed";
            foreach (var name in new[] { nameof(TotalCount), nameof(TrackCount), nameof(GearCount), nameof(BikeCount), nameof(LibrarySizeText) }) Raise(name);
            SelectedPackage = PackagesView.Cast<ModPackage>().FirstOrDefault(x => x.Path == selectedPath) ?? PackagesView.Cast<ModPackage>().FirstOrDefault();
            var warning = snapshot.Warnings.Count > 0 ? $" • {snapshot.Warnings.Count} scan notes (saved in app data)" : "";
            StatusText = $"{TotalCount} packages • {TrackCount} unique tracks{warning}";
            ActivityText = "Updated " + DateTime.Now.ToString("h:mm tt");
            File.WriteAllText(Path.Combine(stateRoot, "scan-notes.txt"), string.Join(Environment.NewLine, snapshot.Warnings));
        }
        catch (Exception ex) { ShowError(ex); }
        finally { IsBusy = false; }
    }
    public async Task<bool> InstallPathsAsync(IEnumerable<string> paths, Window? dialogOwner = null)
    {
        if (IsBusy) return false;
        var installed = false;
        foreach (var source in paths)
        {
            IsBusy = true; StatusText = "Preparing " + Path.GetFileName(source) + "…";
            try
            {
                using var plan = await Task.Run(() => ModInstaller.Prepare(source, stateRoot));
                var preview = new InstallPreviewWindow(plan, PersonalFolder, IsGameRunning()) { Owner = dialogOwner ?? owner };
                if (preview.ShowDialog() != true) { StatusText = "Installation cancelled."; continue; }
                StatusText = "Installing files and saving originals…";
                var running = IsGameRunning();
                var receipt = await Task.Run(() => ModInstaller.Install(plan, PersonalFolder, stateRoot, running));
                installed = true;
                MarkChanged(running);
                StatusText = $"Installed {receipt.Files.Count} files. Undo is available.";
            }
            catch (Exception ex) { ShowError(ex, dialogOwner); }
            finally { IsBusy = false; }
        }
        await ScanAsync();
        return installed;
    }
    private async Task UndoAsync()
    {
        if (IsGameRunning()) { MessageBox.Show(owner, "Close MX Simulator before undoing an installation.", "Game is running"); return; }
        var last = ModInstaller.History(stateRoot).FirstOrDefault(x => !x.Undone && Path.GetFullPath(x.LibraryRoot).Equals(Path.GetFullPath(PersonalFolder), StringComparison.OrdinalIgnoreCase));
        if (last == null) { MessageBox.Show(owner, "No active installations made by this manager were found for this library.", "Nothing to undo"); return; }
        if (MessageBox.Show(owner, $"{(last.Completed ? "Undo" : "Recover interrupted installation:")} {last.Name}?\n\n{last.Files.Count} planned files will be checked and reverted. Original files are restored from backup. Files changed since installation are protected.", "Undo installation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try { await Task.Run(() => ModInstaller.Undo(last, stateRoot)); MarkChanged(false); }
        finally { IsBusy = false; }
        await ScanAsync();
    }
    private async Task ToggleAsync()
    {
        var package = SelectedPackage;
        if (package == null || package.IsReadOnly) return;
        if (IsGameRunning()) { MessageBox.Show(owner, "Close MX Simulator before enabling or disabling a package.", "Game is running"); return; }
        if (!package.IsDisabled && MessageBox.Show(owner, $"Disable {package.Name}?\n\nThe complete package will be stored outside the game library. Other mods may depend on its models or assets. You can enable it here later.", "Disable package", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try { await Task.Run(() => { if (package.IsDisabled) ModInstaller.Enable(package, PersonalFolder, stateRoot); else ModInstaller.Disable(package, PersonalFolder, stateRoot); }); MarkChanged(false); }
        finally { IsBusy = false; }
        await ScanAsync();
    }
    private void MarkChanged(bool running)
    {
        prefs.PendingSinceUtc = running ? DateTime.UtcNow : null; Save(); Raise(nameof(RestartNotice));
    }
    private bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("mx");
        try { return processes.Any(x => !x.HasExited); }
        finally { foreach (var p in processes) p.Dispose(); }
    }
    private void UpdateGameState()
    {
        var running = IsGameRunning();
        if (prefs.PendingSinceUtc is DateTime changed && running)
        {
            var processes = Process.GetProcessesByName("mx");
            try { if (processes.Any(x => x.StartTime.ToUniversalTime() > changed)) { prefs.PendingSinceUtc = null; Save(); } }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        gameRunning = running; Raise(nameof(GameStatusText)); Raise(nameof(RestartNotice));
    }
    private async Task PlaySelectedTrackAsync()
    {
        var package = SelectedPackage;
        if (package == null || package.IsDisabled || package.TrackIds.Count == 0) return;
        var id = package.TrackIds.Count == 1 ? package.TrackIds[0] : Dialogs.ChooseTrack(owner, package.Name, package.TrackIds);
        if (id == null) return;
        _ = GameLaunch.ValidateTrackId(id);
        if (IsGameRunning()) await RestartGameAsync(id);
        else LaunchGame(id);
    }
    private void LaunchGame(string? trackId = null)
    {
        if (IsGameRunning()) { MessageBox.Show(owner, "MX Simulator is already running. Use Restart to load new content if needed.", "Game is running"); return; }
        var exe = Path.Combine(prefs.InstallFolder, "mx.exe");
        if (!File.Exists(exe))
        {
            var dialog = new OpenFileDialog { Title = "Locate MX Simulator's mx.exe", Filter = "MX Simulator (mx.exe)|mx.exe" };
            if (dialog.ShowDialog(owner) != true) return;
            exe = dialog.FileName; prefs.InstallFolder = Path.GetDirectoryName(exe)!; Save();
        }
        var libraryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(PersonalFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..16];
        var setupState = Path.Combine(stateRoot, "libraries", libraryKey);
        Process.Start(trackId == null ? new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! } : GameLaunch.ForTrack(exe, trackId, PersonalFolder, setupState, IsGameRunning()));
        prefs.PendingSinceUtc = null; Save(); UpdateGameState();
        if (trackId != null) StatusText = "Saved setup retained. Track selected: " + trackId + ". Choose New Race → Start in MX Simulator.";
    }
    private async Task RestartGameAsync(string? trackId = null)
    {
        if (!IsGameRunning()) { LaunchGame(trackId); return; }
        var question = trackId == null ? "Close MX Simulator and reopen it to load new content?" : "Restart MX Simulator on this track?\n" + trackId;
        if (MessageBox.Show(owner, question + "\n\nThis ends the current game session. Save your setup and replay first. Quit through the game's menu first if you need to save live settings. The manager will request a normal window close and wait for exit.", "Restart MX Simulator", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var processes = Process.GetProcessesByName("mx");
        try
        {
            if (processes.Length != 1 || !string.Equals(processes[0].MainModule?.FileName, Path.Combine(prefs.InstallFolder, "mx.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The running game could not be matched to this installation. Close it yourself, then use Launch game.");
            IsBusy = true; StatusText = "Waiting for MX Simulator to close…";
            if (!processes[0].CloseMainWindow()) throw new InvalidOperationException("The game did not accept a normal close request. Close it yourself, then use Launch game.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await processes[0].WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new InvalidOperationException("The game is still open. Finish closing it, then use Launch game."); }
            LaunchGame(trackId); if (trackId == null) StatusText = "Game relaunched with the current library.";
        }
        finally { foreach (var process in processes) process.Dispose(); IsBusy = false; }
    }
    private void WatchLibrary()
    {
        watcher?.Dispose(); watcher = null;
        if (!Directory.Exists(PersonalFolder)) return;
        watcher = new FileSystemWatcher(PersonalFolder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        void Changed(string? name)
        {
            if (name != null)
            {
                var parts = name.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(p => new[] { "screenshots", "demos", "replays", "setups", "logs", "cache" }.Contains(p, StringComparer.OrdinalIgnoreCase))) return;
                var file = Path.GetFileName(name);
                if (file.StartsWith("screenshot", StringComparison.OrdinalIgnoreCase) || file.Equals("config", StringComparison.OrdinalIgnoreCase) || file.Equals("regkey", StringComparison.OrdinalIgnoreCase) || file.Equals("lastlog.txt", StringComparison.OrdinalIgnoreCase)) return;
            }
            owner.Dispatcher.BeginInvoke(() => { if (IsGameRunning()) MarkChanged(true); scanTimer.Stop(); scanTimer.Start(); });
        }
        watcher.Created += (_, e) => Changed(e.Name); watcher.Deleted += (_, e) => Changed(e.Name); watcher.Renamed += (_, e) => Changed(e.Name); watcher.Changed += (_, e) => Changed(e.Name); watcher.Error += (_, _) => Changed(null);
    }
    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog { Title = "Export inventory", Filter = "CSV inventory (*.csv)|*.csv", FileName = "MX-Simulator-Inventory.csv" };
        if (dialog.ShowDialog(owner) != true) return;
        static string Cell(string value) { if (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        var rows = new List<string> { "Name,Type,State,Scope,Bytes,Track IDs,Skins,Models,Path" };
        rows.AddRange(Packages.Select(p => string.Join(',', new[] { p.Name, p.Categories, p.StatusLabel, p.Scope, p.SizeBytes.ToString(), string.Join("; ", p.TrackIds), p.SkinCount.ToString(), p.ModelCount.ToString(), p.Path }.Select(Cell))));
        await File.WriteAllLinesAsync(dialog.FileName, rows, new UTF8Encoding(true)); StatusText = "Inventory exported.";
    }
    private void Reveal()
    {
        if (SelectedPackage == null) return;
        var path = SelectedPackage.Path;
        if (Directory.Exists(path)) OpenFolder(path);
        else if (File.Exists(path)) { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); Process.Start(start); }
    }
    private static void OpenFolder(string path) { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private void Save() => File.WriteAllText(Path.Combine(stateRoot, "settings.json"), JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
    private void ShowError(Exception ex, Window? dialogOwner = null) { StatusText = ex.Message; MessageBox.Show(dialogOwner ?? owner, ex.Message, Brand.Name, MessageBoxButton.OK, MessageBoxImage.Error); }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() { watcher?.Dispose(); scanTimer.Stop(); gameTimer.Stop(); }
    private class Preferences
    {
        public string PersonalFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MX Simulator");
        public string InstallFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MX Simulator");
        public bool IncludeBaseGame { get; set; }
        public DateTime? PendingSinceUtc { get; set; }
        public string SortName { get; set; } = "Name A–Z";
    }
}

public record SortOption(string Name, string Member, ListSortDirection Direction);

public sealed class AsyncCommand(Func<object?, Task> execute, Func<bool> canExecute) : ICommand
{
    private bool running;
    public bool CanExecute(object? parameter) => !running && canExecute();
    public async void Execute(object? parameter) { if (!CanExecute(parameter)) return; running = true; CommandManager.InvalidateRequerySuggested(); try { await execute(parameter); } finally { running = false; CommandManager.InvalidateRequerySuggested(); } }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
