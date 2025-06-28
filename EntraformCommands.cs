using Cocona;
using entraform.AdEntities;
using entraform.Services;
using Microsoft.Extensions.Logging;
using Naos.Recipes.WinRM;
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
            if (Files == null)
            {
                _logger.LogInformation("Planning execution for all .ef files in a directory");
            }
            else
            {
                _logger.LogInformation("Planning execution for files: {Files}", string.Join(", ", Files));
            }

            // Init variables
            StringBuilder hclPlan = new StringBuilder("");

            // Get HCL plan from files
            hclPlan = _entraFormHclPlanService.GetHclPlanFromFiles(Files);

            // Try parse HCL plan
            AdPlan adPlan = _entraFormHclPlanService.GetHclPlan(hclPlan.ToString());

            // Test
            var machineManager = new MachineManager(
                "192.168.231.142",
                "Administrator2",
                "A123456a".ToSecureString(),
                autoManageTrustedHosts: true);
            var fileObjectsWithTwoTypedParameters = machineManager.RunScript("{ get-content c:\\windows\\system32\\drivers\\etc\\hosts }");
            
            foreach(var line in fileObjectsWithTwoTypedParameters)
            {
                Console.WriteLine("Result content: " + line.ToString());
            }

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
