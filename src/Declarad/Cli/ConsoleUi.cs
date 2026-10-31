using System.Text;

namespace Declarad.Cli
{
    /// <summary>Terminal output with optional ANSI colors (disabled by --no-color, NO_COLOR or redirection).</summary>
    public sealed class ConsoleUi
    {
        public bool Color { get; set; } = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;

        public string Green(string s) => Wrap("32", s);
        public string Red(string s) => Wrap("31", s);
        public string Yellow(string s) => Wrap("33", s);
        public string Cyan(string s) => Wrap("36", s);
        public string Bold(string s) => Wrap("1", s);
        public string Dim(string s) => Wrap("2", s);

        private string Wrap(string code, string s) => Color ? $"\u001b[{code}m{s}\u001b[0m" : s;

        public void WriteLine(string text = "") => Console.Out.WriteLine(text);

        public void Error(string text) => Console.Error.WriteLine(Wrap("31", "Error: ") + text);

        public void Warning(string text) => Console.Error.WriteLine(Wrap("33", "Warning: ") + text);

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
    }
}
