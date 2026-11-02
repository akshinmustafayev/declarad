namespace Declarad.Cli
{
    internal static class Templates
    {
        public const string Main = """
            # declarad configuration. See README.md for every attribute.

            connection {
              server   = "dc01.corp.local"
              domain   = "corp.local"
              protocol = "ldaps"                  # ldaps (636), starttls or ldap (389; no password changes)
              username = "admin@corp.local"
              password = env("DECLARAD_PASSWORD")
            }

            variable "company" {
              default = "Contoso"
            }

            ou "staff" {
              name        = "Staff"
              description = "${var.company} employees"
            }

            group "engineering" {
              name    = "Engineering"
              path    = ou.staff.dn
              members = [user.jdoe.dn]
            }

            user "jdoe" {
              sam_account_name         = "jdoe"
              given_name               = "John"
              surname                  = "Doe"
              path                     = ou.staff.dn
              title                    = "Engineer"
              initial_password         = env("JDOE_PASSWORD")
              change_password_at_logon = true
            }

            """;
    }
}
