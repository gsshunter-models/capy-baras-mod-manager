namespace MXSimModManager.Core;

public static class ForumDownloadPolicy
{
    public const long MaximumDownloadBytes = 40L * 1024 * 1024 * 1024;
    public const string TracksUrl = "https://forum.mxsimulator.com/viewforum.php?f=4";
    public static bool IsPackage(string path) => new[] { ".zip", ".saf", ".rar", ".7z" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static Uri WebAddress(string text)
    {
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Enter an HTTP or HTTPS forum/download link.");
        return uri;
    }
    public static string SearchUrl(string text) => "https://www.google.com/search?as_sitesearch=forum.mxsimulator.com&q=" + Uri.EscapeDataString("site:forum.mxsimulator.com/viewtopic.php " + text.Trim());
    public static string AlternateSearchUrl(string text) => "https://www.bing.com/search?q=" + Uri.EscapeDataString("site:forum.mxsimulator.com/viewtopic.php " + text.Trim());
    public static string DownloadName(string suggested, string mimeType, string sourceUri)
    {
        var leaf = suggested.Replace('\\', '/').Split('/')[^1];
        if (string.IsNullOrWhiteSpace(leaf) && Uri.TryCreate(sourceUri, UriKind.Absolute, out var uri)) leaf = Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]);
        if (Path.GetExtension(leaf).Length == 0)
        {
            var extension = mimeType.Split(';')[0].Trim().ToLowerInvariant() switch
            {
                "application/zip" or "application/x-zip-compressed" => ".zip",
                "application/vnd.rar" or "application/x-rar-compressed" => ".rar",
                "application/x-7z-compressed" => ".7z",
                _ => ""
            };
            if (extension.Length == 0 && Uri.TryCreate(sourceUri, UriKind.Absolute, out var downloadUri))
            {
                var sourceName = Uri.UnescapeDataString(downloadUri.AbsolutePath.Split('/')[^1]);
                if (IsPackage(sourceName)) { leaf = sourceName; extension = ""; }
            }
            leaf = (string.IsNullOrWhiteSpace(leaf) ? "Track download" : leaf) + extension;
        }
        if (!IsPackage(leaf)) throw new InvalidDataException("Only ZIP, SAF, RAR and 7z mod downloads are supported. Executables and other downloads are blocked.");
        var invalid = Path.GetInvalidFileNameChars();
        leaf = new string(leaf.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        var ext = Path.GetExtension(leaf);
        var stem = Path.GetFileNameWithoutExtension(leaf);
        if (stem.Length > 130) stem = stem[..130];
        if (string.IsNullOrWhiteSpace(stem)) stem = "Track download";
        leaf = stem + ext;
        try { ModInstaller.SafePath(Path.GetTempPath(), leaf); }
        catch (InvalidDataException) { leaf = "mod-" + leaf; ModInstaller.SafePath(Path.GetTempPath(), leaf); }
        return leaf;
    }
}
