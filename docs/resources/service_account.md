# service_account

A group managed service account (gMSA): AD rotates its password automatically, and only the computers you allow can retrieve it.

```hcl
group "report_servers" {
  name    = "report_servers"
  members = [computer.rpt01.dn, computer.rpt02.dn]
}

service_account "svc_reports" {
  name                                    = "svc_reports"
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
- **Delegation.** `allowed_to_delegate_to` is Kerberos constrained delegation (`msDS-AllowedToDelegateTo`: the SPNs this account may delegate to); add `trusted_to_auth_for_delegation = true` for protocol transition. `principals_allowed_to_delegate_to_account` is resource-based constrained delegation (`msDS-AllowedToActOnBehalfOfOtherIdentity`, like `Set-ADServiceAccount -PrincipalsAllowedToDelegateToAccount`); `[]` removes it. `account_is_sensitive = true` prevents the account from being delegated.
- **`protected_from_accidental_deletion`** adds ADUC's deny-delete ACE. It is unmanaged unless set.
- The flags (`trusted_for_delegation`, `trusted_to_auth_for_delegation`, `account_is_sensitive`, `protected_from_accidental_deletion`) are unmanaged unless set. Removing one that declarad set turns it off again.
- Install the account on a host with `Install-ADServiceAccount svc_reports$` (outside declarad).

## Attributes

<!-- schema:service_account -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string |  | required | Account name (the CN). |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `sam_account_name` | string | computed | LDAP `sAMAccountName` | Defaults to name followed by $. |
| `dns_host_name` | string | computed | LDAP `dNSHostName` | DNS host name of the service. Defaults to name.domain. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `display_name` | string |  | LDAP `displayName` | Display name. |
| `notes` | string |  | LDAP `info` | Notes. |
| `managed_by` | string |  | DN or sAMAccountName; LDAP `managedBy` | DN or sAMAccountName of the user or group that manages the account. |
| `service_principal_names` | list(string) |  | authoritative; LDAP `servicePrincipalName` | SPNs. Authoritative when set. |
| `kerberos_encryption_types` | list(string) | `["aes128", "aes256"]` |  | Supported Kerberos encryption types: aes128, aes256, rc4, des_cbc_crc, des_cbc_md5. |
| `password_interval_days` | number | `30` | creation only; LDAP `msDS-ManagedPasswordInterval` | Password change interval in days. AD only accepts it at creation. |
| `principals_allowed_to_retrieve_password` | list(string) |  | DN or sAMAccountName | Computers or groups (DN or sAMAccountName) allowed to use the account. Authoritative. |
| `allowed_to_delegate_to` | list(string) |  | authoritative; LDAP `msDS-AllowedToDelegateTo` | Kerberos constrained delegation: SPNs of the services this account may delegate to. Authoritative when set. |
| `principals_allowed_to_delegate_to_account` | list(string) |  | DN or sAMAccountName | Resource-based constrained delegation: accounts (DN or sAMAccountName) allowed to delegate to this account's services. Authoritative when set; [] clears it. |
| `enabled` | bool | `true` |  | Whether the account is enabled. |
| `protected_from_accidental_deletion` | bool |  |  | Deny 'Delete' and 'Delete subtree' to Everyone, like the ADUC checkbox. Unmanaged unless set. |
| `trusted_for_delegation` | bool |  |  | Trusted for Kerberos (unconstrained) delegation. Unmanaged unless set. |
| `trusted_to_auth_for_delegation` | bool |  |  | Trusted to authenticate for delegation (protocol transition). Unmanaged unless set. |
| `account_is_sensitive` | bool |  |  | Account is sensitive and cannot be delegated. Unmanaged unless set. |
| `groups` | list(string) |  | additive; DN or sAMAccountName | Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone. |
<!-- /schema -->

Also exposes `dn`.
