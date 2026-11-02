using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MXSimModManager.Core;

var tests = new (string Name, Action<TestArea> Run)[]
{
    ("SAF Unicode, CRLF, exact offsets, zero-length entry", SafEncoding),
    ("SAF malformed headers and payload lengths", SafMalformed),
    ("Scanner basenames, protected skins, names, track deduplication", ScannerContent),
    ("Scanner bad SAF warning and base-game separation", ScannerWarningsAndScopes),
    ("ZIP traversal, absolute paths, ADS, reserved names", UnsafeZipPaths),
    ("ZIP file and directory symlinks rejected", ZipLinks),
    ("SAF unsafe virtual path rejected by installer", UnsafeSafPaths),
    ("Executable, DLL, configuration, license files skipped", SkipNonModFiles),
    ("Selected loose track gets its own folder", SelectedTrack),
    ("Redundant ZIP track wrapper normalized", WrappedTrack),
    ("Track folders, nested SAF packages and shared assets kept intact", TrackFolderBoundaries),
    ("Named asset folder preserved", AssetFolder),
    ("Install backs up overwrite, undoes new files, repeat undo safe", BackupUndo),
    ("Undo recovers already restored files", PartialUndoRecovery),
    ("Undo preserves files edited after install", ModifiedFileProtection),
    ("Disable, disabled scan, enable, collision protection", DisableEnable),
    ("Running game allows new files and blocks all replacement", RunningGame),
    ("Custom group persistence and membership", a => GroupTests.Run(a.Sources)),
    ("Disabled installs must be enabled before undo", DisabledUndo),
    ("Setup editor, backup, apply and restore suite", _ => { if (SetupTests.Run() != 0) throw new InvalidOperationException("Setup tests failed."); }),
    ("Interrupted install remains recoverable", InterruptedInstall),
    ("Appearance catalog, native presets, apply, backup and safety", a => AppearanceTests.Run(a.Sources)),
    ("Forum downloads, track launch arguments and install undo", a => ForumAndLaunchTests.Run(a.Sources)),
    ("RAR5 and 7z packages install and undo", a => ArchiveFormatTests.Run(a.Sources)),
    ("Archive entries above 40 GB retain 64-bit lengths and integrity checks", _ => LargePackageTests.Run()),
    ("Public host downloads, names, partial-file cleanup and cancellation", a => DirectDownloadTests.Run(a.Sources)),
    ("Terrain preview paths, native formats, texture placement and cancellation", a => TerrainPreviewTests.Run(a.Sources))
};

var root = Path.Combine(Path.GetTempPath(), "MXSimModManager-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = new List<string>();
try
{
    for (var i = 0; i < tests.Length; i++)
    {
        using var area = new TestArea(Path.Combine(root, i.ToString("D2")));
        try { tests[i].Run(area); Console.WriteLine("PASS " + tests[i].Name); }
        catch (Exception ex)
        {
            failures.Add(tests[i].Name + ": " + ex.Message);
            Console.WriteLine("FAIL " + tests[i].Name + ": " + ex);
        }
    }
    Console.WriteLine($"\n{tests.Length - failures.Count}/{tests.Length} tests passed.");
    var resolveIndex = Array.IndexOf(args, "--resolve-link");
    if (resolveIndex >= 0 && resolveIndex + 1 < args.Length)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var client = new DirectDownloadClient();
        using var batch = await client.ResolveAsync(ForumDownloadPolicy.WebAddress(args[resolveIndex + 1]), cancellation.Token);
        Console.WriteLine($"Public host resolved {batch.Files.Count} package(s).");
        foreach (var file in batch.Files.Take(5)) Console.WriteLine("  " + file.DisplayName);
        var limitIndex = Array.IndexOf(args, "--download-if-under");
        if (batch.Files.Count == 1 && limitIndex >= 0 && limitIndex + 1 < args.Length && long.TryParse(args[limitIndex + 1], out var limit) && batch.Files[0].Size >= 0 && batch.Files[0].Size <= limit)
        {
            var path = Path.Combine(root, batch.Files[0].Name);
            await DirectDownloadClient.DownloadAsync(batch.Files[0], path, null, cancellation.Token);
            using var plan = ModInstaller.Prepare(path, Path.Combine(root, "download-preview"));
            Console.WriteLine($"Public download verified: {new FileInfo(path).Length} bytes, {plan.Files.Count} supported mod files; no game files installed.");
        }
    }
    var packageCheckIndex = Array.IndexOf(args, "--check-package");
    if (packageCheckIndex >= 0 && packageCheckIndex + 1 < args.Length)
    {
        using var plan = ModInstaller.Prepare(args[packageCheckIndex + 1], Path.Combine(root, "package-preview"));
        Console.WriteLine($"Package preview only: {plan.Files.Count} supported files, {ModPackage.FormatSize(plan.Files.Sum(f => f.Size))}; no game files installed.");
        foreach (var file in plan.Files.Take(5)) Console.WriteLine("  " + file.RelativePath);
    }
    if (args.Contains("--scan-real", StringComparer.OrdinalIgnoreCase))
    {
        var scanRootIndex = Array.IndexOf(args, "--scan-root");
        var scanRoot = scanRootIndex >= 0 && scanRootIndex + 1 < args.Length ? args[scanRootIndex + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MX Simulator");
        var snapshot = LibraryScanner.Scan(scanRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MX Simulator"), "", false);
        Console.WriteLine($"Read-only personal library: {snapshot.Packages.Count} packages, {snapshot.UniqueTrackCount} unique tracks, " +
            $"{snapshot.GearPackageCount} gear packages, {snapshot.BikePackageCount} bike packages, {snapshot.Warnings.Count} warnings.");
    }
    Environment.ExitCode = failures.Count == 0 ? 0 : 1;
}
finally
{
    // The only recursive deletion in this harness targets its uniquely generated temp tree.
    var fullRoot = Path.GetFullPath(root);
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullRoot).StartsWith("MXSimModManager-tests-"))
        throw new InvalidOperationException("Refusing to clean up a path outside the generated test folder.");
    if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
}

static void SafEncoding(TestArea a)
{
    var path = Path.Combine(a.Sources, "unicode.saf");
    var description = Encoding.UTF8.GetBytes("Café Track\n");
    var headerLength = WriteSaf(path, "\r\n", ("földer/desc", description), ("földer/empty", []), ("földer/terrain.hf", Encoding.ASCII.GetBytes("9 2 0 1")));
    var entries = SafArchive.ReadIndex(path);
    Equal(3, entries.Count, "Entry count");
    Equal("földer/desc", entries[0].Path, "Unicode filename");
    Equal((long)headerLength, entries[0].Offset, "Header byte offset");
    Equal(entries[0].Offset + description.Length, entries[1].Offset, "Second offset");
    Equal(entries[1].Offset, entries[2].Offset, "Zero-length entry consumes no bytes");
    Equal("Café Track\n", SafArchive.ReadSmallText(path, entries[0]), "Direct text entry read");
    Equal("Caf", SafArchive.ReadSmallText(path, entries[0], 3), "Text read size cap");
}

static void SafMalformed(TestArea a)
{
    var path = Path.Combine(a.Sources, "bad.saf");
    foreach (var contents in new[] { "999 absent.png\n-\n", "0 empty\n-\nextra", "3 incomplete", "NaN x\n-\n", "-1 x\n-\n", "9223372036854775808 x\n-\n", "0 " + new string('x', 65537) + "\n-\n" })
    {
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        Throws<InvalidDataException>(() => SafArchive.ReadIndex(path), "Malformed archive must be rejected");
    }
    WriteSaf(path, "\n", ("desc.scram", Encoding.ASCII.GetBytes("encrypted")));
    Check(SafArchive.ReadSmallText(path, SafArchive.ReadIndex(path)[0]) is null, "Protected text should not be interpreted");
}

static void ScannerContent(TestArea a)
{
    Write(a.Library, "sx2023/terrain.hf", "9 2 0 1");
    Write(a.Library, "sx2023/desc", "A Friendly Track\n");
    Write(a.Library, "sx2023/map.png", "map");
    WriteSaf(Path.Combine(a.Library, "same-track.saf"), "\n",
        ("sx2023/terrain.hf", Encoding.ASCII.GetBytes("9 2 0 1")),
        ("sx2023/desc", Encoding.UTF8.GetBytes("A Friendly Track")),
        ("sx2023/stadiums/brick.png", [1]));
    WriteSaf(Path.Combine(a.Library, "gear.saf"), "\n",
        ("pack/rider_body-Model-Skin.png.scram", [1]),
        ("pack/rider_body-Model_norm.png.scram", [2]),
        ("pack/rider_body-Model_spec.png.scram", [3]),
        ("pack/rider_body-Model.jm", [4]),
        ("pack/rider_head-Helmet-Skin.png.scram", [5]));
    WriteSaf(Path.Combine(a.Library, "bike.saf"), "\n",
        ("pack/250sxfv2016-Model-Skin.png.scram", [1]),
        ("pack/250sxfv2016_frame-Model.jm", [2]),
        ("pack/front_wheel-Tire.jm", [3]));
    Write(a.Library, "screenshots/screenshot001.png", "picture");
    Write(a.Library, "config", "do not interpret");
    Write(a.Library, "regkey", "do not interpret");
    var snapshot = LibraryScanner.Scan(a.Library, "", "", false);
    Equal(1, snapshot.UniqueTrackCount, "Same virtual track must deduplicate");
    Equal(1, snapshot.GearPackageCount, "Gear package count");
    Equal(1, snapshot.BikePackageCount, "Bike package count");
    Equal(4, snapshot.Packages.Count, "Settings and screenshots must not become packages");
    var tracks = snapshot.Packages.Where(p => p.Categories.Contains("Track")).ToList();
    Check(tracks.All(p => !p.Categories.Contains("Bike")), "sx2023 folder must not be mistaken for a bike dyno");
    Check(tracks.All(p => p.Name == "A Friendly Track"), "Track descriptions should provide friendly names");
    Check(tracks.Any(p => p.MapPath == Path.Combine(a.Library, "sx2023", "map.png")), "Loose map should be found");
    var gear = snapshot.Packages.Single(p => p.Categories.Contains("Gear"));
    Equal(2, gear.SkinCount, "Normal/specular textures are not skins");
    Equal(1, gear.ModelCount, "Models counted separately");
    Check(snapshot.Packages.Single(p => p.Categories.Contains("Bike")).Categories.Contains("Wheels"), "Wheel assets should be identified");
}

static void ScannerWarningsAndScopes(TestArea a)
{
    Write(a.Library, "broken.saf", "not a SAF");
    WriteSaf(Path.Combine(a.Install, "stock.saf"), "\n", ("base/terrain.hf", [1]));
    var personal = LibraryScanner.Scan(a.Library, a.Install, "", false);
    Equal(1, personal.Packages.Count, "Base content hidden by default");
    Equal(1, personal.Warnings.Count, "Corrupt archive should warn without aborting scan");
    var all = LibraryScanner.Scan(a.Library, a.Install, "", true);
    var basePackage = all.Packages.Single(p => p.Scope == "Base game");
    Check(basePackage.IsReadOnly && !basePackage.IsDisabled, "Base content is read-only");
}

static void UnsafeZipPaths(TestArea a)
{
    foreach (var name in new[] { "../escaped.png", "sub/../../escaped.png", "sub\\..\\escaped.png", "/absolute.png", "C:/drive.png", "skin.png:payload.exe", "CON.png", "trailing./image.png" })
    {
        var zip = CreateZip(a, new ZipItem(name, "payload"));
        Throws<InvalidDataException>(() => { using var plan = ModInstaller.Prepare(zip, a.State); }, "Unsafe ZIP path " + name);
        Check(!File.Exists(Path.Combine(a.Root, "escaped.png")), "Traversal must not write outside staging");
    }
}

static void ZipLinks(TestArea a)
{
    foreach (var name in new[] { "linked.png", "linked/" })
    {
        var zip = CreateZip(a, new ZipItem(name, "target", unchecked((int)0xA1FF0000)));
        Throws<InvalidDataException>(() => { using var plan = ModInstaller.Prepare(zip, a.State); }, "UNIX ZIP symlink must be rejected: " + name);
    }
}

static void UnsafeSafPaths(TestArea a)
{
    foreach (var name in new[] { "../escaped.png", "skin.png:ads", "C:/drive.png" })
    {
        var saf = Path.Combine(a.Sources, "unsafe.saf");
        WriteSaf(saf, "\n", (name, [1]));
        Throws<InvalidDataException>(() => { using var plan = ModInstaller.Prepare(saf, a.State); }, "Unsafe SAF virtual path");
    }
}

static void SkipNonModFiles(TestArea a)
{
    var zip = CreateZip(a, new ZipItem("safe.png", "image"), new ZipItem("launcher.exe", "exe"), new ZipItem("opengl32.dll", "dll"),
        new ZipItem("config", "settings"), new ZipItem("regkey", "private"), new ZipItem("addon.ini", "settings"),
        new ZipItem("install.bat", "script"), new ZipItem("__MACOSX/ignored.png", "metadata"), new ZipItem("safe.jm.gz", "compressed model"), new ZipItem("launcher.exe.gz", "compressed executable"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    Equal(2, plan.Files.Count, "Only mod data should be staged");
    Check(plan.Files.Any(f => Norm(f.RelativePath) == "safe.png") && plan.Files.Any(f => Norm(f.RelativePath) == "safe.jm.gz"), "Allowed texture and compressed model retained");
    Equal(8, plan.Skipped.Count, "Skipped file count");
    ModInstaller.Install(plan, a.Library, a.State, false);
    Check(File.Exists(Path.Combine(a.Library, "safe.png")), "Texture installed");
    Check(!File.Exists(Path.Combine(a.Library, "config")) && !File.Exists(Path.Combine(a.Library, "opengl32.dll")), "Settings and DLL not installed");
}

static void SelectedTrack(TestArea a)
{
    var track = Path.Combine(a.Sources, "SelectedTrack");
    Write(track, "terrain.hf", "9 2 0 1"); Write(track, "desc", "Selected Track");
    using var plan = ModInstaller.Prepare(track, a.State);
    Check(plan.Files.All(f => Norm(f.RelativePath).StartsWith("SelectedTrack/")), "A selected track directory must retain its name");
    ModInstaller.Install(plan, a.Library, a.State, false);
    Check(File.Exists(Path.Combine(a.Library, "SelectedTrack", "terrain.hf")), "Track installed in named folder");
}

static void WrappedTrack(TestArea a)
{
    var zip = CreateZip(a, new ZipItem("DownloadWrapper/ProperTrack/terrain.hf", "9 2 0 1"),
        new ZipItem("DownloadWrapper/ProperTrack/desc", "Track"), new ZipItem("DownloadWrapper/ProperTrack/objects/tree.jm", "mesh"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    Check(plan.Files.All(f => Norm(f.RelativePath).StartsWith("ProperTrack/")), "Redundant download wrapper should be removed");
    Check(plan.Files.Any(f => Norm(f.RelativePath) == "ProperTrack/objects/tree.jm"), "Track asset subfolders must stay intact");
}

static void TrackFolderBoundaries(TestArea a)
{
    var release = Path.Combine(a.Sources, "SafRelease");
    var folder = Path.Combine(release, "DownloadWrapper", "TrackPackage");
    Directory.CreateDirectory(folder);
    WriteSaf(Path.Combine(folder, "2026Track.saf"), "\n", ("2026Track/terrain.hf.scram", Encoding.ASCII.GetBytes("protected terrain")), ("2026Track/desc", Encoding.UTF8.GetBytes("2026 Track")));
    Write(folder, "objects/tree.jm", "mesh");
    var archive = Path.Combine(a.Sources, "SafRelease.zip");
    ZipFile.CreateFromDirectory(release, archive);
    using (var plan = ModInstaller.Prepare(archive, a.State))
    {
        Check(plan.Files.All(f => Norm(f.RelativePath).StartsWith("TrackPackage/")), "A folder containing a track SAF must be brought out intact, without its download wrapper");
        Check(plan.Files.Any(f => Norm(f.RelativePath) == "TrackPackage/2026Track.saf"), "The SAF stays inside its original folder");
        Check(plan.Files.Any(f => Norm(f.RelativePath) == "TrackPackage/objects/tree.jm"), "Asset subfolders stay inside the same track package");
        Write(a.Library, "TrackPackage/objects/tree.jm", "original");
        var receipt = ModInstaller.Install(plan, a.Library, a.State, false);
        Check(!File.Exists(Path.Combine(a.Library, "2026Track.saf")) && !File.Exists(Path.Combine(a.Library, "terrain.hf")), "No track content is spilled into the library root");
        var installed = LibraryScanner.Scan(a.Library, "", "", false);
        Check(Path.GetFileName(installed.Packages.Single().Path) == "TrackPackage" && installed.UniqueTrackCount == 1, "Installed track SAF folder remains one detectable package");
        ModInstaller.Undo(receipt, a.State);
        Equal("original", File.ReadAllText(Path.Combine(a.Library, "TrackPackage", "objects", "tree.jm")), "Undo restores existing assets inside the preserved folder");
        Check(!File.Exists(Path.Combine(a.Library, "TrackPackage", "2026Track.saf")), "Undo removes the new nested SAF");
    }
    using (var plan = ModInstaller.Prepare(folder, a.State))
        Check(plan.Files.All(f => Norm(f.RelativePath).StartsWith("TrackPackage/")), "Selecting the SAF track folder itself must preserve that folder too");

    var multi = CreateZip(a, new ZipItem("Download/Collection/TrackA/terrain.hf", "9 2 0 1"),
        new ZipItem("Download/Collection/TrackA/desc", "Track A"), new ZipItem("Download/Collection/TrackA/textures/dirt.png", "dirt"),
        new ZipItem("Download/Collection/TrackB/terrain.hf.scram", "protected"), new ZipItem("Download/Collection/TrackB/desc", "Track B"),
        new ZipItem("Download/Collection/SharedObjects/tree.jm", "shared mesh"));
    using (var plan = ModInstaller.Prepare(multi, a.State))
    {
        var paths = plan.Files.Select(f => Norm(f.RelativePath)).ToHashSet();
        Check(paths.Contains("TrackA/terrain.hf") && paths.Contains("TrackB/terrain.hf.scram"), "Multiple track folders must be lifted out together without flattening them");
        Check(paths.Contains("TrackA/textures/dirt.png") && paths.Contains("SharedObjects/tree.jm"), "Track subfolders and sibling shared asset folders keep their names");
        var receipt = ModInstaller.Install(plan, a.Library, a.State, false);
        Equal(2, LibraryScanner.Scan(a.Library, "", "", false).UniqueTrackCount, "Both extracted track folders remain detectable");
        ModInstaller.Undo(receipt, a.State);
        Check(!Directory.Exists(Path.Combine(a.Library, "TrackA")) && !Directory.Exists(Path.Combine(a.Library, "TrackB")), "Undo removes the added track folders");
    }
    var rootArchive = Path.Combine(a.Sources, "RootTrack.zip");
    using (var zip = ZipFile.Open(rootArchive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("terrain.hf").Open())) writer.Write("9 2 0 1");
    using (var plan = ModInstaller.Prepare(rootArchive, a.State))
        Equal("RootTrack/terrain.hf", Norm(plan.Files.Single().RelativePath), "A root-level loose track gets a named folder instead of loose files");
    var namedFolder = Path.Combine(a.Sources, "Track.zip"); Write(namedFolder, "terrain.hf", "9 2 0 1");
    using (var plan = ModInstaller.Prepare(namedFolder, a.State))
        Equal("Track.zip/terrain.hf", Norm(plan.Files.Single().RelativePath), "A selected folder's exact name is preserved even if it resembles an archive filename");
}

static void AssetFolder(TestArea a)
{
    var zip = CreateZip(a, new ZipItem("NamedObjectPack/object.jm", "mesh"), new ZipItem("NamedObjectPack/object.png", "image"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    Check(plan.Files.All(f => Norm(f.RelativePath).StartsWith("NamedObjectPack/")), "Named object folder must not be flattened");
    var source = Path.Combine(a.Sources, "SelectedObjectPack");
    Write(source, "object.jm", "mesh"); Write(source, "object.png", "image");
    using var folderPlan = ModInstaller.Prepare(source, a.State);
    Check(folderPlan.Files.All(f => Norm(f.RelativePath).StartsWith("SelectedObjectPack/")), "Selected asset folder must retain reference paths");
}

static void BackupUndo(TestArea a)
{
    Write(a.Library, "existing.png", "original");
    var zip = CreateZip(a, new ZipItem("existing.png", "replacement"), new ZipItem("NewPack/texture.png", "new"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    var receipt = ModInstaller.Install(plan, a.Library, a.State, false);
    Equal("replacement", File.ReadAllText(Path.Combine(a.Library, "existing.png")), "Overwrite applied");
    Check(receipt.Files.Single(f => f.RelativePath == "existing.png").BackupPath is not null, "Overwrite has original backup");
    Equal(1, ModInstaller.History(a.State).Count, "Completed transaction recorded");
    var staleReceipt = JsonSerializer.Deserialize<InstallReceipt>(JsonSerializer.Serialize(receipt))!;
    ModInstaller.Undo(receipt, a.State);
    Check(receipt.Undone, "Receipt marked undone");
    Equal("original", File.ReadAllText(Path.Combine(a.Library, "existing.png")), "Original restored");
    Check(!Directory.Exists(Path.Combine(a.Library, "NewPack")), "New file and new empty directory removed");
    Throws<InvalidOperationException>(() => ModInstaller.Undo(receipt, a.State), "An already-undone receipt should report its state without mutation");
    ModInstaller.Undo(staleReceipt, a.State);
    Equal("original", File.ReadAllText(Path.Combine(a.Library, "existing.png")), "Repeated undo preserves original");
    Check(ModInstaller.History(a.State).Single().Undone, "Stored receipt reflects undo");
}

static void PartialUndoRecovery(TestArea a)
{
    Write(a.Library, "existing.png", "original");
    var zip = CreateZip(a, new ZipItem("existing.png", "replacement"), new ZipItem("new.png", "new"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    var receipt = ModInstaller.Install(plan, a.Library, a.State, false);
    var overwritten = receipt.Files.Single(f => f.RelativePath == "existing.png");
    var backup = Path.Combine(a.State, "transactions", receipt.Id, overwritten.BackupPath!);
    File.Copy(backup, Path.Combine(a.Library, "existing.png"), true);
    File.Delete(Path.Combine(a.Library, "new.png"));
    ModInstaller.Undo(receipt, a.State);
    Equal("original", File.ReadAllText(Path.Combine(a.Library, "existing.png")), "Already restored original preserved");
    Check(receipt.Undone, "Interrupted undo can finish its receipt");
}

static void ModifiedFileProtection(TestArea a)
{
    Write(a.Library, "existing.png", "original");
    var zip = CreateZip(a, new ZipItem("existing.png", "replacement"), new ZipItem("new.png", "new"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    var receipt = ModInstaller.Install(plan, a.Library, a.State, false);
    Write(a.Library, "new.png", "user edited it");
    Throws<IOException>(() => ModInstaller.Undo(receipt, a.State), "User edits should block undo before mutation");
    Equal("user edited it", File.ReadAllText(Path.Combine(a.Library, "new.png")), "User edit preserved");
    Equal("replacement", File.ReadAllText(Path.Combine(a.Library, "existing.png")), "Other installed files unchanged on failed preflight");
    Check(!receipt.Undone, "Failed undo remains recoverable");
}

static void DisableEnable(TestArea a)
{
    Write(a.Library, "Track/terrain.hf", "9 2 0 1"); Write(a.Library, "Track/desc", "Friendly");
    var package = LibraryScanner.Scan(a.Library, "", "", false).Packages.Single();
    var info = ModInstaller.Disable(package, a.Library, a.State);
    Check(!Directory.Exists(package.Path) && Directory.Exists(info.PayloadPath), "Disable moves the package out of library");
    var disabled = LibraryScanner.Scan(a.Library, "", Path.Combine(a.State, "disabled"), false);
    Equal(0, disabled.UniqueTrackCount, "Disabled tracks excluded from installed count");
    Check(disabled.Packages.Single().IsDisabled, "Disabled package remains visible");
    Write(a.Library, "Track/terrain.hf", "collision");
    Throws<IOException>(() => ModInstaller.Enable(disabled.Packages.Single(), a.Library, a.State), "Enable must not overwrite a collision");
    File.Delete(Path.Combine(a.Library, "Track", "terrain.hf")); Directory.Delete(Path.Combine(a.Library, "Track"));
    ModInstaller.Enable(disabled.Packages.Single(), a.Library, a.State);
    Check(File.Exists(Path.Combine(a.Library, "Track", "terrain.hf")), "Enable restores original location");
    Check(!Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(info.PayloadPath)!)), "Empty disabled container removed");
}

static void RunningGame(TestArea a)
{
    var first = CreateZip(a, new ZipItem("existing.png", "first"));
    using (var plan = ModInstaller.Prepare(first, a.State)) ModInstaller.Install(plan, a.Library, a.State, true);
    var second = CreateZip(a, new ZipItem("new.png", "new"), new ZipItem("existing.png", "replacement"));
    using var secondPlan = ModInstaller.Prepare(second, a.State);
    Throws<IOException>(() => ModInstaller.Install(secondPlan, a.Library, a.State, true), "Running game blocks replacement");
    Equal("first", File.ReadAllText(Path.Combine(a.Library, "existing.png")), "Existing file untouched");
    Check(!File.Exists(Path.Combine(a.Library, "new.png")), "Preflight blocks entire mixed installation");
}

static void DisabledUndo(TestArea a)
{
    var zip = CreateZip(a, new ZipItem("Track/terrain.hf", "9 2 0 1"), new ZipItem("Track/desc", "A Track"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    var receipt = ModInstaller.Install(plan, a.Library, a.State, false);
    var package = LibraryScanner.Scan(a.Library, "", "", false).Packages.Single();
    ModInstaller.Disable(package, a.Library, a.State);
    Throws<IOException>(() => ModInstaller.Undo(receipt, a.State), "Undo should require re-enabling disabled installed content");
    var disabled = LibraryScanner.Scan(a.Library, "", Path.Combine(a.State, "disabled"), false).Packages.Single();
    ModInstaller.Enable(disabled, a.Library, a.State);
    ModInstaller.Undo(receipt, a.State);
    Check(!Directory.Exists(package.Path), "Undo succeeds after enable");
}

static void InterruptedInstall(TestArea a)
{
    Write(a.Library, "existing.png", "original");
    var zip = CreateZip(a, new ZipItem("existing.png", "replacement"), new ZipItem("new.png", "new"));
    using var plan = ModInstaller.Prepare(zip, a.State);
    var receipt = ModInstaller.Install(plan, a.Library, a.State, false);
    receipt.Completed = false;
    var transaction = Path.Combine(a.State, "transactions", receipt.Id);
    File.WriteAllText(Path.Combine(transaction, "receipt.json"), JsonSerializer.Serialize(receipt));
    File.Delete(Path.Combine(transaction, "complete"));
    File.Delete(Path.Combine(a.Library, "new.png"));
    var pending = ModInstaller.History(a.State).Single();
    Check(!pending.Completed, "Interrupted receipt must be visible");
    Throws<IOException>(() => ModInstaller.Install(plan, a.Library, a.State, false), "Recover before a new installation");
    ModInstaller.Undo(pending, a.State);
    Equal("original", File.ReadAllText(Path.Combine(a.Library, "existing.png")), "Interrupted replacement recovered");
}

static int WriteSaf(string path, string newline, params (string Name, byte[] Content)[] entries)
{
    var header = Encoding.UTF8.GetBytes(string.Join("", entries.Select(e => e.Content.Length + " " + e.Name + newline)) + "-" + newline);
    using var stream = File.Create(path);
    stream.Write(header);
    foreach (var entry in entries) stream.Write(entry.Content);
    return header.Length;
}

static string CreateZip(TestArea a, params ZipItem[] items)
{
    var path = Path.Combine(a.Sources, Guid.NewGuid().ToString("N") + ".zip");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var item in items)
    {
        var entry = zip.CreateEntry(item.Path);
        if (item.Attributes != 0) entry.ExternalAttributes = item.Attributes;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(item.Text);
    }
    return path;
}

static void Write(string root, string relative, string content)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(false));
}
static string Norm(string path) => path.Replace('\\', '/');
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}"); }
static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message + ": expected " + typeof(T).Name);
}

sealed record ZipItem(string Path, string Text = "", int Attributes = 0);
sealed class TestArea : IDisposable
{
    public string Root { get; }
    public string Library => Path.Combine(Root, "library");
    public string Install => Path.Combine(Root, "base");
    public string State => Path.Combine(Root, "state");
    public string Sources => Path.Combine(Root, "source");
    public TestArea(string root)
    {
        Root = root;
        foreach (var path in new[] { Library, Install, State, Sources }) Directory.CreateDirectory(path);
    }
    public void Dispose() { }
}
