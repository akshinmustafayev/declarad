using entraform.AdEntities.Group;
using entraform.AdEntities.OrganizationalUnit;
using entraform.AdEntities.User;

namespace entraform.AdEntities
{
    public class AdPlan
    {
        public AdConnection Connection { get; set; }
        public List<AdUser>? Users { get; set; } = new List<AdUser>();
        public List<AdGroup>? Groups { get; set; } = new List<AdGroup>();
        public List<AdOrganizationalUnit>? OrganizationalUnits { get; set; } = new List<AdOrganizationalUnit>();
    }
}
