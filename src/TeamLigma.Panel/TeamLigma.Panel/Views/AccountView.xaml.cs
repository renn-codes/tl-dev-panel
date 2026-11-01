using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

public sealed partial class AccountView : UserControl, IPanelPage
{
    public AccountView() => InitializeComponent();

    public void SetActive(bool active) { if (active) _ = Refresh(); }
    public void OnOverview(Overview o) { }

    async Task Refresh()
    {
        var state = await ApiClient.Instance.AuthStateAsync();
        if (state?.User != null) Session.User = state.User;
        var u = Session.User;
        if (u == null) return;
        NameText.Text = u.Username;
        RoleText.Text = Session.RoleLabel(u.Role);
        LastText.Text = u.LastLogin != null ? $"{Fmt.Time(u.LastLogin)}  ({u.LastLoginIp})" : "—";
        var on = u.TotpEnabled;
        TotpPillText.Text = on ? "Enabled" : "Off";
        TotpPill.Background = new SolidColorBrush(on ? Windows.UI.Color.FromArgb(255, 0x4E, 0x9A, 0x55) : Windows.UI.Color.FromArgb(255, 0x55, 0x54, 0x50));
        if (CodesPanel.Visibility == Visibility.Visible) return; // keep showing new codes until acknowledged
        OnPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        OffPanel.Visibility = !on && SetupPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        OnText.Text = $"Two-factor sign-in is on. You have {u.RecoveryCodesLeft} unused recovery code(s). Enter your password and a current code to disable it.";
    }

    async void OnChangePassword(object sender, RoutedEventArgs e)
    {
        if (NewPass.Password != ConfirmPass.Password) { Shell.Current?.ShowToast("The new passwords do not match.", true); return; }
        var r = await ApiClient.Instance.ChangePasswordAsync(CurrentPass.Password, NewPass.Password);
        Shell.Notify(r);
        if (r.Ok) CurrentPass.Password = NewPass.Password = ConfirmPass.Password = "";
    }

    async void OnBegin(object sender, RoutedEventArgs e)
    {
        var setup = await ApiClient.Instance.BeginTotpAsync();
        if (setup == null) { Shell.Current?.ShowToast("Could not start authenticator setup.", true); return; }
        Qr.SetMatrix(setup.Matrix);
        SecretBox.Text = setup.Secret;
        ConfirmCode.Text = "";
        OffPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        ConfirmCode.Focus(FocusState.Programmatic);
    }

    async void OnConfirmKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await Confirm();
    }

    async void OnConfirm(object sender, RoutedEventArgs e) => await Confirm();

    async Task Confirm()
    {
        var r = await ApiClient.Instance.ConfirmTotpAsync(ConfirmCode.Text ?? "");
        Shell.Current?.ShowToast(r.Message, !r.Ok);
        if (!r.Ok) { ConfirmCode.Text = ""; return; }
        SetupPanel.Visibility = Visibility.Collapsed;
        ShowCodes(r.Codes);
        await Refresh();
    }

    void ShowCodes(List<string> codes)
    {
        CodesBox.Text = string.Join("\n", codes);
        CodesPanel.Visibility = Visibility.Visible;
        OnPanel.Visibility = OffPanel.Visibility = Visibility.Collapsed;
    }

    async void OnCodesSaved(object sender, RoutedEventArgs e)
    {
        CodesPanel.Visibility = Visibility.Collapsed;
        CodesBox.Text = "";
        await Refresh();
    }

    async void OnCancelSetup(object sender, RoutedEventArgs e)
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        await Refresh();
    }

    async void OnDisable(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Disable two-factor sign-in?",
            Content = "Your account will be protected by your password only.",
            PrimaryButtonText = "Disable",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var r = await ApiClient.Instance.DisableTotpAsync(ManagePass.Password, ManageCode.Text ?? "");
        Shell.Notify(r);
        ManagePass.Password = ""; ManageCode.Text = "";
        await Refresh();
    }

    async void OnRegenerate(object sender, RoutedEventArgs e)
    {
        var r = await ApiClient.Instance.RegenerateRecoveryAsync(ManagePass.Password);
        Shell.Current?.ShowToast(r.Message, !r.Ok);
        ManagePass.Password = "";
        if (r.Ok) ShowCodes(r.Codes);
    }
}
