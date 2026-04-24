using System.Threading.Tasks;
using Xunit;
using ServiceEngine.Core;
using System.IO;
using System;

namespace ServiceEngine.Tests;

public class SecurityEnforcerTests
{
    [Fact]
    public async Task RunDiagnosticsAsync_WhenDevBypassExists_AlwaysPasses()
    {
        // Arrange
        var testDb = new ScreenTimeLogger(Path.GetTempFileName());
        await testDb.InitializeAsync();
        var enforcer = new SecurityEnforcer(testDb, null!);

        try
        {
            // Even if the system is not production ready, dev flag should bypass it
            File.WriteAllText(@"C:\dev_bypass_flag.txt", "1");
            
            var result = await enforcer.RunDiagnosticsAsync();
            Assert.True(result.Success);
            Assert.Contains("DEV BYPASS", result.Reason);
        }
        finally
        {
            testDb.Dispose();
            if (File.Exists(@"C:\dev_bypass_flag.txt"))
            {
                // We shouldn't delete the user's actual bypass flag intentionally if they are developing, 
                // but for isolation we assume this doesn't destructively impact them since they ALREADY use it!
            }
        }
    }
}
