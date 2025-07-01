using declarad.AdEntities.User;

namespace declarad.AdEntities.Group
{
    public class AdGroup : IAdItem
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<AdUser> Members { get; set; } = new List<AdUser>();
        public List<AdGroup> MemberOf { get; set; } = new List<AdGroup>();
    }
}
