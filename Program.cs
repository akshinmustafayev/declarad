using Cocona;
using entraform.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace entraform
{
    public class Program
    {
        static void Main(string[] args)
        {
            var builder = CoconaApp.CreateBuilder();

            bool isDebug = File.Exists("debug");
            builder.Services.AddSingleton<IUserService, UserService>();
            builder.Services.AddSingleton<IEntraFormHclPlanService, EntraFormHclPlanService>();
            builder.Services.AddLogging(logging =>
            {
                logging.AddConsole();
                logging.AddDebug();
                logging.SetMinimumLevel(isDebug ? LogLevel.Debug : LogLevel.Information);
            });

            var app = builder.Build();
            app.AddCommands<EntraformCommands>();
            app.Run();

            /*
            string hclText = "user \"jdoe\" {\r\n    name    = \"John Doe\"\r\n    email   = \"jdoe@contoso.local\"\r\n    ou      = \"OU=Employees,DC=contoso,DC=local\"\r\n    groups  = [\"Staff\", \"VPN Access\"]\r\n    enabled = true\r\n}\r\n\r\ngroup \"VPN Access\" {\r\n    description = \"Group for VPN access\"\r\n    members     = [\"jdoe\", \"asmith\"]\r\n}\r\n\r\nou \"Employees\" {\r\n    path = \"OU=Employees,DC=contoso,DC=local\"\r\n}";
            var parsed = HclParser.HclTemplate.Parse(hclText);

            string json = JsonSerializer.Serialize(parsed);
            Console.WriteLine($"Json: {json}");
            */
        }
    }
}
