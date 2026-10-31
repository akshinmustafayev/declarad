# Connections

A connection is a directory declarad manages: one domain controller (or load-balanced name) of one domain.

```hcl
connection {
  server   = "dc01.corp.local"
  domain   = "corp.local"
  protocol = "ldaps"
  username = "svc-declarad@corp.local"
  password = env("DECLARAD_PASSWORD")
}
```

| Attribute | Default | Description |
|---|---|---|
| `server` | `DECLARAD_SERVER` | Host name of the DC. Use the FQDN that matches its certificate for LDAPS. |
| `domain` | derived from `base_dn` | DNS name of the domain. Used for default UPNs, DNS host names and the base DN. |
| `base_dn` | from `domain` | Domain naming context, e.g. `DC=corp,DC=local`. |
| `forest_base_dn` | `base_dn` | Forest root naming context, for DNS zones stored in `ForestDnsZones`. |
| `protocol` | `ldaps` | `ldaps` (port 636), `starttls` or `ldap` (port 389). Passwords and gMSAs require an encrypted connection. |
| `port` | 636 or 389 | |
| `auth` | `simple` | `simple` bind, or `negotiate` (Kerberos/NTLM; uses the current ticket when no username is given). |
| `username` / `password` | `DECLARAD_USERNAME` / `DECLARAD_PASSWORD`, then a prompt | UPN (`user@corp.local`), `DOMAIN\user` or a DN. |
| `insecure_skip_verify` | `false` | Skip certificate validation. Windows only; on Linux/macOS set `LDAPTLS_REQCERT=never`. |
| `ca_cert_dir` | | Linux/macOS: directory of trusted CA certificates (hashed names, as for OpenSSL). |
| `timeout` | `30` | Seconds. |

Connection attributes may use variables, locals and `env()`.

## Several connections

Add labeled connection blocks. The unlabeled one is called `default` and is used by resources that don't say otherwise.

```hcl
connection "eu" {
  server = "dc01.eu.corp.local"
  domain = "eu.corp.local"
}

ou "eu_staff" {
  name       = "Staff"
  connection = connection.eu
}

data "group" "eu_admins" {
  connection       = connection.eu
  sam_account_name = "Domain Admins"
}
```

A configuration may also have only labeled connections. Every resource must then set `connection`.

### In modules

Child modules see the caller's connections under the same names. Pass different ones explicitly, like Terraform's `providers`:

```hcl
module "eu_offices" {
  source      = "./modules/office"
  connections = { default = connection.eu }   # the module's "default" is the caller's "eu"
}
```

Connection blocks are only allowed in the root module.

### Rules

- Each object belongs to one connection. Names must be unique per connection, so the same DN may exist in two directories.
- The connection is recorded in state. Changing a resource's `connection` fails the plan: objects can't move between directories. Remove it from state and import it in the new directory instead.
- Membership references by sAMAccountName are resolved in the owner's directory.

## Referencing connection facts

```hcl
locals {
  upn_suffix = connection.eu.domain
  eu_root    = connection.eu.base_dn
}
```

Available: `name`, `domain`, `base_dn`, `forest_base_dn`, `server`.

## Permissions

The account needs rights to create, modify and delete the object classes you manage in the OUs you manage, plus "Read permissions" and "Modify permissions" on OUs for accidental-deletion protection. Creating gMSAs needs a KDS root key in the domain. DNS management needs write access to the DNS partitions (members of DnsAdmins have it).
