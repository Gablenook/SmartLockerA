using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Keypad
{
    public partial class NumericKeypadControl : UserControl
    {
        private readonly StringBuilder input = new StringBuilder();

        private int passcodeLength = 9;            // default
        public bool AutoSubmitOnLength { get; set; } = true;
        public bool RequireExactLengthOnSubmit { get; set; } = false;

        public event EventHandler<string>? PasscodeComplete;

        public NumericKeypadControl()
        {
            InitializeComponent();
        }

        private void Digit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            if (input.Length >= passcodeLength)
                return;

            input.Append(btn.Content?.ToString());

            if (AutoSubmitOnLength && input.Length == passcodeLength)
            {
                RaiseCompleteAndOptionallyReset();
            }
        }

        private void Submit_Click(object sender, RoutedEventArgs e)
        {
            if (input.Length == 0)
                return;

            if (RequireExactLengthOnSubmit && input.Length != passcodeLength)
            {
                // For kiosk security/UX: don't leak details; just ignore or beep.
                // You can also raise a separate event if you want host feedback.
                return;
            }

            RaiseCompleteAndOptionallyReset();
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => input.Clear();

        private void Backspace_Click(object sender, RoutedEventArgs e)
        {
            if (input.Length > 0)
                input.Remove(input.Length - 1, 1);
        }

        public void SetPasscodeLength(int length)
        {
            passcodeLength = Math.Max(1, length);
            input.Clear();
        }

        public void Reset() => input.Clear();

        public string GetValue() => input.ToString();

        private void RaiseCompleteAndOptionallyReset()
        {
            var value = input.ToString();
            PasscodeComplete?.Invoke(this, value);

            // Usually correct for kiosks: clear immediately so a second user starts clean.
            input.Clear();
        }
    }
}


