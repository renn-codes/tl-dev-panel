using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;
using Windows.System;

namespace TeamLigma.Panel.Views;

public enum ConsoleMode { Line, CtrlCOnly, MySql }

/// <summary>One interactive, colourised server console (screen session) with its input controls.</summary>
public sealed partial class ConsolePanel : UserControl
{
    long _offset = -1;
    bool _busy;
    ConsoleMode _mode;
    readonly List<string> _history = new();
    int _historyIndex;
    ServerStatus? _status;
    string _shutdownCommand = "server shutdown 0h0m10s";

    public string Component { get; set; } = "";
    public string Title { get => TitleText.Text; set => TitleText.Text = value; }

    public ConsoleMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Input.Visibility = value == ConsoleMode.CtrlCOnly ? Visibility.Collapsed : Visibility.Visible;
            CtrlCBox.Visibility = value == ConsoleMode.CtrlCOnly ? Visibility.Visible : Visibility.Collapsed;
            SqlButton.Visibility = value == ConsoleMode.MySql ? Visibility.Visible : Visibility.Collapsed;
            SendButton.Content = value == ConsoleMode.CtrlCOnly ? "Send Ctrl + C" : "Send";
            PromptText.Text = value == ConsoleMode.CtrlCOnly ? "^C" : value == ConsoleMode.MySql ? "mysql>" : "AC>";
            if (value == ConsoleMode.Line)
            {
                ExtraButton.Visibility = Visibility.Visible;
                ExtraButton.Content = "Shutdown (10s)";
                ToolTipService.SetToolTip(ExtraButton, "Sends: server shutdown 0h0m10s");
            }
        }
    }

    public ConsolePanel()
    {
        InitializeComponent();
        CtrlCBox.CtrlC += async (_, _) => await SendCtrlC();
    }

    public void UpdateStatus(ServerStatus s)
    {
        _status = s;
        CommandText.Text = s.Command;
        var running = s.SessionActive;
        StateText.Text = s.ProcessRunning ? (s.Pid is { } p ? $"Running · pid {p}" : "Running") : running ? "Session open" : "Stopped";
        StatePill.Background = new SolidColorBrush(s.ProcessRunning
            ? Windows.UI.Color.FromArgb(255, 0x4E, 0x9A, 0x55)
            : running ? Windows.UI.Color.FromArgb(255, 0xCC, 0x55, 0x00) : Windows.UI.Color.FromArgb(255, 0x55, 0x54, 0x50));
        var can = Session.CanOperate;
        StartButton.IsEnabled = can && !running;
        SendButton.IsEnabled = can && running;
        ExtraButton.IsEnabled = can && running;
        ClearButton.IsEnabled = can;
        Input.IsEnabled = Password.IsEnabled = CtrlCBox.IsEnabled = can;
        if (!can) Input.PlaceholderText = "Read-only: your role cannot send commands";
        SqlButton.IsEnabled = can && running && s.InputHint != "password";
        if (s.ShutdownCommand != null)
        {
            var delay = System.Text.RegularExpressions.Regex.Match(s.ShutdownCommand, @"(\d+)h(\d+)m(\d+)s");
            var secs = delay.Success ? int.Parse(delay.Groups[1].Value) * 3600 + int.Parse(delay.Groups[2].Value) * 60 + int.Parse(delay.Groups[3].Value) : 10;
            ExtraButton.Content = $"Shutdown ({(secs >= 60 ? $"{secs / 60}m{(secs % 60 > 0 ? $"{secs % 60}s" : "")}" : $"{secs}s")})";
            ToolTipService.SetToolTip(ExtraButton, "Sends: " + s.ShutdownCommand);
            _shutdownCommand = s.ShutdownCommand;
        }
        if (_mode == ConsoleMode.MySql)
        {
            Password.PlaceholderText = s.AutoLogin ? "Auto-login is on (password saved in Settings)" : "MySQL password, then Enter";
            var pw = s.InputHint == "password";
            if (pw && Password.Visibility != Visibility.Visible) { Password.Visibility = Visibility.Visible; Input.Visibility = Visibility.Collapsed; }
            else if (!pw && Password.Visibility == Visibility.Visible) { Password.Visibility = Visibility.Collapsed; Input.Visibility = Visibility.Visible; }
        }
    }

    public async Task PollAsync()
    {
        if (_busy || string.IsNullOrEmpty(Component)) return;
        _busy = true;
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var chunk = await ApiClient.Instance.ServerOutputAsync(Component, _offset);
                if (chunk == null) return;
                if (chunk.Reset) Term.Clear();
                _offset = chunk.Offset;
                Term.Append(chunk.Text);
                if (chunk.Text.Length < 500_000) break;
            }
        }
        finally { _busy = false; }
    }

    async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        Shell.Notify(await ApiClient.Instance.StartServerAsync(Component));
        await PollAsync();
    }

    async void OnClear(object sender, RoutedEventArgs e)
    {
        var r = await ApiClient.Instance.ClearConsoleAsync(Component);
        if (r.Ok) { Term.Clear(); _offset = 0; }
        else Shell.Notify(r);
    }

    async void OnExtra(object sender, RoutedEventArgs e)
    {
        if (_mode != ConsoleMode.Line) return;
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Shut down the Worldserver?",
            Content = $"Sends '{_shutdownCommand}'. Connected players are warned before it goes down.",
            PrimaryButtonText = "Shut down",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Shell.Notify(await ApiClient.Instance.SendInputAsync(Component, _shutdownCommand));
    }

    async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_mode == ConsoleMode.CtrlCOnly) { await SendCtrlC(); return; }
        if (Password.Visibility == Visibility.Visible) { await SendPassword(); return; }
        await SendLine();
    }

    async Task SendLine()
    {
        var text = Input.Text ?? "";
        Input.Text = "";
        if (text.Length > 0 && (_history.Count == 0 || _history[^1] != text)) _history.Add(text);
        _historyIndex = _history.Count;
        var r = await ApiClient.Instance.SendInputAsync(Component, text);
        if (!r.Ok) Shell.Notify(r);
        await Task.Delay(150);
        await PollAsync();
    }

    async Task SendPassword()
    {
        var pw = Password.Password ?? "";
        Password.Password = "";
        var r = await ApiClient.Instance.SendInputAsync(Component, pw);
        if (!r.Ok) Shell.Notify(r);
        await Task.Delay(300);
        await PollAsync();
    }

    async Task SendCtrlC()
    {
        Shell.Notify(await ApiClient.Instance.CtrlCAsync(Component));
        await Task.Delay(200);
        await PollAsync();
    }

    async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                await SendLine();
                break;
            case VirtualKey.Up when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Max(0, _historyIndex - 1);
                Input.Text = _history[_historyIndex];
                Input.SelectionStart = Input.Text.Length;
                break;
            case VirtualKey.Down when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                Input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
                Input.SelectionStart = Input.Text.Length;
                break;
        }
    }

    async void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SendPassword();
    }

    async void OnRunSql(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".sql");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            using var stream = await file.OpenStreamForReadAsync();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            Shell.Notify(await ApiClient.Instance.RunSqlAsync(file.Name, ms));
            await Task.Delay(300);
            await PollAsync();
        }
        catch (Exception ex)
        {
            Shell.Current?.ShowToast("Could not read the script: " + ex.Message, true);
        }
    }
}
