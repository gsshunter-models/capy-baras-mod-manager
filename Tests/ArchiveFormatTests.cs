using MXSimModManager.Core;

public static class ArchiveFormatTests
{
    public static void Run(string root)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var state = Path.Combine(root, "state"); var library = Path.Combine(root, "library");
        using (var plan = ModInstaller.Prepare(Path.Combine(fixtures, "Track.7z"), state))
        {
            Check(plan.Files.Any(f => f.RelativePath.Replace('\\', '/') == "TestTrack/terrain.hf"), "7z track paths must be preserved.");
            var receipt = ModInstaller.Install(plan, library, state, false);
            Check(LibraryScanner.Scan(library, "", "", false).UniqueTrackCount == 1, "7z track should be detected after installation.");
            ModInstaller.Undo(receipt, state);
        }
        using (var plan = ModInstaller.Prepare(Path.Combine(fixtures, "Rar5.comment.plain.rar"), state))
        {
            Check(plan.Files.Count == 1 && plan.Files[0].RelativePath == "payload.txt", "RAR5 file must be decoded and named correctly.");
            var expected = File.ReadAllBytes(plan.Files[0].SourcePath);
            Check(System.Text.Encoding.UTF8.GetString(expected) == "hello from RAR!\n", "RAR5 payload must decode to the known fixture text.");
            var receipt = ModInstaller.Install(plan, library, state, false);
            Check(expected.SequenceEqual(File.ReadAllBytes(Path.Combine(library, "payload.txt"))), "RAR data must be installed intact.");
            ModInstaller.Undo(receipt, state);
        }
        bool encryptedRejected = false;
        try { using var ignored = ModInstaller.Prepare(Path.Combine(fixtures, "Rar5.comment.filesOnly.rar"), state); }
        catch (InvalidDataException) { encryptedRejected = true; }
        Check(encryptedRejected, "Encrypted RAR content must be rejected before installation.");
        var fake = Path.Combine(root, "fake.rar"); File.WriteAllText(fake, "not an archive");
        bool rejected = false; try { using var ignored = ModInstaller.Prepare(fake, state); } catch { rejected = true; }
        Check(rejected && !Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories).Any(), "Invalid archives must not install anything.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
