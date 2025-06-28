using Cocona;
using entraform.AdEntities;
using entraform.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
