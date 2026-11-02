using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MXSimModManager.Core;
using MXSimModManager.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "CapyTerrainTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var track = Path.Combine(root, "SyntheticTrack"); Directory.CreateDirectory(track);
            File.WriteAllText(Path.Combine(track, "terrain.hf"), "1 2 -10 90");
            var heights = new ushort[25]; for (var i = 0; i < heights.Length; i++) heights[i] = (ushort)(65535 * i / 24);
            var image = BitmapSource.Create(5, 5, 96, 96, PixelFormats.Gray16, null, heights, 10);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var output = File.Create(Path.Combine(track, "terrain.png"))) encoder.Save(output);
            File.WriteAllText(Path.Combine(track, "decals"), "[4 4] 0 8 1 @SyntheticTrack/dirt.ppm\n");
            File.WriteAllBytes(Path.Combine(track, "dirt.ppm"), Encoding.ASCII.GetBytes("P6\n1 1\n255\n").Concat(new byte[] { 150, 70, 20 }).ToArray());
            var snapshot = LibraryScanner.Scan(root, "", "", false, collectPreviewFiles: true); var package = snapshot.Packages.Single(); var catalog = new PreviewCatalog(snapshot.PreviewFiles);
            var scene = Task.Run(() => TerrainPreviewLoader.Load(catalog, package, "SyntheticTrack", null, default)).GetAwaiter().GetResult();
            var mesh = (MeshGeometry3D)scene.Model.Geometry;
            Check(mesh.Positions.Count == 25 && mesh.TriangleIndices.Count / 3 == 32, "Synthetic mesh topology.");
            Check(Math.Abs(scene.HeightRange - 100) < .02, "16-bit PNG retains the complete configured height range.");
            Check(mesh.Positions.First().Y < mesh.Positions.Last().Y && mesh.Normals.All(n => n.Y > 0), "Heights and upward normals remain aligned.");
            Check(scene.Model.IsFrozen && scene.Textures == 1, "The cross-thread scene is frozen and uses the actual terrain texture.");
            var brush = (ImageBrush)((DiffuseMaterial)scene.Model.Material).Brush; var texture = (BitmapSource)brush.ImageSource;
            var pixel = new byte[4]; texture.CopyPixels(new System.Windows.Int32Rect(256, 256, 1, 1), pixel, 4, 0);
            Check(pixel[0] == 20 && pixel[1] == 70 && pixel[2] == 150, "The baked material matches the native PPM's RGB color.");
            Console.WriteLine("PASS Windows terrain decoding, height precision, mesh orientation, texture baking and frozen scene.");
            File.WriteAllText(Path.Combine(track, "terrain.hf"), "8 2 -10 90");
            var large = new ushort[513 * 513]; for (var i = 0; i < large.Length; i++) large[i] = (ushort)(65535L * i / (large.Length - 1));
            var largeImage = BitmapSource.Create(513, 513, 96, 96, PixelFormats.Gray16, null, large, 1026);
            var largeEncoder = new PngBitmapEncoder(); largeEncoder.Frames.Add(BitmapFrame.Create(largeImage)); using (var output = File.Create(Path.Combine(track, "terrain.png"))) largeEncoder.Save(output);
            snapshot = LibraryScanner.Scan(root, "", "", false, collectPreviewFiles: true); catalog = new PreviewCatalog(snapshot.PreviewFiles); package = snapshot.Packages.Single();
            for (var level = 0; level < PreviewResolution.Levels.Count; level++)
            {
                var resolution = PreviewResolution.Levels[level];
                var result = Task.Run(() => TerrainPreviewLoader.Load(catalog, package, "SyntheticTrack", null, default, level)).GetAwaiter().GetResult();
                var detailMesh = (MeshGeometry3D)result.Model.Geometry;
                Check(detailMesh.Positions.Count == resolution.MeshSize * resolution.MeshSize, "Resolution changes the actual mesh sampling.");
                Check(detailMesh.TriangleIndices.Count / 3 == 2 * (resolution.MeshSize - 1) * (resolution.MeshSize - 1), "Selected mesh topology.");
                var detailBrush = (ImageBrush)((DiffuseMaterial)result.Model.Material).Brush;
                Check(((BitmapSource)detailBrush.ImageSource).PixelWidth == resolution.TextureSize, "Resolution changes the baked texture size.");
            }
            Console.WriteLine("PASS Low/Medium/High/Ultra mesh and texture resolutions, including 512 x 512.");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var control = new TrackPreviewControl { Package = package, Catalog = catalog };
            var window = new Window { Content = control, Width = 500, Height = 400, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            try
            {
                window.Show(); var slider = (Slider)control.FindName("ResolutionSlider"); var visual = (ModelVisual3D)control.FindName("TerrainVisual");
                int TextureEdge() => visual.Content is GeometryModel3D model && model.Material is DiffuseMaterial material && material.Brush is ImageBrush imageBrush ? ((BitmapSource)imageBrush.ImageSource).PixelWidth : 0;
                WaitFor(() => TextureEdge() == 512);
                slider.Value = 0; slider.Value = 2; slider.Value = 3;
                WaitFor(() => TextureEdge() == 2048);
                slider.Value = 1; WaitFor(() => TextureEdge() == 512);
                control.Package = null; Check(control.Visibility == Visibility.Collapsed, "Non-track selection hides the preview.");
                control.Package = package; WaitFor(() => TextureEdge() == 512);
                Console.WriteLine("PASS Automatic selection, resolution slider, stale-load cancellation and cached reselection.");
            }
            finally { window.Close(); }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            var full = Path.GetFullPath(root); var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("CapyTerrainTests-")) throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(full, true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void WaitFor(Func<bool> ready)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Timed out waiting for the preview control.");
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
    }
}
