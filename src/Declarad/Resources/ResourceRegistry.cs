using Declarad.Resources.Dns;

namespace Declarad.Resources
{
    public static class ResourceRegistry
    {
        public static readonly ResourceType Ou = new OuResource();
        public static readonly ResourceType Container = new ContainerResource();
        public static readonly ResourceType Group = new GroupResource();
        public static readonly ResourceType User = new UserResource();
        public static readonly ResourceType Computer = new ComputerResource();
        public static readonly ResourceType Contact = new ContactResource();
        public static readonly ResourceType ServiceAccount = new ServiceAccountResource();
        public static readonly ResourceType PasswordPolicy = new PasswordPolicyResource();
        public static readonly ResourceType GroupMembership = new GroupMembershipResource();
        public static readonly ResourceType GpoLink = new GpoLinkResource();
        public static readonly ResourceType DnsZone = new DnsZoneResource();
        public static readonly ResourceType DnsRecord = new DnsRecordResource();

        /// <summary>All types, in the order objects should be created when no other dependency applies.</summary>
        public static IReadOnlyList<ResourceType> All { get; } = new[]
        {
            Ou, Container, Group, User, Computer, Contact, ServiceAccount, PasswordPolicy, DnsZone, GroupMembership, GpoLink, DnsRecord,
        };

        public static ResourceType? Find(string name) => All.FirstOrDefault(t => t.Name == name);
    }
}
