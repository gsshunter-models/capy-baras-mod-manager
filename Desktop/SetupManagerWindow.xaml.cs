using Microsoft.Win32;
using MXSimModManager.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MXSimModManager.Desktop;

public partial class SetupManagerWindow : Window, INotifyPropertyChanged
{
    private readonly string _personalFolder;
    private readonly string _stateRoot;
    private readonly Func<bool> _isGameRunning;
    private readonly ObservableCollection<EditableSetting> _settings = [];
    private readonly Dictionary<string, string> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private SetupProfile? _currentProfile;
    private bool _dirty;
    private bool _suppressSelection;
    private string _editorTitle = "Select a saved profile";
    private string _profileNote = "Choose a profile, import one, or read the saved game settings to begin.";
    private string _statusText = "No game files have been changed.";
    private bool _loadingEditor;

    public ObservableCollection<SetupProfile> Profiles { get; } = [];
    public ICollectionView SettingsView { get; }
    public string EditorTitle => _editorTitle;
    public string ProfileNote => _profileNote;
    public string StatusText => _statusText;
    public string DirtyText => _dirty ? "Unsaved changes" : "";
    public bool HasSettings => _settings.Count > 0;
    public bool HasProfile => _currentProfile != null;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SetupManagerWindow(string personalFolder, string stateRoot, Func<bool> isGameRunning)
    {
        _personalFolder = personalFolder;
        var libraryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(personalFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..16];
        _stateRoot = Path.Combine(stateRoot, "libraries", libraryKey);
        Directory.CreateDirectory(_stateRoot);
        _isGameRunning = isGameRunning;
        SettingsView = CollectionViewSource.GetDefaultView(_settings);
        SettingsView.Filter = FilterSetting;
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => Run(() => RefreshProfiles(null));
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void SetStatus(string text) { _statusText = text; Notify(nameof(StatusText)); }
    private void SetDirty(bool value) { _dirty = value; Notify(nameof(DirtyText)); }
    private void NotifyEditor()
    {
        Notify(nameof(EditorTitle)); Notify(nameof(ProfileNote)); Notify(nameof(HasSettings)); Notify(nameof(HasProfile)); Notify(nameof(DirtyText));
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "Setup manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private bool FilterSetting(object item)
    {
        if (item is not EditableSetting setting) return false;
        var query = SettingsSearch?.Text?.Trim() ?? "";
        return query.Length == 0 || setting.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            setting.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || setting.Group.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    private void CommitEditor()
    {
        if (!SettingsGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !SettingsGrid.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException("Finish editing the current value before continuing.");
    }
    private void RefreshProfiles(string? selectPath)
    {
        var profiles = SetupManager.List(_personalFolder);
        _suppressSelection = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) Profiles.Add(profile);
            ProfileList.SelectedItem = Profiles.FirstOrDefault(p => SamePath(p.Path, selectPath)) ?? Profiles.FirstOrDefault();
        }
        finally { _suppressSelection = false; }
        if (ProfileList.SelectedItem is SetupProfile selected) LoadProfile(selected);
        else SetEditor([], null, "No saved profiles yet", "Import a native setup or read the saved game settings to create your first profile.");
        SetStatus($"{Profiles.Count} saved profile{(Profiles.Count == 1 ? "" : "s")} found.");
    }
    private static bool SamePath(string? first, string? second) => first != null && second != null &&
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private void SetEditor(IEnumerable<SetupSetting> settings, SetupProfile? profile, string title, string note)
    {
        _loadingEditor = true;
        try
        {
            _settings.Clear(); _baseline.Clear();
            foreach (var setting in settings)
            {
                _baseline[setting.Key] = setting.Value;
                _settings.Add(new EditableSetting(setting, RecomputeDirty));
            }
        }
        finally { _loadingEditor = false; }
        _currentProfile = profile; _editorTitle = title; _profileNote = note;
        SetDirty(false); NotifyEditor(); SettingsView.Refresh();
    }
    private void LoadProfile(SetupProfile profile)
    {
        var settings = SetupManager.ReadSettings(profile.Path);
        SetEditor(settings, profile, profile.Name, profile.HasOtherSettings
            ? "This native profile also includes other game settings. Its game load command can load those fields too. Saving edits creates a setup-only profile and backs up the original."
            : "Edit the Value column, then Save. The original profile is backed up before it is replaced.");
        SetStatus($"Loaded {settings.Count} setup settings from {profile.Name}.");
    }
    private bool ConfirmUnsaved(string nextAction)
    {
        CommitEditor();
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, $"Save your changes before {nextAction}?\n\nYes saves them. No discards the unsaved edits. Cancel keeps this editor open.",
            "Unsaved setup changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveEditor();
    }
    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ProfileList.SelectedItem is not SetupProfile requested) return;
        if (SamePath(requested.Path, _currentProfile?.Path)) return;
        Run(() =>
        {
            if (!ConfirmUnsaved("switching profiles")) { RestoreSelection(); return; }
            try
            {
                LoadProfile(requested);
                _suppressSelection = true;
                try { ProfileList.SelectedItem = Profiles.FirstOrDefault(p => SamePath(p.Path, requested.Path)); }
                finally { _suppressSelection = false; }
            }
            catch { RestoreSelection(); throw; }
        });
    }
    private void RestoreSelection()
    {
        _suppressSelection = true;
        try { ProfileList.SelectedItem = Profiles.FirstOrDefault(p => SamePath(p.Path, _currentProfile?.Path)); }
        finally { _suppressSelection = false; }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (ConfirmUnsaved("refreshing profiles")) RefreshProfiles(_currentProfile?.Path);
    });
    private bool WarnAboutDiskSettings()
    {
        if (!_isGameRunning()) return true;
        return MessageBox.Show(this, "MX Simulator is running. Its config file on disk may not contain your current in-game values.\n\nFor live values, press T in the game and use /writesetup NAME, then Refresh here.\n\nContinue using the values saved on disk?",
            "Use saved values from disk?", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
    }
    private void ReadCurrent_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!ConfirmUnsaved("reading the saved game settings") || !WarnAboutDiskSettings()) return;
        var settings = SetupManager.ReadCurrent(_personalFolder);
        if (settings.Count == 0) throw new InvalidDataException("No supported bike setup settings were found in the saved game config.");
        _suppressSelection = true;
        try { ProfileList.SelectedItem = null; }
        finally { _suppressSelection = false; }
        SetEditor(settings, null, "Saved game settings", "These values came from the config file on disk. Choose Save or Save as to create a setup profile.");
        SetStatus("Read saved settings. No files were changed.");
    });
    private string? AskName(string title, string initial, bool canReplace)
    {
        var name = Dialogs.Prompt(this, title, "Name this native setup profile.", initial)?.Trim();
        if (name == null) return null;
        if (name.Length == 0) throw new InvalidDataException("Enter a setup name.");
        // Core validates the full name before writing; this check only handles collisions.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Use a filename without folder separators.");
        var destination = Path.Combine(_personalFolder, "setups", name);
        if (Directory.Exists(destination)) throw new IOException("A folder already uses this setup name. Choose another name.");
        if (File.Exists(destination))
        {
            if (!canReplace) throw new IOException("A saved setup already uses that name. Choose a new name.");
            if (MessageBox.Show(this, $"Replace the saved profile '{name}' with the settings in this editor?\n\nThe original file will be backed up. Only bike setup fields will be written; any other fields in that profile will be removed from the new copy.",
                "Replace saved profile?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return null;
        }
        return name;
    }
    private bool SaveEditor()
    {
        try
        {
            CommitEditor();
            if (!HasSettings) throw new InvalidOperationException("Load or import some setup settings first.");
            if (_currentProfile == null) return SaveAsEditor();
            if (_currentProfile.HasOtherSettings && MessageBox.Show(this,
                "This native profile contains additional game settings. Saving the editor writes only bike setup fields. The complete original file will be backed up.\n\nSave this setup-only version?",
                "Save edited setup?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            var saved = SetupManager.SaveEdited(_personalFolder, _stateRoot, _currentProfile.Name, _settings.Select(row => row.ToSetting()));
            RefreshProfiles(saved.Path);
            SetStatus(SavedMessage(saved));
            return true;
        }
        catch (Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Could not save setup", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private bool SaveAsEditor()
    {
        CommitEditor();
        if (!HasSettings) throw new InvalidOperationException("Load or import some setup settings first.");
        var name = AskName("Save setup as", _currentProfile == null ? "My setup" : _currentProfile.Name + " copy", true);
        if (name == null) return false;
        var saved = SetupManager.SaveEdited(_personalFolder, _stateRoot, name, _settings.Select(row => row.ToSetting()));
        RefreshProfiles(saved.Path); SetStatus(SavedMessage(saved));
        return true;
    }
    private static string SavedMessage(SetupProfile profile) => profile.LastBackupPath == null
        ? $"Saved {profile.Name}."
        : $"Saved {profile.Name}. Original profile backup: {profile.LastBackupPath}";
    private void Save_Click(object sender, RoutedEventArgs e) => SaveEditor();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Run(() => SaveAsEditor());
    private void CaptureCurrent_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_isGameRunning()) throw new InvalidOperationException("Close MX Simulator before saving its disk config as a setup. For live values while playing, press T, use /writesetup NAME, then Refresh here.");
        if (!ConfirmUnsaved("capturing the saved game settings")) return;
        var name = AskName("Save disk settings as", "My saved setup", false);
        if (name == null) return;
        var saved = SetupManager.SaveCurrent(_personalFolder, _stateRoot, name, _isGameRunning());
        RefreshProfiles(saved.Path); SetStatus(SavedMessage(saved) + " Values were read from disk.");
    });
    private void Import_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!ConfirmUnsaved("importing a profile")) return;
        var picker = new OpenFileDialog { Title = "Import native MX Simulator setup", Filter = "Native setup files|*|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var name = AskName("Import setup", Path.GetFileNameWithoutExtension(picker.FileName), false);
        if (name == null) return;
        var imported = SetupManager.Import(picker.FileName, _personalFolder, _stateRoot, name);
        RefreshProfiles(imported.Path); SetStatus($"Imported {imported.Name}. Only supported bike setup fields were copied.");
    });
    private bool UseSavedProfile(string action)
    {
        CommitEditor();
        if (_currentProfile == null) throw new InvalidOperationException("Save this editor as a profile first.");
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, $"Save your edits before {action}?\n\nYes saves and uses the new values. No uses the last saved profile. Cancel stops.",
            "Use saved setup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveEditor();
    }
    private void Export_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!UseSavedProfile("exporting")) return;
        var picker = new SaveFileDialog { Title = "Export native setup — choose a new filename", FileName = _currentProfile!.Name + " export", Filter = "Native setup file|*|Text file|*.txt", AddExtension = false, OverwritePrompt = false };
        if (picker.ShowDialog(this) != true) return;
        if (File.Exists(picker.FileName) || Directory.Exists(picker.FileName))
            throw new IOException("Export creates a new file. Choose a new filename to preserve the existing file.");
        SetupManager.Export(_currentProfile!.Path, picker.FileName);
        SetStatus($"Exported setup to {picker.FileName}.");
    });
    private void Apply_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_isGameRunning()) throw new InvalidOperationException("Close MX Simulator before applying settings to its saved config. To load a profile while playing, use Copy game load command instead.");
        if (!UseSavedProfile("applying this setup")) return;
        if (MessageBox.Show(this, $"Apply '{_currentProfile!.Name}' to the saved game config?\n\nOnly supported bike setup fields will change. A complete config backup will be created first.",
            "Apply saved setup?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var backup = SetupManager.Apply(_currentProfile.Path, _personalFolder, _stateRoot, _isGameRunning());
        SetStatus($"Applied {_currentProfile.Name}. Previous config backup: {backup}");
    });
    private void CopyCommand_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (!UseSavedProfile("copying its load command")) return;
        if (_currentProfile!.HasOtherSettings && MessageBox.Show(this,
            "This native profile contains settings beyond the bike setup. The game's /readsetup command can load those fields too.\n\nCopy its command?",
            "Full native profile", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        if (string.IsNullOrWhiteSpace(_currentProfile.LoadCommand))
            throw new InvalidOperationException("This profile name cannot be used safely in a game command. Save as a profile with a simple name first.");
        Clipboard.SetText(_currentProfile.LoadCommand);
        SetStatus($"Copied {_currentProfile.LoadCommand}. In the game, press T, paste, and press Enter.");
    });
    private void RestoreBackup_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_isGameRunning()) throw new InvalidOperationException("Close MX Simulator before restoring its saved config.");
        var backups = SetupManager.ListBackups(_stateRoot);
        if (backups.Count == 0) throw new InvalidOperationException("No config backups have been created yet. Applying a setup creates one automatically.");
        var latest = backups.OrderByDescending(File.GetLastWriteTimeUtc).First();
        var picker = new OpenFileDialog { Title = "Restore a previous game config backup", InitialDirectory = Path.GetDirectoryName(latest), FileName = Path.GetFileName(latest), Filter = "Config backups|*|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (!backups.Any(path => SamePath(path, picker.FileName))) throw new InvalidDataException("Choose one of this manager's saved config backups.");
        if (MessageBox.Show(this, $"Restore the complete game config from this backup?\n\n{picker.FileName}\n\nThe current config will also be backed up before restoration.",
            "Restore previous game settings?", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var backup = SetupManager.RestoreBackup(picker.FileName, _personalFolder, _stateRoot, _isGameRunning());
        SetStatus($"Restored previous game config. The config it replaced was saved to: {backup}");
    });
    private void OpenBackups_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var backupFolder = Path.Combine(_stateRoot, "setup-backups");
        Directory.CreateDirectory(backupFolder);
        Process.Start(new ProcessStartInfo(backupFolder) { UseShellExecute = true });
    });
    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SettingsView == null || SettingsGrid == null) return;
        try { CommitEditor(); SettingsView.Refresh(); }
        catch (InvalidOperationException) { }
    }
    private void RecomputeDirty()
    {
        if (!_loadingEditor) SetDirty(_settings.Any(setting => !_baseline.TryGetValue(setting.Key, out var original) || setting.Value != original));
    }
    private void SettingsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        RecomputeDirty();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try { if (!ConfirmUnsaved("closing the setup manager")) e.Cancel = true; }
        catch (Exception ex) { e.Cancel = true; SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Setup manager", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public sealed class EditableSetting : INotifyPropertyChanged, IEditableObject
    {
        private readonly Action _changed;
        private string _value;
        private string? _editingValue;
        public string Key { get; }
        public string Label { get; }
        public string Group { get; }
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                _changed();
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public EditableSetting(SetupSetting source, Action changed)
        {
            Key = source.Key; Label = source.Label; Group = source.Group; _value = source.Value; _changed = changed;
        }
        public SetupSetting ToSetting() => new() { Key = Key, Label = Label, Group = Group, Value = Value };
        public void BeginEdit() => _editingValue ??= _value;
        public void EndEdit() => _editingValue = null;
        public void CancelEdit()
        {
            if (_editingValue == null) return;
            var restored = _editingValue; _editingValue = null; Value = restored;
        }
    }
}
