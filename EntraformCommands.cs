using Cocona;
using entraform.AdEntities;
using entraform.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace entraform
{
    public class EntraformCommands
    {
        private readonly ILogger<EntraformCommands> _logger;
        private readonly IUserService _userService;
        private readonly IEntraFormHclPlanService _entraFormHclPlanService;

        public EntraformCommands(
            ILogger<EntraformCommands> logger,
            IUserService userService,
            IEntraFormHclPlanService entraFormHclPlanService)
        {
            _logger = logger;
            _userService = userService;
            _entraFormHclPlanService = entraFormHclPlanService;
        }

        [Command("plan")]
        public void Plan(
            [Argument(Name = "Files", Description = "files.")]
            IEnumerable<string> Files = null
        )
        {
            // Show message
            _logger.LogInformation("Planning execution for file: {Files}", Files);

            // Init variables
            StringBuilder hclPlan = new StringBuilder("");

            // Get HCL plan from files
            hclPlan = _entraFormHclPlanService.GetHclPlanFromFiles(Files);

            // Try parse HCL plan
            AdPlan adPlan = _entraFormHclPlanService.GetHclPlan(hclPlan.ToString());
            Console.WriteLine(adPlan.Users[0].Name);
            Console.WriteLine(adPlan.Users[0].Surname);
            Console.WriteLine(adPlan.Users[0].DisplayName);
            Console.WriteLine(adPlan.Users[0].SamAccountName);
            Console.WriteLine(adPlan.Users[0].Email);
            Console.WriteLine(adPlan.Users[0].Ou);
            Console.WriteLine(adPlan.Users[0].Groups[0].Name);
            Console.WriteLine(adPlan.Users[0].Groups[1].Name);
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine(adPlan.Groups[0].Name);
            Console.WriteLine(adPlan.Groups[0].Members[0].SamAccountName);
            Console.WriteLine(adPlan.Groups[0].Members[1].SamAccountName);
            Console.WriteLine(Environment.NewLine);
            Console.WriteLine(Environment.NewLine);

            _logger.LogDebug("Parsed HCL:\n{HCL}", hclPlan.ToString());
        }

        [Command("user add")]
        public void AddUser([Option('u')] string username, [Option('o')] string ou)
        {
            _logger.LogInformation("Adding user {User} to OU {Ou}", username, ou);
            _userService.Add(username, ou);
        }
    }
}
