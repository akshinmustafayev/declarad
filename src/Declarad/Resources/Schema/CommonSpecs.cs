using System.Text.RegularExpressions;
using Declarad.Directory;
using Declarad.Hcl.Values;

namespace Declarad.Resources.Schema
{
    internal static partial class CommonSpecs
    {
        [GeneratedRegex("^[A-Za-z]{2}$")]
        private static partial Regex CountryRegex();

        public static string? ValidDn(Value v) =>
            Dn.IsValid(V.Str(v)) ? null : $"'{V.Str(v)}' is not a valid distinguished name (expected e.g. OU=Sales,DC=corp,DC=local).";

        public static string? ValidCountry(Value v) =>
            CountryRegex().IsMatch(V.Str(v) ?? "") ? null : $"'{V.Str(v)}' is not a two-letter ISO 3166 country code.";

        public static string? NotBlank(Value v) =>
            string.IsNullOrWhiteSpace(V.Str(v)) ? "must not be empty." : null;

        public static AttributeSpec Name(string description) => new("name", AttrType.String)
        {
            Description = description,
            Required = true,
            Validate = NotBlank,
        };

        /// <summary>Parent container; defaults to <paramref name="defaultContainer"/> under the domain root (or the root itself).</summary>
        public static AttributeSpec Path(string? defaultContainer) => new("path", AttrType.String)
        {
            Description = "Distinguished name of the parent OU or container.",
            IsDn = true,
            DefaultFunc = (_, ctx) => Value.From(defaultContainer == null ? ctx.BaseDn : $"{defaultContainer},{ctx.BaseDn}"),
            Validate = ValidDn,
        };

        public static AttributeSpec Str(string name, string ldap, string description) => new(name, AttrType.String)
        {
            Ldap = ldap,
            Description = description,
        };

        public static AttributeSpec Country() => new("country", AttrType.String)
        {
            Ldap = "c",
            Description = "Two-letter country code.",
            Validate = ValidCountry,
        };

        /// <summary>Additive group membership (DNs or sAMAccountNames), shared by users, computers, contacts and service accounts.</summary>
        public static AttributeSpec Groups() => new("groups", AttrType.StringList)
        {
            Description = "Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone.",
            IsDn = true,
            AcceptsSamAccountName = true,
            Deferred = true,
            Additive = true,
        };

        public static AttributeSpec Bool(string name, string description, bool defaultValue) => new(name, AttrType.Bool)
        {
            Description = description,
            Default = Value.From(defaultValue),
        };

        public static AttributeSpec Number(string name, string ldap, string description, long? defaultValue = null) => new(name, AttrType.Number)
        {
            Ldap = ldap,
            Format = LdapFormat.Int,
            Description = description,
            Default = defaultValue == null ? null : Value.From(defaultValue.Value),
        };

        public static AttributeSpec DnLink(string name, string ldap, string description) => new(name, AttrType.String)
        {
            Ldap = ldap,
            Description = description,
            IsDn = true,
            AcceptsSamAccountName = true,
            Deferred = true,
        };
    }
}
