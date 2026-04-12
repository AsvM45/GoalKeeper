using ServiceEngine.AI;
using ServiceEngine.Core;
using ServiceEngine.IPC;
using ServiceEngine.Workers;

namespace ServiceEngine;

internal sealed class Program
{
    internal static async Task Main(string[] args)
    {
        // ── Developer safety bypass ────────────────────────────────────────────
        // If this file exists, IsArmed is forced to false regardless of DB state.
        // Create it before running to prevent any DACL/reboot-immunity activation.
        const string DevBypassFlag = @"C:\dev_bypass_flag.txt";
        bool devMode = File.Exists(DevBypassFlag);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "GoalKeeperService";
        });

        // ── Register core services ─────────────────────────────────────────────
        builder.Services.AddSingleton<ScreenTimeLogger>();
        builder.Services.AddSingleton<StateManager>();
        builder.Services.AddSingleton<SecurityEnforcer>(sp =>
            new SecurityEnforcer(sp.GetRequiredService<ScreenTimeLogger>(), devMode));
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
}
