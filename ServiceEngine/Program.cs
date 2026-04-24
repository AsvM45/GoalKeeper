using ServiceEngine.AI;
using ServiceEngine.Core;
using ServiceEngine.IPC;
using ServiceEngine.Workers;

using Serilog;

namespace ServiceEngine;

internal sealed class Program
{
    internal static async Task Main(string[] args)
    {
        // ── Global Exception Handling & Serilog ────────────────────────────────
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(@"C:\ProgramData\GoalKeeper\logs\service-engine-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Fatal((Exception)e.ExceptionObject, "AppDomain Unhandled Exception");
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Fatal(e.Exception, "TaskScheduler Unobserved Exception");
            e.SetObserved();
        };

        try
        {
            // ── Developer safety bypass ────────────────────────────────────────────
            // If this file exists, IsArmed is forced to false regardless of DB state.
            // Create it before running to prevent any DACL/reboot-immunity activation.
            const string DevBypassFlag = @"C:\dev_bypass_flag.txt";
            bool devMode = File.Exists(DevBypassFlag);

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog(Log.Logger);
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "GoalKeeperService";
            });

        // ── Register core services ─────────────────────────────────────────────
        builder.Services.AddSingleton<ScreenTimeLogger>();
        builder.Services.AddSingleton<StateManager>();
        builder.Services.AddSingleton<SecurityEnforcer>(sp =>
            new SecurityEnforcer(
                sp.GetRequiredService<ScreenTimeLogger>(),
                devMode,
                sp.GetRequiredService<ILogger<SecurityEnforcer>>()));
        builder.Services.AddSingleton<PipeServer>();
        builder.Services.AddSingleton<AIClient>();

        // ── Register background workers ────────────────────────────────────────
        builder.Services.AddHostedService<ProcessWatcher>();
        builder.Services.AddHostedService<WindowWatcher>();
        builder.Services.AddHostedService<PipeServer>(sp => sp.GetRequiredService<PipeServer>());

        var host = builder.Build();

        // ── Initialize DB and apply schema ─────────────────────────────────────
        var logger = host.Services.GetRequiredService<ScreenTimeLogger>();
        await logger.InitializeAsync();

        // ── Apply reboot-immunity locks if nuclear mode persisted ──────────────
        var security = host.Services.GetRequiredService<SecurityEnforcer>();
        await security.ApplyStartupLocksAsync();

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
