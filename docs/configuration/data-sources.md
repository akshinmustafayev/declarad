# Data sources

Data sources read existing directory objects without managing them. Use them to reference things declarad doesn't own: built-in groups, GPOs, objects managed by another team, the domain SID.

```hcl
data "group" "domain_admins" {
  sam_account_name = "Domain Admins"
}

data "gpo" "baseline" {
  name = "Security Baseline"
}

group_membership "tier0" {
  group   = data.group.domain_admins.dn
  members = [group.tier0_admins.dn]
}

gpo_link "baseline" {
  gpo    = data.gpo.baseline.id
  target = ou.corp.dn
}
```

Data sources are read when the configuration is loaded by `plan`, `apply`, `import` and `export`. `validate` and `graph` treat their results as unknown. They accept `for_each`, `count`, `depends_on` and `connection`.

A data source is read before anything is applied, so it can't look up an object that the same apply creates. Reference the resource directly instead.

## Object lookups

There is one data source per object type: `ou`, `container`, `group`, `user`, `computer`, `contact`, `service_account`, `password_policy`, `dns_zone`. Set exactly one lookup argument:

| Argument | Looks up by |
|---|---|
| `dn` | Distinguished name. |
| `guid` | objectGUID. |
| `name` | Name (CN, OU or DC value), searched under `base`. Must match exactly one object. |
| `sam_account_name` | sAMAccountName (types that have one). |
| `base` | Optional search base for `name`/`sam_account_name`: default is the domain root (the DomainDnsZones DNS container for `dns_zone`). |

The result has every attribute of the [resource type](../README.md#resource-reference) (sensitive and creation-only ones excluded), plus `dn`, `guid` and `sid`.

```hcl
data "user" "ceo" {
  sam_account_name = "jsmith"
}

data "ou" "legacy" {
  name = "Legacy"
}

data "computer" "dc01" {
  dn = "CN=DC01,OU=Domain Controllers,DC=corp,DC=local"
}

data "dns_zone" "corp" {
  name = "corp.local"
}

output "ceo_title" {
  value = data.user.ceo.title
}
```

## gpo

Finds a Group Policy Object under `CN=Policies,CN=System`.

| Argument | Description |
|---|---|
| `name` | Display name, as shown in GPMC. |
| `id` | GUID, with or without braces. |

Result: `id` (`{GUID}`, ready for `gpo_link`), `dn`, `name`, `file_sys_path` (SYSVOL path), `status` (`all_settings_enabled`, `user_settings_disabled`, `computer_settings_disabled` or `all_settings_disabled`).

## domain

No arguments. Result: `dn` (naming context), `name` (DNS name), `sid` (domain SID, handy to build well-known SIDs).

```hcl
data "domain" "this" {}

output "domain_admins_sid" {
  value = "${data.domain.this.sid}-512"
}
```

## objects

A raw LDAP search, for anything the typed lookups don't cover.

| Argument | Default | Description |
|---|---|---|
| `filter` | (required) | LDAP filter. Escape user input with `filterescape()`. |
| `base` | domain root | Search base. |
| `scope` | `subtree` | `subtree` or `onelevel`. |
| `attributes` | `[]` | LDAP attributes to return. |

Result:
- `objects`: a list of `{ dn, guid, sid, <attribute> = ... }`. Single-valued attributes are strings, multi-valued ones lists, and missing ones `null`.
- `dns`: the list of DNs.

```hcl
data "objects" "sales_people" {
  filter     = "(&(objectCategory=person)(objectClass=user)(department=Sales))"
  attributes = ["sAMAccountName", "mail"]
}

group "sales_all" {
  name    = "sales_all"
  members = data.objects.sales_people.dns
}
```
