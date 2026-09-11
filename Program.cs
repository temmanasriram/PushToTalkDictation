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
            logger.LogInformation("Push-to-Talk Dictation started.");
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
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("PTTD_")
            .Build();

        var settings = new AppSettings();
        configuration.Bind(settings);

        var services = new ServiceCollection();

        services.AddSingleton(settings);
        services.AddSingleton<IConfiguration>(configuration);

        services.AddLogging(builder =>
        {
            var level = Enum.TryParse<LogLevel>(settings.Logging.MinimumLevel, ignoreCase: true, out var parsed)
                ? parsed
                : LogLevel.Information;

            builder.SetMinimumLevel(level);
            builder.AddProvider(new FileLoggerProvider(settings.Logging.FilePath, level));
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
