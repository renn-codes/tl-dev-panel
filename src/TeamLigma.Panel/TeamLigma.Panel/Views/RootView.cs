using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

/// <summary>Top-level host: shows the sign-in screen or the panel shell depending on the session.</summary>
public sealed partial class RootView : Grid
{
    public static RootView? Current { get; private set; }
    Shell? _shell;
    bool _showingLogin;

    public RootView()
    {
        Current = this;
        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TlBg"];
        ApiClient.Instance.Unauthorized += () => DispatcherQueue.TryEnqueue(() => _ = ShowLoginAsync("Your session has ended. Please sign in again."));
        Loaded += async (_, _) => await StartAsync();
    }

    async Task StartAsync()
    {
        AuthState? state = null;
        for (var i = 0; i < 5 && state == null; i++)
        {
            state = await ApiClient.Instance.AuthStateAsync();
            if (state == null) await Task.Delay(1500);
        }
        if (state is { Authenticated: true, User: not null }) ShowShell(state.User);
        else await ShowLoginAsync(state == null ? "The panel service is unreachable." : null, state);
    }

    public void ShowShell(UserInfo user)
    {
        Session.User = user;
        _showingLogin = false;
        _shell?.Shutdown();
        Children.Clear();
        _shell = new Shell();
        Children.Add(_shell);
    }

    public async Task ShowLoginAsync(string? message = null, AuthState? state = null)
    {
        if (_showingLogin) return;
        _showingLogin = true;
        _shell?.Shutdown();
        _shell = null;
        Session.User = null;
        state ??= await ApiClient.Instance.AuthStateAsync();
        Children.Clear();
        Children.Add(new LoginView(state?.SetupRequired == true, message));
    }

    public async Task SignOutAsync()
    {
        await ApiClient.Instance.LogoutAsync();
        await ShowLoginAsync("You have been signed out.");
    }
}
