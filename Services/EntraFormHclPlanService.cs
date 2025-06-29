using entraform.AdEntities;
using entraform.AdEntities.Group;
using entraform.AdEntities.OrganizationalUnit;
using entraform.AdEntities.User;
using entraform.Utils;
using Microsoft.Extensions.Logging;
using Octopus.CoreParsers.Hcl;
using Sprache;
using System.Text;

namespace entraform.Services
{
    public interface IEntraFormHclPlanService
    {
        StringBuilder GetHclPlanFromFiles(IEnumerable<string> Files);
        AdPlan GetHclPlan(string hclPlan);
        void CheckServerConnection();
    }

    public class EntraFormHclPlanService : IEntraFormHclPlanService
    {
        private readonly ILogger<EntraFormHclPlanService> _logger;
        private AdPlan adPlan = new AdPlan();

        public EntraFormHclPlanService(ILogger<EntraFormHclPlanService> logger)
        {
            _logger = logger;
        }

        public StringBuilder GetHclPlanFromFiles(IEnumerable<string> Files)
        {
            StringBuilder hclPlan = new StringBuilder("");

            // Combine parsed files in one line
            if (Files == null)
            {
                string[] allEFFiles = Directory.GetFiles(Environment.CurrentDirectory, "*.ef", SearchOption.AllDirectories);

                if (allEFFiles != null)
                {
                    foreach (string edFile in allEFFiles)
                    {
                        hclPlan.Append(File.ReadAllText(edFile));
                        hclPlan.Append(Environment.NewLine);
                        hclPlan.Append(Environment.NewLine);
                    }
                }
                else
                {
                    _logger.LogError("No .ef files found in the current directory or subdirectories.");
                    Environment.Exit(1);
                }
            }
            else
            {
                if (Files.Count() > 0)
                {
                    foreach (string file in Files)
                    {
                        if (File.Exists(file))
                        {
                            hclPlan.Append(File.ReadAllText(file));
                            hclPlan.Append(Environment.NewLine);
                            hclPlan.Append(Environment.NewLine);
                        }
                    }
                }
            }

            return hclPlan;
        }

        public AdPlan GetHclPlan(string hclPlan)
        {
            try 
            { 
                HclElement parsedHcl = HclParser.HclTemplate.Parse(hclPlan);
                if (parsedHcl.Children != null && parsedHcl.Children.Count() > 0)
                {
                    foreach (HclElement hclElement in parsedHcl.Children)
                    {
                        switch(hclElement.Name.ToLower())
                        {

                            case "configuration":
                                {
                                    MapConfiguration(hclElement);
                                }
                                break;
                            case "user":
                                {
                                    MapAdUser(hclElement);
                                }
                                break;
                            case "group":
                                {
                                    MapAdGroup(hclElement);
                                }
                                break;
                            case "ou":
                                {
                                    MapAdOrganizationalUnit(hclElement);
                                }
                                break;
                            case "gpo":
                                {
                                    
                                }
                                break;
                            case "resource":
                                {
                                    
                                }
                                break;
                            case "variable":
                                {

                                }
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to parse HCL file. Please fix error: {Error}", ex.Message);
                Environment.Exit(1);
            }

            return adPlan;
        }

        private void MapConfiguration(HclElement hclElement)
        {
            AdConnection adConnection = new AdConnection();
            adConnection.Name = hclElement.Value;

            if (hclElement.Children != null && hclElement.Children.Count() > 0)
            {
                foreach (HclElement property in hclElement.Children)
                {
                    switch (property.Name.ToLower())
                    {
                        case "server":
                            adConnection.Server = property.Value;
                            break;
                        case "user":
                            adConnection.User = property.Value;
                            break;
                        case "password":
                            adConnection.Password = property.Value;
                            break;
                    }
                }
            }

            adPlan.Connection = adConnection;
        }

        private void MapAdUser(HclElement hclElement)
        {
            AdUser adUser = new AdUser();

            if(hclElement.Children != null && hclElement.Children.Count() > 0)
            {
                foreach(HclElement property in hclElement.Children)
                {
                    switch (property.Name.ToLower())
                    {
                        case "name":
                            adUser.Name = property.Value;
                            break;
                        case "surname":
                            adUser.Surname = property.Value;
                            break;
                        case "displayname":
                            adUser.DisplayName = property.Value;
                            break;
                        case "samaccountname":
                            adUser.SamAccountName = property.Value;
                            break;
                        case "email":
                            adUser.Email = property.Value;
                            break;
                        case "ou":
                            adUser.Ou = property.Value;
                            break;
                        case "enabled":
                            adUser.Enabled = bool.TryParse(property.Value, out var result) && result;
                            break;
                        case "groups":
                            {
                                if (property.Children != null && property.Children.Count() > 0)
                                {
                                    foreach (HclElement group in property.Children)
                                    {
                                        if (!string.IsNullOrEmpty(group.Value))
                                        {
                                            if (adUser.Groups?.Count > 0 && adUser.Groups.Where(x => x.Name == group.Value).Any())
                                            {
                                                _logger.LogError("Duplicate user group {Group} for user with SamAccountName {User} already specified. Please remove duplicate data.", group.Value, hclElement.Value);
                                                Environment.Exit(1);
                                            }

                                            adUser.Groups?.Add(new AdGroup { Name = group.Value });
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
            }

            if(adUser.Name == null)
            {
                _logger.LogError("Name for user {User} is required but not provided in the file.", hclElement.Value);
                Environment.Exit(1);
            }

            if (adUser.Surname == null)
            {
                _logger.LogError("Surname for user {User} is required but not provided in the file.", hclElement.Value);
                Environment.Exit(1);
            }

            if (adUser.SamAccountName == null)
            {
                _logger.LogError("SamAccountName for user {User} is required but not provided in the file.", hclElement.Value);
                Environment.Exit(1);
            }

            if (adUser.DisplayName == null)
            {
                adUser.DisplayName = $"{adUser.Name} {adUser.Surname}";
            }

            var existingUsers = new HashSet<string>(adPlan.ActionSteps.Where(x => x.AdItem is AdUser user).Select(x => ((AdUser)x.AdItem!).SamAccountName), StringComparer.OrdinalIgnoreCase);
            if (existingUsers.Contains(adUser.SamAccountName))
            {
                _logger.LogError("Duplicate user with SamAccountName {User} already specified. Please remove duplicate data.", hclElement.Value);
                Environment.Exit(1);
            }

            adPlan.ActionSteps.Add(
                new AdActionStep
                {
                    Id = Guid.NewGuid(),
                    Ordering = adPlan.ActionSteps.Count + 1,
                    AdItem = adUser,
                    Type = AdItemType.User,
                    Operation = AdItemOperation.None
                }
            );
        }

        private void MapAdGroup(HclElement hclElement)
        {
            AdGroup adGroup = new AdGroup();
            adGroup.Name = hclElement.Value;

            if (hclElement.Children != null && hclElement.Children.Count() > 0)
            {
                foreach (HclElement property in hclElement.Children)
                {
                    switch (property.Name.ToLower())
                    {
                        case "description":
                            adGroup.Description = property.Value;
                            break;
                        case "members":
                            {
                                if (property.Children != null && property.Children.Count() > 0)
                                {
                                    foreach (HclElement user in property.Children)
                                    {
                                        if (!string.IsNullOrEmpty(user.Value))
                                        {
                                            if (adGroup.Members?.Count > 0 && adGroup.Members.Where(x => x.SamAccountName == user.Value).Any())
                                            {
                                                _logger.LogError("Duplicate user {User} for group with name {Group} already specified. Please remove duplicate data.", user.Value, hclElement.Value);
                                                Environment.Exit(1);
                                            }

                                            adGroup.Members?.Add(new AdUser { SamAccountName = user.Value });
                                        }
                                    }
                                }
                            }
                            break;
                        case "memberof":
                            {
                                if (property.Children != null && property.Children.Count() > 0)
                                {
                                    foreach (HclElement group in property.Children)
                                    {
                                        if (!string.IsNullOrEmpty(group.Value))
                                        {
                                            if (adGroup.MemberOf?.Count > 0 && adGroup.MemberOf.Where(x => x.Name == group.Value).Any())
                                            {
                                                _logger.LogError("Duplicate group {Group} for group with name {Group} already specified. Please remove duplicate data.", group.Value, hclElement.Value);
                                                Environment.Exit(1);
                                            }

                                            adGroup.MemberOf?.Add(new AdGroup { Name = group.Value });
                                        }
                                    }
                                }
                            }
                            break;
                    }
                }
            }

            var existingGroups = new HashSet<string>(adPlan.ActionSteps.Where(x => x.AdItem is AdGroup group).Select(x => ((AdGroup)x.AdItem!).Name), StringComparer.OrdinalIgnoreCase);
            if (existingGroups.Contains(adGroup.Name))
            {
                _logger.LogError("Duplicate group with name {Group} already specified. Please remove duplicate data.", hclElement.Value);
                Environment.Exit(1);
            }

            adPlan.ActionSteps.Add(
                new AdActionStep
                {
                    Id = Guid.NewGuid(),
                    Ordering = adPlan.ActionSteps.Count + 1,
                    AdItem = adGroup,
                    Type = AdItemType.Group,
                    Operation = AdItemOperation.None
                }
            );
        }

        private void MapAdOrganizationalUnit(HclElement hclElement)
        {
            AdOrganizationalUnit adOrganizationalUnit = new AdOrganizationalUnit();
            adOrganizationalUnit.Name = hclElement.Value;

            if (hclElement.Children != null && hclElement.Children.Count() > 0)
            {
                foreach (HclElement property in hclElement.Children)
                {
                    switch (property.Name.ToLower())
                    {
                        case "distinguishedname":
                            adOrganizationalUnit.DistinguishedName = property.Value;
                            break;
                    }
                }
            }

            var existingOrganizationalUnits = new HashSet<string>(adPlan.ActionSteps.Where(x => x.AdItem is AdOrganizationalUnit organizationalUnit).Select(x => ((AdOrganizationalUnit)x.AdItem!).Name), StringComparer.OrdinalIgnoreCase);
            if (existingOrganizationalUnits.Contains(adOrganizationalUnit.Name))
            {
                _logger.LogError("Duplicate organizational unit with name {Name} already specified. Please remove duplicate data.", hclElement.Value);
                Environment.Exit(1);
            }

            adPlan.ActionSteps.Add(
                new AdActionStep
                {
                    Id = Guid.NewGuid(),
                    Ordering = adPlan.ActionSteps.Count + 1,
                    AdItem = adOrganizationalUnit,
                    Type = AdItemType.OrganizationalUnit,
                    Operation = AdItemOperation.None
                }
            );
        }

        public void CheckServerConnection()
        {
            if(adPlan.Connection.Server == null || adPlan.Connection.User == null || adPlan.Connection.Password == null)
            {
                _logger.LogError("AD connection details are not provided. Please provide server, user, and password details.");
                Environment.Exit(1);
            }
            
            bool isConnected = TcpConnectionChecker.CheckTcpConnection(adPlan.Connection.Server, 5985, 5000);
            if(isConnected == false)
            {
                _logger.LogError("Failed to connect to the server {Server} on port 5985. Please check your connection details for {Name} connection.", adPlan.Connection.Server, adPlan.Connection.Name);
                Environment.Exit(1);
            }
        }
    }
}
