using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConfigUI.Services;

namespace ConfigUI.Views;

public partial class SetupWizard : Window
{
    public SetupWizard()
    {
        InitializeComponent();
    }

    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        DiagPanel.Children.Clear();
        DiagPanel.Children.Add(new TextBlock { Text = "Running diagnostics…", Opacity = 0.6 });

        var response = await App.Pipe.SendAsync(PipeMessage.Create(MessageType.RunDiagnostics, new { }));
        DiagPanel.Children.Clear();

        if (response?.Type == MessageType.DiagnosticResult)
        {
            bool success = response.GetBool("success");
            string? reason = response.GetString("reason");

            if (success)
            {
                DiagPanel.Children.Add(MakeDiagRow("SQLite writable", true));
                DiagPanel.Children.Add(MakeDiagRow("WMI available", true));
                DiagPanel.Children.Add(MakeDiagRow("Process enumeration", true));
                ArmButton.IsEnabled = true;
            }
            else
            {
                DiagPanel.Children.Add(MakeDiagRow($"FAILED: {reason}", false));
                ArmButton.IsEnabled = false;
            }
        }
        else
        {
            DiagPanel.Children.Add(new TextBlock
            {
                Text = "Could not reach service. Make sure GoalKeeperService is running.",
                Foreground = Brushes.Tomato,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private async void ArmSystem_Click(object sender, RoutedEventArgs e)
    {
        var warn = MessageBox.Show(
            "WARNING: Arming the system will apply OS-level tamper-proofing.\n\n" +
            "• You will NOT be able to kill the service via Task Manager.\n" +
            "• Nuclear Mode will persist across reboots.\n" +
            "• Recovery requires booting into Windows Safe Mode.\n\n" +
            "You must complete a typing challenge to confirm. Continue?",
            "Arm System – Final Warning",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (warn != MessageBoxResult.Yes) return;

        var challenge = new TypingChallenge();
        if (challenge.ShowDialog() != true || challenge.CompletionToken == null) return;

        var response = await App.Pipe.SendAsync(
            PipeMessage.Create(MessageType.ArmSystem,
                new { challengeToken = challenge.CompletionToken }));

        if (response?.GetBool("isArmed") == true)
        {
            MessageBox.Show(
                "System armed successfully!\n\nGoalKeeper is now tamper-proof.\n\n" +
                "REMEMBER: If you ever lock yourself out, boot Windows into Safe Mode to uninstall.",
                "Armed", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        else
        {
            MessageBox.Show(
                $"Arming failed: {response?.GetString("reason") ?? "Unknown error"}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static StackPanel MakeDiagRow(string text, bool ok)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Controls.Primitives.ToggleButton
        {
            // PackIcon substitution via TextBlock (actual icon lib usage)
        });
        var icon = new TextBlock
        {
            Text = ok ? "✓" : "✗",
            Foreground = ok ? Brushes.LimeGreen : Brushes.Tomato,
            FontSize = 16,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        row.Children.Add(icon);
        row.Children.Add(label);
        return row;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
