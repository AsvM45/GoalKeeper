using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ConfigUI.Views;

/// <summary>
/// Forces the user to type a long, exact paragraph without backspace or typos.
/// On success, generates a one-time HMAC token that the ServiceEngine validates.
/// </summary>
public partial class TypingChallenge : Window
{
    // The paragraph the user must type. Deliberately complex and long.
    private static readonly string[] Paragraphs =
    [
        "I am making a deliberate choice to change my settings. I understand that this system is designed to protect me from impulsive decisions. I accept full responsibility for this modification and confirm that I am not acting under pressure or distraction. My goal is to improve my productivity and focus.",
        "Focused work requires protecting deep attention from constant interruption. By choosing to adjust these settings, I acknowledge that short-term discomfort often precedes meaningful long-term results. I commit to using this freedom wisely and returning to focused work immediately after.",
        "The purpose of friction is not to punish, but to create space between impulse and action. I have waited, I have thought, and I have decided. This change is intentional, measured, and aligned with my genuine goals. I accept accountability for what I choose to do next."
    ];

    private readonly string _targetParagraph;
    private bool _completed;

    public string? CompletionToken { get; private set; }
    public bool WasConfirmed => _completed;

    public TypingChallenge()
    {
        InitializeComponent();

        var rng = new Random();
        _targetParagraph = Paragraphs[rng.Next(Paragraphs.Length)];
        TargetText.Text = _targetParagraph;
        ProgressBar.Maximum = _targetParagraph.Length;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Any backspace or delete = complete reset
        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            e.Handled = true;
            ResetInput("Backspace detected – start over.");
        }
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var typed = InputBox.Text;
        var target = _targetParagraph;

        // Check if typed so far matches the beginning of the target
        if (!target.StartsWith(typed, StringComparison.Ordinal) && typed.Length > 0)
        {
            ResetInput("Typo detected – start over.");
            return;
        }

        ProgressBar.Value = typed.Length;

        if (typed == target)
        {
            ConfirmButton.IsEnabled = true;
            InputBox.IsReadOnly = true;
        }
    }

    private void ResetInput(string reason)
    {
        InputBox.TextChanged -= InputBox_TextChanged;
        InputBox.Text = "";
        InputBox.TextChanged += InputBox_TextChanged;
        ProgressBar.Value = 0;
        ConfirmButton.IsEnabled = false;

        // Flash the border red briefly to indicate reset
        InputBox.BorderBrush = System.Windows.Media.Brushes.Red;
        var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            InputBox.ClearValue(System.Windows.Controls.TextBox.BorderBrushProperty);
            timer.Stop();
        };
        timer.Start();
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        // Generate one-time HMAC token
        var token = GenerateToken();
        CompletionToken = token;

        // Register token with service
        await App.Pipe.SendAsync(Services.PipeMessage.Create("STORE_TOKEN", new { token }));

        _completed = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _completed = false;
        DialogResult = false;
        Close();
    }

    private static string GenerateToken()
    {
        var entropy = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(entropy).ToLowerInvariant();
    }
}
