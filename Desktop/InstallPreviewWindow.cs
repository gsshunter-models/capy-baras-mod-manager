using MXSimModManager.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MXSimModManager.Desktop;
public sealed class InstallPreviewWindow : Window
{
    public InstallPreviewWindow(InstallPlan plan, string library, bool gameRunning)
    {
        Title = "Review mod installation — " + Brand.Name; Icon = Brand.Icon; Width = 780; Height = 590; MinWidth = 620; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brand.Background; Foreground = Brand.Text; FontFamily = new FontFamily("Segoe UI");
        var root = new Grid { Margin = new Thickness(25) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var conflicts = plan.Files.Count(x => File.Exists(ModInstaller.SafePath(library, x.RelativePath)));
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = plan.SourceName, FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = $"{plan.Files.Count} files • {ModPackage.FormatSize(plan.Files.Sum(x => x.Size))} • {conflicts} replacements\nDestination: {library}", Margin = new Thickness(0, 12, 0, 10), TextWrapping = TextWrapping.Wrap, Foreground = Brand.Muted });
        var folders = plan.Files.Select(x => x.RelativePath.Replace('\\', '/')).Where(x => x.Contains('/')).Select(x => x.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count > 0) header.Children.Add(new TextBlock { Text = "Folders kept intact: " + string.Join(", ", folders.Take(4)) + (folders.Count > 4 ? $" (+{folders.Count - 4} more)" : ""), TextWrapping = TextWrapping.Wrap, Foreground = Brand.Muted, Margin = new Thickness(0, 0, 0, 10) });
        header.Children.Add(new TextBlock { Text = gameRunning && conflicts > 0 ? "Close MX Simulator before replacing these existing files." : conflicts > 0 ? "Original files will be backed up. Undo restores them." : gameRunning ? "New content may require a game restart to appear." : "New content will be available on the next game launch.", Foreground = gameRunning && conflicts > 0 ? Brushes.Salmon : Brand.Accent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) });
        root.Children.Add(header);
        var list = new ListBox { Background = Brand.Panel, Foreground = Brand.Text, BorderBrush = Brand.Border, FontSize = 12 };
        foreach (var file in plan.Files) list.Items.Add((File.Exists(ModInstaller.SafePath(library, file.RelativePath)) ? "REPLACE   " : "NEW   ") + file.RelativePath);
        Grid.SetRow(list, 1); root.Children.Add(list);
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (plan.Skipped.Count > 0) footer.Children.Add(new TextBlock { Text = $"{plan.Skipped.Count} unsupported/document/executable files skipped. Only mod data will be installed.", TextWrapping = TextWrapping.Wrap, Foreground = Brand.Muted, Margin = new Thickness(0, 0, 0, 12) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(20, 10, 20, 10), Margin = new Thickness(0, 0, 10, 0) };
        var install = new Button { Content = "Install with undo backup", IsEnabled = !(gameRunning && conflicts > 0), Padding = new Thickness(20, 10, 20, 10), Background = Brand.Accent };
        install.Click += (_, _) => { DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(install); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer);
    }
}

