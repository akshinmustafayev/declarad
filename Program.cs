using Cocona;
using declarad.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace declarad
{
    public class Program
    {
        static void Main(string[] args)
        {
            var builder = CoconaApp.CreateBuilder();

            bool isDebug = File.Exists("debug");
            builder.Services.AddSingleton<IUserService, UserService>();
            builder.Services.AddSingleton<IEntraFormHclPlanService, EntraFormHclPlanService>();
            builder.Services.AddSingleton<IEntraFormConnectionService, EntraFormConnectionService>();
            builder.Services.AddLogging(logging =>
            {
                logging.AddConsole();
                logging.AddDebug();
                logging.SetMinimumLevel(isDebug ? LogLevel.Debug : LogLevel.Information);
            });

            var app = builder.Build();
            app.AddCommands<EntraformCommands>();
            app.Run();
        }
    }
}
