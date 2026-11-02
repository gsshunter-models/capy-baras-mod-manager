using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MXSimModManager.Core;

public sealed class SetupProfile
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Details { get; set; } = "";
    public string LoadCommand { get; set; } = "";
    public int SettingCount { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string? LastBackupPath { get; set; }
    public bool HasOtherSettings { get; set; }
}

public sealed class SetupSetting
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string Group { get; set; } = "";
}

/// <summary>Manages native MX Simulator setup files and merges bike settings into config.</summary>
public static class SetupManager
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly Regex Assignment = new(@"^(?<prefix>\s*(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*)(?<value>.*?)(?<trailing>[ \t]*)$", RegexOptions.Compiled);
    private static readonly Regex SafeName = new(@"^[\p{L}\p{N}][\p{L}\p{N} ._-]{0,79}$", RegexOptions.Compiled);
    private static readonly HashSet<string> StringKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "bike", "arcade", "front_brake", "rear_brake", "stop_wheelies", "auto_shift", "auto_dab",
        "auto_align", "sticky_dab", "reverse_steering", "lean_indicator"
    };
    private static readonly string[] Keys =
    [
        "bike", "stability", "leveling", "roll_damping", "roll_limit_angle", "roll_limit_factor",
        "steering_damping", "steering_strength", "max_steering_force", "direct_steering",
        "slow_damping_speed", "slow_damping_factor", "land_damping_rampup", "land_damping_falloff",
        "land_damping_factor", "full_lock_speed", "restricted_lock_speed", "restricted_lock",
        "arcade", "front_brake", "rear_brake", "stop_wheelies", "auto_shift", "auto_dab", "auto_align",
        "sticky_dab", "reverse_steering", "lean_indicator", "auto_clutch_rpms", "brake_torque",
        "rear_brake_torque", "max_dab_speed", "front_sprocket_adjustment", "rear_sprocket_adjustment",
        "fork_compression_adjustment", "fork_slow_compression_adjustment", "fork_rebound_adjustment",
        "fork_slow_rebound_adjustment", "fork_spring_rate_adjustment", "fork_preload_adjustment",
        "fork_oil_level_adjustment", "shock_compression_adjustment", "shock_slow_compression_adjustment",
        "shock_rebound_adjustment", "shock_slow_rebound_adjustment", "shock_spring_rate_adjustment",
        "shock_preload_adjustment", "rider_yspring", "rider_ydamping", "rider_zspring", "rider_zdamping",
        "rider_mass_distribution"
    ];
    private static readonly HashSet<string> AllowedKeys = new(Keys, StringComparer.OrdinalIgnoreCase);
    internal static readonly string[] AppearanceKeys = ["bike", "bike_texture", "rider_texture", "head_texture", "wheel_texture"];

    internal static Dictionary<string, string> ReadSelectedValues(string path, IEnumerable<string> keys)
    {
        RejectLicensePath(path);
        var allowed = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in SetupDocument.Read(path).Assignments())
            if (allowed.Contains(assignment.Key)) result[assignment.Key] = assignment.Value;
        return result;
    }

    internal static List<string> ReadSettingKeys(string path) => SetupDocument.Read(path).Assignments().Select(a => a.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    internal static string ApplyAppearanceValues(IReadOnlyDictionary<string, string> values, string personalFolder, string stateRoot, bool gameRunning)
    {
        RequireClosed(gameRunning);
        var validated = ValidateAppearance(values);
        var config = ConfigPath(personalFolder);
        var document = SetupDocument.Read(config);
        var updated = document.Merge(validated);
        var backup = Backup(document.OriginalBytes, stateRoot, "config", ".config");
        WriteConfig(config, document.OriginalBytes, updated);
        return backup;
    }

    internal static SetupProfile SaveAppearanceProfile(IReadOnlyDictionary<string, string> values, string personalFolder, string stateRoot, string name) =>
        WriteProfile(personalFolder, stateRoot, name, ValidateAppearance(values), false, AppearanceKeys);

    private static Dictionary<string, string> ValidateAppearance(IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (!AppearanceKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(pair.Value) ||
                pair.Value.Length > 512 || pair.Value.Any(char.IsControl))
                throw new InvalidDataException("Only valid bike and appearance selectors can be saved.");
            result.Add(pair.Key, pair.Value);
        }
        if (result.Count == 0) throw new InvalidDataException("No appearance settings were selected.");
        return result;
    }

    public static List<SetupProfile> List(string personalFolder)
    {
        var folder = Path.Combine(personalFolder, "setups");
        if (!Directory.Exists(folder)) return [];
        ModInstaller.EnsureNoLinks(personalFolder, folder);
        var result = new List<SetupProfile>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (Path.GetFileName(path).StartsWith('.') || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var profile = Describe(path);
                if (profile.SettingCount > 0) result.Add(profile);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                // A damaged/unrelated file does not hide the remaining native setups.
            }
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<SetupSetting> ReadSettings(string profilePath)
    {
        RejectLicensePath(profilePath);
        var document = SetupDocument.Read(profilePath);
        return Settings(document);
    }

    public static List<SetupSetting> ReadCurrent(string personalFolder)
    {
        var path = ConfigPath(personalFolder);
        return Settings(SetupDocument.Read(path));
    }

    public static SetupProfile SaveCurrent(string personalFolder, string stateRoot, string name, bool gameRunning)
    {
        RequireClosed(gameRunning);
        var settings = ReadCurrent(personalFolder);
        return SaveProfile(personalFolder, stateRoot, name, settings, false);
    }

    public static SetupProfile SaveEdited(string personalFolder, string stateRoot, string name, IEnumerable<SetupSetting> settings) =>
        SaveProfile(personalFolder, stateRoot, name, settings, true);

    public static SetupProfile Import(string source, string personalFolder, string stateRoot, string name) =>
        SaveProfile(personalFolder, stateRoot, name, ReadSettings(source), false);

    public static void Export(string profilePath, string destination)
    {
        RejectLicensePath(profilePath);
        var document = SetupDocument.Read(profilePath);
        if (Settings(document).Count == 0) throw new InvalidDataException("This file contains no supported bike setup settings.");
        if (document.Assignments().Any(a => IsSensitiveKey(a.Key)))
            throw new InvalidDataException("This file contains private account settings and cannot be exported as a setup.");
        ModInstaller.EnsureNoLinks(Path.GetDirectoryName(Path.GetFullPath(destination))!, destination);
        // Export native profiles unchanged; never overwrite an existing destination.
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(document.OriginalBytes);
        output.Flush(true);
    }

    public static string Apply(string profilePath, string personalFolder, string stateRoot, bool gameRunning)
    {
        RequireClosed(gameRunning);
        var settings = Validate(ReadSettings(profilePath));
        if (settings.Count == 0) throw new InvalidDataException("This file contains no supported bike setup settings.");
        var config = ConfigPath(personalFolder);
        var document = SetupDocument.Read(config);
        var updated = document.Merge(settings);
        var backup = Backup(document.OriginalBytes, stateRoot, "config", ".config");
        WriteConfig(config, document.OriginalBytes, updated);
        return backup;
    }

    public static List<string> ListBackups(string stateRoot)
    {
        var root = Path.Combine(stateRoot, "setup-backups", "config");
        if (!Directory.Exists(root)) return [];
        ModInstaller.EnsureNoLinks(stateRoot, root);
        return Directory.EnumerateFiles(root, "*.config")
            .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
    }

    public static string RestoreBackup(string backupPath, string personalFolder, string stateRoot, bool gameRunning)
    {
        RequireClosed(gameRunning);
        var root = Path.Combine(stateRoot, "setup-backups", "config");
        var fullBackup = ModInstaller.SafePath(root, Path.GetRelativePath(root, backupPath));
        if (!Path.GetExtension(fullBackup).Equals(".config", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a configuration backup created by this manager.");
        ModInstaller.EnsureNoLinks(stateRoot, fullBackup);
        var restored = SetupDocument.Read(fullBackup);
        var config = ConfigPath(personalFolder);
        var current = SetupDocument.Read(config);
        var preservedCurrent = Backup(current.OriginalBytes, stateRoot, "config", ".config");
        WriteConfig(config, current.OriginalBytes, restored.OriginalBytes);
        return preservedCurrent;
    }

    private static SetupProfile SaveProfile(string personalFolder, string stateRoot, string name,
        IEnumerable<SetupSetting> settings, bool overwrite)
        => WriteProfile(personalFolder, stateRoot, name, Validate(settings), overwrite, Keys);

    private static SetupProfile WriteProfile(string personalFolder, string stateRoot, string name,
        Dictionary<string, string> validated, bool overwrite, IReadOnlyList<string> keyOrder)
    {
        if (string.IsNullOrWhiteSpace(personalFolder) || !Directory.Exists(personalFolder))
            throw new DirectoryNotFoundException("The MX Simulator personal folder was not found.");
        ValidateName(name);
        if (validated.Count == 0) throw new InvalidDataException("No supported bike settings were selected.");
        var folder = Path.Combine(personalFolder, "setups");
        ModInstaller.EnsureNoLinks(personalFolder, folder);
        Directory.CreateDirectory(folder);
        var destination = ModInstaller.SafePath(folder, name);
        ModInstaller.EnsureNoLinks(personalFolder, destination);
        if (Directory.Exists(destination)) throw new IOException("A folder already uses this setup name.");
        var contents = new UTF8Encoding(false).GetBytes(string.Join("", keyOrder.Where(validated.ContainsKey).Select(k => k + "=" + validated[k] + "\n")));
        string? backup = null;
        if (File.Exists(destination))
        {
            if (!overwrite) throw new IOException("A setup already uses that name. Choose a new name or use Save edits to replace it with a backup.");
            var original = SetupDocument.Read(destination);
            backup = Backup(original.OriginalBytes, stateRoot, "profiles", ".setup");
            WriteConfig(destination, original.OriginalBytes, contents);
        }
        else
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(contents);
            output.Flush(true);
        }
        var profile = Describe(destination);
        profile.LastBackupPath = backup;
        return profile;
    }

    private static SetupProfile Describe(string path)
    {
        RejectLicensePath(path);
        var document = SetupDocument.Read(path);
        var settings = Settings(document);
        var other = document.Assignments().Any(a => !AllowedKeys.Contains(a.Key));
        var name = Path.GetFileName(path);
        return new SetupProfile
        {
            Name = name, Path = path, SettingCount = settings.Count, ModifiedUtc = File.GetLastWriteTimeUtc(path), HasOtherSettings = other,
            LoadCommand = IsValidName(name) ? "/readsetup " + name : "",
            Details = $"{settings.Count} bike / handling settings" +
                (other ? " · Native game load also applies the other fields saved in this file." : " · Native bike setup file.")
        };
    }

    private static List<SetupSetting> Settings(SetupDocument document)
    {
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Assignments())
            if (AllowedKeys.Contains(item.Key)) assignments[item.Key] = item.Value;
        return Keys.Where(assignments.ContainsKey).Select(key => new SetupSetting
        {
            Key = key, Label = Label(key), Value = assignments[key], Group = Group(key)
        }).ToList();
    }

    private static Dictionary<string, string> Validate(IEnumerable<SetupSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (setting is null || !AllowedKeys.Contains(setting.Key ?? ""))
                throw new InvalidDataException("Only supported bike and handling settings can be saved.");
            var key = Keys.First(k => k.Equals(setting.Key, StringComparison.OrdinalIgnoreCase));
            var value = setting.Value?.Trim() ?? "";
            if (value.Length == 0 || value.Length > 512 || value.Any(char.IsControl))
                throw new InvalidDataException($"{Label(key)} needs a single nonempty value.");
            if (!StringKeys.Contains(key))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    throw new InvalidDataException($"{Label(key)} must be a finite number using a decimal point.");
                // Suspension adjustment values use the native 0–100 menu scale. Gearing offsets do not.
                if ((key.StartsWith("fork_", StringComparison.Ordinal) || key.StartsWith("shock_", StringComparison.Ordinal)) && number is < 0 or > 100)
                    throw new InvalidDataException($"{Label(key)} must be between 0 and 100.");
            }
            else if (key != "bike")
            {
                string[] choices = key is "front_brake" or "rear_brake" ? ["Normal", "Antilock"] :
                    key == "stop_wheelies" ? ["No", "Yes", "Front Only"] : ["No", "Yes"];
                value = choices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)) ??
                    throw new InvalidDataException($"{Label(key)} must be {string.Join(" or ", choices)}.");
            }
            if (!result.TryAdd(key, value)) throw new InvalidDataException($"Duplicate setup setting: {key}");
        }
        return result;
    }

    private static string ConfigPath(string personalFolder)
    {
        if (!Directory.Exists(personalFolder)) throw new DirectoryNotFoundException("The MX Simulator personal folder was not found.");
        var path = Path.Combine(personalFolder, "config");
        ModInstaller.EnsureNoLinks(personalFolder, path);
        if (!File.Exists(path)) throw new FileNotFoundException("No saved game config was found. Start MX Simulator and quit through its menu first.", path);
        return path;
    }

    private static void RequireClosed(bool gameRunning)
    {
        if (gameRunning) throw new InvalidOperationException("Close MX Simulator before reading its saved current setup or applying/restoring config. Use /writesetup NAME in the game to save a live setup.");
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !SafeName.IsMatch(name) || name.EndsWith(' ') || name.EndsWith('.')) return false;
        try { ModInstaller.SafePath(Path.GetTempPath(), name); return true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException) { return false; }
    }
    private static void ValidateName(string name)
    {
        if (!IsValidName(name)) throw new InvalidDataException("Use a setup name of 1–80 letters, numbers, spaces, dots, underscores or hyphens, starting with a letter or number.");
        RejectLicensePath(name);
    }
    private static bool IsSensitiveKey(string key) => key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("regkey", StringComparison.OrdinalIgnoreCase) || key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("auth", StringComparison.OrdinalIgnoreCase);
    private static void RejectLicensePath(string path)
    {
        if (Path.GetFileName(path).Equals("regkey", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("License files are not setup files.");
    }

    private static string Backup(byte[] bytes, string stateRoot, string category, string extension)
    {
        var root = Path.Combine(stateRoot, "setup-backups", category);
        ModInstaller.EnsureNoLinks(stateRoot, root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + extension);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
        return path;
    }

    private static void WriteConfig(string path, byte[] expected, byte[] updated)
    {
        // Detect an edit made since the snapshot before replacing the destination.
        if (!SetupDocument.Read(path).OriginalBytes.AsSpan().SequenceEqual(expected))
            throw new IOException("The file changed during this operation. Refresh and try again; its backup was preserved.");
        var temporary = path + ".mxmm-setup-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(updated); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Group(string key) => key == "bike" ? "Bike" : key.StartsWith("fork_") ? "Fork suspension" :
        key.StartsWith("shock_") ? "Shock suspension" : key.Contains("sprocket") ? "Gearing" : key.StartsWith("rider_") ? "Rider" :
        StringKeys.Contains(key) || key.Contains("brake") || key.Contains("clutch") || key.Contains("dab") ? "Brakes and assists" : "Advanced stability";
    private static string Label(string key)
    {
        if (key == "rider_yspring") return "Rider vertical spring";
        if (key == "rider_ydamping") return "Rider vertical damping";
        if (key == "rider_zspring") return "Rider forward/back spring";
        if (key == "rider_zdamping") return "Rider forward/back damping";
        var text = key.Replace("_adjustment", "").Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private sealed record TextLine(string Text, string Ending);
    private sealed class SetupDocument(byte[] originalBytes, Encoding encoding, byte[] preamble, List<TextLine> lines)
    {
        public byte[] OriginalBytes { get; } = originalBytes;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public static SetupDocument Read(string path)
        {
            ModInstaller.EnsureNoLinks(Path.GetDirectoryName(Path.GetFullPath(path))!, path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumFileBytes) throw new InvalidDataException("Setup/config file exceeds the 1 MB limit.");
            var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            Encoding encoding; byte[] bom; int skip;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = StrictUtf8; bom = [0xEF, 0xBB, 0xBF]; skip = 3; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); bom = [0xFF, 0xFE]; skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); bom = [0xFE, 0xFF]; skip = 2; }
            else { encoding = StrictUtf8; bom = []; skip = 0; }
            string text;
            try { text = encoding.GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException) when (skip == 0)
            {
                encoding = (Encoding)Encoding.Latin1.Clone();
                encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                text = encoding.GetString(bytes);
            }
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("This file is not a text setup/config file.");
            var lines = new List<TextLine>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\r' && text[i] != '\n') continue;
                var end = i; var ending = text[i].ToString();
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { ending = "\r\n"; i++; }
                lines.Add(new TextLine(text[start..end], ending)); start = i + 1;
            }
            if (start < text.Length) lines.Add(new TextLine(text[start..], ""));
            return new SetupDocument(bytes, encoding, bom, lines);
        }

        public IEnumerable<(string Key, string Value)> Assignments()
        {
            foreach (var line in lines)
            {
                var match = Assignment.Match(line.Text);
                if (match.Success)
                {
                    var key = match.Groups["key"].Value;
                    var value = match.Groups["value"].Value;
                    yield return (key, HasInlineComments(key) ? StripComment(value).Value.Trim() : value.Trim());
                }
            }
        }

        public byte[] Merge(Dictionary<string, string> values)
        {
            var output = new List<TextLine>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                var match = Assignment.Match(line.Text);
                if (!match.Success || !values.TryGetValue(match.Groups["key"].Value, out var value)) { output.Add(line); continue; }
                var comment = HasInlineComments(match.Groups["key"].Value) ? StripComment(match.Groups["value"].Value).Comment : "";
                output.Add(line with { Text = match.Groups["prefix"].Value + value + comment + match.Groups["trailing"].Value });
                seen.Add(match.Groups["key"].Value);
            }
            var newline = lines.FirstOrDefault(l => l.Ending.Length > 0)?.Ending ?? "\n";
            foreach (var key in values.Keys.Where(k => !seen.Contains(k)))
            {
                if (output.Count > 0 && output[^1].Ending.Length == 0) output[^1] = output[^1] with { Ending = newline };
                output.Add(new TextLine(key + "=" + values[key], newline));
            }
            var text = string.Concat(output.Select(l => l.Text + l.Ending));
            byte[] data;
            try { data = encoding.GetBytes(text); }
            catch (EncoderFallbackException ex)
            {
                throw new InvalidDataException("The original config encoding cannot represent this selector. No changes were made. Use a UTF-8 config before selecting Unicode names.", ex);
            }
            return [.. preamble, .. data];
        }

        private static bool HasInlineComments(string key) => AllowedKeys.Contains(key) && !AppearanceKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        private static (string Value, string Comment) StripComment(string value)
        {
            for (var i = 1; i < value.Length; i++)
                if ((value[i] == '#' || value[i] == ';') && char.IsWhiteSpace(value[i - 1]))
                {
                    var start = i - 1;
                    while (start > 0 && char.IsWhiteSpace(value[start - 1])) start--;
                    return (value[..start], value[start..]);
                }
            return (value, "");
        }
    }
}
