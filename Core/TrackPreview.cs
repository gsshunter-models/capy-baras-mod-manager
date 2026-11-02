using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MXSimModManager.Core;

public sealed record PreviewFile(string VirtualPath, string FilePath, string Root, long Offset, long Size, bool InArchive, string Scope)
{
    public static bool Relevant(string path)
    {
        var leaf = path.Replace('\\', '/').Split('/')[^1].ToLowerInvariant();
        foreach (var suffix in new[] { ".gz", ".scram" }) if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^suffix.Length];
        return leaf is "terrain.hf" or "tilemap" or "tileinfo" or "texturelist" or "decals" || new[] { ".png", ".ppm", ".pgm", ".jpg", ".jpeg" }.Contains(Path.GetExtension(leaf), StringComparer.OrdinalIgnoreCase);
    }
    public byte[] Read(int maximumBytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (VirtualPath.EndsWith(".scram", StringComparison.OrdinalIgnoreCase) || VirtualPath.EndsWith(".scram.gz", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This track resource is protected and cannot be previewed.");
        if (Size < 0 || Size > maximumBytes) throw new InvalidDataException("This resource exceeds the fast preview size limit.");
        ModInstaller.EnsureNoLinks(Root, FilePath);
        using var source = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (Offset < 0 || Size > source.Length - Offset) throw new InvalidDataException("The track changed while its preview was loading. Refresh the library.");
        source.Position = Offset; var bytes = new byte[(int)Size]; var read = 0;
        while (read < bytes.Length) { token.ThrowIfCancellationRequested(); var count = source.Read(bytes, read, Math.Min(65536, bytes.Length - read)); if (count == 0) throw new EndOfStreamException(); read += count; }
        if (!VirtualPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) return bytes;
        using var zipped = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream(); var buffer = new byte[32768]; int n;
        while ((n = zipped.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); if (output.Length + n > maximumBytes) throw new InvalidDataException("Compressed preview resource is too large."); output.Write(buffer, 0, n); }
        return output.ToArray();
    }
}

public sealed class PreviewCatalog
{
    private readonly Dictionary<string, List<PreviewFile>> files = new(StringComparer.OrdinalIgnoreCase);
    public PreviewCatalog(IEnumerable<PreviewFile> entries)
    {
        foreach (var file in entries)
        {
            if (!ValidPath(file.VirtualPath)) continue;
            var key = file.VirtualPath.Replace('\\', '/');
            if (!files.TryGetValue(key, out var values)) files[key] = values = [];
            values.Add(file);
        }
    }
    public static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.StartsWith('\\') && !path.Contains(':') && !path.Replace('\\', '/').Split('/').Any(p => p is "" or "." or "..");
    public PreviewFile? Find(string path, ModPackage package)
    {
        path = path.Trim().Trim('"').TrimStart('@').Replace('\\', '/');
        if (!ValidPath(path)) return null;
        foreach (var suffix in new[] { "", ".gz", ".scram", ".scram.gz" })
            if (files.TryGetValue(path + suffix, out var matches))
            {
                var selected = matches.FirstOrDefault(f => BelongsTo(f, package));
                if (selected != null) return selected;
                var shared = matches.Where(f => f.Scope != "Disabled").OrderByDescending(f => f.Scope == "Personal").ThenBy(f => f.InArchive).ThenByDescending(f => f.FilePath, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (shared != null) return shared;
            }
        return null;
    }
    public PreviewFile? Resource(string reference, string trackId, ModPackage package)
    {
        reference = reference.Trim().Trim('"');
        if (reference.StartsWith('@')) return Find(reference, package);
        if (!ValidPath(reference)) return null;
        return Find(trackId + "/" + reference, package) ?? Find(reference, package);
    }
    private static bool BelongsTo(PreviewFile file, ModPackage package) => file.FilePath.Equals(package.Path, StringComparison.OrdinalIgnoreCase) || file.FilePath.StartsWith(package.Path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed record TerrainInfo(int Dimension, double Spacing, double Minimum, double Maximum)
{
    public int ImageSize => (1 << (Dimension + 1)) + 1;
    public double WorldSize => (ImageSize - 1) * Spacing;
    public static TerrainInfo Parse(string text)
    {
        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4 || !int.TryParse(fields[0], out var dimension) || dimension is < 1 or > 13) throw new InvalidDataException("The terrain.hf dimensions are not supported.");
        var values = fields.Skip(1).Take(3).Select(ParseNumber).ToArray();
        if (values[0] <= 0 || values[0] > 10000 || values[2] < values[1] || values.Any(v => Math.Abs(v) > 10000000)) throw new InvalidDataException("The track has invalid terrain dimensions or heights.");
        return new(dimension, values[0], values[1], values[2]);
    }
    internal static double ParseNumber(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : throw new InvalidDataException("Invalid terrain number.");
}

public sealed record TerrainDecal(double X, double Z, double Rotation, double Size, double Aspect, string Texture);
public sealed record PreviewPixels(int Width, int Height, byte[] Bgra);
public sealed record PreviewResolution(string Name, int MeshSize, int TextureSize)
{
    public static IReadOnlyList<PreviewResolution> Levels { get; } = Array.AsReadOnly(new[]
    {
        new PreviewResolution("Low", 65, 256), new PreviewResolution("Medium", 129, 512), new PreviewResolution("High", 257, 1024), new PreviewResolution("Ultra", 512, 2048)
    });
}

public static partial class TerrainPreviewData
{
    public const int MeshSize = 129;
    public const int TextureSize = 512;
    public const int MaximumDecals = 60000;
    [GeneratedRegex(@"^\s*\[\s*(?<x>[-+\d.eE]+)\s+(?<z>[-+\d.eE]+)\s*\]\s+(?<r>[-+\d.eE]+)\s+(?<s>[-+\d.eE]+)\s+(?<a>[-+\d.eE]+)\s+(?<t>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DecalPattern();
    public static List<TerrainDecal> ParseDecals(string text, CancellationToken token = default)
    {
        var result = new List<TerrainDecal>(); using var lines = new StringReader(text); string? line;
        while ((line = lines.ReadLine()) != null)
        {
            token.ThrowIfCancellationRequested(); if (result.Count >= MaximumDecals) break;
            if (line.Length > 8192) continue; var m = DecalPattern().Match(line); if (!m.Success) continue;
            try
            {
                var x = TerrainInfo.ParseNumber(m.Groups["x"].Value); var z = TerrainInfo.ParseNumber(m.Groups["z"].Value);
                var rotation = TerrainInfo.ParseNumber(m.Groups["r"].Value); var size = TerrainInfo.ParseNumber(m.Groups["s"].Value); var aspect = TerrainInfo.ParseNumber(m.Groups["a"].Value);
                if (size is <= 0 or > 10000000 || aspect is < .001 or > 1000 || Math.Abs(x) > 1e8 || Math.Abs(z) > 1e8) continue;
                result.Add(new(x, z, rotation, size, aspect, m.Groups["t"].Value.Trim('"')));
            }
            catch (InvalidDataException) { }
        }
        return result;
    }
    public static List<string> TileTextures(string? tileInfo, string? textureList)
    {
        var tiles = new List<string>();
        if (!string.IsNullOrWhiteSpace(tileInfo))
        {
            string texture = ""; using var lines = new StringReader(tileInfo); string? line;
            while ((line = lines.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("texture ", StringComparison.OrdinalIgnoreCase)) texture = line[8..].Trim().Trim('"');
                else if (line.Equals("addtile", StringComparison.OrdinalIgnoreCase)) { tiles.Add(texture); texture = ""; }
                if (tiles.Count >= 256) break;
            }
            if (texture.Length > 0 && tiles.Count < 256) tiles.Add(texture);
        }
        if (tiles.Count == 0 && textureList != null)
            foreach (var line in textureList.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(256))
                tiles.Add(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Last());
        return tiles;
    }
    public static byte[] BakeTexture(double worldSize, IReadOnlyList<TerrainDecal> decals, IReadOnlyList<string> tiles, byte[]? tilemap, Func<string, PreviewPixels?> load, CancellationToken token = default, int textureSize = TextureSize)
    {
        if (!double.IsFinite(worldSize) || worldSize <= 0) throw new InvalidDataException("Invalid terrain size.");
        if (textureSize is < 64 or > 2048) throw new InvalidDataException("Unsupported preview texture resolution.");
        var edge = textureSize; var output = new byte[edge * edge * 4];
        byte[] fallback = [64, 89, 110];
        var tileEdge = tilemap == null ? 0 : (int)Math.Sqrt(tilemap.Length);
        if (tileEdge * tileEdge != tilemap?.Length) tileEdge = 0;
        var tileColors = tiles.Select(t => Average(load(t))).ToArray();
        for (var y = 0; y < edge; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < edge; x++)
            {
                var tile = tileEdge > 0 ? tilemap![Math.Min(tileEdge - 1, y * tileEdge / edge) * tileEdge + Math.Min(tileEdge - 1, x * tileEdge / edge)] : 0;
                var color = tile < tileColors.Length ? tileColors[tile] : fallback;
                var i = (y * edge + x) * 4; output[i] = color[0]; output[i + 1] = color[1]; output[i + 2] = color[2]; output[i + 3] = 255;
            }
        }
        foreach (var decal in decals)
        {
            token.ThrowIfCancellationRequested(); var image = load(decal.Texture); if (image == null) continue;
            var width = decal.Size; var height = decal.Size / decal.Aspect;
            var c = Math.Cos(decal.Rotation); var s = Math.Sin(decal.Rotation);
            var rx = (Math.Abs(c) * width + Math.Abs(s) * height) / 2; var rz = (Math.Abs(s) * width + Math.Abs(c) * height) / 2;
            var x0 = (int)Math.Clamp(Math.Floor((decal.X - rx) / worldSize * edge), 0, edge); var x1 = (int)Math.Clamp(Math.Ceiling((decal.X + rx) / worldSize * edge), 0, edge);
            var z0 = (int)Math.Clamp(Math.Floor((decal.Z - rz) / worldSize * edge), 0, edge); var z1 = (int)Math.Clamp(Math.Ceiling((decal.Z + rz) / worldSize * edge), 0, edge);
            for (var y = z0; y < z1; y++)
            {
                if ((y & 15) == 0) token.ThrowIfCancellationRequested();
                var dz = (y + .5) * worldSize / edge - decal.Z;
                for (var x = x0; x < x1; x++)
                {
                    var dx = (x + .5) * worldSize / edge - decal.X;
                    var u = (c * dx + s * dz) / width + .5; var v = (-s * dx + c * dz) / height + .5;
                    if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;
                    var source = ((int)(v * image.Height) * image.Width + (int)(u * image.Width)) * 4;
                    var alpha = image.Bgra[source + 3]; if (alpha == 0) continue; var target = (y * edge + x) * 4;
                    for (var channel = 0; channel < 3; channel++) output[target + channel] = (byte)((image.Bgra[source + channel] * alpha + output[target + channel] * (255 - alpha) + 127) / 255);
                }
            }
        }
        return output;
    }
    private static byte[] Average(PreviewPixels? image)
    {
        if (image == null) return [64, 89, 110];
        long b = 0, g = 0, r = 0, total = 0;
        for (var i = 0; i < image.Bgra.Length; i += 4) { var a = image.Bgra[i + 3]; b += image.Bgra[i] * a; g += image.Bgra[i + 1] * a; r += image.Bgra[i + 2] * a; total += a; }
        return total == 0 ? [64, 89, 110] : [(byte)(b / total), (byte)(g / total), (byte)(r / total)];
    }
    public static PreviewPixels ReadPnm(byte[] bytes, int maximumEdge = 128)
    {
        var offset = 0;
        string Next()
        {
            while (offset < bytes.Length)
            {
                if (bytes[offset] == '#') { while (offset < bytes.Length && bytes[offset] != '\n') offset++; }
                else if (char.IsWhiteSpace((char)bytes[offset])) offset++; else break;
            }
            var start = offset; while (offset < bytes.Length && !char.IsWhiteSpace((char)bytes[offset]) && bytes[offset] != '#') offset++;
            if (offset == start || offset - start > 32) throw new InvalidDataException("Invalid PNM image header.");
            return Encoding.ASCII.GetString(bytes, start, offset - start);
        }
        var format = Next(); if (format is not ("P6" or "P5")) throw new InvalidDataException("Only binary PPM and PGM images are supported.");
        if (!int.TryParse(Next(), out var width) || !int.TryParse(Next(), out var height) || !int.TryParse(Next(), out var maximum) || width is < 1 or > 16385 || height is < 1 or > 16385 || maximum is < 1 or > 65535) throw new InvalidDataException("Invalid PNM image dimensions.");
        if (offset >= bytes.Length || !char.IsWhiteSpace((char)bytes[offset])) throw new InvalidDataException("Invalid PNM image header.");
        if (bytes[offset++] == '\r' && offset < bytes.Length && bytes[offset] == '\n') offset++;
        var channels = format == "P6" ? 3 : 1; var sampleBytes = maximum > 255 ? 2 : 1;
        if ((long)width * height * channels * sampleBytes > bytes.Length - offset) throw new InvalidDataException("Incomplete PNM image.");
        var scale = Math.Min(1d, maximumEdge / (double)Math.Max(width, height)); var w = Math.Max(1, (int)(width * scale)); var h = Math.Max(1, (int)(height * scale)); var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var source = offset + ((y * height / h) * width + x * width / w) * channels * sampleBytes; var target = (y * w + x) * 4;
            for (var c = 0; c < 3; c++) { var p = source + (channels == 1 ? 0 : 2 - c) * sampleBytes; var v = sampleBytes == 1 ? bytes[p] : (bytes[p] << 8) | bytes[p + 1]; output[target + c] = (byte)Math.Clamp(v * 255 / maximum, 0, 255); }
            output[target + 3] = 255;
        }
        return new(w, h, output);
    }
}
