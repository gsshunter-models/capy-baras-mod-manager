using MXSimModManager.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace MXSimModManager.Desktop;

public sealed record TerrainPreviewScene(GeometryModel3D Model, string TrackId, string Status, double HeightRange, int Decals, int Textures, long Milliseconds);

public static class TerrainPreviewLoader
{
    public static TerrainPreviewScene Load(PreviewCatalog catalog, ModPackage package, string trackId, Action<TerrainPreviewScene>? geometryReady, CancellationToken token, int resolutionLevel = 1)
    {
        if (resolutionLevel < 0 || resolutionLevel >= PreviewResolution.Levels.Count) throw new ArgumentOutOfRangeException(nameof(resolutionLevel));
        var resolution = PreviewResolution.Levels[resolutionLevel];
        var watch = Stopwatch.StartNew();
        var configFile = catalog.Find(trackId + "/terrain.hf", package) ?? throw new InvalidDataException("This track's terrain.hf file is unavailable.");
        var info = TerrainInfo.Parse(Encoding.UTF8.GetString(configFile.Read(4096, token)).TrimStart('\uFEFF'));
        var heightFile = catalog.Find(trackId + "/terrain.png", package) ?? throw new InvalidDataException("This track's heightmap is missing or protected.");
        var heightBytes = heightFile.Read(64 * 1024 * 1024, token);
        var (sourceWidth, sourceHeight) = PngSize(heightBytes);
        if (sourceWidth != info.ImageSize || sourceHeight != info.ImageSize) throw new InvalidDataException("The heightmap dimensions do not match terrain.hf.");
        var heightImage = Decode(heightBytes, resolution.MeshSize);
        var grayscale = new FormatConvertedBitmap(heightImage, PixelFormats.Gray16, null, 0);
        int size = heightImage.PixelWidth;
        if (size < 2 || size != heightImage.PixelHeight) throw new InvalidDataException("The track heightmap is not square.");
        var gray = new byte[size * size * 2]; grayscale.CopyPixels(gray, size * 2, 0);
        var values = new double[size * size]; double low = double.MaxValue, high = double.MinValue;
        for (var i = 0; i < values.Length; i++)
        {
            var h = info.Minimum + (gray[i * 2] | gray[i * 2 + 1] << 8) / 65535d * (info.Maximum - info.Minimum);
            values[i] = h; low = Math.Min(low, h); high = Math.Max(high, h);
        }
        var mesh = new MeshGeometry3D(); var middle = (low + high) / 2;
        var step = 1d / (size - 1);
        for (var z = 0; z < size; z++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < size; x++)
            {
                var i = z * size + x;
                mesh.Positions.Add(new Point3D(x * step - .5, (values[i] - middle) / info.WorldSize, z * step - .5));
                mesh.TextureCoordinates.Add(new Point(x * step, z * step));
                var dx = (values[z * size + Math.Min(size - 1, x + 1)] - values[z * size + Math.Max(0, x - 1)]) / info.WorldSize;
                var dz = (values[Math.Min(size - 1, z + 1) * size + x] - values[Math.Max(0, z - 1) * size + x]) / info.WorldSize;
                var normal = new Vector3D(-dx / (step * (x is 0 || x == size - 1 ? 1 : 2)), 1, -dz / (step * (z is 0 || z == size - 1 ? 1 : 2))); normal.Normalize(); mesh.Normals.Add(normal);
                if (x == size - 1 || z == size - 1) continue;
                foreach (var index in new[] { i, i + size, i + 1, i + 1, i + size, i + size + 1 }) mesh.TriangleIndices.Add(index);
            }
        }
        mesh.Freeze();
        var ground = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(136, 111, 76))); ground.Freeze();
        var initialModel = new GeometryModel3D(mesh, ground) { BackMaterial = ground }; initialModel.Freeze();
        geometryReady?.Invoke(new(initialModel, trackId, "Terrain ready · loading textures…", high - low, 0, 0, watch.ElapsedMilliseconds));
        token.ThrowIfCancellationRequested();

        string? Text(string name, int maximum)
        {
            var resource = catalog.Find(trackId + "/" + name, package);
            try { return resource == null ? null : Encoding.UTF8.GetString(resource.Read(maximum, token)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
        }
        var decals = TerrainPreviewData.ParseDecals(Text("decals", 16 * 1024 * 1024) ?? "", token);
        var tiles = TerrainPreviewData.TileTextures(Text("tileinfo", 1024 * 1024), Text("texturelist", 256 * 1024));
        byte[]? tilemap = null;
        try { tilemap = catalog.Find(trackId + "/tilemap", package)?.Read(16 * 1024 * 1024, token); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        var images = new Dictionary<string, PreviewPixels?>(StringComparer.OrdinalIgnoreCase); int loaded = 0, missing = 0;
        PreviewPixels? Image(string reference)
        {
            token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(reference)) return null;
            if (images.TryGetValue(reference, out var cached)) return cached;
            if (images.Count >= 96) { missing++; return null; }
            PreviewPixels? result = null;
            try
            {
                var resource = catalog.Resource(reference, trackId, package);
                if (resource != null)
                {
                    var bytes = resource.Read(32 * 1024 * 1024, token);
                    if (bytes.Length > 2 && bytes[0] == 'P' && bytes[1] is (byte)'6' or (byte)'5') result = TerrainPreviewData.ReadPnm(bytes);
                    else
                    {
                        var bitmap = Decode(bytes, 128); var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        result = new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            images[reference] = result; if (result != null) loaded++; else missing++; return result;
        }
        var baked = TerrainPreviewData.BakeTexture(info.WorldSize, decals, tiles, tilemap, Image, token, resolution.TextureSize);
        token.ThrowIfCancellationRequested();
        var texture = BitmapSource.Create(resolution.TextureSize, resolution.TextureSize, 96, 96, PixelFormats.Bgra32, null, baked, resolution.TextureSize * 4); texture.Freeze();
        var brush = new ImageBrush(texture) { Stretch = Stretch.Fill }; brush.Freeze(); var material = new DiffuseMaterial(brush); material.Freeze();
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material }; model.Freeze();
        return new(model, trackId, missing > 0 ? "Low-detail preview · some textures unavailable" : loaded > 0 ? "Low-detail terrain + textures" : "Low-detail terrain · textures unavailable", high - low, decals.Count, loaded, watch.ElapsedMilliseconds);
    }
    private static BitmapSource Decode(byte[] bytes, int maximumEdge)
    {
        using var input = new MemoryStream(bytes, false);
        var probe = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = probe.Frames[0]; int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width < 1 || height < 1 || width > 32769 || height > 32769 || (long)width * height > 270000000) throw new InvalidDataException("This image is too large for a fast preview.");
        input.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        if (width >= height) image.DecodePixelWidth = Math.Min(width, maximumEdge); else image.DecodePixelHeight = Math.Min(height, maximumEdge);
        image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
    }
    private static (int Width, int Height) PngSize(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("This track has no readable PNG heightmap.");
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }
}
