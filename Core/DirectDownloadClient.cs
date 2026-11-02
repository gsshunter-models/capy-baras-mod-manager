using CG.Web.MegaApiClient;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MXSimModManager.Core;

public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes);

public sealed class ResolvedDownloadFile
{
    public required string Name { get; init; }
    public required string Host { get; init; }
    public long Size { get; init; }
    public string DisplayName => Name + (Size >= 0 ? " — " + ModPackage.FormatSize(Size) : "");
    internal Func<CancellationToken, Task<Stream>> Open { get; init; } = null!;
}

public sealed class ResolvedDownloadBatch(IReadOnlyList<ResolvedDownloadFile> files, Action cleanup) : IDisposable
{
    public IReadOnlyList<ResolvedDownloadFile> Files { get; } = files;
    public void Dispose() => cleanup();
}

/// <summary>Normal anonymous public downloads, without a host's landing-page UI.</summary>
public sealed class DirectDownloadClient : IDisposable
{
    private readonly HttpClient http;
    public DirectDownloadClient(HttpMessageHandler? handler = null)
    {
        http = handler == null
            ? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30), AllowAutoRedirect = true, MaxAutomaticRedirections = 8, UseCookies = false })
            : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MXSimModManager/1.2");
    }
    private static bool Host(Uri uri, string domain) => uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    public static bool IsMega(Uri uri) => Host(uri, "mega.nz") || Host(uri, "mega.co.nz");
    public static bool CanHandle(Uri uri) => uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
        (Host(uri, "mediafire.com") && (uri.AbsolutePath.StartsWith("/file/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/download/", StringComparison.OrdinalIgnoreCase) || uri.Host.StartsWith("download", StringComparison.OrdinalIgnoreCase) || uri.Query.Length > 1)
        || IsMega(uri) && (uri.AbsolutePath.StartsWith("/file/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/folder/", StringComparison.OrdinalIgnoreCase) || uri.Fragment.StartsWith("#!") || uri.Fragment.StartsWith("#F!") || uri.Fragment.StartsWith("#P!"))
        || Host(uri, "dropbox.com") && (uri.AbsolutePath.StartsWith("/s/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/scl/", StringComparison.OrdinalIgnoreCase))
        || Host(uri, "dropboxusercontent.com") || ForumDownloadPolicy.IsPackage(Uri.UnescapeDataString(uri.AbsolutePath)));

    public static Uri NormalizeMegaLink(Uri uri)
    {
        if (!IsMega(uri)) throw new InvalidDataException("This is not a MEGA public link.");
        var value = uri.AbsolutePath + uri.Fragment;
        var legacy = Regex.Match(uri.Fragment, @"^#(?<folder>F)?!(?<id>[A-Za-z0-9_-]+)!(?<key>[A-Za-z0-9_-]+)(?:!(?<selected>[A-Za-z0-9_-]+))?$", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (legacy.Success) value = "/" + (legacy.Groups["folder"].Success ? "folder" : "file") + "/" + legacy.Groups["id"].Value + "#" + legacy.Groups["key"].Value + (legacy.Groups["selected"].Success ? "/file/" + legacy.Groups["selected"].Value : "");
        var match = Regex.Match(value, @"^/(?<kind>file|folder)/(?<id>[A-Za-z0-9_-]{8})#(?<key>[A-Za-z0-9_-]+)(?:/(?<selection>file|folder)/(?<selected>[A-Za-z0-9_-]{8}))?/?$", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success || match.Groups["key"].Value.Length != (match.Groups["kind"].Value == "file" ? 43 : 22))
            throw new InvalidDataException("Use the complete public MEGA file or folder link, including its # decryption key. Password-protected links must be opened on the download page.");
        return new Uri("https://mega.nz" + value);
    }

    public static Uri DropboxDownloadLink(Uri uri)
    {
        if (!Host(uri, "dropbox.com")) return uri;
        var builder = new UriBuilder(uri) { Fragment = "" };
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !new[] { "dl", "raw" }.Contains(Uri.UnescapeDataString(p.Split('=')[0]), StringComparer.OrdinalIgnoreCase)).ToList();
        query.Add("dl=1"); builder.Query = string.Join("&", query); return builder.Uri;
    }

    public static Uri MediaFireDownloadLink(string html)
    {
        foreach (Match tag in Regex.Matches(html, @"<a\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
        {
            var attrs = Regex.Matches(tag.Value, "(?<name>[\\w-]+)\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)'|(?<v>[^\\s>]+))", RegexOptions.None, TimeSpan.FromSeconds(1))
                .Cast<Match>().GroupBy(m => m.Groups["name"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.Last().Groups["v"].Value), StringComparer.OrdinalIgnoreCase);
            if (!attrs.TryGetValue("id", out var id) || !id.Equals("downloadButton", StringComparison.OrdinalIgnoreCase)) continue;
            string? link = null;
            if (attrs.TryGetValue("data-scrambled-url", out var scrambled))
            {
                try { link = Encoding.UTF8.GetString(Convert.FromBase64String(scrambled)); } catch (FormatException) { }
            }
            if (link == null && attrs.TryGetValue("href", out var href)) link = href;
            if (Uri.TryCreate(link, UriKind.Absolute, out var result) && result.Scheme is "http" or "https" && Host(result, "mediafire.com") && string.IsNullOrEmpty(result.UserInfo)) return result;
        }
        throw new InvalidDataException("MediaFire did not provide a public download button. Use Open download page to check whether the link is unavailable or needs an extra step.");
    }

    public async Task<ResolvedDownloadBatch> ResolveAsync(Uri link, CancellationToken token)
    {
        if (!CanHandle(link)) throw new InvalidDataException("Paste a public MediaFire, MEGA, Dropbox, or direct ZIP / SAF / RAR / 7z link. Other hosts can be downloaded in the browser.");
        if (IsMega(link)) return await Task.Run(() => ResolveMega(NormalizeMegaLink(link), token), token).ConfigureAwait(false);
        if (Host(link, "mediafire.com") && !link.Host.StartsWith("download", StringComparison.OrdinalIgnoreCase))
        {
            var page = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                page.EnsureSuccessStatusCode();
                var mime = page.Content.Headers.ContentType?.MediaType ?? "";
                // Some public MediaFire URLs already redirect to the package for non-browser clients.
                if (!mime.Contains("html", StringComparison.OrdinalIgnoreCase) && !mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return HttpBatch(page, link);
                if (page.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("The MediaFire landing page is too large to read.");
                await using var input = await page.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var output = new MemoryStream(); var buffer = new byte[32 * 1024]; int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                { if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("The MediaFire landing page is too large to read."); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
                link = MediaFireDownloadLink(Encoding.UTF8.GetString(output.ToArray()));
            }
            catch { page.Dispose(); throw; }
            page.Dispose();
        }
        link = DropboxDownloadLink(link);
        var response = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return HttpBatch(response, link);
    }
    private static ResolvedDownloadBatch HttpBatch(HttpResponseMessage response, Uri link)
    {
        try
        {
            response.EnsureSuccessStatusCode();
            var mime = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mime.Contains("html", StringComparison.OrdinalIgnoreCase) || mime.Contains("json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The host returned a download page instead of a package. Use Open download page to continue.");
            var size = response.Content.Headers.ContentLength ?? -1;
            var disposition = response.Content.Headers.ContentDisposition;
            var finalUri = response.RequestMessage?.RequestUri ?? link;
            var suggested = disposition?.FileNameStar ?? disposition?.FileName;
            if (string.IsNullOrWhiteSpace(suggested) && Host(link, "mediafire.com")) suggested = link.Segments.Select(Uri.UnescapeDataString).Select(s => s.TrimEnd('/')).LastOrDefault(ForumDownloadPolicy.IsPackage);
            var name = ForumDownloadPolicy.DownloadName((suggested ?? "").Trim('"'), mime, finalUri.AbsoluteUri);
            return new ResolvedDownloadBatch([new ResolvedDownloadFile { Name = name, Host = finalUri.Host, Size = size, Open = ct => response.Content.ReadAsStreamAsync(ct) }], response.Dispose);
        }
        catch { response.Dispose(); throw; }
    }
    private static ResolvedDownloadBatch ResolveMega(Uri link, CancellationToken token)
    {
        bool Retry(int attempt, out TimeSpan delay) { token.ThrowIfCancellationRequested(); delay = TimeSpan.FromMilliseconds(400 * attempt); return attempt <= 3; }
        var client = new MegaApiClient(new Options(computeApiRequestRetryWaitDelay: Retry));
        try
        {
            token.ThrowIfCancellationRequested(); client.LoginAnonymous(); token.ThrowIfCancellationRequested();
            var isFolder = link.AbsolutePath.StartsWith("/folder/", StringComparison.Ordinal);
            var nodes = isFolder ? client.GetNodesFromLink(link).ToList() : [client.GetNodeFromLink(link)];
            token.ThrowIfCancellationRequested();
            var suffix = link.Fragment.Split('/');
            var selected = suffix.Length >= 3 ? suffix[2] : null;
            var candidates = nodes.Where(n => n.Type == NodeType.File && ForumDownloadPolicy.IsPackage(n.Name));
            if (selected != null && suffix[1] == "file") candidates = candidates.Where(n => n.Id == selected);
            if (selected != null && suffix[1] == "folder")
            {
                var descendants = new HashSet<string> { selected };
                bool added;
                do { added = false; foreach (var node in nodes) if (descendants.Contains(node.ParentId) && descendants.Add(node.Id)) added = true; } while (added);
                candidates = candidates.Where(n => descendants.Contains(n.Id));
            }
            var files = candidates.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Take(1001).Select(node =>
                new ResolvedDownloadFile { Name = ForumDownloadPolicy.DownloadName(node.Name, "", ""), Host = "mega.nz", Size = node.Size,
                    Open = ct => Task.Run(() => { ct.ThrowIfCancellationRequested(); return client.Download(node, ct); }, ct) }).ToList();
            if (files.Count == 0) throw new InvalidDataException("This MEGA link contains no ZIP, SAF, RAR or 7z packages.");
            if (files.Count > 1000) throw new InvalidDataException("This folder has more than 1,000 packages. Paste a link to a specific file or subfolder.");
            return new ResolvedDownloadBatch(files, () => client.Logout());
        }
        catch { if (client.IsLoggedIn) client.Logout(); throw; }
    }
    public static async Task DownloadAsync(ResolvedDownloadFile file, string destination, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        var created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(destination)) throw new IOException("A completed file already exists at the download destination.");
            await using var input = await file.Open(token).ConfigureAwait(false);
            using var cancelRead = token.Register(() => { try { input.Dispose(); } catch { } });
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                created = true; var buffer = new byte[128 * 1024]; long received = 0; var watch = Stopwatch.StartNew();
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    received = checked(received + count);
                    if (file.Size >= 0 && received > file.Size) throw new InvalidDataException("The host sent more bytes than the expected file size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (watch.ElapsedMilliseconds >= 150) { progress?.Report(new(received, Math.Max(0, file.Size))); watch.Restart(); }
                }
                token.ThrowIfCancellationRequested();
                if (file.Size >= 0 && received != file.Size) throw new InvalidDataException("The download ended before the complete package arrived. Try the download again.");
                if (received == 0) throw new InvalidDataException("The host returned an empty file.");
                await output.FlushAsync(token).ConfigureAwait(false);
                progress?.Report(new(received, received));
            }
            await using (var verify = File.OpenRead(partial))
            {
                var prefix = new byte[256]; var length = await verify.ReadAsync(prefix, token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(prefix, 0, length).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                if (text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded file is a web page instead of a package. Use Open download page to continue.");
            }
            token.ThrowIfCancellationRequested(); File.Move(partial, destination); created = false;
        }
        finally { if (created && File.Exists(partial)) File.Delete(partial); }
    }
    public void Dispose() => http.Dispose();
}
