using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

/// <summary>Server-side folder browser shown inside a dialog (folders only).</summary>
public sealed partial class DirectoryPicker : Grid
{
    readonly TextBox _path = new() { FontFamily = (FontFamily)Application.Current.Resources["TlMono"], FontSize = 13 };
    readonly Button _up = new() { Content = "↑ Up", Height = 34, Padding = new Thickness(12, 0, 12, 0) };
    readonly StackPanel _list = new() { Spacing = 2 };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    readonly TextBox _newName = new() { PlaceholderText = "new folder name", Width = 220 };
    string? _parent;

    public string CurrentPath { get; private set; } = "/";

    public DirectoryPicker(string? start)
    {
        Width = 680;
        Height = 470;
        RowSpacing = 10;
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            RowDefinitions.Add(new RowDefinition { Height = h });
        var res = Application.Current.Resources;

        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var go = new Button { Content = "Go", Height = 34, Padding = new Thickness(12, 0, 12, 0) };
        Grid.SetColumn(_path, 1); Grid.SetColumn(go, 2);
        top.Children.Add(_up); top.Children.Add(_path); top.Children.Add(go);
        Children.Add(top);

        var listBorder = new Border
        {
            Background = (Brush)res["TlTerminal"], BorderBrush = (Brush)res["TlCardBorder"], BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(4),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list },
        };
        SetRow(listBorder, 1);
        Children.Add(listBorder);

        SetRow(_status, 2);
        Children.Add(_status);

        var mk = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var create = new Button { Content = "Create folder here", Height = 32, Padding = new Thickness(12, 0, 12, 0) };
        mk.Children.Add(_newName); mk.Children.Add(create);
        SetRow(mk, 3);
        Children.Add(mk);

        _up.Click += async (_, _) => { if (_parent != null) await Load(_parent); };
        go.Click += async (_, _) => await Load(_path.Text);
        _path.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await Load(_path.Text); } };
        create.Click += async (_, _) => await CreateFolder();
        _newName.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await CreateFolder(); } };
        Loaded += async (_, _) => await Load(start);
    }

    async Task Load(string? path)
    {
        var l = await ApiClient.Instance.ListDirsAsync(path);
        if (l == null) { SetStatus("Could not reach the panel service.", true); return; }
        CurrentPath = l.Path;
        _parent = l.Parent;
        _path.Text = l.Path;
        _up.IsEnabled = l.Parent != null;
        _list.Children.Clear();
        var res = Application.Current.Resources;
        if (l.Entries.Count == 0)
            _list.Children.Add(new TextBlock { Text = l.Error ?? "No sub-folders.", Margin = new Thickness(8), Foreground = (Brush)res["TlTextMuted"] });
        foreach (var d in l.Entries)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = "▸  " + d.Name, FontFamily = (FontFamily)res["TlMono"], FontSize = 13, Foreground = (Brush)res["TlText"], TextTrimming = TextTrimming.CharacterEllipsis });
            var tag = d.HasBuildNinja ? "build.ninja" : d.HasCMakeLists ? "CMake source" : null;
            if (tag != null)
            {
                var t = new TextBlock { Text = tag, FontSize = 11.5, Foreground = new SolidColorBrush(d.HasBuildNinja ? Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73) : Windows.UI.Color.FromArgb(255, 0xE0, 0x7A, 0x3F)) };
                Grid.SetColumn(t, 1);
                row.Children.Add(t);
            }
            var b = new Button
            {
                Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(8, 5, 8, 5),
            };
            var target = d.Path;
            b.Click += async (_, _) => await Load(target);
            _list.Children.Add(b);
        }
        if (l.Error != null) SetStatus(l.Error, true);
        else if (l.HasBuildNinja) SetStatus($"Ready: {l.Path} contains build.ninja, so Start Build can use it.", false);
        else if (l.ParentHasCMakeLists) SetStatus($"No build.ninja yet. After selecting it, run Prepare (CMake): 'cmake ../' will use the source in {l.Parent}.", false);
        else SetStatus("This folder has no build.ninja, and its parent has no CMakeLists.txt. Prepare runs 'cmake ../', so the build directory should sit inside the AzerothCore source folder.", true);
    }

    void SetStatus(string text, bool warn)
    {
        _status.Text = text;
        _status.Foreground = warn ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0xB3, 0x41)) : (Brush)Application.Current.Resources["TlTextSecondary"];
    }

    async Task CreateFolder()
    {
        var name = (_newName.Text ?? "").Trim();
        if (name.Length == 0) return;
        var target = CurrentPath.TrimEnd('/') + "/" + name;
        var r = await ApiClient.Instance.MakeDirAsync(target);
        if (!r.Ok) { SetStatus(r.Message, true); return; }
        _newName.Text = "";
        await Load(target);
    }
}
