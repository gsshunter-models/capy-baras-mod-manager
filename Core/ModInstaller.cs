using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace MXSimModManager.Core;

public static class ModInstaller
{
    private const long MaximumBytes = 40L * 1024 * 1024 * 1024;
    private const int MaximumFiles = 100000;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".saf", ".scram", ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".ppm", ".pgm", ".jm", ".hf", ".shp", ".seq", ".braap", ".soundtable", ".wav", ".ogg", ".txt", ".norm", ".spec", ".amb", ".dat" };
    private static readonly HashSet<string> BareNames = new(StringComparer.OrdinalIgnoreCase)
    { "terrain", "tilemap", "tileinfo", "timing_gates", "desc", "billboards", "statues", "decals", "lighting", "edinfo", "shading", "friction", "texturelist", "flaggers", "objects", "lines", "sky", "water" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SafePath(string root, string relative)
    {
        var clean = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(clean) || clean.StartsWith('/') || clean.Contains(':')) throw new InvalidDataException($"Unsafe package path: {relative}");
        var parts = clean.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Unsafe package path: {relative}");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                throw new InvalidDataException($"Reserved package path: {relative}");
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, clean.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes the library.");
        return full;
    }

    public static void EnsureNoLinks(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var stop = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!current.Equals(stop, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Path is outside the expected folder.");
        while (current.Length >= stop.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Linked folders/files are not supported: {current}");
            if (current.Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    public static InstallPlan Prepare(string source, string stateRoot)
    {
        source = Path.GetFullPath(source);
        var staging = Path.Combine(stateRoot, "staging", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(source) && Path.GetFullPath(staging).StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose the mod folder itself, outside the manager's storage folder.");
        Directory.CreateDirectory(staging);
        var skipped = new List<string>();
        try
        {
            if (Directory.Exists(source))
            {
                EnsureNoLinks(source, source);
                CopyFolderToStage(source, staging, skipped);
            }
            else if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(source);
                if (zip.Entries.Count > MaximumFiles) throw new InvalidDataException("This archive contains too many files.");
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Linked archive entries are not supported.");
                    if (name.EndsWith('/')) { if (name.Length > 1) SafePath(staging, name.TrimEnd('/')); continue; }
                    var output = SafePath(staging, name);
                    total = checked(total + entry.Length);
                    if (total > MaximumBytes) throw new InvalidDataException("Unpacked content exceeds the 40 GB limit. Install smaller packages separately.");
                    if (!IsModData(name)) { skipped.Add(name); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var input = entry.Open();
                    using var target = new FileStream(output, FileMode.CreateNew);
                    CopyLimited(input, target, entry.Length);
                }
            }
            else if (File.Exists(source) && new[] { ".rar", ".7z" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            {
                using var archive = ArchiveFactory.OpenArchive(source);
                if (archive.Type is not (ArchiveType.Rar or ArchiveType.SevenZip)) throw new InvalidDataException("The archive format does not match a supported RAR or 7z package.");
                var entries = archive.Entries.Take(MaximumFiles + 1).ToList();
                if (entries.Count > MaximumFiles) throw new InvalidDataException("This archive contains too many entries.");
                long total = 0;
                foreach (var entry in entries)
                {
                    var key = entry.Key?.Replace('\\', '/').TrimEnd('/') ?? "";
                    SafePath(staging, key);
                    var attributes = entry.Attrib ?? 0;
                    if (!string.IsNullOrEmpty(entry.LinkTarget) || (attributes & (int)FileAttributes.ReparsePoint) != 0 || (attributes & 0xF000) == 0xA000 || ((attributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("Linked archive entries are not supported.");
                    if (entry.IsEncrypted) throw new InvalidDataException("Password-protected archives are not supported. Extract them yourself, then install the mod folder.");
                    if (!entry.IsComplete) throw new InvalidDataException("This archive is missing a part. Download all parts and extract them first.");
                    if (entry.Size < 0 || (total = checked(total + entry.Size)) > MaximumBytes) throw new InvalidDataException("Unpacked content exceeds the 40 GB limit.");
                }
                if (archive.Type == ArchiveType.SevenZip || entries.Any(e => e.IsSolid))
                {
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                        if (!reader.Entry.IsDirectory) ExtractArchiveFile(reader.Entry.Key, reader.Entry.Size, staging, skipped, () => reader.OpenEntryStream());
                }
                else foreach (var entry in entries)
                    if (!entry.IsDirectory) ExtractArchiveFile(entry.Key, entry.Size, staging, skipped, entry.OpenEntryStream);
            }
            else if (File.Exists(source) && Path.GetExtension(source).Equals(".saf", StringComparison.OrdinalIgnoreCase))
            {
                EnsureNoLinks(Path.GetDirectoryName(source)!, source);
                if (new FileInfo(source).Length > MaximumBytes) throw new InvalidDataException("This SAF exceeds the 40 GB install limit.");
                File.Copy(source, Path.Combine(staging, Path.GetFileName(source)));
            }
            else throw new InvalidDataException("Choose a ZIP, SAF, RAR, 7z, or an extracted mod folder.");

            var stagedFiles = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
            if (stagedFiles.Length == 0) throw new InvalidDataException("No supported MX Simulator mod files were found.");
            var trackFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in stagedFiles)
            {
                var filename = Path.GetFileName(file);
                if (IsTerrainName(filename)) trackFolders.Add(Path.GetDirectoryName(file)!);
                if (!Path.GetExtension(file).Equals(".saf", StringComparison.OrdinalIgnoreCase)) continue;
                // Inspect SAF indexes without unpacking them. A folder holding a track SAF
                // is itself a package boundary and must not be flattened into its files.
                foreach (var item in SafArchive.ReadIndex(file))
                {
                    SafePath(staging, item.Path);
                    if (IsTerrainName(Path.GetFileName(item.Path))) trackFolders.Add(Path.GetDirectoryName(file)!);
                }
            }
            var contentRoot = staging;
            // Preserve asset directory names: tracks may reference @Folder/model.jm.
            // Remove only an outer wrapper, stopping at the parent of track packages.
            // Keep their folder names and every asset subfolder intact, including SAF folders.
            var candidate = staging;
            while (true)
            {
                if (trackFolders.Contains(candidate)) break;
                var topFiles = Directory.GetFiles(candidate);
                var dirs = Directory.GetDirectories(candidate);
                if (dirs.Any(trackFolders.Contains)) { contentRoot = candidate; break; }
                if (topFiles.Length != 0 || dirs.Length != 1) break;
                candidate = dirs[0];
            }
            var allFiles = Directory.GetFiles(contentRoot, "*", SearchOption.AllDirectories);
            // A playable loose track needs its own virtual folder, including when selected directly.
            var sourceIsFolder = Directory.Exists(source);
            var prefix = HasTerrain(contentRoot) || sourceIsFolder && trackFolders.Contains(contentRoot)
                ? Path.GetFileName(contentRoot == staging ? source.TrimEnd(Path.DirectorySeparatorChar) : contentRoot) : "";
            if (!sourceIsFolder && new[] { ".zip", ".rar", ".7z" }.Contains(Path.GetExtension(prefix), StringComparer.OrdinalIgnoreCase)) prefix = Path.GetFileNameWithoutExtension(prefix);
            if (Directory.Exists(source) && contentRoot == staging && prefix.Length == 0)
            {
                var rootFiles = Directory.GetFiles(staging);
                bool rootSkinFiles = rootFiles.Any(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^(rider_|front_wheel|rear_wheel|wheels|[0-9]{2,3}sx|(?:crf|cr|kx|rm|yz|fc|tc|mc)[0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                bool distribution = rootFiles.Any(f => Path.GetExtension(f).Equals(".saf", StringComparison.OrdinalIgnoreCase)) || Directory.GetDirectories(staging).Any(HasTerrain);
                if (!rootSkinFiles && !distribution) prefix = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
            }
            var files = new List<InstallEntry>();
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in allFiles)
            {
                var relative = Path.GetRelativePath(contentRoot, file);
                if (prefix.Length > 0) relative = Path.Combine(prefix, relative);
                SafePath(staging, relative);
                if (!destinations.Add(relative)) throw new InvalidDataException("The package contains duplicate destination filenames.");
                files.Add(new InstallEntry(file, relative, new FileInfo(file).Length));
            }
            return new InstallPlan { StagingFolder = staging, SourceName = Path.GetFileName(source), Files = files, Skipped = skipped };
        }
        catch { Directory.Delete(staging, true); throw; }
    }

    private static bool HasTerrain(string dir) => File.Exists(Path.Combine(dir, "terrain.hf")) || File.Exists(Path.Combine(dir, "terrain.hf.scram"));
    private static bool IsTerrainName(string name) => name.Equals("terrain.hf", StringComparison.OrdinalIgnoreCase) || name.Equals("terrain.hf.scram", StringComparison.OrdinalIgnoreCase);
    private static void ExtractArchiveFile(string? key, long size, string staging, List<string> skipped, Func<Stream> open)
    {
        var name = key ?? throw new InvalidDataException("An archive entry has no name.");
        var output = SafePath(staging, name);
        if (!IsModData(name)) { skipped.Add(name); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var input = open(); using var target = new FileStream(output, FileMode.CreateNew);
        CopyLimited(input, target, size);
    }
    private static bool IsModData(string relative)
    {
        var filename = Path.GetFileName(relative);
        if (filename.Equals("config", StringComparison.OrdinalIgnoreCase) || filename.Equals("regkey", StringComparison.OrdinalIgnoreCase)) return false;
        if (filename.StartsWith("readme", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("license", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("changelog", StringComparison.OrdinalIgnoreCase)) return false;
        if (relative.Split('/', '\\').Any(s => s.StartsWith('.') || s.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))) return false;
        var extension = Path.GetExtension(filename);
        if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase)) return IsModData(relative[..^3]);
        return AllowedExtensions.Contains(extension) || extension.Length == 0 && BareNames.Contains(filename);
    }
    private static void CopyFolderToStage(string source, string staging, List<string> skipped)
    {
        long total = 0; int count = 0;
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((source, 0)); int directories = 0;
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            if (++directories > MaximumFiles || depth > 128) throw new InvalidDataException("This folder tree is too deep or contains too many directories.");
            EnsureNoLinks(source, dir);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                EnsureNoLinks(source, file);
                if (++count > MaximumFiles) throw new InvalidDataException("Too many files in this folder.");
                var relative = Path.GetRelativePath(source, file);
                var output = SafePath(staging, relative);
                if (!IsModData(relative)) { skipped.Add(relative); continue; }
                total = checked(total + new FileInfo(file).Length);
                if (total > MaximumBytes) throw new InvalidDataException("This folder exceeds the 40 GB install limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(file, output, false);
            }
            foreach (var child in Directory.EnumerateDirectories(dir))
                if (!Path.GetFileName(child).StartsWith('.')) pending.Push((child, depth + 1));
        }
    }
    private static void CopyLimited(Stream input, Stream output, long expected)
    {
        var buffer = new byte[81920]; long written = 0; int read;
        while ((read = input.Read(buffer)) != 0)
        {
            written = checked(written + read);
            if (written > expected || written > MaximumBytes) throw new InvalidDataException("Archive size does not match its index.");
            output.Write(buffer, 0, read);
        }
        if (written != expected) throw new InvalidDataException("Archive entry is incomplete.");
    }
    public static string HashFile(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }

    public static InstallReceipt Install(InstallPlan plan, string libraryRoot, string stateRoot, bool gameRunning)
    {
        libraryRoot = Path.GetFullPath(libraryRoot);
        Directory.CreateDirectory(libraryRoot);
        if (History(stateRoot).Any(r => !r.Completed && !r.Undone && Path.GetFullPath(r.LibraryRoot).Equals(libraryRoot, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("An interrupted installation needs recovery. Close the game and use Undo last install before installing more files.");
        foreach (var entry in plan.Files)
        {
            var dest = SafePath(libraryRoot, entry.RelativePath);
            EnsureNoLinks(libraryRoot, dest);
            if (Directory.Exists(dest)) throw new IOException($"A folder already occupies {entry.RelativePath}.");
            if (gameRunning && File.Exists(dest)) throw new IOException("Close MX Simulator before replacing existing files. New files can be installed while it is open.");
        }
        var receipt = new InstallReceipt { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow, Name = plan.SourceName, LibraryRoot = libraryRoot };
        var transactionRoot = Path.Combine(stateRoot, "transactions", receipt.Id);
        Directory.CreateDirectory(transactionRoot);
        var applied = new List<InstalledFile>();
        try
        {
            foreach (var entry in plan.Files)
            {
                var dest = SafePath(libraryRoot, entry.RelativePath);
                var item = new InstalledFile { RelativePath = entry.RelativePath, InstalledHash = HashFile(entry.SourcePath) };
                if (File.Exists(dest))
                {
                    item.BackupPath = Path.Combine("backup", entry.RelativePath);
                    var backup = SafePath(transactionRoot, item.BackupPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(dest, backup, false);
                }
                receipt.Files.Add(item);
            }
            WriteReceipt(transactionRoot, receipt);
            foreach (var pair in plan.Files.Zip(receipt.Files))
            {
                var dest = SafePath(libraryRoot, pair.First.RelativePath);
                EnsureNoLinks(libraryRoot, dest);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var temp = dest + ".mxmm-" + receipt.Id;
                try { File.Copy(pair.First.SourcePath, temp, false); File.Move(temp, dest, true); applied.Add(pair.Second); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            receipt.Completed = true; WriteReceipt(transactionRoot, receipt);
            File.WriteAllText(Path.Combine(transactionRoot, "complete"), "Installed " + DateTime.UtcNow.ToString("O"));
            return receipt;
        }
        catch
        {
            receipt.Completed = false;
            bool recovered = true;
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                try { RestoreFile(libraryRoot, transactionRoot, item); }
                catch (IOException) { recovered = false; }
                catch (UnauthorizedAccessException) { recovered = false; }
            }
            receipt.Undone = recovered; WriteReceipt(transactionRoot, receipt);
            if (!recovered) File.WriteAllText(Path.Combine(transactionRoot, "complete"), "Interrupted install: use Undo to restore originals.");
            throw;
        }
    }
    public static List<InstallReceipt> History(string stateRoot)
    {
        var root = Path.Combine(stateRoot, "transactions");
        if (!Directory.Exists(root)) return [];
        var result = new List<InstallReceipt>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(dir, "receipt.json"))) continue;
            try
            {
                var item = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Path.Combine(dir, "receipt.json")));
                if (item != null)
                {
                    var marker = Path.Combine(dir, "complete");
                    if (!item.Completed && File.Exists(marker) && File.ReadAllText(marker).StartsWith("Installed ", StringComparison.Ordinal)) item.Completed = true;
                    result.Add(item);
                }
            }
            catch (JsonException) { }
        }
        return result.OrderByDescending(x => x.CreatedUtc).ToList();
    }
    public static void Undo(InstallReceipt receipt, string stateRoot)
    {
        if (receipt.Undone) throw new InvalidOperationException("This installation was already undone.");
        if (!Guid.TryParseExact(receipt.Id, "N", out _)) throw new InvalidDataException("Invalid receipt.");
        var transactionRoot = SafePath(Path.Combine(stateRoot, "transactions"), receipt.Id);
        var disabledRoot = Path.Combine(stateRoot, "disabled");
        if (Directory.Exists(disabledRoot))
        {
            foreach (var metadata in Directory.EnumerateFiles(disabledRoot, "disabled.json", SearchOption.AllDirectories))
            {
                var disabled = JsonSerializer.Deserialize<DisabledPackage>(File.ReadAllText(metadata));
                if (disabled == null) continue;
                var original = Path.GetFullPath(disabled.OriginalPath).TrimEnd(Path.DirectorySeparatorChar);
                if (receipt.Files.Any(f => { var target = SafePath(receipt.LibraryRoot, f.RelativePath); return target.Equals(original, StringComparison.OrdinalIgnoreCase) || target.StartsWith(original + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }))
                    throw new IOException($"Enable '{disabled.Name}' before undoing this installation.");
            }
        }
        foreach (var item in receipt.Files)
        {
            var dest = SafePath(receipt.LibraryRoot, item.RelativePath);
            EnsureNoLinks(receipt.LibraryRoot, dest);
            var original = item.BackupPath != null ? SafePath(transactionRoot, item.BackupPath) : null;
            if (original != null && !File.Exists(original)) throw new IOException("An original backup is missing. Undo stopped.");
            var currentHash = File.Exists(dest) ? HashFile(dest) : null;
            var alreadyRestored = original == null ? currentHash == null : currentHash != null && currentHash == HashFile(original);
            if (!alreadyRestored && currentHash != item.InstalledHash)
                throw new IOException($"{item.RelativePath} changed after installation. Undo stopped to preserve your changes.");
        }
        foreach (var item in receipt.Files.AsEnumerable().Reverse()) RestoreFile(receipt.LibraryRoot, transactionRoot, item);
        receipt.Undone = true; WriteReceipt(transactionRoot, receipt);
        foreach (var item in receipt.Files)
        {
            var dir = Path.GetDirectoryName(SafePath(receipt.LibraryRoot, item.RelativePath));
            while (dir != null && Directory.Exists(dir) && !dir.Equals(receipt.LibraryRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any())
            { Directory.Delete(dir); dir = Path.GetDirectoryName(dir); }
        }
    }
    private static void RestoreFile(string library, string transaction, InstalledFile item)
    {
        var dest = SafePath(library, item.RelativePath);
        if (item.BackupPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var temporary = dest + ".mxmm-undo-" + Guid.NewGuid().ToString("N");
            try { File.Copy(SafePath(transaction, item.BackupPath), temporary, false); File.Move(temporary, dest, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        else if (File.Exists(dest)) File.Delete(dest);
    }
    private static void WriteReceipt(string folder, InstallReceipt receipt)
    {
        var temporary = Path.Combine(folder, "receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt, JsonOptions); stream.Flush(true);
            }
            File.Move(temporary, Path.Combine(folder, "receipt.json"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static DisabledPackage Disable(ModPackage package, string libraryRoot, string stateRoot)
    {
        if (package.IsReadOnly || package.Scope != "Personal" || package.IsDisabled) throw new InvalidOperationException("Select an enabled personal mod package.");
        var relative = Path.GetRelativePath(libraryRoot, package.Path);
        var original = SafePath(libraryRoot, relative);
        EnsureNoLinks(libraryRoot, original);
        var container = Path.Combine(stateRoot, "disabled", Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(container, "payload"); Directory.CreateDirectory(payload);
        var destination = Path.Combine(payload, Path.GetFileName(original));
        var info = new DisabledPackage { OriginalPath = original, PayloadPath = destination, Name = package.Name };
        File.WriteAllText(Path.Combine(container, "disabled.json"), JsonSerializer.Serialize(info, JsonOptions));
        try { if (Directory.Exists(original)) Directory.Move(original, destination); else File.Move(original, destination); }
        catch { Directory.Delete(container, true); throw; }
        return info;
    }
    public static void Enable(ModPackage package, string libraryRoot, string stateRoot)
    {
        if (!package.IsDisabled) throw new InvalidOperationException("Select a disabled package.");
        var disabledRoot = Path.Combine(stateRoot, "disabled");
        var relative = Path.GetRelativePath(disabledRoot, package.Path);
        SafePath(disabledRoot, relative);
        var parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 3 || !Guid.TryParseExact(parts[0], "N", out _)) throw new InvalidDataException("Invalid disabled package location.");
        var container = Path.Combine(disabledRoot, parts[0]);
        var info = JsonSerializer.Deserialize<DisabledPackage>(File.ReadAllText(Path.Combine(container, "disabled.json"))) ?? throw new InvalidDataException("Missing disabled package information.");
        if (!Path.GetFullPath(info.PayloadPath).Equals(Path.GetFullPath(package.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Disabled package location mismatch.");
        var original = SafePath(libraryRoot, Path.GetRelativePath(libraryRoot, info.OriginalPath));
        EnsureNoLinks(disabledRoot, package.Path); EnsureNoLinks(libraryRoot, original);
        if (File.Exists(original) || Directory.Exists(original)) throw new IOException("Another package already uses the original location. Rename or move it before enabling this one.");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        if (Directory.Exists(package.Path)) Directory.Move(package.Path, original); else File.Move(package.Path, original);
        Directory.Delete(container, true);
    }
}
