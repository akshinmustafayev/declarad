# service_account

A group managed service account (gMSA): AD rotates its password automatically, and only the computers you allow can retrieve it.

```hcl
group "report_servers" {
  members = [computer.rpt01.dn, computer.rpt02.dn]
}

service_account "svc_reports" {
  description                             = "Reporting service"
  service_principal_names                 = ["HTTP/reports.corp.local"]
  kerberos_encryption_types               = ["aes128", "aes256"]
  principals_allowed_to_retrieve_password = [group.report_servers.dn]
}
```

DN: `CN=<name>,<path>`; `path` defaults to `CN=Managed Service Accounts`. Defaults: `sam_account_name` is `name$` and `dns_host_name` is `name.domain`.

## Requirements

- The domain needs a KDS root key (`Add-KdsRootKey`), effective on all DCs, before the first gMSA can be created.
- An encrypted connection (LDAPS or StartTLS).

## Behaviour

- `principals_allowed_to_retrieve_password` is authoritative. It writes `msDS-GroupMSAMembership` as a security descriptor granting the listed principals (DNs or sAMAccountNames; usually a group of computers). It is applied after all objects exist.
- `password_interval_days` can only be set at creation (an AD restriction).
- Install the account on a host with `Install-ADServiceAccount svc_reports$` (outside declarad).

## Attributes

<!-- schema:service_account -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string | block label |  | Account name (the CN). Defaults to the block label. |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `sam_account_name` | string | computed | LDAP `sAMAccountName` | Defaults to name followed by $. |
| `dns_host_name` | string | computed | LDAP `dNSHostName` | DNS host name of the service. Defaults to name.domain. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `service_principal_names` | list(string) |  | authoritative; LDAP `servicePrincipalName` | SPNs. Authoritative when set. |
| `kerberos_encryption_types` | list(string) | `["aes128", "aes256"]` |  | Supported Kerberos encryption types: aes128, aes256, rc4, des_cbc_crc, des_cbc_md5. |
| `password_interval_days` | number | `30` | creation only; LDAP `msDS-ManagedPasswordInterval` | Password change interval in days. AD only accepts it at creation. |
| `principals_allowed_to_retrieve_password` | list(string) |  | DN or sAMAccountName | Computers or groups (DN or sAMAccountName) allowed to use the account. Authoritative. |
| `enabled` | bool | `true` |  | Whether the account is enabled. |
| `groups` | list(string) |  | additive; DN or sAMAccountName | Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone. |
<!-- /schema -->

Also exposes `dn`.
