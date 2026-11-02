using Microsoft.Win32;
using MXSimModManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace MXSimModManager.Desktop;

public partial class AppearanceWindow : Window, INotifyPropertyChanged
{
    private readonly string _personalFolder;
    private readonly string _installFolder;
    private readonly string _stateRoot;
    private readonly Func<bool> _isGameRunning;
    private readonly DispatcherTimer _gameTimer;
    private AppearanceCatalog _catalog = new();
    private AppearanceSelection _baseline = new();
    private AppearanceChoice? _selectedBike, _selectedBikeSkin, _selectedHelmet, _selectedGear, _selectedWheels;
    private SetupProfile? _selectedPreset;
    private bool _loading;
    private bool _dirty;
    private string _statusText = "Reading installed appearance assets…";

    public ObservableCollection<AppearanceChoice> BikeOptions { get; } = [];
    public ObservableCollection<AppearanceChoice> BikeSkinOptions { get; } = [];
    public ObservableCollection<AppearanceChoice> HelmetOptions { get; } = [];
    public ObservableCollection<AppearanceChoice> GearOptions { get; } = [];
    public ObservableCollection<AppearanceChoice> WheelsOptions { get; } = [];
    public ObservableCollection<SetupProfile> Presets { get; } = [];
    public AppearanceChoice? SelectedBike
    {
        get => _selectedBike;
        set
        {
            if (ReferenceEquals(_selectedBike, value)) return;
            var previousSkin = _selectedBikeSkin?.Value;
            _selectedBike = value; Notify();
            if (!_loading)
            {
                Fill(BikeSkinOptions, value == null ? [] : _catalog.GetBikeSkins(value.Value));
                SelectedBikeSkin = BikeSkinOptions.FirstOrDefault(choice => choice.Value == previousSkin) ??
                    BikeSkinOptions.FirstOrDefault(choice => choice.Value == "default");
            }
            Notify(nameof(ContentSummary)); RecomputeDirty();
        }
    }
    public AppearanceChoice? SelectedBikeSkin { get => _selectedBikeSkin; set { _selectedBikeSkin = value; Notify(); RecomputeDirty(); } }
    public AppearanceChoice? SelectedHelmet { get => _selectedHelmet; set { _selectedHelmet = value; Notify(); RecomputeDirty(); } }
    public AppearanceChoice? SelectedGear { get => _selectedGear; set { _selectedGear = value; Notify(); RecomputeDirty(); } }
    public AppearanceChoice? SelectedWheels { get => _selectedWheels; set { _selectedWheels = value; Notify(); RecomputeDirty(); } }
    public SetupProfile? SelectedPreset
    {
        get => _selectedPreset;
        set { _selectedPreset = value; Notify(); Notify(nameof(HasPreset)); Notify(nameof(PresetNote)); }
    }
    public bool HasPreset => SelectedPreset != null;
    public string PresetNote => SelectedPreset?.HasOtherSettings == true
        ? "This profile contains bike setup values too. Load it into the editor, then save a new appearance preset to preserve your setup numbers. Direct game commands for full setups are blocked here."
        : "Appearance presets contain only your five appearance choices. Suspension, gearing, stability and rider number stay unchanged. Existing setup files are never overwritten.";
    public string DirtyText => _dirty ? "Unsaved selection" : "";
    public string StatusText => _statusText;
    public string GameStatusText => _isGameRunning()
        ? "Game running. Save a preset and copy its load command to change appearance in game."
        : "Game closed. Apply will update the saved config and preserve a backup.";
    public string ContentSummary => $"{_catalog.Bikes.Count} bike models · {BikeSkinOptions.Count(choice => choice.IsAvailable)} skins for this bike · " +
        $"{_catalog.Helmets.Count} helmets · {_catalog.Gear.Count} gear choices · {_catalog.Wheels.Count} wheel choices" +
        (_catalog.Warnings.Count > 0 ? $" · {_catalog.Warnings.Count} scan warnings" : "");
    public event PropertyChangedEventHandler? PropertyChanged;

    public AppearanceWindow(string personalFolder, string installFolder, string stateRoot, Func<bool> isGameRunning)
    {
        _personalFolder = personalFolder; _installFolder = installFolder; _isGameRunning = isGameRunning;
        var libraryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(personalFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..16];
        _stateRoot = Path.Combine(stateRoot, "libraries", libraryKey);
        Directory.CreateDirectory(_stateRoot);
        InitializeComponent();
        DataContext = this;
        foreach (var combo in Combos()) combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Combo_TextChanged), true);
        Loaded += (_, _) => Run(InitializeLibrary);
        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameTimer.Tick += (_, _) => Notify(nameof(GameStatusText));
        _gameTimer.Start();
        Closed += (_, _) => _gameTimer.Stop();
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void SetStatus(string text) { _statusText = text; Notify(nameof(StatusText)); Notify(nameof(GameStatusText)); }
    private void SetDirty(bool value) { _dirty = value; Notify(nameof(DirtyText)); }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Appearance manager", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private IEnumerable<ComboBox> Combos() => [BikeCombo, BikeSkinCombo, HelmetCombo, GearCombo, WheelsCombo];
    private static void Fill(ObservableCollection<AppearanceChoice> target, IEnumerable<AppearanceChoice> choices)
    {
        target.Clear(); foreach (var choice in choices) target.Add(choice);
    }
    private static AppearanceChoice? FindOrPreserve(ObservableCollection<AppearanceChoice> choices, string value)
    {
        var known = choices.FirstOrDefault(choice => choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (known != null) return known;
        var unavailable = new AppearanceChoice { Label = string.IsNullOrEmpty(value) ? "Unspecified (saved; not found)" : value + " (saved; not found)", Value = value, IsAvailable = false };
        choices.Insert(0, unavailable);
        return unavailable;
    }
    private AppearanceSelection Snapshot() => new()
    {
        Bike = SelectedBike?.Value ?? "", BikeSkin = SelectedBikeSkin?.Value ?? "", Helmet = SelectedHelmet?.Value ?? "",
        Gear = SelectedGear?.Value ?? "", Wheels = SelectedWheels?.Value ?? ""
    };
    private static AppearanceSelection Copy(AppearanceSelection source) => new()
    { Bike = source.Bike, BikeSkin = source.BikeSkin, Helmet = source.Helmet, Gear = source.Gear, Wheels = source.Wheels };
    private static bool SameSelection(AppearanceSelection left, AppearanceSelection right) =>
        left.Bike == right.Bike && left.BikeSkin == right.BikeSkin && left.Helmet == right.Helmet && left.Gear == right.Gear && left.Wheels == right.Wheels;
    private void SetEditor(AppearanceSelection selection, bool setBaseline)
    {
        _loading = true;
        try
        {
            Fill(BikeOptions, _catalog.Bikes); Fill(HelmetOptions, _catalog.Helmets); Fill(GearOptions, _catalog.Gear); Fill(WheelsOptions, _catalog.Wheels);
            SelectedBike = FindOrPreserve(BikeOptions, selection.Bike);
            Fill(BikeSkinOptions, string.IsNullOrEmpty(selection.Bike) ? [] : _catalog.GetBikeSkins(selection.Bike));
            SelectedBikeSkin = FindOrPreserve(BikeSkinOptions, selection.BikeSkin);
            SelectedHelmet = FindOrPreserve(HelmetOptions, selection.Helmet);
            SelectedGear = FindOrPreserve(GearOptions, selection.Gear);
            SelectedWheels = FindOrPreserve(WheelsOptions, selection.Wheels);
            if (setBaseline) _baseline = Copy(selection);
        }
        finally { _loading = false; }
        SetDirty(!SameSelection(Snapshot(), _baseline)); Notify(nameof(ContentSummary));
    }
    private void InitializeLibrary()
    {
        _catalog = AppearanceManager.Scan(_personalFolder, _installFolder);
        string? readError = null;
        AppearanceSelection current;
        try { current = AppearanceManager.ReadCurrent(_personalFolder); }
        catch (Exception ex) { current = new AppearanceSelection(); readError = ex.Message; }
        SetEditor(current, true); RefreshPresets(null);
        SetStatus(readError == null ? "Loaded the saved appearance. Missing selections remain visible until you choose replacements." : "Installed choices loaded. Saved appearance could not be read: " + readError);
    }
    private void RefreshPresets(string? selectedPath)
    {
        var presets = AppearanceManager.ListPresets(_personalFolder);
        Presets.Clear(); foreach (var profile in presets.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)) Presets.Add(profile);
        SelectedPreset = Presets.FirstOrDefault(profile => profile.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase)) ?? Presets.FirstOrDefault();
    }
    private void Combo_TextChanged(object sender, TextChangedEventArgs e) => RecomputeDirty();
    private void RecomputeDirty()
    {
        if (_loading || !IsInitialized) return;
        var typed = Combos().Any(combo => combo.SelectedItem is AppearanceChoice choice
            ? combo.Text != choice.Label : !string.IsNullOrEmpty(combo.Text));
        SetDirty(typed || !SameSelection(Snapshot(), _baseline));
    }
    private AppearanceSelection ReadEditor()
    {
        foreach (var combo in Combos())
        {
            if (combo.SelectedItem is not AppearanceChoice choice || combo.Text != choice.Label)
                throw new InvalidOperationException("Choose an installed option from each dropdown. Typed names must match a listed option.");
        }
        return Snapshot();
    }
    private void Rescan_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var selection = Snapshot();
        _catalog = AppearanceManager.Scan(_personalFolder, _installFolder);
        SetEditor(selection, false); RefreshPresets(SelectedPreset?.Path);
        SetStatus("Rescanned installed assets and preserved your current selections. New mod files may still need a game restart.");
    });
    private bool ConfirmUnsaved(string action)
    {
        RecomputeDirty();
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, $"Save your current appearance as a preset before {action}?\n\nYes saves it. No discards the unsaved selection. Cancel keeps the editor open.",
            "Unsaved appearance", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SavePresetCore() != null;
    }
    private void ReadCurrent_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!ConfirmUnsaved("reading saved appearance")) return;
        if (_isGameRunning() && MessageBox.Show(this, "The game is running. Its config file may not contain your current live appearance.\n\nRead the values saved on disk?",
            "Read saved values?", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        SetEditor(AppearanceManager.ReadCurrent(_personalFolder), true);
        SetStatus("Read appearance from the saved game config. No files were changed.");
    });
    private SetupProfile? SavePresetCore()
    {
        var selection = ReadEditor();
        var name = Dialogs.Prompt(this, "Save appearance preset", "Name this new native appearance preset. Existing presets will be preserved.", "My appearance")?.Trim();
        if (name == null) return null;
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Enter a profile name without folder separators.");
        var destination = Path.Combine(_personalFolder, "setups", name);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("That setup name already exists. Choose a new name to preserve it.");
        var saved = AppearanceManager.SavePreset(_catalog, selection, _personalFolder, _stateRoot, name);
        RefreshPresets(saved.Path); _baseline = Copy(selection); SetDirty(false);
        SetStatus($"Saved appearance preset '{saved.Name}'. Select Copy game load command to use it while playing.");
        return saved;
    }
    private void SavePreset_Click(object sender, RoutedEventArgs e) => Run(() => SavePresetCore());
    private void LoadPreset_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var requested = SelectedPreset;
        if (requested == null) return;
        if (!ConfirmUnsaved("loading this preset into the editor")) return;
        var preset = AppearanceManager.ReadPreset(requested.Path);
        var merged = Snapshot();
        if (!string.IsNullOrEmpty(preset.Bike)) merged.Bike = preset.Bike;
        if (!string.IsNullOrEmpty(preset.BikeSkin)) merged.BikeSkin = preset.BikeSkin;
        if (!string.IsNullOrEmpty(preset.Helmet)) merged.Helmet = preset.Helmet;
        if (!string.IsNullOrEmpty(preset.Gear)) merged.Gear = preset.Gear;
        if (!string.IsNullOrEmpty(preset.Wheels)) merged.Wheels = preset.Wheels;
        SetEditor(merged, true);
        SelectedPreset = Presets.FirstOrDefault(profile => profile.Path.Equals(requested.Path, StringComparison.OrdinalIgnoreCase));
        SetStatus($"Loaded '{requested.Name}' into the editor. Missing fields were kept from your previous selection; the game config was not changed.");
    });
    private void CopyPresetCommand(SetupProfile profile)
    {
        profile = AppearanceManager.ListPresets(_personalFolder).FirstOrDefault(p =>
            p.Path.Equals(profile.Path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("This preset no longer exists. Refresh the appearance presets and try again.");
        if (string.IsNullOrWhiteSpace(profile.LoadCommand)) throw new InvalidOperationException("This preset's filename cannot be used in a safe game command. Save a new appearance preset with a simple name.");
        if (profile.HasOtherSettings)
            throw new InvalidOperationException("To preserve your setup numbers, select Load selected into editor, then Save selection as preset using a new name. Copy the command from that appearance-only preset.");
        Clipboard.SetText(profile.LoadCommand);
        SetStatus($"Copied {profile.LoadCommand}. In MX Simulator, press T, paste, then press Enter.");
    }
    private void CopyCommand_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (SelectedPreset == null) return;
        RecomputeDirty();
        if (_dirty)
        {
            var answer = MessageBox.Show(this, "The editor has an unsaved selection.\n\nSave a new appearance preset and copy its command? Yes saves it; No copies the currently selected saved preset; Cancel stops.",
                "Copy saved appearance command", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                var saved = SavePresetCore();
                if (saved != null) CopyPresetCommand(saved);
                return;
            }
        }
        CopyPresetCommand(SelectedPreset!);
    });
    private void Apply_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_isGameRunning())
        {
            if (MessageBox.Show(this, "MX Simulator is running, so its saved config will stay unchanged.\n\nSave this selection as an appearance preset and copy its game load command? You can paste it in the game after pressing T.",
                "Load appearance while playing", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                var preset = SavePresetCore();
                if (preset != null) CopyPresetCommand(preset);
            }
            return;
        }
        var selection = ReadEditor();
        if (MessageBox.Show(this, "Apply these five appearance choices to the saved game config?\n\nThe complete previous config will be backed up before any change.",
            "Apply saved appearance?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var backup = AppearanceManager.Apply(_catalog, selection, _personalFolder, _stateRoot, _isGameRunning());
        _baseline = Copy(selection); SetDirty(false);
        SetStatus("Applied appearance. Previous config backup: " + backup);
    });
    private void RestoreBackup_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_isGameRunning()) throw new InvalidOperationException("Close MX Simulator before restoring its saved config.");
        if (!ConfirmUnsaved("restoring a previous config")) return;
        var backups = SetupManager.ListBackups(_stateRoot);
        if (backups.Count == 0) throw new InvalidOperationException("No config backups have been created for this library yet.");
        var latest = backups.OrderByDescending(File.GetLastWriteTimeUtc).First();
        var picker = new OpenFileDialog { Title = "Restore a previous game config backup", InitialDirectory = Path.GetDirectoryName(latest), FileName = Path.GetFileName(latest), Filter = "Config backups|*.config|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (!backups.Any(path => Path.GetFullPath(path).Equals(Path.GetFullPath(picker.FileName), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose one of this library's config backups created by the manager.");
        if (MessageBox.Show(this, $"Restore the complete saved game config from this backup?\n\n{picker.FileName}\n\nThe current config will also be backed up first.",
            "Restore game config?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var backup = SetupManager.RestoreBackup(picker.FileName, _personalFolder, _stateRoot, _isGameRunning());
        SetEditor(AppearanceManager.ReadCurrent(_personalFolder), true);
        SetStatus("Restored previous config. The replaced config was backed up to: " + backup);
    });
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try { if (!ConfirmUnsaved("closing the appearance manager")) e.Cancel = true; }
        catch (Exception ex) { e.Cancel = true; SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Appearance manager", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
