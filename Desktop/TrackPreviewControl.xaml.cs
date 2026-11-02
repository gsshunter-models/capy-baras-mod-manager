using MXSimModManager.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace MXSimModManager.Desktop;

public partial class TrackPreviewControl : UserControl
{
    public static readonly DependencyProperty PackageProperty = DependencyProperty.Register(nameof(Package), typeof(ModPackage), typeof(TrackPreviewControl), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty CatalogProperty = DependencyProperty.Register(nameof(Catalog), typeof(PreviewCatalog), typeof(TrackPreviewControl), new PropertyMetadata(null, Changed));
    public ModPackage? Package { get => (ModPackage?)GetValue(PackageProperty); set => SetValue(PackageProperty, value); }
    public PreviewCatalog? Catalog { get => (PreviewCatalog?)GetValue(CatalogProperty); set => SetValue(CatalogProperty, value); }
    private readonly SemaphoreSlim work = new(1);
    private readonly Dictionary<string, TerrainPreviewScene> cache = [];
    private readonly Queue<string> recent = new();
    private CancellationTokenSource? pending;
    private TerrainPreviewScene? scene;
    private bool selecting, manual;
    private int resolutionLevel = 1;
    private double yaw = 40, pitch = 48, zoom = 1.6;
    private Point? drag;
    public TrackPreviewControl()
    {
        InitializeComponent(); Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { if (!manual) RequestPackage(); };
        Unloaded += (_, _) => pending?.Cancel(); ResetCamera();
    }
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var control = (TrackPreviewControl)owner;
        if (e.Property == CatalogProperty) { control.cache.Clear(); control.recent.Clear(); }
        if (control.IsLoaded) control.RequestPackage();
    }
    private void RequestPackage()
    {
        pending?.Cancel(); scene = null; TerrainVisual.Content = null;
        var package = Package; if (package == null || package.TrackIds.Count == 0) { Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible; selecting = true;
        LayoutBox.ItemsSource = package.TrackIds; LayoutBox.SelectedIndex = 0;
        LayoutBox.Visibility = package.TrackIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        selecting = false; ResetCamera(); LoadLayout();
    }
    private void Layout_Changed(object sender, SelectionChangedEventArgs e) { if (!selecting && IsLoaded) { ResetCamera(); LoadLayout(); } }
    private void Resolution_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        resolutionLevel = Math.Clamp((int)Math.Round(e.NewValue), 0, PreviewResolution.Levels.Count - 1);
        var level = PreviewResolution.Levels[resolutionLevel];
        if (ResolutionLabel != null) ResolutionLabel.Text = $"Resolution: {level.Name} · {level.MeshSize} × {level.MeshSize}";
        if (IsLoaded && !selecting) LoadLayout();
    }
    private async void LoadLayout()
    {
        pending?.Cancel(); var request = pending = new CancellationTokenSource(); var token = request.Token;
        var catalog = Catalog; var package = Package; var trackId = LayoutBox.SelectedItem as string;
        if (catalog == null || package == null || trackId == null) { OverlayText.Text = "Preparing track files…"; OverlayText.Visibility = Visibility.Visible; pending = null; request.Dispose(); return; }
        var level = resolutionLevel; var key = package.Path + "|" + trackId + "|" + level;
        TerrainVisual.Content = null; scene = null; OverlayText.Text = "Loading terrain…"; OverlayText.Visibility = Visibility.Visible; PreviewStatus.Text = "Fast preview";
        if (cache.TryGetValue(key, out var found)) { ApplyScene(found); WriteTestReport(found, null, true); pending = null; request.Dispose(); return; }
        bool locked = false;
        try
        {
            await Task.Delay(150, token); await work.WaitAsync(token); locked = true;
            var result = await Task.Run(() => TerrainPreviewLoader.Load(catalog, package, trackId, early => Dispatcher.BeginInvoke(() => { if (!token.IsCancellationRequested && ReferenceEquals(pending, request)) ApplyScene(early); }), token, level), token);
            if (token.IsCancellationRequested || !ReferenceEquals(pending, request)) return;
            while (cache.Count >= 8 && recent.TryDequeue(out var oldest)) cache.Remove(oldest);
            cache[key] = result; recent.Enqueue(key); ApplyScene(result); WriteTestReport(result, null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(pending, request))
            { TerrainVisual.Content = null; OverlayText.Text = ex is InvalidDataException or IOException ? ex.Message : "This track could not be previewed."; OverlayText.Visibility = Visibility.Visible; PreviewStatus.Text = "3D preview unavailable"; WriteTestReport(null, ex.Message); }
        }
        finally { if (locked) work.Release(); if (ReferenceEquals(pending, request)) pending = null; request.Dispose(); }
    }
    private void ApplyScene(TerrainPreviewScene value)
    { scene = value; TerrainVisual.Content = value.Model; PreviewStatus.Text = value.Status; OverlayText.Visibility = Visibility.Collapsed; }
    private void ResetCamera() { yaw = 40; pitch = 48; zoom = 1.6; UpdateCamera(); }
    private void UpdateCamera()
    {
        var a = yaw * Math.PI / 180; var p = pitch * Math.PI / 180;
        Camera.Position = new Point3D(2 * Math.Cos(p) * Math.Sin(a), 2 * Math.Sin(p), 2 * Math.Cos(p) * Math.Cos(a));
        Camera.LookDirection = new Vector3D(-Camera.Position.X, -Camera.Position.Y, -Camera.Position.Z); Camera.Width = zoom;
    }
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetCamera();
    private void View_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton != MouseButton.Left) return; drag = e.GetPosition(ViewSurface); ViewSurface.CaptureMouse(); e.Handled = true; }
    private void View_MouseMove(object sender, MouseEventArgs e)
    {
        if (drag is not Point previous || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(ViewSurface); yaw += (position.X - previous.X) * .5; pitch = Math.Clamp(pitch + (position.Y - previous.Y) * .5, 8, 88); drag = position; UpdateCamera();
    }
    private void View_MouseUp(object sender, MouseButtonEventArgs e) { drag = null; ViewSurface.ReleaseMouseCapture(); }
    private void View_MouseWheel(object sender, MouseWheelEventArgs e) { zoom = Math.Clamp(zoom * (e.Delta > 0 ? .86 : 1.16), .12, 4); UpdateCamera(); e.Handled = true; }
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (scene == null) return;
        var viewer = new TrackPreviewControl { manual = true, selecting = true, Package = Package, Catalog = Catalog, Visibility = Visibility.Visible, Margin = new Thickness(15) };
        viewer.LayoutBox.ItemsSource = new[] { scene.TrackId }; viewer.LayoutBox.SelectedIndex = 0; viewer.ResolutionSlider.Value = resolutionLevel; viewer.selecting = false;
        viewer.ExpandButton.Visibility = Visibility.Collapsed; viewer.ApplyScene(scene);
        new Window { Owner = Window.GetWindow(this), Title = scene.TrackId + " — 3D terrain — " + Brand.Name, Icon = Brand.Icon, Width = 1000, Height = 740, MinWidth = 600, MinHeight = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brand.Background, Content = viewer }.Show();
    }
    private void WriteTestReport(TerrainPreviewScene? scene, string? error, bool cached = false)
    {
        var args = Environment.GetCommandLineArgs(); if (!args.Contains("--preview-report")) return;
        var state = Array.IndexOf(args, "--state"); if (state < 0 || state + 1 >= args.Length) return;
        var folder = args[state + 1];
        try { File.WriteAllText(Path.Combine(folder, "preview-report.json"), JsonSerializer.Serialize(new { scene?.TrackId, scene?.Status, scene?.Milliseconds, scene?.Decals, scene?.Textures, Resolution = resolutionLevel, Cached = cached, Vertices = (scene?.Model.Geometry as MeshGeometry3D)?.Positions.Count, Triangles = (scene?.Model.Geometry as MeshGeometry3D)?.TriangleIndices.Count / 3, Error = error })); }
        catch (IOException) { }
    }
}
