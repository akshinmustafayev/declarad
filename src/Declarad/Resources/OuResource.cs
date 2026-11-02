using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    public sealed class OuResource : ResourceType
    {
        public override string Name => "ou";
        public override string ObjectClass => "organizationalUnit";
        public override string RdnType => "OU";
        public override string Description => "Organizational unit";

        public override IReadOnlyList<AttributeSpec> Schema { get; } = new[]
        {
            CommonSpecs.Name("OU name (the OU= part of the DN)."),
            CommonSpecs.Path(null),
            CommonSpecs.Str("description", "description", "Free-text description."),
            CommonSpecs.Str("display_name", "displayName", "Display name."),
            CommonSpecs.Str("street_address", "street", "Street address."),
            CommonSpecs.Str("city", "l", "City."),
            CommonSpecs.Str("state", "st", "State or province."),
            CommonSpecs.Str("postal_code", "postalCode", "Postal code."),
            CommonSpecs.Str("po_box", "postOfficeBox", "P.O. box."),
            CommonSpecs.Country(),
            CommonSpecs.Str("country_name", "co", "Country name as shown in ADUC (e.g. Germany). Set it together with country and country_code."),
            CommonSpecs.Number("country_code", "countryCode", "ISO 3166 numeric country code (e.g. 276 for Germany)."),
            CommonSpecs.Str("office", "physicalDeliveryOfficeName", "Office."),
            CommonSpecs.Str("phone", "telephoneNumber", "Telephone number."),
            CommonSpecs.Str("fax", "facsimileTelephoneNumber", "Fax number."),
            CommonSpecs.List("upn_suffixes", "uPNSuffixes", "UPN suffixes offered for users in this OU (used by Exchange and some tools). Authoritative when set."),
            CommonSpecs.DnLink("managed_by", "managedBy", "DN or sAMAccountName of the managing user or group."),
            new AttributeSpec("block_inheritance", AttrType.Bool)
            {
                Ldap = "gPOptions",
                Format = LdapFormat.IntBool,
                Description = "Block Group Policy inheritance on this OU.",
                Default = Value.False,
            },
            CommonSpecs.Protection(true),
        };
    }
}
