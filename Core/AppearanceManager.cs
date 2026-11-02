using System.Text;
using System.Text.RegularExpressions;

namespace MXSimModManager.Core;

public sealed class AppearanceChoice
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string ModelPrefix { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsAvailable { get; set; } = true;
}

public sealed class AppearanceSelection
{
    public string Bike { get; set; } = "";
    public string BikeSkin { get; set; } = "";
    public string Gear { get; set; } = "";
    public string Helmet { get; set; } = "";
    public string Wheels { get; set; } = "";
}

public sealed class AppearanceCatalog
{
    public List<AppearanceChoice> Bikes { get; set; } = [];
    public List<AppearanceChoice> Gear { get; set; } = [];
    public List<AppearanceChoice> Helmets { get; set; } = [];
    public List<AppearanceChoice> Wheels { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public AppearanceSelection Current { get; set; } = new();
    internal Dictionary<string, List<AppearanceChoice>> SkinsByFamily { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AppearanceChoice> GetBikeSkins(string bikeValue)
    {
        var family = Bikes.FirstOrDefault(b => b.Value.Equals(bikeValue, StringComparison.OrdinalIgnoreCase))?.ModelPrefix;
        return family is not null && SkinsByFamily.TryGetValue(family, out var skins) ? [.. skins] : [];
    }
}

public static class AppearanceManager
{
    private static readonly Regex Dyno = new(@"(?<id>(?<family>(?:[0-9]{2,3}(?:sxf|sx)|(?:crf|cr|kxf|kx|rmz|rm|yzf|yz|fc|tc|mcf|mc)[0-9]{2,3}f?)(?:_se)?)(?:\([12][0-9]{3}\))?)(?=\x00)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Auxiliary = new(@"_(?:norm|normal|spec|amb|ao|roughness|metallic)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    { "setups", "setup", "screenshots", "demos", "replays", "logs", "cache", "backups", "backup", ".git", ".codex", ".vs" };

    public static AppearanceCatalog Scan(string personalFolder, string installFolder, CancellationToken cancellationToken = default)
    {
        var catalog = new AppearanceCatalog();
        try { catalog.Current = ReadCurrent(personalFolder); }
        catch (Exception ex) when (FileError(ex)) { Warn(catalog, "Saved appearance could not be read: " + ex.Message); }
        var assets = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        ReadAssets(installFolder, 0, assets, catalog, cancellationToken);
        if (string.IsNullOrWhiteSpace(installFolder) || string.IsNullOrWhiteSpace(personalFolder) ||
            !string.Equals(Path.GetFullPath(personalFolder), Path.GetFullPath(installFolder), StringComparison.OrdinalIgnoreCase))
            ReadAssets(personalFolder, 100, assets, catalog, cancellationToken);
        var dynos = ReadDynos(installFolder, catalog);
        var hidden = 0;
        foreach (var dyno in dynos.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!catalog.SkinsByFamily.TryGetValue(dyno.Value, out var skins))
            {
                skins = BuildSkins(assets, dyno.Value, [dyno.Value + "_frame", dyno.Value + "_fork_upper", dyno.Value + "_fork_lower", dyno.Value + "_swingarm"], ref hidden);
                catalog.SkinsByFamily[dyno.Value] = skins;
            }
            if (skins.Count == 0) continue;
            catalog.Bikes.Add(new AppearanceChoice { Label = dyno.Key, Value = dyno.Key, ModelPrefix = dyno.Value,
                Source = "Native bike ID and bodywork family discovered from installed game metadata" });
        }
        catalog.Gear = BuildSkins(assets, "rider_body", ["rider_body"], ref hidden);
        catalog.Helmets = BuildSkins(assets, "rider_head", ["rider_head"], ref hidden);
        catalog.Wheels = BuildSkins(assets, "wheels", ["front_wheel", "rear_wheel"], ref hidden);
        if (hidden > 0) Warn(catalog, $"{hidden} texture selectors were hidden because matching models could not be confirmed.");
        if (catalog.Bikes.Count == 0) Warn(catalog, "No supported bike IDs with available skin/model files were discovered. Check the game installation folder.");
        return catalog;
    }

    public static AppearanceSelection ReadCurrent(string personalFolder) => ReadPreset(Path.Combine(personalFolder, "config"));

    public static AppearanceSelection ReadPreset(string profilePath)
    {
        var values = SetupManager.ReadSelectedValues(profilePath, SetupManager.AppearanceKeys);
        return new AppearanceSelection
        {
            Bike = values.GetValueOrDefault("bike", ""), BikeSkin = values.GetValueOrDefault("bike_texture", ""),
            Gear = values.GetValueOrDefault("rider_texture", ""), Helmet = values.GetValueOrDefault("head_texture", ""),
            Wheels = values.GetValueOrDefault("wheel_texture", "")
        };
    }

    public static List<SetupProfile> ListPresets(string personalFolder)
    {
        var root = Path.Combine(personalFolder, "setups");
        if (!Directory.Exists(root)) return [];
        ModInstaller.EnsureNoLinks(personalFolder, root);
        var profiles = new List<SetupProfile>();
        foreach (var file in Directory.EnumerateFiles(root))
        {
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                var values = SetupManager.ReadSelectedValues(file, SetupManager.AppearanceKeys);
                if (values.Count > 0) profiles.Add(DescribePreset(file, values.Count));
            }
            catch (Exception ex) when (FileError(ex)) { }
        }
        return profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Apply(AppearanceCatalog catalog, AppearanceSelection selection, string personalFolder, string stateRoot, bool gameRunning)
    {
        if (gameRunning) throw new InvalidOperationException("Close MX Simulator before applying saved config, or save a native appearance preset and use its /readsetup command in the game.");
        Validate(catalog, selection);
        ValidateRiderPayload(catalog, selection, personalFolder);
        return SetupManager.ApplyAppearanceValues(Values(selection), personalFolder, stateRoot, gameRunning);
    }

    public static SetupProfile SavePreset(AppearanceCatalog catalog, AppearanceSelection selection, string personalFolder, string stateRoot, string name)
    {
        Validate(catalog, selection);
        ValidateRiderPayload(catalog, selection, personalFolder);
        var saved = SetupManager.SaveAppearanceProfile(Values(selection), personalFolder, stateRoot, name);
        return DescribePreset(saved.Path, Values(selection).Count);
    }

    private static SetupProfile DescribePreset(string path, int count)
    {
        var other = SetupManager.ReadSettingKeys(path).Any(k => !SetupManager.AppearanceKeys.Contains(k, StringComparer.OrdinalIgnoreCase));
        var name = Path.GetFileName(path);
        // The shared profile writer validates command-safe names for newly created presets.
        var commandSafe = Regex.IsMatch(name, @"^[\p{L}\p{N}][\p{L}\p{N} ._-]{0,79}$") && !name.EndsWith(' ') && !name.EndsWith('.');
        return new SetupProfile { Name = name, Path = path, SettingCount = count, ModifiedUtc = File.GetLastWriteTimeUtc(path),
            HasOtherSettings = other, LoadCommand = commandSafe ? "/readsetup " + name : "",
            Details = $"{count} appearance setting{(count == 1 ? "" : "s")}" + (other ? " · Native load also applies the other fields in this file." : " · Native appearance-only preset.") };
    }

    private static Dictionary<string, string> Values(AppearanceSelection s) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    { ["bike"] = s.Bike, ["bike_texture"] = s.BikeSkin, ["rider_texture"] = s.Gear, ["head_texture"] = s.Helmet, ["wheel_texture"] = s.Wheels }
        .Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static void Validate(AppearanceCatalog catalog, AppearanceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(selection);
        RequireChoice(catalog.Bikes, selection.Bike, catalog.Current.Bike, "bike");
        var unchangedBike = selection.Bike.Equals(catalog.Current.Bike, StringComparison.OrdinalIgnoreCase);
        RequireChoice(catalog.GetBikeSkins(selection.Bike), selection.BikeSkin, unchangedBike ? catalog.Current.BikeSkin : "", "compatible bike skin");
        RequireChoice(catalog.Gear, selection.Gear, catalog.Current.Gear, "gear");
        RequireChoice(catalog.Helmets, selection.Helmet, catalog.Current.Helmet, "helmet");
        RequireChoice(catalog.Wheels, selection.Wheels, catalog.Current.Wheels, "wheels");
    }
    private static void RequireChoice(IEnumerable<AppearanceChoice> choices, string value, string current, string label)
    {
        if (value.Length > 512 || value.Contains('|') || value.Any(char.IsControl) ||
            !(choices.Any(c => c.IsAvailable && c.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) || value.Equals(current, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Select an installed {label}. An unavailable saved value can only be kept unchanged.");
    }

    private static void ValidateRiderPayload(AppearanceCatalog catalog, AppearanceSelection selection, string personalFolder)
    {
        var family = catalog.Bikes.FirstOrDefault(b => b.Value.Equals(selection.Bike, StringComparison.OrdinalIgnoreCase))?.ModelPrefix;
        if (string.IsNullOrWhiteSpace(family)) return; // Preserve an unknown saved bike without guessing its resources.
        var identity = SetupManager.ReadSelectedValues(Path.Combine(personalFolder, "config"), ["name", "number"]);
        static string Resource(string prefix, string selector) => "@" + prefix + (string.IsNullOrWhiteSpace(selector) || selector.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : "-" + selector) + ".png";
        var payload = selection.Bike + "|" + Resource("rider_body", selection.Gear) + "|" + Resource(family, selection.BikeSkin) + "|" + Resource("wheels", selection.Wheels) + "|" + Resource("rider_head", selection.Helmet) + "\n" + identity.GetValueOrDefault("number", "") + "\n" + identity.GetValueOrDefault("name", "");
        if (Encoding.UTF8.GetByteCount(payload) >= 512)
            throw new InvalidDataException("This combination has names that are too long for MX Simulator's multiplayer rider information. Choose shorter named skins, gear, helmet or wheels.");
    }

    private static List<AppearanceChoice> BuildSkins(Dictionary<string, Asset> assets, string texturePrefix, string[] modelParts, ref int hidden)
    {
        var choices = new List<AppearanceChoice>();
        foreach (var asset in assets.Values.Where(a => a.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            var stem = asset.Name[..^4];
            if (Auxiliary.IsMatch(stem)) continue;
            if (stem.Equals(texturePrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (modelParts.All(part => assets.ContainsKey(part + ".jm")))
                    choices.Add(new AppearanceChoice { Label = "Default", Value = "default", ModelPrefix = texturePrefix, Source = asset.Source });
                continue;
            }
            if (!stem.StartsWith(texturePrefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
            var selector = stem[(texturePrefix.Length + 1)..];
            if (selector.Length == 0 || selector.Contains('|') || selector.Any(char.IsControl)) continue;
            var matchedModel = false;
            var candidate = selector;
            while (candidate.Length > 0)
            {
                if (modelParts.All(part => assets.ContainsKey(part + "-" + candidate + ".jm"))) { matchedModel = true; break; }
                var hyphen = candidate.LastIndexOf('-');
                if (hyphen < 0) break;
                candidate = candidate[..hyphen];
            }
            if (!matchedModel && !selector.Contains('-')) matchedModel = modelParts.All(part => assets.ContainsKey(part + ".jm"));
            if (!matchedModel) { hidden++; continue; }
            choices.Add(new AppearanceChoice { Label = selector, Value = selector, ModelPrefix = texturePrefix, Source = asset.Source });
        }
        return choices.GroupBy(c => c.Value, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(c => c.Value != "default").ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ReadDynos(string installFolder, AppearanceCatalog catalog)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var executable = Path.Combine(installFolder, "mx.exe");
        try
        {
            ModInstaller.EnsureNoLinks(installFolder, executable);
            using var input = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 128 * 1024 * 1024) throw new InvalidDataException("Game metadata file exceeds the supported size.");
            var data = new byte[(int)input.Length]; input.ReadExactly(data);
            var text = Encoding.Latin1.GetString(data);
            foreach (Match match in Dyno.Matches(text))
            {
                var id = match.Groups["id"].Value;
                if (result.ContainsKey(id)) continue;
                var family = match.Groups["family"].Value;
                var after = text.Substring(match.Index, Math.Min(1400, text.Length - match.Index));
                // Native bike records contain the ID and explicit model/texture resource names.
                // Require both in the same bounded record; never infer a skin version from the dyno year.
                var frame = Regex.Match(after, "@(?<prefix>" + Regex.Escape(family) + @"(?:v[0-9]{4})?)_frame\.jm\x00", RegexOptions.CultureInvariant);
                if (!frame.Success || !after.Contains("@" + frame.Groups["prefix"].Value + ".png\0", StringComparison.Ordinal)) continue;
                result[id] = frame.Groups["prefix"].Value;
            }
        }
        catch (Exception ex) when (FileError(ex)) { Warn(catalog, "Native bike metadata could not be indexed: " + ex.Message); }
        return result;
    }

    private static void ReadAssets(string root, int rootPriority, Dictionary<string, Asset> assets, AppearanceCatalog catalog, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); var count = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > 128 || ++count > 100000) { Warn(catalog, "An unusually deep or large folder tree was skipped."); continue; }
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Path.GetExtension(file).Equals(".saf", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            foreach (var entry in SafArchive.ReadIndex(file))
                                AddAsset(entry.Path, Path.GetFileName(file) + " → " + entry.Path, rootPriority + 10, assets);
                        }
                        catch (Exception ex) when (FileError(ex)) { Warn(catalog, "Appearance archive could not be indexed: " + file + ": " + ex.Message); }
                    }
                    // Loose player resources are confirmed only at a game search root.
                    // SAF indices retain the archive's own resource directory convention.
                    else if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
                        AddAsset(Path.GetFileName(file), file, rootPriority + 20, assets);
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (!IgnoredFolders.Contains(Path.GetFileName(child)) && !Path.GetFileName(child).StartsWith('.')) pending.Push((child, depth + 1));
            }
            catch (Exception ex) when (FileError(ex)) { Warn(catalog, "Appearance assets could not be listed: " + directory + ": " + ex.Message); }
        }
    }

    private static void AddAsset(string path, string source, int priority, Dictionary<string, Asset> assets)
    {
        var clean = path.Replace('\\', '/');
        if (clean.StartsWith('/') || clean.Contains(':') || clean.Split('/').Any(p => p is "." or "..")) return;
        var name = clean.Split('/')[^1];
        while (name.EndsWith(".scram", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            name = name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name[..^6];
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".jm", StringComparison.OrdinalIgnoreCase)) return;
        if (!assets.TryGetValue(name, out var existing) || existing.Priority <= priority) assets[name] = new Asset(name, source, priority);
    }
    private sealed record Asset(string Name, string Source, int Priority);
    private static bool FileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static void Warn(AppearanceCatalog catalog, string message) { if (catalog.Warnings.Count < 50) catalog.Warnings.Add(message); }
}
