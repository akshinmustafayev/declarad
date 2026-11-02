using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    /// <summary>A mail contact (an address book entry that is not a security principal).</summary>
    public sealed class ContactResource : ResourceType
    {
        public override string Name => "contact";
        public override string ObjectClass => "contact";
        public override string RdnType => "CN";
        public override string Description => "Mail contact";

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            new AttributeSpec("name", AttrType.String)
            {
                Description = "Object name (the CN).",
                Required = true,
                Validate = CommonSpecs.NotBlank,
            },
            CommonSpecs.Path("CN=Users"),
            CommonSpecs.Str("given_name", "givenName", "First name."),
            CommonSpecs.Str("surname", "sn", "Last name."),
            CommonSpecs.Str("initials", "initials", "Initials."),
            new AttributeSpec("display_name", AttrType.String)
            {
                Ldap = "displayName",
                Description = "Display name. Defaults to \"given_name surname\" when either is set.",
                DefaultFunc = (get, _) => UserResource.FullName(get),
            },
            CommonSpecs.Str("description", "description", "Free-text description."),
            CommonSpecs.Str("email", "mail", "E-mail address."),
            CommonSpecs.Str("title", "title", "Job title."),
            CommonSpecs.Str("department", "department", "Department."),
            CommonSpecs.Str("company", "company", "Company."),
            CommonSpecs.Str("office", "physicalDeliveryOfficeName", "Office."),
            CommonSpecs.Str("phone", "telephoneNumber", "Telephone number."),
            CommonSpecs.Str("mobile", "mobile", "Mobile number."),
            CommonSpecs.Str("street_address", "streetAddress", "Street address."),
            CommonSpecs.Str("city", "l", "City."),
            CommonSpecs.Str("state", "st", "State or province."),
            CommonSpecs.Str("postal_code", "postalCode", "Postal code."),
            CommonSpecs.Country(),
            new AttributeSpec("proxy_addresses", AttrType.StringList)
            {
                Ldap = "proxyAddresses",
                Description = "E-mail proxy addresses. Authoritative when set.",
            },
            CommonSpecs.DnLink("manager", "manager", "DN of the contact's manager."),
            CommonSpecs.Groups(),
        };
    }
}
