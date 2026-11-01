using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TeamLigma.Panel.Services;
using TeamLigma.Shared;

namespace TeamLigma.Panel.Views;

/// <summary>Administrator settings: directories, build tools, server executables, MySQL login and more.</summary>
public sealed partial class SettingsView : UserControl, IPanelPage
{
    enum Kind { Text, Directory, Executable, File, Number }

    readonly Dictionary<string, TextBox> _fields = new();
    readonly Dictionary<string, (Ellipse Dot, TextBlock Text)> _status = new();
    readonly PasswordBox _password = new();
    readonly CheckBox _clearPassword = new() { Content = "Remove the saved password" };
    readonly ToggleSwitch _autoLogin = new() { Header = "Log in automatically when the MySQL console starts", OnContent = "On", OffContent = "Off" };
    readonly TextBlock _testResult = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    readonly StackPanel _preview = new() { Spacing = 8 };
    readonly Border _warnings = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(1) };
    readonly TextBlock _warningsText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    readonly Button _save = new() { Content = "Save settings", Padding = new Thickness(18, 0, 18, 0), Height = 38 };

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static Style St(string key) => (Style)Application.Current.Resources[key];

    public SettingsView()
    {
        _save.Style = St("AccentButtonStyle");
        _warnings.Child = _warningsText;

        var page = new StackPanel { Padding = new Thickness(28, 24, 28, 32), Spacing = 18 };

        // header
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "Settings", Style = St("TlPageTitle") });
        titles.Children.Add(new TextBlock { Text = "Leave a field empty to use the default shown in grey. Changes apply to the next build, task or server start.", Foreground = Res("TlTextMuted"), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(titles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var reload = new Button { Content = "Reload", Height = 38, Padding = new Thickness(14, 0, 14, 0) };
        reload.Click += async (_, _) => await Load();
        _save.Click += async (_, _) => await Save();
        actions.Children.Add(reload);
        actions.Children.Add(_save);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        page.Children.Add(header);
        page.Children.Add(_warnings);

        // Build
        var build = Card("BUILD");
        build.Children.Add(Field("DefaultBuildDir", "Default build directory", Kind.Directory, "Where Start Build, Prepare and Clean run unless another folder is chosen in the Build Console."));
        build.Children.Add(Field("InstallDir", "Install directory", Kind.Directory, "Becomes -DCMAKE_INSTALL_PREFIX. The default server executables live in its bin/ folder. Run Prepare (CMake) again after changing it."));
        build.Children.Add(Field("ExtraCMakeArgs", "Extra CMake options", Kind.Text, "Appended to the Prepare command, e.g. -DCMAKE_BUILD_TYPE=Release"));
        build.Children.Add(Field("HistoryRetention", "Builds kept in History", Kind.Number, "Older finished builds and their logs are removed automatically. 0 or empty keeps everything."));
        page.Children.Add(Wrap(build));

        // Build tools
        var tools = Card("BUILD TOOLS", out var toolsHeader);
        var detect = new Button { Content = "Detect from PATH", Padding = new Thickness(12, 4, 12, 4) };
        detect.Click += async (_, _) => await Detect();
        Grid.SetColumn(detect, 1);
        toolsHeader.Children.Add(detect);
        tools.Children.Add(Field("CMakePath", "CMake", Kind.Executable, null));
        tools.Children.Add(Field("NinjaPath", "Ninja", Kind.Executable, "Used for builds, installs, Clean and checks on external builds."));
        tools.Children.Add(Field("CCompilerPath", "C compiler", Kind.Executable, "Becomes -DCMAKE_C_COMPILER."));
        tools.Children.Add(Field("CxxCompilerPath", "C++ compiler", Kind.Executable, "Becomes -DCMAKE_CXX_COMPILER."));
        tools.Children.Add(Field("ScreenPath", "GNU screen", Kind.Executable, "Runs builds and the server consoles in detached sessions."));
        page.Children.Add(Wrap(tools));

        // Servers
        var servers = Card("SERVER EXECUTABLES");
        servers.Children.Add(Field("WorldserverPath", "Worldserver executable", Kind.Executable, "Started from its own folder."));
        servers.Children.Add(Field("WorldserverConfig", "Worldserver config file", Kind.File, "Optional: passed as -c <file>. Empty uses the server's built-in location."));
        servers.Children.Add(Field("AuthserverPath", "Authserver executable", Kind.Executable, null));
        servers.Children.Add(Field("AuthserverConfig", "Authserver config file", Kind.File, "Optional: passed as -c <file>."));
        servers.Children.Add(Field("MySqlClientPath", "MySQL client executable", Kind.Executable, null));
        servers.Children.Add(Field("ShutdownDelaySeconds", "Worldserver shutdown delay (seconds)", Kind.Number, "Used by the Shutdown button: server shutdown <delay>."));
        page.Children.Add(Wrap(servers));

        // MySQL
        var mysql = Card("MYSQL LOGIN");
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var host = Field("MySqlHost", "Host", Kind.Text, "Empty connects through the local socket.");
        var port = Field("MySqlPort", "Port", Kind.Number, null);
        Grid.SetColumn(port, 1);
        grid.Children.Add(host); grid.Children.Add(port);
        mysql.Children.Add(grid);
        mysql.Children.Add(Field("MySqlUser", "User", Kind.Text, null));
        var pwRow = new StackPanel { Spacing = 6 };
        pwRow.Children.Add(new TextBlock { Text = "Password", Foreground = Res("TlText"), FontSize = 14 });
        pwRow.Children.Add(_password);
        pwRow.Children.Add(_clearPassword);
        pwRow.Children.Add(new TextBlock { Text = "Stored encrypted on the server and never sent back to the browser. It is never placed on a command line.", Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
        mysql.Children.Add(pwRow);
        mysql.Children.Add(Field("MySqlDatabase", "Default database", Kind.Text, "Optional: the MySQL console opens this database."));
        mysql.Children.Add(_autoLogin);
        mysql.Children.Add(new TextBlock { Text = "When on, the panel types the saved password at the client's 'Enter password:' prompt, so the console opens logged in.", Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var test = new Button { Content = "Test connection", Padding = new Thickness(12, 5, 12, 5) };
        test.Click += async (_, _) => await TestMySql();
        testRow.Children.Add(test);
        testRow.Children.Add(_testResult);
        _testResult.VerticalAlignment = VerticalAlignment.Center;
        mysql.Children.Add(testRow);
        _clearPassword.Checked += (_, _) => { _password.Password = ""; _password.IsEnabled = false; };
        _clearPassword.Unchecked += (_, _) => _password.IsEnabled = true;
        page.Children.Add(Wrap(mysql));

        // Preview
        var preview = Card("COMMANDS THE PANEL WILL RUN");
        preview.Children.Add(new TextBlock { Text = "Based on the saved settings (save to refresh).", Style = St("TlLabel") });
        preview.Children.Add(_preview);
        page.Children.Add(Wrap(preview));

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page };
    }

    public void SetActive(bool active) { if (active) _ = Load(); }
    public void OnOverview(Overview o) { }

    // ---------------- layout helpers ----------------

    static StackPanel Card(string title) => Card(title, out _);

    static StackPanel Card(string title, out Grid header)
    {
        var stack = new StackPanel { Spacing = 14 };
        header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, Style = St("TlCardTitle"), VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(header);
        return stack;
    }

    static Border Wrap(StackPanel content) => new() { Style = St("TlCardStyle"), Child = content };

    FrameworkElement Field(string key, string label, Kind kind, string? help)
    {
        var box = new TextBox();
        if (kind is Kind.Directory or Kind.Executable or Kind.File) box.FontFamily = (FontFamily)Application.Current.Resources["TlMono"];
        if (kind == Kind.Number) box.InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } };
        _fields[key] = box;
        var row = new StackPanel { Spacing = 4 };
        // A separate label, so a monospace input doesn't turn its header monospace too.
        row.Children.Add(new TextBlock { Text = label, Foreground = Res("TlText"), FontSize = 14, Margin = new Thickness(0, 0, 0, 2) });
        if (kind == Kind.Directory)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom, Height = 32, Padding = new Thickness(12, 0, 12, 0) };
            browse.Click += async (_, _) => await BrowseDirectory(box, label);
            Grid.SetColumn(browse, 1);
            g.Children.Add(box);
            g.Children.Add(browse);
            row.Children.Add(g);
        }
        else row.Children.Add(box);

        if (kind == Kind.Executable)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x55, 0x54, 0x50)) };
            var text = new TextBlock { Style = St("TlLabel"), TextTrimming = TextTrimming.CharacterEllipsis };
            line.Children.Add(dot);
            line.Children.Add(text);
            row.Children.Add(line);
            _status[key] = (dot, text);
        }
        if (help != null) row.Children.Add(new TextBlock { Text = help, Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
        return row;
    }

    async Task BrowseDirectory(TextBox box, string label)
    {
        var picker = new DirectoryPicker(string.IsNullOrWhiteSpace(box.Text) ? box.PlaceholderText.Replace("Default: ", "") : box.Text);
        var dlg = new ContentDialog { XamlRoot = XamlRoot, Title = label, Content = picker, PrimaryButtonText = "Use this folder", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary) box.Text = picker.CurrentPath;
    }

    // ---------------- data ----------------

    async Task Load()
    {
        var s = await ApiClient.Instance.SettingsAsync();
        if (s == null) { Shell.Current?.ShowToast("Could not load the settings.", true); return; }
        Apply(s);
    }

    void Apply(PanelSettingsDto s)
    {
        foreach (var (key, box) in _fields)
        {
            box.Text = Get(s, key) ?? "";
            box.PlaceholderText = s.Defaults.TryGetValue(key, out var d) ? $"Default: {d}" : "";
        }
        _password.Password = "";
        _password.PlaceholderText = s.MySqlPasswordSet ? "Saved. Type a new one to replace it." : "Not saved";
        _clearPassword.IsChecked = false;
        _clearPassword.Visibility = s.MySqlPasswordSet ? Visibility.Visible : Visibility.Collapsed;
        _autoLogin.IsOn = s.MySqlAutoLogin;
        foreach (var t in s.Tools)
        {
            if (!_status.TryGetValue(t.Key, out var st)) continue;
            st.Dot.Fill = new SolidColorBrush(t.Found ? Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73) : Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x4B));
            st.Text.Text = t.Found ? $"Found: {t.Path}{(t.Version != null ? "  ·  " + t.Version : "")}" : $"Not found: {t.Path}";
            st.Text.Foreground = t.Found ? Res("TlTextMuted") : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x4B));
        }
        _preview.Children.Clear();
        foreach (var p in s.Preview)
        {
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = p.Label, Style = St("TlLabel"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
            var cmd = new TextBlock { Text = p.Command, Style = St("TlMonoText"), Foreground = Res("TlText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, IsTextSelectionEnabled = true };
            Grid.SetColumn(cmd, 1);
            g.Children.Add(cmd);
            _preview.Children.Add(g);
        }
    }

    static string? Get(PanelSettingsDto s, string key) => key switch
    {
        "DefaultBuildDir" => s.DefaultBuildDir,
        "InstallDir" => s.InstallDir,
        "ExtraCMakeArgs" => s.ExtraCMakeArgs,
        "HistoryRetention" => s.HistoryRetention?.ToString(),
        "CMakePath" => s.CMakePath,
        "NinjaPath" => s.NinjaPath,
        "CCompilerPath" => s.CCompilerPath,
        "CxxCompilerPath" => s.CxxCompilerPath,
        "ScreenPath" => s.ScreenPath,
        "WorldserverPath" => s.WorldserverPath,
        "AuthserverPath" => s.AuthserverPath,
        "MySqlClientPath" => s.MySqlClientPath,
        "WorldserverConfig" => s.WorldserverConfig,
        "AuthserverConfig" => s.AuthserverConfig,
        "ShutdownDelaySeconds" => s.ShutdownDelaySeconds?.ToString(),
        "MySqlHost" => s.MySqlHost,
        "MySqlPort" => s.MySqlPort?.ToString(),
        "MySqlUser" => s.MySqlUser,
        "MySqlDatabase" => s.MySqlDatabase,
        _ => null,
    };

    string? T(string key) => string.IsNullOrWhiteSpace(_fields[key].Text) ? null : _fields[key].Text.Trim();

    bool TryNumber(string key, string label, out int? value)
    {
        value = null;
        var t = T(key);
        if (t == null) return true;
        if (int.TryParse(t, out var n)) { value = n; return true; }
        Shell.Current?.ShowToast($"{label} must be a whole number.", true);
        return false;
    }

    PanelSettingsDto? Collect()
    {
        if (!TryNumber("HistoryRetention", "Builds kept in History", out var retention)) return null;
        if (!TryNumber("ShutdownDelaySeconds", "Shutdown delay", out var delay)) return null;
        if (!TryNumber("MySqlPort", "MySQL port", out var port)) return null;
        return new PanelSettingsDto
        {
            DefaultBuildDir = T("DefaultBuildDir"),
            InstallDir = T("InstallDir"),
            ExtraCMakeArgs = T("ExtraCMakeArgs"),
            HistoryRetention = retention,
            CMakePath = T("CMakePath"),
            NinjaPath = T("NinjaPath"),
            CCompilerPath = T("CCompilerPath"),
            CxxCompilerPath = T("CxxCompilerPath"),
            ScreenPath = T("ScreenPath"),
            WorldserverPath = T("WorldserverPath"),
            AuthserverPath = T("AuthserverPath"),
            MySqlClientPath = T("MySqlClientPath"),
            WorldserverConfig = T("WorldserverConfig"),
            AuthserverConfig = T("AuthserverConfig"),
            ShutdownDelaySeconds = delay,
            MySqlHost = T("MySqlHost"),
            MySqlPort = port,
            MySqlUser = T("MySqlUser"),
            MySqlDatabase = T("MySqlDatabase"),
            MySqlAutoLogin = _autoLogin.IsOn,
            MySqlNewPassword = string.IsNullOrEmpty(_password.Password) ? null : _password.Password,
            MySqlClearPassword = _clearPassword.IsChecked == true,
        };
    }

    async Task Save()
    {
        var dto = Collect();
        if (dto == null) return;
        _save.IsEnabled = false;
        try
        {
            var r = await ApiClient.Instance.SaveSettingsAsync(dto);
            Shell.Current?.ShowToast(r.Message, !r.Ok);
            ShowWarnings(r.Ok ? r.Warnings : new List<string> { r.Message }.Concat(r.Warnings).ToList(), error: !r.Ok);
            if (r.Ok && r.Settings != null) Apply(r.Settings);
        }
        finally { _save.IsEnabled = true; }
    }

    void ShowWarnings(List<string> items, bool error)
    {
        _warnings.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _warnings.BorderBrush = error ? Res("TlDanger") : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0xB3, 0x41));
        _warnings.Background = new SolidColorBrush(error ? Windows.UI.Color.FromArgb(255, 0x3A, 0x24, 0x20) : Windows.UI.Color.FromArgb(255, 0x36, 0x2E, 0x1C));
        _warningsText.Text = string.Join("\n", items.Select(i => "• " + i));
        _warningsText.Foreground = Res("TlText");
    }

    async Task Detect()
    {
        var d = await ApiClient.Instance.DetectToolsAsync();
        if (d == null) { Shell.Current?.ShowToast("Detection failed.", true); return; }
        foreach (var (key, path) in d.Paths)
            if (_fields.TryGetValue(key, out var box)) box.Text = path;
        Shell.Current?.ShowToast($"Found {d.Paths.Count} tool(s). Review them, then Save settings.");
    }

    async Task TestMySql()
    {
        var dto = Collect();
        if (dto == null) return;
        _testResult.Text = "Testing…";
        _testResult.Foreground = Res("TlTextMuted");
        var r = await ApiClient.Instance.TestMySqlAsync(dto);
        _testResult.Text = r.Message;
        _testResult.Foreground = r.Ok ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73)) : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x4B));
    }
}
