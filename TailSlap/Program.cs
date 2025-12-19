using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    [STAThread]
    static void Main()
    {
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
                Logger.Log("UI Exception: " + e.Exception);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                Logger.Log("Non-UI Exception: " + e.ExceptionObject);
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
            Application.Run(mainForm);
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
        services.AddSingleton<ITextRefinerFactory, TextRefinerFactory>();
        services.AddSingleton<IRemoteTranscriberFactory, RemoteTranscriberFactory>();
        services.AddSingleton<IHistoryService, HistoryService>();

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
