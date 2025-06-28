using entraform.AdEntities.User;

namespace entraform.AdEntities.Group
{
    public class AdGroup
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<AdUser> Members { get; set; } = new List<AdUser>();
        public List<AdGroup> MemberOf { get; set; } = new List<AdGroup>();
    }
}
