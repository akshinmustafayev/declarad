using Declarad.Hcl.Values;
using Declarad.Resources.Schema;

namespace Declarad.Resources
{
    /// <summary>userAccountControl flags shared by users, computers and service accounts.</summary>
    internal static class Uac
    {
        public const long AccountDisable = 0x2;
        public const long PasswordNotRequired = 0x20;
        public const long NormalAccount = 0x200;
        public const long WorkstationTrust = 0x1000;
        public const long EncryptedTextPasswordAllowed = 0x80;
        public const long DontExpirePassword = 0x10000;
        public const long SmartcardRequired = 0x40000;
        public const long TrustedForDelegation = 0x80000;
        public const long NotDelegated = 0x100000;
        public const long UseDesKeyOnly = 0x200000;
        public const long DontRequirePreauth = 0x400000;
        public const long TrustedToAuthForDelegation = 0x1000000;

        /// <summary>Account options stored as userAccountControl flags (the Account tab checkboxes). Unmanaged unless set.</summary>
        public static readonly (string Name, long Flag, string Description)[] Options =
        {
            ("smartcard_required", SmartcardRequired, "Smart card is required for interactive logon."),
            ("password_not_required", PasswordNotRequired, "The account may have an empty password."),
            ("reversible_password_encryption", EncryptedTextPasswordAllowed, "Store the password using reversible encryption."),
            ("trusted_for_delegation", TrustedForDelegation, "Trusted for Kerberos (unconstrained) delegation."),
            ("trusted_to_auth_for_delegation", TrustedToAuthForDelegation, "Trusted to authenticate for delegation (protocol transition)."),
            ("account_is_sensitive", NotDelegated, "Account is sensitive and cannot be delegated."),
            ("use_des_key_only", UseDesKeyOnly, "Use only Kerberos DES encryption types."),
            ("dont_require_preauth", DontRequirePreauth, "Do not require Kerberos preauthentication."),
        };

        public static (string Name, long Flag, string Description)[] OptionsNamed(params string[] names) =>
            Options.Where(o => names.Contains(o.Name)).ToArray();

        public static IEnumerable<AttributeSpec> OptionSpecs(IEnumerable<(string Name, long Flag, string Description)> options) =>
            options.Select(o => new AttributeSpec(o.Name, AttrType.Bool) { Description = o.Description + " Unmanaged unless set." });

        public static long WithOptions(long uac, IEnumerable<(string Name, long Flag, string Description)> options, Func<string, Value> get)
        {
            foreach (var option in options)
            {
                uac = Set(uac, option.Flag, V.Bool(get(option.Name)));
            }
            return uac;
        }

        public static void ReadOptions(long uac, IEnumerable<(string Name, long Flag, string Description)> options, Dictionary<string, Value> result)
        {
            foreach (var option in options)
            {
                result[option.Name] = Value.From((uac & option.Flag) != 0);
            }
        }

        public static long Set(long uac, long flag, bool? on) => on switch
        {
            true => uac | flag,
            false => uac & ~flag,
            null => uac,
        };

        public static long WithEnabled(long uac, Value enabled) => Set(uac, AccountDisable, V.Bool(enabled) is { } e ? !e : null);
    }
}
