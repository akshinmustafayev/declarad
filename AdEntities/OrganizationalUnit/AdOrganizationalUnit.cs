namespace declarad.AdEntities.OrganizationalUnit
{
    public class AdOrganizationalUnit : IAdItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? Description { get; set; }
        public string? DisplayName { get; set; }
        public string? PostalCode { get; set; }
        public string? State { get; set; }
        public string? StreetAddress { get; set; }
        public bool ProtectedFromAccidentalDeletion { get; set; } = true;
    }
}
