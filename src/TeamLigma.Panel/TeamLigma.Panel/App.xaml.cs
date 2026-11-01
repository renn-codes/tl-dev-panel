using Uno.Resizetizer;

namespace TeamLigma.Panel;

public partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
#if __WASM__ || HAS_UNO_WASM
        Uno.WinRTFeatureConfiguration.Storage.Pickers.WasmConfiguration =
            Uno.WasmPickerConfiguration.FileSystemAccessApiWithFallback;
#endif
    }

    public static Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "Team Ligma Development Panel" };
        MainWindow.Content = new Views.RootView();
        MainWindow.SetWindowIcon();
        MainWindow.Activate();
    }

    public static void InitializeLogging()
    {
#if DEBUG
        var factory = LoggerFactory.Create(builder =>
        {
#if __WASM__
            builder.AddProvider(new global::Uno.Extensions.Logging.WebAssembly.WebAssemblyConsoleLoggerProvider());
#else
            builder.AddConsole();
#endif
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("Uno", LogLevel.Warning);
            builder.AddFilter("Windows", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);
        });
        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;
#if HAS_UNO
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
#endif
    }
}
