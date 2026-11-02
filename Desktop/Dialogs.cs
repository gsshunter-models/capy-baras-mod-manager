using MXSimModManager.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MXSimModManager.Desktop;
public static class Dialogs
{
    private static Window Shell(Window owner, string title, string message, out StackPanel content)
    {
        var window = new Window { Owner = owner, Title = title + " — " + Brand.Name, Icon = Brand.Icon, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brand.Panel, Foreground = Brand.Text, FontFamily = new FontFamily("Segoe UI") };
        content = new StackPanel { Margin = new Thickness(24) }; window.Content = content;
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        return window;
    }
    private static void Buttons(Window window, StackPanel content, string label = "Save")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        row.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0) });
        var save = new Button { Content = label, IsDefault = true, Padding = new Thickness(22, 8, 22, 8), Background = Brand.Accent, Foreground = Brand.AccentText }; save.Click += (_, _) => window.DialogResult = true;
        row.Children.Add(save); content.Children.Add(row);
    }
    public static string? Prompt(Window owner, string title, string message, string initial, int maxLength = 80)
    {
        var window = Shell(owner, title, message, out var content);
        var input = new TextBox { Text = initial, Padding = new Thickness(10), MaxLength = maxLength }; content.Children.Add(input); Buttons(window, content);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }
    public static ModGroup? ChooseGroup(Window owner, IEnumerable<ModGroup> groups)
    {
        var window = Shell(owner, "Add to group", "Choose a group for the selected packages. A multi-track SAF stays together as one package.", out var content);
        var list = new ComboBox { ItemsSource = groups.OrderBy(g => g.Name).ToList(), DisplayMemberPath = "Name", SelectedIndex = 0, Padding = new Thickness(8) }; content.Children.Add(list); Buttons(window, content);
        return window.ShowDialog() == true ? list.SelectedItem as ModGroup : null;
    }
    public static string? ChooseTrack(Window owner, string packageName, IEnumerable<string> tracks)
    {
        var window = Shell(owner, "Choose a track layout", packageName + " contains multiple layouts. Choose one to play.", out var content);
        window.Width = 580;
        var list = new ListBox { ItemsSource = tracks.Order(StringComparer.OrdinalIgnoreCase).ToList(), SelectedIndex = 0, MaxHeight = 340, Padding = new Thickness(8) };
        content.Children.Add(list); Buttons(window, content, "Play track");
        return window.ShowDialog() == true ? list.SelectedItem as string : null;
    }
    public static ResolvedDownloadFile? ChooseDownload(Window owner, IReadOnlyList<ResolvedDownloadFile> files)
    {
        var window = Shell(owner, "Choose a package", "This MEGA folder contains multiple packages. Choose the file to download.", out var content);
        window.Width = 660;
        var list = new ListBox { ItemsSource = files, DisplayMemberPath = "DisplayName", SelectedIndex = 0, MaxHeight = 340, Padding = new Thickness(8) };
        content.Children.Add(list); Buttons(window, content, "Download");
        return window.ShowDialog() == true ? list.SelectedItem as ResolvedDownloadFile : null;
    }
}
