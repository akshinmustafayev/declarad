# computer

A pre-staged computer account. When the machine joins the domain with the same name, it takes over the account and sets its password.

```hcl
computer "web01" {
  path                    = ou.servers.dn
  description             = "Intranet web server"
  service_principal_names = ["HTTP/web01.corp.local", "HTTP/intranet.corp.local"]
  groups                  = [group.web_servers.dn]
}

computer "kiosk" {
  count = 10
  name  = format("KIOSK%02d", count.index + 1)
  path  = ou.kiosks.dn
}
```

DN: `CN=<name>,<path>`; `path` defaults to `CN=Computers`. `name` is limited to 15 characters (NetBIOS). Defaults: `sam_account_name` is `NAME$` and `dns_host_name` is `name.domain`.

## Behaviour

- Created with `userAccountControl` = workstation trust + password not required (as ADUC pre-staging does); `enabled = false` disables it.
- `service_principal_names` is authoritative when set. Omit it if the machine registers its own SPNs.
- A computer object is also a `user` in AD's schema; declarad matches by structural class, so a `user` resource never adopts a computer.

## Attributes

<!-- schema:computer -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string | block label |  | Computer name (the CN). Defaults to the block label. |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `sam_account_name` | string | computed | LDAP `sAMAccountName` | Defaults to the upper-cased name followed by $. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `location` | string |  | LDAP `location` | Location. |
| `dns_host_name` | string | computed | LDAP `dNSHostName` | Fully qualified host name. Defaults to name.domain. |
| `service_principal_names` | list(string) |  | authoritative; LDAP `servicePrincipalName` | SPNs. Authoritative when set. |
| `managed_by` | string |  | DN or sAMAccountName; LDAP `managedBy` | DN or sAMAccountName of the managing user or group. |
| `enabled` | bool | `true` |  | Whether the account is enabled. |
| `groups` | list(string) |  | additive; DN or sAMAccountName | Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone. |
<!-- /schema -->

Also exposes `dn`.
