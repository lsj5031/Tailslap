using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using TailSlap;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (UiaProbeCommand.IsProbeInvocation(args))
        {
            Environment.ExitCode = UiaProbeCommand.Run(args);
            return;
        }

        try
        {
            Logger.Log("App starting");
        }
        catch { }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            try
            {
                Logger.Error("UI Exception", e.Exception);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                Logger.Error("Non-UI Exception", e.ExceptionObject as Exception);
            }
            catch { }
        };

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch { }

        using var mutex = new Mutex(true, "TailSlap_SingleInstance", out bool created);
        if (!created)
            return;

        ApplicationConfiguration.Initialize();

        var services = new ServiceCollection();
        ConfigureServices(services);

        using var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true }
        );

        try
        {
            var mainForm = serviceProvider.GetRequiredService<MainForm>();

            // Initialize clipboard service with UI context (must be after Form is created)
            // The WindowsFormsSynchronizationContext is installed when the first Control is created
            ClipboardService.Initialize();

            // Use ApplicationContext to run message loop without showing the form.
            // This is the standard pattern for tray-only applications.
            var context = new ApplicationContext();
            mainForm.FormClosed += (_, __) => context.ExitThread();

            // Create handle without showing the form (needed for hotkeys and message pump)
            mainForm.CreateControl();
            _ = mainForm.Handle; // Ensure handle is created

            Application.Run(context);
        }
        finally
        {
            Logger.Shutdown();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ClipboardHelper>();
        services.AddSingleton<ITextRefinerFactory, TextRefinerFactory>();
        services.AddSingleton<IRemoteTranscriberFactory, RemoteTranscriberFactory>();
        services.AddSingleton<IAudioRecorderFactory, AudioRecorderFactory>();
        services.AddSingleton<IRealtimeTranscriberFactory, RealtimeTranscriberFactory>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IRefinementController, RefinementController>();
        services.AddSingleton<INotificationService, NotificationServiceAdapter>();
        services.AddSingleton<ILoggerService, LoggerServiceAdapter>();
        services.AddSingleton<IAutoStartService, AutoStartServiceAdapter>();
        services.AddSingleton<TextTyper>();
        services.AddSingleton<ITranscriptionResultSink, TranscriptionResultSink>();
        services.AddSingleton<ITypelessController>(sp => new TypelessController(
            sp.GetRequiredService<IConfigService>(),
            sp.GetRequiredService<IRemoteTranscriberFactory>(),
            sp.GetRequiredService<IAudioRecorderFactory>(),
            sp.GetRequiredService<TextTyper>(),
            sp.GetRequiredService<ITranscriptionResultSink>()
        ));
        services.AddSingleton<ITranscriptionController>(sp => new TranscriptionController(
            sp.GetRequiredService<IConfigService>(),
            sp.GetRequiredService<IRemoteTranscriberFactory>(),
            sp.GetRequiredService<IAudioRecorderFactory>(),
            sp.GetRequiredService<TextTyper>(),
            sp.GetRequiredService<ITranscriptionResultSink>()
        ));
        services.AddSingleton<KeyboardHook>(sp =>
        {
            var config = sp.GetRequiredService<IConfigService>();
            var cfg = config.CreateValidatedCopy();
            return new KeyboardHook(cfg.TypelessHotkey);
        });
        services.AddSingleton<IRealtimeTranscriptionController>(
            sp => new RealtimeTranscriptionController(
                sp.GetRequiredService<IConfigService>(),
                sp.GetRequiredService<IClipboardService>(),
                sp.GetRequiredService<IRealtimeTranscriberFactory>(),
                sp.GetRequiredService<IAudioRecorderFactory>(),
                sp.GetRequiredService<ITranscriptionResultSink>()
            )
        );

        services.AddTransient<MainForm>();

        services
            .AddHttpClient(
                HttpClientNames.Default,
                client =>
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    MaxConnectionsPerServer = 10,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                }
            )
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
    }
}
