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

    // Container builds (Portainer)
    readonly ToggleSwitch _containerBuilds = new() { Header = "Run Start Build in a one-shot Docker container through Portainer", OnContent = "On", OffContent = "Off" };
    readonly PasswordBox _token = new();
    readonly CheckBox _clearToken = new() { Content = "Remove the saved access token" };
    readonly CheckBox _registerVolumes = new() { Content = "List each successful build in Portainer's Volumes section, named after its version" };
    readonly CheckBox _insecureTls = new() { Content = "Accept a self-signed certificate (Portainer's default HTTPS certificate on port 9443)" };
    readonly TextBox _variables = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 150, MaxHeight = 320 };
    readonly StackPanel _portainerResult = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    readonly Button _addMissing = new() { Content = "Add missing variables", Padding = new Thickness(12, 5, 12, 5), Visibility = Visibility.Collapsed };
    List<string> _missingVariables = new();

    // Template variables as fields (read from the Portainer custom template)
    sealed record VarInput(TemplateField Field, TextBox? Box, PasswordBox? Secret);
    readonly List<VarInput> _varInputs = new();
    readonly List<string> _keptExtras = new();
    readonly StackPanel _varFields = new() { Spacing = 14 };
    readonly StackPanel _varAuto = new() { Spacing = 3 };
    readonly StackPanel _varExtras = new() { Spacing = 6 };
    readonly TextBlock _varStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    readonly Button _loadVars = new() { Content = "Load from template", Padding = new Thickness(12, 4, 12, 4) };
    readonly CheckBox _advanced = new() { Content = "Edit as text (advanced)", Visibility = Visibility.Collapsed };
    readonly StackPanel _advancedPanel = new() { Spacing = 4 };
    bool _fieldsMode, _loadingVars;
    string? _loadedTemplateKey;

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

        // Container builds
        var cont = Card("CONTAINER BUILDS (PORTAINER BE)");
        cont.Children.Add(_containerBuilds);
        cont.Children.Add(new TextBlock { Text = "When on, Start Build deploys the custom template below as a stack named <prefix>-<build number>. The panel follows the container's log for the terminal, progress and History, and removes the stack and its container once the container stops. Prepare and Clean still run on this host.", Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
        cont.Children.Add(Field("PortainerUrl", "Portainer URL", Kind.Text, "The address you open Portainer at, e.g. https://docker-host.lan:9443"));
        var tokenRow = new StackPanel { Spacing = 6 };
        tokenRow.Children.Add(new TextBlock { Text = "Access token", Foreground = Res("TlText"), FontSize = 14 });
        tokenRow.Children.Add(_token);
        tokenRow.Children.Add(_clearToken);
        tokenRow.Children.Add(new TextBlock { Text = "Create one in Portainer under My account → Access tokens. Stored encrypted and never sent back to the browser.", Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
        cont.Children.Add(tokenRow);
        cont.Children.Add(_insecureTls);
        var ids = new Grid { ColumnSpacing = 14 };
        ids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ids.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var env = Field("PortainerEndpointId", "Environment ID", Kind.Number, "The number in the environment's URL in Portainer (…/#!/2/docker/…).");
        var tpl = Field("PortainerTemplateId", "Custom template ID", Kind.Number, "Templates → Custom; the number in the template's URL. It must be a Docker Compose (standalone) template.");
        Grid.SetColumn(tpl, 1);
        ids.Children.Add(env); ids.Children.Add(tpl);
        cont.Children.Add(ids);
        var more = new Grid { ColumnSpacing = 14 };
        for (var i = 0; i < 3; i++) more.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var prefix = Field("PortainerStackPrefix", "Stack name prefix", Kind.Text, "Stacks are named <prefix>-<build number>.");
        var service = Field("PortainerService", "Build service", Kind.Text, "Only for templates with several services: the one that runs the build.");
        var max = Field("PortainerMaxConcurrent", "Concurrent container builds", Kind.Number, "How many container builds may run at once.");
        Grid.SetColumn(service, 1); Grid.SetColumn(max, 2);
        more.Children.Add(prefix); more.Children.Add(service); more.Children.Add(max);
        cont.Children.Add(more);
        cont.Children.Add(_registerVolumes);
        cont.Children.Add(new TextBlock { Text = "Creates a named volume pointing at the build's folder under output_path. The files never move, and deleting the volume in Portainer leaves them on disk.", Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -10, 0, 0) });
        cont.Children.Add(Field("PortainerMemoryLimit", "Memory limit", Kind.Text, "How much RAM a build container may use, e.g. 12g or 16384m. clang needs about 2 GB per CPU thread (more for the final worldserver link); a container that runs out is killed and the build fails. Empty = automatic: 2 GB per CPU thread, capped at this machine's RAM. Fills the template's {{ memory_limit }}."));
        cont.Children.Add(Field("PortainerImage", "Builder image", Kind.Text, "The image the build container runs, filled into the template's {{ builder_image }}. A local tag (build it on the Docker host) or a registry image such as registry.lan:5000/team/acore-builder:1.2."));
        var varsRow = new StackPanel { Spacing = 8 };
        var varsHead = new Grid();
        varsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        varsHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        varsHead.Children.Add(new TextBlock { Text = "Template variables", Foreground = Res("TlText"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_loadVars, 1);
        varsHead.Children.Add(_loadVars);
        _loadVars.Click += async (_, _) => await LoadVariables(quiet: false);
        varsRow.Children.Add(varsHead);
        varsRow.Children.Add(_varStatus);
        varsRow.Children.Add(_varFields);
        varsRow.Children.Add(_varAuto);
        varsRow.Children.Add(_varExtras);
        varsRow.Children.Add(_advanced);
        _advanced.Checked += (_, _) => { if (!_loadingVars) EnterTextMode(fromFields: true); };
        _advanced.Unchecked += async (_, _) => { if (!_loadingVars) await LoadVariables(quiet: true); };
        _variables.FontFamily = (FontFamily)Application.Current.Resources["TlMono"];
        _variables.PlaceholderText = "git_repo=http://gitea.lan:3000/team/azerothcore-wotlk.git\ngit_branch=master\ngit_username=builder\ngit_password=…\noutput_path=/srv/acore/builds\noutput_uid=1000\noutput_gid=1000";
        _advancedPanel.Children.Add(_variables);
        varsRow.Children.Add(_advancedPanel);
        _advancedPanel.Children.Add(new TextBlock { Text = "One name=value per line for the template's {{ name }} fields. The panel always sets build_number, and fills builder_image (Builder image), build_jobs (CPU threads), extra_cmake_args (Extra CMake options) and memory_limit (Memory limit) unless you set them here; anything else falls back to the template's default. Stored encrypted; values of names containing pass, token, secret or key come back as ******** (leave them as they are to keep them).", Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
        cont.Children.Add(varsRow);
        // Reload the fields when the template ID changes.
        _fields["PortainerTemplateId"].LostFocus += async (_, _) => { if (TemplateKey() != _loadedTemplateKey) await LoadVariables(quiet: true); };
        var ptest = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var testPortainer = new Button { Content = "Test connection", Padding = new Thickness(12, 5, 12, 5) };
        testPortainer.Click += async (_, _) => await TestPortainer();
        _addMissing.Click += (_, _) => AddMissingVariables();
        ptest.Children.Add(testPortainer);
        ptest.Children.Add(_addMissing);
        cont.Children.Add(ptest);
        cont.Children.Add(_portainerResult);
        _clearToken.Checked += (_, _) => { _token.Password = ""; _token.IsEnabled = false; };
        _clearToken.Unchecked += (_, _) => _token.IsEnabled = true;
        page.Children.Add(Wrap(cont));

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
        _containerBuilds.IsOn = s.ContainerBuilds;
        _insecureTls.IsChecked = s.PortainerInsecureTls;
        _registerVolumes.IsChecked = s.PortainerRegisterVolumes;
        _variables.Text = s.PortainerVariables ?? "";
        // Start from the saved values, then turn them into fields if the template can be read.
        _fieldsMode = false;
        _ = LoadVariables(quiet: true);
        _token.Password = "";
        _token.PlaceholderText = s.PortainerApiKeySet ? "Saved. Paste a new one to replace it." : "Not saved";
        _clearToken.IsChecked = false;
        _clearToken.Visibility = s.PortainerApiKeySet ? Visibility.Visible : Visibility.Collapsed;
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
        "PortainerUrl" => s.PortainerUrl,
        "PortainerEndpointId" => s.PortainerEndpointId?.ToString(),
        "PortainerTemplateId" => s.PortainerTemplateId?.ToString(),
        "PortainerStackPrefix" => s.PortainerStackPrefix,
        "PortainerService" => s.PortainerService,
        "PortainerMaxConcurrent" => s.PortainerMaxConcurrent?.ToString(),
        "PortainerImage" => s.PortainerImage,
        "PortainerMemoryLimit" => s.PortainerMemoryLimit,
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
        if (!TryNumber("PortainerEndpointId", "Environment ID", out var endpoint)) return null;
        if (!TryNumber("PortainerTemplateId", "Custom template ID", out var template)) return null;
        if (!TryNumber("PortainerMaxConcurrent", "Concurrent container builds", out var maxBuilds)) return null;
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
            ContainerBuilds = _containerBuilds.IsOn,
            PortainerUrl = T("PortainerUrl"),
            PortainerEndpointId = endpoint,
            PortainerTemplateId = template,
            PortainerInsecureTls = _insecureTls.IsChecked == true,
            PortainerStackPrefix = T("PortainerStackPrefix"),
            PortainerService = T("PortainerService"),
            PortainerMaxConcurrent = maxBuilds,
            PortainerImage = T("PortainerImage"),
            PortainerMemoryLimit = T("PortainerMemoryLimit"),
            PortainerRegisterVolumes = _registerVolumes.IsChecked == true,
            PortainerVariables = _fieldsMode ? VariablesText() : _variables.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
            PortainerNewApiKey = string.IsNullOrEmpty(_token.Password) ? null : _token.Password.Trim(),
            PortainerClearApiKey = _clearToken.IsChecked == true,
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

    async Task TestPortainer()
    {
        var dto = Collect();
        if (dto == null) return;
        _portainerResult.Children.Clear();
        _portainerResult.Visibility = Visibility.Visible;
        _portainerResult.Children.Add(new TextBlock { Text = "Testing…", Style = St("TlLabel") });
        _addMissing.Visibility = Visibility.Collapsed;
        var r = await ApiClient.Instance.TestPortainerAsync(dto);
        _portainerResult.Children.Clear();
        var green = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6F, 0xBF, 0x73));
        var red = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x53, 0x4B));
        var amber = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0xB3, 0x41));
        _portainerResult.Children.Add(new TextBlock { Text = r.Message, Foreground = r.Ok ? green : red, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        foreach (var d in r.Details) _portainerResult.Children.Add(new TextBlock { Text = "✓ " + d, Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
        foreach (var w in r.Warnings) _portainerResult.Children.Add(new TextBlock { Text = "⚠ " + w, Foreground = amber, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        if (r.Variables.Count > 0)
        {
            _portainerResult.Children.Add(new TextBlock { Text = "VARIABLES FOR THE NEXT BUILD", Style = St("TlLabel"), Margin = new Thickness(0, 6, 0, 0) });
            foreach (var v in r.Variables)
            {
                var g = new Grid { ColumnSpacing = 12 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                g.Children.Add(new TextBlock { Text = v.Name, Style = St("TlMonoText"), Foreground = Res("TlText") });
                var val = new TextBlock { Text = v.Source == "missing" ? "(no value)" : v.Value.Length == 0 ? "(empty)" : v.Value, Style = St("TlMonoText"), TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
                Grid.SetColumn(val, 1);
                g.Children.Add(val);
                var src = new TextBlock { Text = v.Source, FontSize = 12, Foreground = v.Source == "missing" ? red : Res("TlTextMuted") };
                Grid.SetColumn(src, 2);
                g.Children.Add(src);
                ToolTipService.SetToolTip(g, v.Label);
                _portainerResult.Children.Add(g);
            }
        }
        _missingVariables = r.Variables.Where(v => v.Source == "missing").Select(v => v.Name).ToList();
        // With fields, the missing ones are already marked "required"; the button is for the text editor.
        _addMissing.Visibility = _missingVariables.Count > 0 && !_fieldsMode ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- template variables as fields ----------------

    string TemplateKey() => $"{T("PortainerUrl")}|{T("PortainerEndpointId")}|{T("PortainerTemplateId")}";

    static readonly SolidColorBrush Amber = new(Windows.UI.Color.FromArgb(255, 0xE0, 0xB3, 0x41));

    /// <summary>Reads the custom template's variables from Portainer and shows a field for each.
    /// The current values (fields or text) are sent along, so unsaved edits carry over.</summary>
    async Task LoadVariables(bool quiet)
    {
        if (_loadingVars) return;
        var dto = Collect();
        if (dto == null) return;
        _loadingVars = true;
        _loadVars.IsEnabled = false;
        try
        {
            _varStatus.Text = "Reading the template's variables from Portainer…";
            _varStatus.Foreground = Res("TlTextMuted");
            var r = await ApiClient.Instance.TemplateVariablesAsync(dto);
            if (!r.Ok)
            {
                if (_fieldsMode) _variables.Text = VariablesText();
                EnterTextMode(fromFields: false);
                _advanced.Visibility = Visibility.Collapsed;
                _varStatus.Text = r.Message.TrimEnd('.', ' ') + ". Until then, enter the values as text below.";
                _varStatus.Foreground = Amber;
                if (!quiet) Shell.Current?.ShowToast(r.Message, true);
                return;
            }
            ShowFields(r);
            _loadedTemplateKey = TemplateKey();
            if (!quiet) Shell.Current?.ShowToast("Loaded the template's variables. Fill them in, then Save settings.");
        }
        finally
        {
            _loadVars.IsEnabled = true;
            _loadingVars = false;
        }
    }

    void ShowFields(TemplateVariablesResult r)
    {
        _varInputs.Clear();
        _varFields.Children.Clear();
        foreach (var f in r.Fields)
        {
            var row = new StackPanel { Spacing = 4 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = f.Label, Foreground = Res("TlText"), FontSize = 14 });
            if (f.Label != f.Name)
                head.Children.Add(new TextBlock { Text = f.Name, Style = St("TlMonoText"), FontSize = 12, Foreground = Res("TlTextMuted"), VerticalAlignment = VerticalAlignment.Center });
            if (f.Required)
                head.Children.Add(new TextBlock { Text = "no value yet", FontSize = 11.5, Foreground = Amber, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(head);
            var placeholder = f.Default.Length > 0 ? $"Default: {f.Default}" : "No default: passed empty if left blank";
            if (f.Secret)
            {
                var pw = new PasswordBox { PlaceholderText = f.HasValue ? "Saved. Type a new value to replace it." : placeholder };
                row.Children.Add(pw);
                _varInputs.Add(new VarInput(f, null, pw));
            }
            else
            {
                var box = new TextBox { Text = f.Value, PlaceholderText = placeholder, FontFamily = (FontFamily)Application.Current.Resources["TlMono"] };
                row.Children.Add(box);
                _varInputs.Add(new VarInput(f, box, null));
            }
            if (f.Description.Length > 0) row.Children.Add(new TextBlock { Text = f.Description, Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap });
            _varFields.Children.Add(row);
        }

        _varAuto.Children.Clear();
        if (r.Auto.Count > 0)
        {
            _varAuto.Children.Add(new TextBlock { Text = "FILLED IN BY THE PANEL", Style = St("TlLabel"), Margin = new Thickness(0, 4, 0, 2) });
            foreach (var a in r.Auto)
            {
                var g = new Grid { ColumnSpacing = 12 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(new TextBlock { Text = a.Name, Style = St("TlMonoText"), Foreground = Res("TlText") });
                var from = a.Source == "settings" ? "set as a template variable below (overrides the setting)" : $"from {a.Label}";
                var val = new TextBlock { Text = $"{(a.Value.Length == 0 ? "(empty)" : a.Value)}   ·   {from}", Style = St("TlLabel"), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(val, 1);
                g.Children.Add(val);
                _varAuto.Children.Add(g);
            }
        }

        _keptExtras.Clear();
        _varExtras.Children.Clear();
        void Extra(string line, string why)
        {
            _keptExtras.Add(line);
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = $"⚠ {line.Split('=')[0]} {why}", Foreground = Amber, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 2) };
            remove.Click += (_, _) => { _keptExtras.Remove(line); _varExtras.Children.Remove(g); Shell.Current?.ShowToast("Removed. Save settings to keep the change."); };
            Grid.SetColumn(remove, 1);
            g.Children.Add(remove);
            _varExtras.Children.Add(g);
        }
        foreach (var o in r.Overrides) Extra(o, "is set as a template variable, which overrides its setting.");
        foreach (var u in r.Unused) Extra(u, "is saved, but this template doesn't use it.");

        _fieldsMode = true;
        _varFields.Opacity = 1;
        _varFields.IsHitTestVisible = true;
        _varExtras.Visibility = Visibility.Visible;
        _advancedPanel.Visibility = Visibility.Collapsed;
        _advanced.Visibility = Visibility.Visible;
        _advanced.IsChecked = false; // _loadingVars is set, so this doesn't trigger a reload
        _varStatus.Text = r.Message + (r.Fields.Count == 0 ? " Everything it uses is filled in by the panel." : " Blank fields use the template's default, or are passed empty when it has none.");
        _varStatus.Foreground = r.Fields.Any(f => f.Required) ? Amber : Res("TlTextMuted");
    }

    /// <summary>The fields as name=value lines. An untouched saved secret is sent as the mask, which keeps it.</summary>
    string VariablesText()
    {
        var lines = new List<string>();
        foreach (var v in _varInputs)
        {
            if (v.Box != null && v.Box.Text.Trim().Length > 0) lines.Add($"{v.Field.Name}={v.Box.Text.Trim()}");
            else if (v.Secret != null && v.Secret.Password.Length > 0) lines.Add($"{v.Field.Name}={v.Secret.Password}");
            else if (v.Secret != null && v.Field.HasValue) lines.Add($"{v.Field.Name}={SecretMask.Value}");
            // Blank with a template default: leave it out so the default applies. Blank without one: pass it empty
            // (e.g. git_username for a public repository) instead of blocking the build.
            else if (v.Field.Default.Length == 0) lines.Add($"{v.Field.Name}=");
        }
        lines.AddRange(_keptExtras);
        return string.Join("\n", lines);
    }

    /// <summary>Switches to the name=value text editor: "Edit as text", or the fallback when the template can't be read.</summary>
    void EnterTextMode(bool fromFields)
    {
        if (fromFields && _fieldsMode) _variables.Text = VariablesText();
        _fieldsMode = false;
        _advancedPanel.Visibility = Visibility.Visible;
        if (fromFields)
        {
            // The fields stay visible but inactive; unticking "Edit as text" reloads them from the text.
            _varFields.Opacity = 0.45;
            _varFields.IsHitTestVisible = false;
            _varExtras.Visibility = Visibility.Collapsed;
        }
        else
        {
            _varInputs.Clear();
            _varFields.Children.Clear();
            _varAuto.Children.Clear();
            _varExtras.Children.Clear();
            _keptExtras.Clear();
        }
    }


    void AddMissingVariables()
    {
        var text = _variables.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        foreach (var name in _missingVariables) text += (text.Length > 0 ? "\n" : "") + name + "=";
        _variables.Text = text;
        _addMissing.Visibility = Visibility.Collapsed;
        Shell.Current?.ShowToast("Fill in the new lines, then Save settings.");
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
