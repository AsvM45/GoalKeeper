using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ServiceEngine.Core;

/// <summary>
/// A global utility for executing background tasks safely, ensuring that any unhandled exceptions
/// are caught and logged rather than terminating the process or silently swallowing errors.
/// </summary>
public static class SafeTask
{
    public static void Run(Func<Task> action, ILogger logger, string taskName)
    {
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FATAL CRASH in background task: {TaskName}", taskName);
            }
        });
    }

    public static void Run(Action action, ILogger logger, string taskName)
    {
        Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FATAL CRASH in background task: {TaskName}", taskName);
            }
        });
    }
}
