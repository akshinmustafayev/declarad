using System.Runtime.InteropServices;
using System.Text;

namespace Declarad.Cli
{
    /// <summary>
    /// Terminal output with optional ANSI colors. Colors are off when disabled by --no-color, NO_COLOR or TERM=dumb,
    /// when the stream is redirected, and on Windows consoles that can't interpret escape sequences.
    /// </summary>
    public sealed class ConsoleUi
    {
        private static readonly bool ColorAllowed =
            Environment.GetEnvironmentVariable("NO_COLOR") == null && Environment.GetEnvironmentVariable("TERM") != "dumb";

        private bool _errorColor = ColorAllowed && !Console.IsErrorRedirected && EnableAnsi(StdErrorHandle);

        public bool Color { get; set; } = ColorAllowed && !Console.IsOutputRedirected && EnableAnsi(StdOutputHandle);

        /// <summary>Disables colors on both streams.</summary>
        public void DisableColor()
        {
            Color = false;
            _errorColor = false;
        }

        public string Green(string s) => Wrap("32", s);
        public string Red(string s) => Wrap("31", s);
        public string Yellow(string s) => Wrap("33", s);
        public string Cyan(string s) => Wrap("36", s);
        public string Bold(string s) => Wrap("1", s);
        public string Dim(string s) => Wrap("2", s);

        private string Wrap(string code, string s) => Wrap(code, s, Color);

        private static string Wrap(string code, string s, bool color) => color ? $"\u001b[{code}m{s}\u001b[0m" : s;

        public void WriteLine(string text = "") => Console.Out.WriteLine(text);

        public void Error(string text) => Console.Error.WriteLine(Wrap("31", "Error: ", _errorColor) + text);

        public void Warning(string text) => Console.Error.WriteLine(Wrap("33", "Warning: ", _errorColor) + text);

        public bool IsInteractive => !Console.IsInputRedirected;

        /// <summary>Asks for explicit "yes", like Terraform.</summary>
        public bool Confirm(string question)
        {
            WriteLine(Bold(question));
            WriteLine("  Only 'yes' will be accepted to approve.");
            Console.Out.Write(Bold("\n  Enter a value: "));
            return Console.ReadLine()?.Trim() == "yes";
        }

        public string? Prompt(string prompt, bool secret)
        {
            if (!IsInteractive)
            {
                return null;
            }
            Console.Error.Write(prompt);
            if (!secret)
            {
                return Console.ReadLine();
            }
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    break;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0)
                    {
                        sb.Length--;
                    }
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                }
            }
            Console.Error.WriteLine();
            return sb.ToString();
        }

        private const int StdOutputHandle = -11;
        private const int StdErrorHandle = -12;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        /// <summary>
        /// Windows consoles (cmd, Windows PowerShell) print escape sequences literally ("?[32m") unless virtual terminal
        /// processing is switched on for the handle. Returns whether the console can show ANSI colors.
        /// </summary>
        private static bool EnableAnsi(int stdHandle)
        {
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }
            try
            {
                IntPtr handle = GetStdHandle(stdHandle);
                if (!GetConsoleMode(handle, out uint mode))
                {
                    return false;
                }
                return (mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}
