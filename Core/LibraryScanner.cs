using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MXSimModManager.Core;

public static class LibraryScanner
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".codex", ".vs", "screenshots", "screenshot", "demos", "replays",
        "setups", "setup", "logs", "crashlogs", "cache", "backups", "backup"
    };
    private static readonly Regex BikeName = new(
        @"^(?:(?:[0-9]{2,3}(?:sxf|sx))|(?:(?:crf|cr|kxf|kx|rmz|rm|yzf|yz|fc|tc|mcf|mc)[0-9]{2,3}f?))(?:v[0-9]{4})?(?:[-_.]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex GearName = new(@"^rider_(?:body(?:_fp)?|head|boots|hands|feet)(?:[-_.]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex WheelName = new(@"^(?:front_wheel|rear_wheel|wheels)(?:[-_.]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AuxiliaryTexture = new(@"_(?:norm|spec|amb|normal|roughness|metallic|ao)(?:\.[^.]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static LibrarySnapshot Scan(string personalFolder, string installFolder, string disabledFolder,
        bool includeBaseGame, CancellationToken cancellationToken = default)
    {
        var snapshot = new LibrarySnapshot();
        ScanRoot(personalFolder, "Personal", false, false, snapshot, cancellationToken);
        if (includeBaseGame && !SamePath(personalFolder, installFolder))
            ScanRoot(installFolder, "Base game", false, true, snapshot, cancellationToken);
        if (!string.IsNullOrWhiteSpace(disabledFolder) && Directory.Exists(disabledFolder))
        {
            foreach (var container in EnumerateDirectories(disabledFolder, snapshot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(container)) continue;
                var payload = System.IO.Path.Combine(container, "payload");
                if (Directory.Exists(payload))
                    ScanRoot(payload, "Disabled", true, false, snapshot, cancellationToken);
            }
        }

        snapshot.Packages = snapshot.Packages
            .OrderBy(p => p.IsDisabled).ThenBy(p => p.Scope == "Base game")
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var installed = snapshot.Packages.Where(p => !p.IsDisabled).ToList();
        snapshot.UniqueTrackCount = installed.SelectMany(p => p.TrackIds)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        snapshot.GearPackageCount = installed.Count(p => p.Categories.Contains("Gear", StringComparison.Ordinal));
        snapshot.BikePackageCount = installed.Count(p => p.Categories.Contains("Bike", StringComparison.Ordinal));
        snapshot.TotalBytes = snapshot.Packages.Sum(p => p.SizeBytes);
        return snapshot;
    }

    private static void ScanRoot(string root, string scope, bool disabled, bool readOnly,
        LibrarySnapshot snapshot, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        if (!Directory.Exists(root))
        {
            Warn(snapshot, $"{scope} folder was not found: {root}");
            return;
        }
        var loose = new PackageBuilder(root, scope, disabled, true);
        foreach (var file in EnumerateFiles(root, snapshot))
        {
            token.ThrowIfCancellationRequested();
            if (IsReparsePoint(file)) continue;
            var extension = System.IO.Path.GetExtension(file);
            if (extension.Equals(".saf", StringComparison.OrdinalIgnoreCase))
            {
                var package = new PackageBuilder(file, scope, disabled, readOnly);
                package.SizeBytes = FileSize(file, snapshot);
                ReadSaf(file, package, snapshot, token);
                snapshot.Packages.Add(package.Build());
            }
            else if (IsLooseAsset(System.IO.Path.GetFileName(file)))
            {
                loose.SizeBytes += FileSize(file, snapshot);
                loose.AddVirtualFile(System.IO.Path.GetFileName(file));
            }
        }
        if (loose.Relevant)
        {
            var package = loose.Build();
            package.Name = scope == "Base game" ? "Loose base-game assets" : "Loose files in personal folder";
            package.Details = "Individual asset files in the folder root. Manage these files manually.";
            snapshot.Packages.Add(package);
        }

        foreach (var directory in EnumerateDirectories(root, snapshot))
        {
            token.ThrowIfCancellationRequested();
            if (IgnoredDirectories.Contains(System.IO.Path.GetFileName(directory)) || IsReparsePoint(directory)) continue;
            var package = new PackageBuilder(directory, scope, disabled, readOnly);
            ScanDirectory(directory, root, package, snapshot, token);
            if (package.Relevant) snapshot.Packages.Add(package.Build());
        }
    }

    private static void ScanDirectory(string directory, string root, PackageBuilder package,
        LibrarySnapshot snapshot, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var file in EnumerateFiles(current, snapshot))
            {
                token.ThrowIfCancellationRequested();
                if (IsReparsePoint(file)) continue;
                package.SizeBytes += FileSize(file, snapshot);
                if (System.IO.Path.GetExtension(file).Equals(".saf", StringComparison.OrdinalIgnoreCase))
                {
                    ReadSaf(file, package, snapshot, token);
                    continue;
                }
                var virtualPath = Normalize(System.IO.Path.GetRelativePath(root, file));
                package.AddVirtualFile(virtualPath);
                if (BaseName(virtualPath).Equals("terrain.hf", StringComparison.OrdinalIgnoreCase))
                {
                    var desc = System.IO.Path.Combine(current, "desc");
                    if (File.Exists(desc))
                    {
                        try { package.AddTrackName(ReadLooseDescription(desc)); }
                        catch (Exception ex) when (IsFileError(ex)) { Warn(snapshot, $"Cannot read track name {desc}: {ex.Message}"); }
                    }
                    var map = System.IO.Path.Combine(current, "map.png");
                    if (package.MapPath is null && File.Exists(map)) package.MapPath = map;
                }
            }
            foreach (var child in EnumerateDirectories(current, snapshot))
                if (!IgnoredDirectories.Contains(System.IO.Path.GetFileName(child)) && !IsReparsePoint(child))
                    pending.Push(child);
        }
    }

    private static void ReadSaf(string archive, PackageBuilder package, LibrarySnapshot snapshot, CancellationToken token)
    {
        package.Relevant = true;
        try
        {
            var entries = SafArchive.ReadIndex(archive);
            var descriptions = new Dictionary<string, SafEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var path = Normalize(entry.Path);
                package.AddVirtualFile(path);
                if (BaseName(path).Equals("desc", StringComparison.OrdinalIgnoreCase))
                    descriptions.TryAdd(Parent(path), entry);
            }
            foreach (var entry in entries)
            {
                var path = Normalize(entry.Path);
                if (!BaseName(Unprotect(path)).Equals("terrain.hf", StringComparison.OrdinalIgnoreCase)) continue;
                if (descriptions.TryGetValue(Parent(path), out var desc))
                    package.AddTrackName(FirstLine(SafArchive.ReadSmallText(archive, desc)));
            }
            package.IndexedArchives++;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            package.IndexErrors++;
            Warn(snapshot, $"Cannot index {archive}: {ex.Message}");
        }
    }

    private static string? ReadLooseDescription(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(stream.Length, 16384)];
        stream.ReadExactly(bytes);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        return FirstLine(Encoding.UTF8.GetString(bytes));
    }

    private static string? FirstLine(string? value)
    {
        var line = value?.Split('\n', 2)[0].Trim('\r', ' ', '\t', '\uFEFF');
        if (string.IsNullOrWhiteSpace(line)) return null;
        return line.Length > 240 ? line[..240] : line;
    }

    private static bool IsLooseAsset(string name)
    {
        if (name.StartsWith("screenshot", StringComparison.OrdinalIgnoreCase)) return false;
        var extension = System.IO.Path.GetExtension(Unprotect(name));
        return extension.Equals(".jm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".braap", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".soundtable", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string BaseName(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";
    private static string Unprotect(string path) => path.EndsWith(".scram", StringComparison.OrdinalIgnoreCase) ? path[..^6] : path;
    private static bool SamePath(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (IsFileError(ex)) { return true; }
    }
    private static long FileSize(string path, LibrarySnapshot snapshot)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (IsFileError(ex)) { Warn(snapshot, $"Cannot measure {path}: {ex.Message}"); return 0; }
    }
    private static string[] EnumerateFiles(string path, LibrarySnapshot snapshot)
    {
        try { return Directory.GetFiles(path); }
        catch (Exception ex) when (IsFileError(ex)) { Warn(snapshot, $"Cannot list {path}: {ex.Message}"); return []; }
    }
    private static string[] EnumerateDirectories(string path, LibrarySnapshot snapshot)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex) when (IsFileError(ex)) { Warn(snapshot, $"Cannot list {path}: {ex.Message}"); return []; }
    }
    private static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static void Warn(LibrarySnapshot snapshot, string message)
    {
        if (snapshot.Warnings.Count < 100) snapshot.Warnings.Add(message);
        else if (snapshot.Warnings.Count == 100) snapshot.Warnings.Add("Additional scan warnings were omitted.");
    }

    private sealed class PackageBuilder(string path, string scope, bool disabled, bool readOnly)
    {
        private readonly HashSet<string> _categories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _trackIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _trackNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skinPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _modelPaths = new(StringComparer.OrdinalIgnoreCase);
        public long SizeBytes;
        public bool Relevant;
        public string? MapPath;
        public int IndexedArchives;
        public int IndexErrors;

        public void AddTrackName(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name)) _trackNames.Add(name);
        }

        public void AddVirtualFile(string virtualPath)
        {
            virtualPath = Normalize(virtualPath);
            var file = BaseName(Unprotect(virtualPath));
            var extension = System.IO.Path.GetExtension(file);
            var gear = GearName.IsMatch(file);
            var bike = BikeName.IsMatch(file);
            var wheel = WheelName.IsMatch(file);
            if (file.Equals("terrain.hf", StringComparison.OrdinalIgnoreCase))
            {
                _categories.Add("Track");
                _trackIds.Add(Parent(virtualPath));
                Relevant = true;
            }
            if (gear) { _categories.Add("Gear"); Relevant = true; }
            if (bike) { _categories.Add("Bike"); Relevant = true; }
            if (wheel) { _categories.Add("Bike"); _categories.Add("Wheels"); Relevant = true; }
            if (extension.Equals(".jm", StringComparison.OrdinalIgnoreCase))
            {
                _modelPaths.Add(virtualPath);
                Relevant = true;
            }
            if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                Relevant = true;
                if ((gear || bike || wheel) && !AuxiliaryTexture.IsMatch(System.IO.Path.GetFileNameWithoutExtension(file)))
                    _skinPaths.Add(virtualPath);
            }
            if (extension.Equals(".braap", StringComparison.OrdinalIgnoreCase) || extension.Equals(".soundtable", StringComparison.OrdinalIgnoreCase))
            {
                _categories.Add("Sound");
                Relevant = true;
            }
            if (extension.Equals(".shp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ppm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".inc", StringComparison.OrdinalIgnoreCase))
                Relevant = true;
        }

        public ModPackage Build()
        {
            var name = File.Exists(path) ? System.IO.Path.GetFileNameWithoutExtension(path) : System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            if (_trackIds.Count == 1 && _trackNames.Count == 1) name = _trackNames.First();
            var categories = new[] { "Track", "Gear", "Bike", "Wheels", "Sound" }.Where(_categories.Contains).ToArray();
            var details = new List<string>();
            if (_trackIds.Count > 0) details.Add($"{_trackIds.Count} track{(_trackIds.Count == 1 ? "" : "s")}");
            if (_skinPaths.Count > 0) details.Add($"{_skinPaths.Count} skin{(_skinPaths.Count == 1 ? "" : "s")}");
            if (_modelPaths.Count > 0) details.Add($"{_modelPaths.Count} model{(_modelPaths.Count == 1 ? "" : "s")}");
            if (IndexedArchives > 0) details.Add($"{IndexedArchives} SAF index{(IndexedArchives == 1 ? "" : "es")} read");
            if (IndexErrors > 0) details.Add("Some archive contents could not be read");
            if (details.Count == 0) details.Add("Supporting assets or unclassified content");
            var evidence = _trackIds.Count > 0 ? "Track detected from terrain.hf" :
                _categories.Contains("Gear") ? "Gear detected from rider asset filenames" :
                _categories.Contains("Bike") ? "Bike assets detected from model and skin filenames" :
                _categories.Contains("Sound") ? "Engine audio definition files" : "Archive or asset files";
            if (_trackNames.Count > 1) evidence += ": " + string.Join("; ", _trackNames.Take(4));
            return new ModPackage
            {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())))[..24],
                Name = name, Path = path, Scope = scope, IsDisabled = disabled, IsReadOnly = readOnly,
                Categories = categories.Length == 0 ? "Assets" : string.Join(", ", categories),
                Details = string.Join(" · ", details), Evidence = evidence, SizeBytes = SizeBytes,
                TrackIds = _trackIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                TrackNames = _trackNames.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                SkinCount = _skinPaths.Count, ModelCount = _modelPaths.Count, MapPath = MapPath
                , ModifiedUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path)
            };
        }
    }
}
