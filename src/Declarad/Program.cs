using Cocona;
using Declarad.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Declarad
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Global options before the subcommand, as in Terraform: -chdir=DIR.
            var remaining = Chdir.Apply(args);
            if (remaining == null)
            {
                return 1;
            }
            args = remaining;

            // Logging must be configured before the host is built, so the flag is read up front.
            bool verbose = args.Contains("-v") || args.Contains("--verbose");

            var builder = CoconaApp.CreateBuilder(args, options => options.TreatPublicMethodsAsCommands = false);
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            // Logs go to stderr so plan output stays clean for piping.
            builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

            var app = builder.Build();
            app.AddCommands<Commands>();
            // Cocona sets the process exit code from the int each command returns.
            app.Run();
            return Environment.ExitCode;
        }
    }
}
