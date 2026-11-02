# user

A user account.

```hcl
user "jdoe" {
  sam_account_name         = "jdoe"
  given_name               = "John"
  surname                  = "Doe"
  path                     = ou.staff.dn
  title                    = "Engineer"
  department               = "Engineering"
  email                    = "john.doe@corp.example"
  manager                  = user.asmith.dn
  groups                   = ["VPN Users", group.engineering.dn]
  initial_password         = env("JDOE_INITIAL_PASSWORD")
  change_password_at_logon = true

  lifecycle {
    ignore_changes = [phone, mobile]       # maintained by the helpdesk
  }
}
```

DN: `CN=<name>,<path>`. `sam_account_name` is required (the block label is only the resource's address in declarad). Defaults:
- `display_name`: "given_name surname".
- `name`: `display_name`, then `sam_account_name`.
- `user_principal_name`: `sam@domain`.
- `path`: `CN=Users`.

## Behaviour

- **Creation.** Accounts are created disabled. The password is set (LDAPS or StartTLS required), `pwdLastSet` is set when `change_password_at_logon = true`, then the account is enabled. If any of this fails (e.g. the password violates policy), the new account is deleted again and the error explains why.
- **An enabled user needs `initial_password` at creation** (or `enabled = false`). The password is never read back, compared or stored. Changing it later has no effect. Reset passwords outside declarad.
- **`enabled`** and **`password_never_expires`** manage bits of `userAccountControl`; other bits are preserved.
- **Account options** (`smartcard_required`, `account_is_sensitive`, `trusted_for_delegation`, `trusted_to_auth_for_delegation`, `dont_require_preauth`, `use_des_key_only`, `reversible_password_encryption`, `password_not_required`) are the other `userAccountControl` checkboxes on the Account tab. They are managed only when set, so adopting existing users never flips them. Delegation flags need the *Enable computer and user accounts to be trusted for delegation* right.
- **`logon_workstations`** is the *Log On To...* list. `[]` allows every computer again.
- **Country.** ADUC writes three attributes: `country` (`c`, e.g. `DE`), `country_name` (`co`, `Germany`) and `country_code` (`countryCode`, `276`). Set all three for ADUC to show the country correctly.
- **Lists** (`other_phones`, `other_mobiles`, `other_web_pages`, `service_principal_names`, `alt_security_identities`, ...) are authoritative when set: values not listed are removed.
- **Exchange attributes** (`mail_nickname`, `hide_from_address_lists`, `extension_attribute_1`...`15`) and the **Unix attributes** (`uid_number`, `gid_number`, `unix_home_directory`, `login_shell`) exist only when the schema has them; leave them unset otherwise.
- **`account_expires`** takes a date `YYYY-MM-DD` (end of that day UTC) or `"never"`.
- **`groups` is additive**: listed groups are joined, and a membership is removed only if declarad added it earlier and you removed it from the list. Primary group membership (Domain Users) is invisible to `memberOf` and can't be managed here.
- **Renames.** Changing `name` renames the object (the CN); `display_name` and `sam_account_name` are ordinary attributes, changed in place. Renaming the **block label** changes nothing in AD (see [renaming a block](../configuration/moved-and-import.md#renaming-a-block)).
- **`manager`** is applied after all objects exist, so two users may reference each other.

## Attributes

<!-- schema:user -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `sam_account_name` | string |  | required; LDAP `sAMAccountName` | Logon name (pre-Windows 2000). |
| `name` | string | computed |  | Object name (the CN). Defaults to display_name, then sam_account_name. |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `user_principal_name` | string | computed | LDAP `userPrincipalName` | UPN. Defaults to sam_account_name@domain. |
| `given_name` | string |  | LDAP `givenName` | First name. |
| `surname` | string |  | LDAP `sn` | Last name. |
| `initials` | string |  | LDAP `initials` | Initials. |
| `middle_name` | string |  | LDAP `middleName` | Middle name. |
| `display_name` | string | computed | LDAP `displayName` | Display name. Defaults to "given_name surname" when either is set. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `email` | string |  | LDAP `mail` | E-mail address. |
| `title` | string |  | LDAP `title` | Job title. |
| `department` | string |  | LDAP `department` | Department. |
| `company` | string |  | LDAP `company` | Company. |
| `division` | string |  | LDAP `division` | Division. |
| `office` | string |  | LDAP `physicalDeliveryOfficeName` | Office. |
| `phone` | string |  | LDAP `telephoneNumber` | Telephone number. |
| `other_phones` | list(string) |  | authoritative; LDAP `otherTelephone` | Other telephone numbers. |
| `mobile` | string |  | LDAP `mobile` | Mobile number. |
| `other_mobiles` | list(string) |  | authoritative; LDAP `otherMobile` | Other mobile numbers. |
| `home_phone` | string |  | LDAP `homePhone` | Home telephone number. |
| `other_home_phones` | list(string) |  | authoritative; LDAP `otherHomePhone` | Other home telephone numbers. |
| `pager` | string |  | LDAP `pager` | Pager number. |
| `other_pagers` | list(string) |  | authoritative; LDAP `otherPager` | Other pager numbers. |
| `fax` | string |  | LDAP `facsimileTelephoneNumber` | Fax number. |
| `other_faxes` | list(string) |  | authoritative; LDAP `otherFacsimileTelephoneNumber` | Other fax numbers. |
| `ip_phone` | string |  | LDAP `ipPhone` | IP phone number. |
| `other_ip_phones` | list(string) |  | authoritative; LDAP `otherIpPhone` | Other IP phone numbers. |
| `notes` | string |  | LDAP `info` | Notes (the Telephones tab's Notes field). |
| `employee_id` | string |  | LDAP `employeeID` | Employee ID. |
| `employee_number` | string |  | LDAP `employeeNumber` | Employee number. |
| `employee_type` | string |  | LDAP `employeeType` | Employee type. |
| `street_address` | string |  | LDAP `streetAddress` | Street address. |
| `po_box` | string |  | LDAP `postOfficeBox` | P.O. box. |
| `city` | string |  | LDAP `l` | City. |
| `state` | string |  | LDAP `st` | State or province. |
| `postal_code` | string |  | LDAP `postalCode` | Postal code. |
| `country` | string |  | LDAP `c` | Two-letter country code. |
| `country_name` | string |  | LDAP `co` | Country name as shown in ADUC (e.g. Germany). Set it together with country and country_code. |
| `country_code` | number |  | LDAP `countryCode` | ISO 3166 numeric country code (e.g. 276 for Germany). |
| `home_directory` | string |  | LDAP `homeDirectory` | Home folder UNC path. |
| `home_drive` | string |  | LDAP `homeDrive` | Home drive letter, e.g. H:. |
| `script_path` | string |  | LDAP `scriptPath` | Logon script. |
| `profile_path` | string |  | LDAP `profilePath` | Roaming profile path. |
| `web_page` | string |  | LDAP `wWWHomePage` | Web page. |
| `other_web_pages` | list(string) |  | authoritative; LDAP `url` | Other web pages. |
| `preferred_language` | string |  | LDAP `preferredLanguage` | Preferred language, e.g. en-US. |
| `logon_workstations` | list(string) |  |  | Computers the user may log on to ("Log On To..."). Authoritative when set; [] allows all computers. |
| `service_principal_names` | list(string) |  | authoritative; LDAP `servicePrincipalName` | SPNs (for service accounts that are users). Authoritative when set. |
| `alt_security_identities` | list(string) |  | authoritative; LDAP `altSecurityIdentities` | Certificate and Kerberos name mappings (e.g. X509:<I>...<S>...). Authoritative when set. |
| `mail_nickname` | string |  | LDAP `mailNickname` | Exchange alias (requires the Exchange schema). |
| `hide_from_address_lists` | bool |  | LDAP `msExchHideFromAddressLists` | Hide from Exchange address lists (requires the Exchange schema). |
| `proxy_addresses` | list(string) |  | authoritative; LDAP `proxyAddresses` | E-mail proxy addresses (e.g. SMTP:jdoe@corp.com). Authoritative when set. |
| `account_expires` | string |  |  | Account expiry date (YYYY-MM-DD, UTC) or "never". |
| `manager` | string |  | DN or sAMAccountName; LDAP `manager` | DN or sAMAccountName of the user's manager. |
| `enabled` | bool | `true` |  | Whether the account is enabled. |
| `password_never_expires` | bool | `false` |  | Set 'Password never expires'. |
| `initial_password` | string |  | creation only; sensitive | Password set when the account is created. Never read back or compared. |
| `change_password_at_logon` | bool | `false` | creation only | Require a password change at first logon (applied at creation only). |
| `uid_number` | number |  | LDAP `uidNumber` | Unix user ID (RFC 2307). |
| `gid_number` | number |  | LDAP `gidNumber` | Unix primary group ID (RFC 2307). |
| `unix_home_directory` | string |  | LDAP `unixHomeDirectory` | Unix home directory (RFC 2307). |
| `login_shell` | string |  | LDAP `loginShell` | Unix login shell (RFC 2307). |
| `smartcard_required` | bool |  |  | Smart card is required for interactive logon. Unmanaged unless set. |
| `password_not_required` | bool |  |  | The account may have an empty password. Unmanaged unless set. |
| `reversible_password_encryption` | bool |  |  | Store the password using reversible encryption. Unmanaged unless set. |
| `trusted_for_delegation` | bool |  |  | Trusted for Kerberos (unconstrained) delegation. Unmanaged unless set. |
| `trusted_to_auth_for_delegation` | bool |  |  | Trusted to authenticate for delegation (protocol transition). Unmanaged unless set. |
| `account_is_sensitive` | bool |  |  | Account is sensitive and cannot be delegated. Unmanaged unless set. |
| `use_des_key_only` | bool |  |  | Use only Kerberos DES encryption types. Unmanaged unless set. |
| `dont_require_preauth` | bool |  |  | Do not require Kerberos preauthentication. Unmanaged unless set. |
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
