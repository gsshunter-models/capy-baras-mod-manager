using System.IO.Compression;
using MXSimModManager.Core;

public static class ForumAndLaunchTests
{
    public static void Run(string root)
    {
        Check(ForumDownloadPolicy.WebAddress("forum.mxsimulator.com/viewforum.php?f=4").Scheme == "https", "Scheme-less forum links should use HTTPS.");
        foreach (var uri in new[] { "file:///C:/private", "javascript:alert(1)", "https://user:pass@example.com/x", "mxsimulator://example" }) Throws<InvalidDataException>(() => ForumDownloadPolicy.WebAddress(uri));
        Check(ForumDownloadPolicy.SearchUrl("Redbud & sand").Contains("Redbud%20%26%20sand") && ForumDownloadPolicy.SearchUrl("test").Contains("as_sitesearch=forum.mxsimulator.com") && ForumDownloadPolicy.SearchUrl("test").Contains("site%3Aforum.mxsimulator.com%2Fviewtopic.php"), "Search must encode user text and restrict results to forum posts.");
        Check(new Uri(ForumDownloadPolicy.AlternateSearchUrl("Redbud")).Host == "www.bing.com" && ForumDownloadPolicy.AlternateSearchUrl("Redbud").Contains("site%3Aforum.mxsimulator.com%2Fviewtopic.php"), "An alternate search provider must retain the forum source restriction.");
        foreach (var name in new[] { "Track.zip", "TRACK.SAF", "track.rar", "track.7z" }) Check(ForumDownloadPolicy.IsPackage(name), "Supported package extension");
        foreach (var name in new[] { "installer.exe", "Track.zip.exe", "opengl32.dll", "file.lnk" }) Throws<InvalidDataException>(() => ForumDownloadPolicy.DownloadName(name, "application/zip", "https://example.com/track.zip"));
        Check(ForumDownloadPolicy.DownloadName("download", "application/zip", "https://example.com/file") == "download.zip", "Recognized download MIME types should supply missing extensions.");
        Check(ForumDownloadPolicy.DownloadName("download", "application/octet-stream", "https://example.com/track.saf?token=ignored") == "track.saf", "Direct SAF links should supply missing filename extensions.");
        var safe = ForumDownloadPolicy.DownloadName("../../CON.zip", "", "https://example.com/file");
        Check(safe == "mod-CON.zip" && Path.GetFileName(safe) == safe, "Suggested names must not escape download storage or use device names.");
        var longName = ForumDownloadPolicy.DownloadName(new string('a', 300) + ".zip", "", "https://example.com/file");
        Check(longName.Length <= 134 && longName.EndsWith(".zip"), "Long download names retain their extension.");

        var start = GameLaunch.ForTrack(Path.Combine(root, "mx.exe"), "Track folder/Day layout");
        Check(start.ArgumentList.Count == 2 && start.ArgumentList[0] == "--track" && start.ArgumentList[1] == "Track folder/Day layout" && !start.UseShellExecute, "Track launch must pass a single literal folder argument without a shell.");
        foreach (var id in new[] { "../track", "C:/track", "--editor", "track\n--connect", "track\"quoted", "." }) Throws<InvalidDataException>(() => GameLaunch.ForTrack(Path.Combine(root, "mx.exe"), id));

        // Confirm a downloaded package takes the same transactional install/undo path.
        var zipPath = Path.Combine(root, "Forum track.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("ForumTrack/terrain.hf").Open())) writer.Write("9 2 0 1");
            using (var writer = new StreamWriter(zip.CreateEntry("ForumTrack/desc").Open())) writer.Write("Forum Track");
        }
        var state = Path.Combine(root, "state"); var library = Path.Combine(root, "library");
        using var plan = ModInstaller.Prepare(zipPath, state);
        var receipt = ModInstaller.Install(plan, library, state, false);
        Check(LibraryScanner.Scan(library, "", "", false).Packages.Single().TrackIds.Single() == "ForumTrack", "Downloaded track should become selectable for Play.");
        ModInstaller.Undo(receipt, state);
        Check(!Directory.Exists(Path.Combine(library, "ForumTrack")), "Undo must revert a forum install.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
