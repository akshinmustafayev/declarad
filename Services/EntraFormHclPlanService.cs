using entraform.AdEntities;
using entraform.AdEntities.Group;
using entraform.AdEntities.User;
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

                if(parsedHcl.Children != null && parsedHcl.Children.Count() > 0)
                {
                    foreach (HclElement hclElement in parsedHcl.Children)
                    {
                        switch(hclElement.Name.ToLower())
                        {
                            case "user":
                                {
                                    adPlan.Users?.Add(MapAdUser(hclElement));
                                }
                                break;
                            case "group":
                                {
                                    adPlan.Groups?.Add(MapAdGroup(hclElement));
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

        private AdUser MapAdUser(HclElement hclElement)
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

            if (adPlan.Users?.Count > 0 && adPlan.Users.Where(x => x.SamAccountName == adUser.SamAccountName).Any())
            {
                _logger.LogError("Duplicate user with SamAccountName {User} already specified. Please remove Duplicate data.", hclElement.Value);
                Environment.Exit(1);
            }

            return adUser;
        }

        private AdGroup MapAdGroup(HclElement hclElement)
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

            if (adGroup.Name == null)
            {
                _logger.LogError("Name for group {Group} is required but not provided in the file.", hclElement.Value);
                Environment.Exit(1);
            }

            return adGroup;
        }
    }
}
