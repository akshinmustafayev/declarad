# group

A security or distribution group.

```hcl
group "engineering" {
  name        = "Engineering"
  path        = ou.groups.dn
  description = "All engineers"
  scope       = "global"
  category    = "security"
  members     = [user.jdoe.dn, user.asmith.dn, "svc-build"]   # DNs or sAMAccountNames
  managed_by  = user.jdoe.dn
}

group "all_staff" {
  name     = "All Staff"
  scope    = "universal"
  category = "distribution"
  email    = "all@corp.example"
  members  = flatten([for d in module.department : d.member_dns])
}

# Nest a group into groups declarad doesn't manage (additive).
group "helpdesk" {
  name   = "helpdesk"
  groups = ["Remote Desktop Users"]
}
```

DN: `CN=<name>,<path>`; `path` defaults to `CN=Users`.

## Behaviour

- **`members` is authoritative**: after apply the group contains exactly the listed members, and anyone added by hand is removed. Omit `members` to leave membership alone. Entries may be DNs or sAMAccountNames; names resolve to managed resources first, then to directory objects.
- **Membership is applied after all objects exist**, so groups can contain each other and members created in the same apply.
- **Merging with `groups`**: when a user, computer, contact, service account or group lists this group in its `groups`, and this group manages `members`, the object is merged into `members`, so the two declarations never fight.
- **Scope changes** follow AD's rules: global ↔ domain local goes through universal automatically.
- `groups` (nesting this group into others) is additive: only memberships declarad added are removed later.
- Large groups (more than 1500 members) are read completely through ranged retrieval.
- **`protected_from_accidental_deletion`** adds ADUC's deny-delete ACE. It is unmanaged unless set; a protected group can't be deleted until you set it to `false` and apply.
- **Exchange attributes** (`mail_nickname`, `hide_from_address_lists`, `extension_attribute_1`...`15`) and `gid_number` (Unix, RFC 2307) exist only when the schema has them; leave them unset otherwise. `proxy_addresses` is authoritative when set.

## Attributes

<!-- schema:group -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string |  | required | Group name (the CN). |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `sam_account_name` | string | computed | LDAP `sAMAccountName` | Pre-Windows 2000 name. Defaults to name. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `display_name` | string |  | LDAP `displayName` | Display name. |
| `email` | string |  | LDAP `mail` | E-mail address. |
| `notes` | string |  | LDAP `info` | Notes. |
| `proxy_addresses` | list(string) |  | authoritative; LDAP `proxyAddresses` | E-mail proxy addresses (e.g. SMTP:team@corp.com). Authoritative when set. |
| `mail_nickname` | string |  | LDAP `mailNickname` | Exchange alias (requires the Exchange schema). |
| `hide_from_address_lists` | bool |  | LDAP `msExchHideFromAddressLists` | Hide from Exchange address lists (requires the Exchange schema). |
| `gid_number` | number |  | LDAP `gidNumber` | Unix group ID (RFC 2307). |
| `scope` | string | `"global"` | one of `global`, `domain_local`, `universal` | global, domain_local or universal. |
| `category` | string | `"security"` | one of `security`, `distribution` | security or distribution. |
| `managed_by` | string |  | DN or sAMAccountName; LDAP `managedBy` | DN or sAMAccountName of the managing user or group. |
| `members` | list(string) |  | authoritative; DN or sAMAccountName; LDAP `member` | Authoritative member list (DNs or sAMAccountNames). Omit to leave membership unmanaged. |
| `protected_from_accidental_deletion` | bool |  |  | Deny 'Delete' and 'Delete subtree' to Everyone, like the ADUC checkbox. Unmanaged unless set. |
| `extension_attribute_1` | string |  | LDAP `extensionAttribute1` | Custom attribute 1 (requires the Exchange schema). |
| `extension_attribute_2` | string |  | LDAP `extensionAttribute2` | Custom attribute 2 (requires the Exchange schema). |
| `extension_attribute_3` | string |  | LDAP `extensionAttribute3` | Custom attribute 3 (requires the Exchange schema). |
| `extension_attribute_4` | string |  | LDAP `extensionAttribute4` | Custom attribute 4 (requires the Exchange schema). |
| `extension_attribute_5` | string |  | LDAP `extensionAttribute5` | Custom attribute 5 (requires the Exchange schema). |
| `extension_attribute_6` | string |  | LDAP `extensionAttribute6` | Custom attribute 6 (requires the Exchange schema). |
| `extension_attribute_7` | string |  | LDAP `extensionAttribute7` | Custom attribute 7 (requires the Exchange schema). |
| `extension_attribute_8` | string |  | LDAP `extensionAttribute8` | Custom attribute 8 (requires the Exchange schema). |
| `extension_attribute_9` | string |  | LDAP `extensionAttribute9` | Custom attribute 9 (requires the Exchange schema). |
| `extension_attribute_10` | string |  | LDAP `extensionAttribute10` | Custom attribute 10 (requires the Exchange schema). |
| `extension_attribute_11` | string |  | LDAP `extensionAttribute11` | Custom attribute 11 (requires the Exchange schema). |
| `extension_attribute_12` | string |  | LDAP `extensionAttribute12` | Custom attribute 12 (requires the Exchange schema). |
| `extension_attribute_13` | string |  | LDAP `extensionAttribute13` | Custom attribute 13 (requires the Exchange schema). |
| `extension_attribute_14` | string |  | LDAP `extensionAttribute14` | Custom attribute 14 (requires the Exchange schema). |
| `extension_attribute_15` | string |  | LDAP `extensionAttribute15` | Custom attribute 15 (requires the Exchange schema). |
| `groups` | list(string) |  | additive; DN or sAMAccountName | Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone. |
<!-- /schema -->

Also exposes `dn`.
