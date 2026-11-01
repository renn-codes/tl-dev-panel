using Microsoft.UI.Xaml.Input;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;
using Windows.System;

namespace TeamLigma.Panel.Views;

public sealed partial class LoginView : UserControl
{
    readonly bool _setup;
    string? _challenge;
    bool _recovery, _busy;

    public LoginView(bool setup, string? message)
    {
        InitializeComponent();
        _setup = setup;
        if (setup)
        {
            Heading.Text = "Create the administrator";
            SubHeading.Text = "No accounts exist yet. The first account becomes the panel administrator.";
            ConfirmBox.Visibility = SetupHint.Visibility = Visibility.Visible;
            SubmitText.Text = "Create account";
        }
        if (message != null) ShowMessage(message, isError: !message.StartsWith("You have been"));
        Loaded += (_, _) => { UserBox.Focus(FocusState.Programmatic); StartEffects(); };
        Unloaded += (_, _) => StopEffects();
        Root.SizeChanged += (_, _) => BuildFire();
    }

    void ShowMessage(string? text, bool isError = true)
    {
        MessageBox.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = text ?? "";
        MessageBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[isError ? "TlDanger" : "TlAccent"];
    }

    async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await Submit();
    }

    async void OnSubmit(object sender, RoutedEventArgs e) => await Submit();

    async Task Submit()
    {
        if (_busy) return;
        _busy = true;
        SubmitButton.IsEnabled = false;
        try
        {
            LoginResult? r;
            if (_challenge != null)
            {
                r = await ApiClient.Instance.TotpLoginAsync(_challenge, CodeBox.Text ?? "");
                if (r != null && !r.Ok && r.Message.Contains("expired")) ResetToPassword();
            }
            else if (_setup)
            {
                if (PassBox.Password != ConfirmBox.Password) { ShowMessage("The passwords do not match."); return; }
                r = await ApiClient.Instance.SetupAsync(UserBox.Text ?? "", PassBox.Password ?? "");
            }
            else
            {
                r = await ApiClient.Instance.LoginAsync(UserBox.Text ?? "", PassBox.Password ?? "");
            }

            if (r == null) { ShowMessage("The panel service is unreachable."); return; }
            if (r.Ok && r.State?.User != null) { RootView.Current?.ShowShell(r.State.User); return; }
            if (r.RequiresTotp && r.Challenge != null)
            {
                _challenge = r.Challenge;
                PasswordStep.Visibility = Visibility.Collapsed;
                TotpStep.Visibility = BackLink.Visibility = Visibility.Visible;
                Heading.Text = "Two-factor sign-in";
                SubHeading.Text = "Open your authenticator app and enter the 6-digit code for Team Ligma Panel.";
                SubmitText.Text = "Verify";
                ShowMessage(null);
                CodeBox.Text = "";
                CodeBox.Focus(FocusState.Programmatic);
                return;
            }
            ShowMessage(r.Message);
            if (_challenge == null) PassBox.Password = "";
            else { CodeBox.Text = ""; CodeBox.Focus(FocusState.Programmatic); }
        }
        finally
        {
            _busy = false;
            SubmitButton.IsEnabled = true;
        }
    }

    void OnToggleRecovery(object sender, RoutedEventArgs e)
    {
        _recovery = !_recovery;
        CodeLabel.Text = _recovery ? "Recovery code" : "Authenticator code";
        CodeBox.PlaceholderText = _recovery ? "xxxx-xxxx" : "123 456";
        RecoveryLink.Content = _recovery ? "Use my authenticator app instead" : "Use a recovery code instead";
        CodeBox.Text = "";
        CodeBox.Focus(FocusState.Programmatic);
    }

    void OnBack(object sender, RoutedEventArgs e) { ResetToPassword(); ShowMessage(null); }

    void ResetToPassword()
    {
        _challenge = null;
        PasswordStep.Visibility = Visibility.Visible;
        TotpStep.Visibility = BackLink.Visibility = Visibility.Collapsed;
        Heading.Text = "Sign in";
        SubHeading.Text = "Enter the realm with your panel account.";
        SubmitText.Text = "Sign in";
        PassBox.Password = "";
        PassBox.Focus(FocusState.Programmatic);
    }
}
