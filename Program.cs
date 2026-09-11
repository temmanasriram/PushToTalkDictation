using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PushToTalkDictation.Audio;
using PushToTalkDictation.Configuration;
using PushToTalkDictation.Diagnostics;
using PushToTalkDictation.Injection;
using PushToTalkDictation.Input;
using PushToTalkDictation.Stt;
using PushToTalkDictation.Tray;

namespace PushToTalkDictation;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Global\PushToTalkDictation.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Push-to-Talk Dictation is already running. Look for the microphone icon in the system tray.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // TrayApplicationContext captures SynchronizationContext.Current in its constructor so
        // the pipeline can marshal UI updates back here. WinForms does not install one until
        // Application.Run starts the message loop, which is after the container builds the tray,
        // so install it up front. Application.Run keeps an existing WinForms context.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var services = BuildServices();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        Application.ThreadException += (_, e) =>
            logger.LogError(e.Exception, "Unhandled exception on the UI thread.");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception in the process.");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };

        try
        {
            // Built on the UI thread on purpose: the hook and the WinForms timers
            // inside HotkeyWatcher require the thread that pumps messages.
            var tray = services.GetRequiredService<TrayApplicationContext>();
            logger.LogInformation("{App} started.", AppInfo.NameAndVersion);
            Application.Run(tray);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error during startup.");
            MessageBox.Show(ex.Message, "Push-to-Talk Dictation failed to start",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // DictationController is IAsyncDisposable, so the provider must be torn
            // down asynchronously — a synchronous Dispose() would throw on it.
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            logger.LogInformation("Shut down cleanly.");
        }
    }

    private static ServiceProvider BuildServices()
    {
        // Two layers, in order: the documented defaults, then the settings UI's overrides.
        // Keeping them separate means the UI never has to rewrite appsettings.json and strip
        // its comments - see SettingsStore. Environment variables still win over both.
        var baseBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(SettingsStore.FileName, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("PTTD_")
            .Build();

        var settings = new AppSettings();
        configuration.Bind(settings);

        // What the settings UI diffs against, so it writes only genuine changes.
        var baseline = new AppSettings();
        baseBuilder.AddEnvironmentVariables("PTTD_").Build().Bind(baseline);

        var services = new ServiceCollection();

        services.AddSingleton(settings);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(new SettingsStore(baseline));
        services.AddSingleton<SettingsService>();

        var level = Enum.TryParse<LogLevel>(settings.Logging.MinimumLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        // Registered so the settings UI can change the level without a restart.
        var fileLogger = new FileLoggerProvider(settings.Logging.FilePath, level);
        services.AddSingleton(fileLogger);

        services.AddLogging(builder =>
        {
            // Trace, not the configured level: the provider above owns the filtering, so
            // that it can be changed at runtime. Without this the builder's filter would
            // still be pinned to whatever the level was at startup.
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(fileLogger);
        });

        services.AddHttpClient(HttpSpeechToTextEngine.HttpClientName);

        // Input
        services.AddSingleton<GlobalKeyboardHook>();
        services.AddSingleton<HotkeyWatcher>();

        // Audio
        services.AddSingleton<IAudioRecorder, AudioRecorder>();

        // STT — all three are registered; the factory picks the configured one.
        // Add your own engine here and in SpeechToTextEngineFactory.
        services.AddSingleton<SherpaOnnxSpeechToTextEngine>();
        services.AddSingleton<WhisperNetSpeechToTextEngine>();
        services.AddSingleton<HttpSpeechToTextEngine>();
        services.AddSingleton<ISpeechToTextEngine>(SpeechToTextEngineFactory.Create);

        // Output
        services.AddSingleton<ITextInjector, TextInjector>();

        // Orchestration + UI
        services.AddSingleton<DictationController>();
        services.AddSingleton<TrayApplicationContext>();

        return services.BuildServiceProvider();
    }
}
