using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

public sealed partial class UsersView : UserControl, IPanelPage
{
    static readonly (string Title, GridLength Width)[] Columns =
    {
        ("USER", new GridLength(1, GridUnitType.Star)),
        ("ROLE", new GridLength(170)),
        ("TWO-FACTOR", new GridLength(120)),
        ("LAST SIGN-IN", new GridLength(230)),
        ("", new GridLength(420)),
    };

    public UsersView()
    {
        InitializeComponent();
        foreach (var r in Roles.All) NewRole.Items.Add(Session.RoleLabel(r));
        NewRole.SelectedIndex = 1;
        Layout(HeaderRow);
        for (var i = 0; i < Columns.Length; i++)
            Cell(HeaderRow, i, new TextBlock { Text = Columns[i].Title, Style = (Style)Application.Current.Resources["TlLabel"] });
    }

    public void SetActive(bool active) { if (active) _ = Refresh(); }
    public void OnOverview(Overview o) { }

    static void Layout(Grid g) { g.ColumnSpacing = 12; foreach (var c in Columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width }); }
    static void Cell(Grid g, int col, FrameworkElement e) { e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, col); g.Children.Add(e); }

    async Task Refresh()
    {
        var users = await ApiClient.Instance.UsersAsync();
        if (users == null) return;
        Rows.Children.Clear();
        foreach (var u in users) Rows.Children.Add(MakeRow(u));
    }

    FrameworkElement MakeRow(UserInfo u)
    {
        var res = Application.Current.Resources;
        var g = new Grid { Padding = new Thickness(18, 10, 18, 10), BorderBrush = (Brush)res["TlCardBorder"], BorderThickness = new Thickness(0, 0, 0, 1) };
        Layout(g);
        var me = u.Username == Session.User?.Username;
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new TextBlock { Text = u.Username, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)res["TlText"] });
        if (me) name.Children.Add(new TextBlock { Text = "(you)", Foreground = (Brush)res["TlTextMuted"], VerticalAlignment = VerticalAlignment.Center });
        if (u.Locked) name.Children.Add(Pill("Locked", Windows.UI.Color.FromArgb(255, 0xC8, 0x40, 0x3A)));
        Cell(g, 0, name);

        var role = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var r in Roles.All) role.Items.Add(Session.RoleLabel(r));
        role.SelectedIndex = Array.IndexOf(Roles.All, u.Role);
        role.SelectionChanged += async (_, _) =>
        {
            var newRole = Roles.All[role.SelectedIndex];
            if (newRole == u.Role) return;
            Shell.Notify(await ApiClient.Instance.SetRoleAsync(u.Username, newRole));
            await Refresh();
        };
        Cell(g, 1, role);
        Cell(g, 2, u.TotpEnabled ? Pill("Enabled", Windows.UI.Color.FromArgb(255, 0x4E, 0x9A, 0x55)) : Pill("Off", Windows.UI.Color.FromArgb(255, 0x55, 0x54, 0x50)));
        Cell(g, 3, new TextBlock { Text = u.LastLogin != null ? $"{Fmt.Time(u.LastLogin)} · {u.LastLoginIp}" : "never", Style = (Style)res["TlMonoText"] });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Action("Reset password", () => ResetPassword(u)));
        if (u.TotpEnabled) actions.Children.Add(Action("Reset 2FA", () => Confirm($"Remove two-factor sign-in for '{u.Username}'?",
            "Use this when they have lost their authenticator device. They can enrol again from My Account.", "Reset 2FA",
            () => ApiClient.Instance.AdminResetTotpAsync(u.Username))));
        if (u.Locked) actions.Children.Add(Action("Unlock", async () => { Shell.Notify(await ApiClient.Instance.UnlockUserAsync(u.Username)); await Refresh(); }));
        if (!me) actions.Children.Add(Action("Delete", () => Confirm($"Delete user '{u.Username}'?", "They will be signed out and can no longer use the panel.", "Delete",
            () => ApiClient.Instance.DeleteUserAsync(u.Username))));
        Cell(g, 4, actions);
        return g;
    }

    static Border Pill(string text, Windows.UI.Color color) => new()
    {
        CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 2), Background = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
    };

    static Button Action(string text, Func<Task> act)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
        b.Click += async (_, _) => await act();
        return b;
    }

    async Task Confirm(string title, string body, string verb, Func<Task<ActionResult>> act)
    {
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = body, PrimaryButtonText = verb, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Shell.Notify(await act());
        await Refresh();
    }

    async Task ResetPassword(UserInfo u)
    {
        var box = new PasswordBox { Header = "New password (10+ characters)", Width = 360 };
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = $"Reset password for '{u.Username}'", Content = box, PrimaryButtonText = "Reset", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Shell.Notify(await ApiClient.Instance.AdminResetPasswordAsync(u.Username, box.Password));
        await Refresh();
    }

    async void OnCreate(object sender, RoutedEventArgs e)
    {
        var r = await ApiClient.Instance.CreateUserAsync(NewUser.Text ?? "", NewPass.Password ?? "", Roles.All[Math.Max(0, NewRole.SelectedIndex)]);
        Shell.Notify(r);
        if (r.Ok) { NewUser.Text = ""; NewPass.Password = ""; await Refresh(); }
    }
}
