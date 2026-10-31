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
        public const long DontExpirePassword = 0x10000;

        public static long Set(long uac, long flag, bool? on) => on switch
        {
            true => uac | flag,
            false => uac & ~flag,
            null => uac,
        };

        public static long WithEnabled(long uac, Value enabled) => Set(uac, AccountDisable, V.Bool(enabled) is { } e ? !e : null);
    }
}
