using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MXSimModManager.Core;

public static class DirectDownloadTests
{
    public static void Run(string root) => RunAsync(root).GetAwaiter().GetResult();
    private static async Task RunAsync(string root)
    {
        var cdn = "https://download123.mediafire.com/token/Track.rar?x=1&y=2";
        Check(DirectDownloadClient.MediaFireDownloadLink("<a href='https://ads.example/a.zip'>ad</a><a href='" + cdn.Replace("&", "&amp;") + "' id='downloadButton'>Download</a>").AbsoluteUri == cdn, "Use only the real MediaFire button, decoding HTML entities.");
        Check(DirectDownloadClient.MediaFireDownloadLink("<a id=downloadButton data-scrambled-url='" + Convert.ToBase64String(Encoding.UTF8.GetBytes(cdn)) + "'>Download</a>").AbsoluteUri == cdn, "Decode MediaFire's scrambled public button URL.");
        foreach (var spoof in new[] { "https://mediafire.com.evil.test/a.zip", "javascript:alert(1)", "file:///C:/file.zip", "https://user:pass@mediafire.com/a.zip" })
            Throws<InvalidDataException>(() => DirectDownloadClient.MediaFireDownloadLink("<a id='downloadButton' href='" + spoof + "'>Download</a>"));
        Throws<InvalidDataException>(() => DirectDownloadClient.MediaFireDownloadLink("<form>Sign in</form>"));
        Check(!DirectDownloadClient.CanHandle(new Uri("https://mega.nz.evil.test/login")), "Host lookalikes are not supported providers.");
        Check(!DirectDownloadClient.CanHandle(new Uri("file:///C:/track.zip")), "Local paths cannot become network downloads.");
        var dropbox = DirectDownloadClient.DropboxDownloadLink(new Uri("https://www.dropbox.com/scl/fi/file/Track.zip?rlkey=keep%2Bthis&dl=0&raw=1"));
        Check(dropbox.Query == "?rlkey=keep%2Bthis&dl=1", "Dropbox keeps access parameters while choosing the standard download endpoint.");
        var key = new string('a', 43); var folderKey = new string('b', 22);
        Check(DirectDownloadClient.NormalizeMegaLink(new Uri("https://mega.co.nz/#!abcdefgh!" + key)).AbsoluteUri == "https://mega.nz/file/abcdefgh#" + key, "Legacy MEGA file links normalize to the supported API format.");
        Check(DirectDownloadClient.NormalizeMegaLink(new Uri("https://mega.nz/#F!abcdefgh!" + folderKey + "!ijklmnop")).Fragment.EndsWith("/file/ijklmnop"), "Legacy selected folder files remain selected.");
        Check(DirectDownloadClient.NormalizeMegaLink(new Uri("https://mega.nz/folder/abcdefgh#" + folderKey + "/folder/ijklmnop")).Fragment.EndsWith("/folder/ijklmnop"), "Modern subfolder selections remain selected.");
        foreach (var bad in new[] { "https://mega.nz/file/abcdefgh", "https://mega.nz/file/abcdefgh#short", "https://mega.nz/#P!password", "https://mega.nz.evil.test/file/abcdefgh#" + key }) Throws<InvalidDataException>(() => DirectDownloadClient.NormalizeMegaLink(new Uri(bad)));

        var requests = new List<string>();
        var bytes = Encoding.ASCII.GetBytes("RAR test payload");
        using (var client = new DirectDownloadClient(new FakeHandler(request =>
        {
            requests.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "www.mediafire.com"
                ? Response(Encoding.UTF8.GetBytes("<a id='downloadButton' href='" + cdn + "'>Download</a>"), "text/html")
                : Response(bytes, "application/vnd.rar", "Track.rar");
        })))
        {
            using var batch = await client.ResolveAsync(new Uri("https://www.mediafire.com/file/token/Track.rar/file"), default);
            var destination = Path.Combine(root, "Track.rar");
            await DirectDownloadClient.DownloadAsync(batch.Files.Single(), destination, null, default);
            Check(File.ReadAllBytes(destination).SequenceEqual(bytes), "The landing page resolves and downloads through the ordinary CDN endpoint.");
            Check(requests.SequenceEqual(new[] { "www.mediafire.com", "download123.mediafire.com" }), "One landing request and one streamed package request.");
            await ThrowsAsync<IOException>(() => DirectDownloadClient.DownloadAsync(batch.Files.Single(), destination, null, default));
            Check(File.ReadAllBytes(destination).SequenceEqual(bytes), "A completed destination is never overwritten by a retry.");
        }
        Check(!Directory.GetFiles(root, "*.part").Any(), "No owned temporary files remain after failed download.");
        using (var client = new DirectDownloadClient(new FakeHandler(_ => Response(bytes, "application/octet-stream"))))
        {
            using var batch = await client.ResolveAsync(new Uri("https://www.mediafire.com/file/token/Track.rar/file"), default);
            Check(batch.Files.Single().Name == "Track.rar", "MediaFire may return the package directly without an HTML landing page or a filename header.");
            var destination = Path.Combine(root, "MediaFire-direct.rar");
            await DirectDownloadClient.DownloadAsync(batch.Files.Single(), destination, null, default);
            Check(File.ReadAllBytes(destination).SequenceEqual(bytes), "Direct MediaFire responses must retain their unconsumed stream.");
        }
        using (var client = new DirectDownloadClient(new FakeHandler(_ => Response(bytes, "application/zip", "../../CON.zip"))))
        {
            using var batch = await client.ResolveAsync(new Uri("https://example.test/get/track.zip"), default);
            Check(batch.Files.Single().Name == "mod-CON.zip", "Content-Disposition names use the existing safe filename policy.");
        }
        using (var client = new DirectDownloadClient(new FakeHandler(_ => Response(bytes, "application/zip", "installer.exe"))))
            await ThrowsAsync<InvalidDataException>(() => client.ResolveAsync(new Uri("https://example.test/track.zip"), default));
        using (var client = new DirectDownloadClient(new FakeHandler(_ => Response(bytes, "text/html", "Track.zip"))))
            await ThrowsAsync<InvalidDataException>(() => client.ResolveAsync(new Uri("https://example.test/track.zip"), default));
        foreach (var largeSize in new[] { 40L * 1024 * 1024 * 1024 + 1, 64L * 1024 * 1024 * 1024, long.MaxValue })
        using (var client = new DirectDownloadClient(new FakeHandler(_ => Response(bytes, "application/zip", "Track.zip", largeSize))))
        {
            using var batch = await client.ResolveAsync(new Uri("https://example.test/track.zip"), default);
            Check(batch.Files.Single().Size == largeSize, "Package sizes above 40 GB must resolve without a size cap or truncation.");
            var destination = Path.Combine(root, "large-truncated.zip");
            await ThrowsAsync<InvalidDataException>(() => DirectDownloadClient.DownloadAsync(batch.Files.Single(), destination, null, default));
            Check(!File.Exists(destination) && !Directory.GetFiles(root, "*.part").Any(), "A large advertised package still requires a complete transfer and cleans up on truncation.");
        }
        await FailedDownload(root, "truncated.zip", () => Response(bytes, "application/zip", "Track.zip", bytes.Length + 100), typeof(InvalidDataException));
        await FailedDownload(root, "overlong.zip", () => Response(bytes, "application/zip", "Track.zip", bytes.Length - 1), typeof(InvalidDataException));
        await FailedDownload(root, "html.zip", () => Response(Encoding.UTF8.GetBytes("<!DOCTYPE html><html>host login</html>"), "application/octet-stream", "Track.zip"), typeof(InvalidDataException));
        await FailedDownload(root, "empty.zip", () => Response([], "application/zip", "Track.zip"), typeof(InvalidDataException));
        using (var client = new DirectDownloadClient(new FakeHandler(_ => Response(bytes, "application/zip", "Track.zip"))))
        {
            using var batch = await client.ResolveAsync(new Uri("https://example.test/track.zip"), default);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var destination = Path.Combine(root, "cancelled.zip");
            await ThrowsAsync<OperationCanceledException>(() => DirectDownloadClient.DownloadAsync(batch.Files.Single(), destination, null, cancellation.Token));
            Check(!File.Exists(destination) && !Directory.GetFiles(root, "*.part").Any(), "Cancelled downloads never publish an installable file.");
        }
        using (var client = new DirectDownloadClient(new FakeHandler(_ => Response(bytes, "application/zip", "Track.zip"))))
        {
            using var batch = await client.ResolveAsync(new Uri("https://example.test/track.zip"), default);
            using var cancellation = new CancellationTokenSource();
            var destination = Path.Combine(root, "cancel-at-completion.zip");
            var progress = new SynchronousProgress(_ => cancellation.Cancel());
            await ThrowsAsync<OperationCanceledException>(() => DirectDownloadClient.DownloadAsync(batch.Files.Single(), destination, progress, cancellation.Token));
            Check(!File.Exists(destination) && !Directory.GetFiles(root, "*.part").Any(), "Cancellation before publication cleans up the complete temporary file.");
        }
    }
    private static async Task FailedDownload(string root, string name, Func<HttpResponseMessage> response, Type exception)
    {
        using var client = new DirectDownloadClient(new FakeHandler(_ => response()));
        using var batch = await client.ResolveAsync(new Uri("https://example.test/track.zip"), default);
        var destination = Path.Combine(root, name); Exception? failure = null;
        try { await DirectDownloadClient.DownloadAsync(batch.Files.Single(), destination, null, default); } catch (Exception ex) { failure = ex; }
        Check(failure != null && exception.IsInstanceOfType(failure), "Failed package stream should report " + exception.Name);
        Check(!File.Exists(destination) && !Directory.GetFiles(root, "*.part").Any(), "Failed package streams must leave no completed or partial files.");
    }
    private static HttpResponseMessage Response(byte[] bytes, string mime, string? name = null, long? size = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        response.Content.Headers.ContentLength = size ?? bytes.Length;
        if (name != null) response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = '"' + name + '"' };
        return response;
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { var response = respond(request); response.RequestMessage = request; return Task.FromResult(response); } }
    private sealed class SynchronousProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => report(value); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
