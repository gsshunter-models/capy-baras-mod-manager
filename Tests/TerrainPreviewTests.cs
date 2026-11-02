using System.Text;
using MXSimModManager.Core;

public static class TerrainPreviewTests
{
    public static void Run(string root)
    {
        var info = TerrainInfo.Parse("9 2.0 -5 95\n");
        Check(info.ImageSize == 1025 && info.WorldSize == 2048 && info.Minimum == -5 && info.Maximum == 95, "Native dimensions and world spacing must be preserved.");
        foreach (var text in new[] { "9 0 0 1", "99 1 0 1", "9 NaN 0 1", "9 1 10 0", "9 1 0 Infinity", "invalid" }) Throws<InvalidDataException>(() => TerrainInfo.Parse(text));
        var decals = TerrainPreviewData.ParseDecals("[50 50] 0 100 1 @track/dirt.png\n[1 2] NaN 3 1 @invalid.png\n[0 0] 0 -1 1 @invalid.png\n[2 3] 1.57 10 2 @track/my texture.png\n");
        Check(decals.Count == 2 && decals[1].Texture == "@track/my texture.png", "Only finite, positive decal definitions are accepted; resource spaces are retained.");
        var tiles = TerrainPreviewData.TileTextures("name Dirt\ntexture @dirt.ppm\naddtile\nname Grass\ntexture @grass.ppm\naddtile\n", null);
        Check(tiles.SequenceEqual(new[] { "@dirt.ppm", "@grass.ppm" }), "Tile textures keep their native tilemap indices.");
        Check(TerrainPreviewData.TileTextures(null, "dirt.ppm\n1 20 50 grass.ppm\n").SequenceEqual(new[] { "dirt.ppm", "grass.ppm" }), "Legacy texturelist references remain usable.");
        var ppm = TerrainPreviewData.ReadPnm(Encoding.ASCII.GetBytes("P6\n# synthetic image\n2 1\n255\n").Concat(new byte[] { 255, 0, 0, 0, 255, 0 }).ToArray());
        Check(ppm.Width == 2 && ppm.Bgra.SequenceEqual(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }), "PPM RGB is decoded to the expected BGRA pixels.");
        var pgm = TerrainPreviewData.ReadPnm(Encoding.ASCII.GetBytes("P5\n2 1\n65535\n").Concat(new byte[] { 0, 0, 255, 255 }).ToArray());
        Check(pgm.Bgra[0] == 0 && pgm.Bgra[4] == 255, "16-bit PNM samples use network byte order and the full height range.");
        Throws<InvalidDataException>(() => TerrainPreviewData.ReadPnm(Encoding.ASCII.GetBytes("P6\n999999 2\n255\n")));
        Throws<InvalidDataException>(() => TerrainPreviewData.ReadPnm(Encoding.ASCII.GetBytes("P6\n2 2\n255\n")));
        var red = new PreviewPixels(1, 1, [0, 0, 255, 255]); var halfBlue = new PreviewPixels(1, 1, [255, 0, 0, 128]);
        PreviewPixels? Image(string name) => name switch { "red" => red, "blue" => halfBlue, _ => null };
        var baked = TerrainPreviewData.BakeTexture(100, [new(50, 50, 0, 100, 1, "red"), new(50, 50, 0, 50, 1, "blue")], [], null, Image);
        var center = (256 * 512 + 256) * 4;
        Check(baked[center] == 128 && baked[center + 2] == 127 && baked[center + 3] == 255, "Later decals blend over earlier ones using their actual alpha.");
        Check(baked[0] == 0 && baked[2] == 255, "Decals retain their world placement and footprint.");
        var tileTexture = TerrainPreviewData.BakeTexture(100, [], ["red", "missing"], [0, 1, 1, 0], Image);
        Check(tileTexture[2] == 255 && tileTexture[(511 * 4) + 2] == 110, "Tilemap indices select the correct low-resolution surface colors.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Throws<OperationCanceledException>(() => TerrainPreviewData.BakeTexture(100, [], [], null, Image, cancelled.Token));

        var archive = Path.Combine(root, "track.saf"); var payload = Encoding.UTF8.GetBytes("9 2 0 100");
        using (var stream = File.Create(archive)) { stream.Write(Encoding.UTF8.GetBytes(payload.Length + " Track/terrain.hf\n-\n")); stream.Write(payload); }
        var entry = SafArchive.ReadIndex(archive).Single();
        var resource = new PreviewFile(entry.Path, archive, root, entry.Offset, entry.Size, true, "Personal");
        Check(resource.Read(1024).SequenceEqual(payload), "Preview reads only the selected SAF entry at its indexed offset.");
        Throws<InvalidDataException>(() => resource.Read(2));
        Throws<OperationCanceledException>(() => resource.Read(1024, cancelled.Token));
        var package = new ModPackage { Path = archive, TrackIds = ["Track"] };
        var catalog = new PreviewCatalog([resource, resource with { VirtualPath = "Track/terrain.png.scram" }]);
        Check(catalog.Find("Track/terrain.hf", package) == resource, "Archive resources resolve by their complete virtual path.");
        foreach (var unsafePath in new[] { "../regkey", "C:/config", "/etc/config", "Track/../config" }) Check(catalog.Resource(unsafePath, "Track", package) == null, "Unsafe resource paths must never escape the virtual library.");
        Throws<InvalidDataException>(() => catalog.Find("Track/terrain.png", package)!.Read(1024));
        var snapshot = LibraryScanner.Scan(root, "", "", false, collectPreviewFiles: true);
        Check(snapshot.PreviewFiles.Any(f => f.VirtualPath == "Track/terrain.hf" && f.InArchive), "The inventory scan shares resource locations with the preview without extracting archives.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
