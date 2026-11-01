using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

public interface IPanelPage
{
    void SetActive(bool active);
    void OnOverview(Overview o);
}

public sealed partial class Shell : UserControl
{
    public static Shell? Current { get; private set; }
    readonly List<(Button Button, Border Marker, UIElement Page)> _nav = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    bool _busy;

    public Shell()
    {
        InitializeComponent();
        Current = this;
        AddNav("Overview", OverviewPage);
        AddNav("Build Console", BuildPage);
        AddNav("History", HistoryPage);
        AddNav("Server Monitor", ServersPage);
        AddNav("My Account", AccountPage);
        if (Session.IsAdmin) AddNav("Users", UsersPage);
        UserNameText.Text = Session.User?.Username ?? "";
        UserRoleText.Text = Session.RoleLabel(Session.User?.Role) + (Session.User?.TotpEnabled == true ? " · 2FA" : "");
        Select(0);
        _timer.Tick += async (_, _) => await Poll();
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        Loaded += async (_, _) => { _timer.Start(); await Poll(); };
    }

    void AddNav(string title, UIElement page)
    {
        var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6, 10, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        var text = new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(marker);
        content.Children.Add(text);
        var b = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 8, 10, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        var index = _nav.Count;
        b.Click += (_, _) => Select(index);
        Nav.Children.Add(b);
        _nav.Add((b, marker, page));
    }

    public void Select(int index)
    {
        for (var i = 0; i < _nav.Count; i++)
        {
            var (button, marker, page) = _nav[i];
            var on = i == index;
            marker.Background = on ? (Brush)Application.Current.Resources["TlAccent"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Background = on ? (Brush)Application.Current.Resources["TlAccentSoft"] : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = on ? (Brush)Application.Current.Resources["TlText"] : (Brush)Application.Current.Resources["TlTextSecondary"];
            page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            (page as IPanelPage)?.SetActive(on);
        }
    }

    /// <summary>Stops all polling before the shell is discarded (sign-out / session end).</summary>
    public void Shutdown()
    {
        _timer.Stop();
        foreach (var (_, _, page) in _nav) (page as IPanelPage)?.SetActive(false);
    }

    async void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (RootView.Current != null) await RootView.Current.SignOutAsync();
    }

    async Task Poll()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var o = await ApiClient.Instance.OverviewAsync();
            ConnDot.Fill = new SolidColorBrush(ApiClient.Instance.Connected ? Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73) : Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x4B));
            ConnText.Text = ApiClient.Instance.Connected ? "Connected to panel service" : "Panel service unreachable";
            if (o == null) return;
            VersionText.Text = $"Team Ligma · v{o.Version} · {o.System.Host}";
            SideBuild.Text = o.Running.Count > 1 ? $"{o.Running.Count} builds running"
                : o.Build == null ? "Build —" : $"Build #{o.Build.Id} · {Fmt.PhaseLabel(o.Build.Phase)}";
            SideBuildDot.Fill = Fmt.PhaseBrush(o.Build?.Phase);
            Dot(SideWorldDot, o.Servers.FirstOrDefault(s => s.Name == "world")?.ProcessRunning == true);
            Dot(SideAuthDot, o.Servers.FirstOrDefault(s => s.Name == "auth")?.ProcessRunning == true);
            Dot(SideMysqlDot, o.System.Components.FirstOrDefault(c => c.Name == "mysqld")?.Running == true);
            foreach (var (_, _, page) in _nav) (page as IPanelPage)?.OnOverview(o);
        }
        finally { _busy = false; }
    }

    static void Dot(Microsoft.UI.Xaml.Shapes.Ellipse e, bool on)
        => e.Fill = new SolidColorBrush(on ? Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73) : Windows.UI.Color.FromArgb(255, 0x55, 0x54, 0x50));

    public void ShowToast(string message, bool error = false)
    {
        ToastText.Text = message;
        Toast.BorderBrush = error ? (Brush)Application.Current.Resources["TlDanger"] : (Brush)Application.Current.Resources["TlAccent"];
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public static void Notify(ActionResult r) => Current?.ShowToast(r.Message, !r.Ok);
}
