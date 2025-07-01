using entraform.AdEntities.Group;

namespace entraform.AdEntities.User
{
    public class AdUser : IAdItem
    {
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Password { get; set; }
        public string? DisplayName { get; set; }
        public string SamAccountName { get; set; }
        public string? UserPrincipalName { get; set; }
        public string? Email { get; set; }
        public string? Ou { get; set; }
        public List<AdGroup>? Groups { get; set; } = new();
        public bool? Enabled { get; set; }
        public bool? ChangePasswordAtLogon { get; set; }
    }
}
