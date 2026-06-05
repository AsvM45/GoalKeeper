using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace ConfigUI.Views;

/// <summary>
/// Forces the user to type a 250-word paragraph without copy-paste, backspace, or typos.
/// On success, generates a one-time token that the ServiceEngine validates.
/// </summary>
public partial class TypingChallenge : Window
{
    private const int RequiredWordCount = 250;

    private static readonly string[] WordPool =
    [
        "deliberate", "choice", "settings", "protect", "impulsive", "decisions",
        "responsibility", "modification", "pressure", "distraction", "productivity",
        "focus", "attention", "commitment", "accountability", "intentional",
        "measured", "aligned", "goals", "friction", "purpose", "punish",
        "impulse", "action", "waited", "thought", "decided", "change",
        "wisdom", "returning", "work", "immediately", "deep", "interruption",
        "discomfort", "results", "long-term", "short-term", "meaningful",
        "protecting", "constant", "choosing", "adjust", "acknowledge",
        "0rganize", "c0mmit", "f0cus", "pr0ductive", "resp0nsible",
        "dec1de", "1ntentional", "a11ow", "b1ock", "c0ntinue",
        "Oxygen", "Omit", "0ffset", "Olive", "1etter", "1evel",
    ];

    private readonly string _targetParagraph;
    private int _typedIndex;
    private bool _completed;

    public string? CompletionToken { get; private set; }
    public bool WasConfirmed => _completed;

    public TypingChallenge()
    {
        InitializeComponent();
        _targetParagraph = GenerateChallengeText();
        TargetText.Text = _targetParagraph;
        ProgressBar.Maximum = _targetParagraph.Length;
    }

    private static string GenerateChallengeText()
    {
        var rng = new Random();
        var words = new List<string>(RequiredWordCount);
        while (words.Count < RequiredWordCount)
            words.Add(WordPool[rng.Next(WordPool.Length)]);

        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(words[i]);
            if ((i + 1) % 20 == 0 && i < words.Count - 1)
                sb.Append('.').Append(' ');
        }
        sb.Append('.');
        return sb.ToString();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            e.Handled = true;
            ResetChallenge("Backspace or Delete detected — start over.");
            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ResetChallenge("Paste blocked — type manually.");
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.X && Keyboard.Modifiers == ModifierKeys.Control)
            e.Handled = true;
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var typed = InputBox.Text;

        if (typed.Length < _typedIndex)
        {
            ResetChallenge("Editing detected — start over.");
            return;
        }

        for (int i = _typedIndex; i < typed.Length; i++)
        {
            if (i >= _targetParagraph.Length || typed[i] != _targetParagraph[i])
            {
                ResetChallenge("Typo detected — start over.");
                return;
            }
        }

        _typedIndex = typed.Length;
        ProgressBar.Value = _typedIndex;

        if (_typedIndex == _targetParagraph.Length)
        {
            ConfirmButton.IsEnabled = true;
            InputBox.IsReadOnly = true;
        }
    }

    private void ResetChallenge(string reason)
    {
        _typedIndex = 0;
        InputBox.TextChanged -= InputBox_TextChanged;
        InputBox.Text = "";
        InputBox.TextChanged += InputBox_TextChanged;
        ProgressBar.Value = 0;
        ConfirmButton.IsEnabled = false;
        InputBox.IsReadOnly = false;
        InputBox.Focus();

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
        var token = GenerateToken();
        CompletionToken = token;
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
