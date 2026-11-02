# contact

A mail contact: an address-book entry for someone outside the domain. It is not a security principal and can't log on.

```hcl
contact "vendor_support" {
  name       = "Vendor Support"
  given_name = "Vendor"
  surname    = "Support"
  email      = "support@vendor.example"
  company    = "Vendor Ltd"
  path       = ou.contacts.dn
  groups     = [group.vendors.dn]
}
```

DN: `CN=<name>,<path>`; `path` defaults to `CN=Users`. Contacts can be members of distribution groups (via `groups` here, or a group's `members` by DN).

## Attributes

<!-- schema:contact -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string |  | required | Object name (the CN). |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `given_name` | string |  | LDAP `givenName` | First name. |
| `surname` | string |  | LDAP `sn` | Last name. |
| `initials` | string |  | LDAP `initials` | Initials. |
| `display_name` | string | computed | LDAP `displayName` | Display name. Defaults to "given_name surname" when either is set. |
| `description` | string |  | LDAP `description` | Free-text description. |
| `email` | string |  | LDAP `mail` | E-mail address. |
| `title` | string |  | LDAP `title` | Job title. |
| `department` | string |  | LDAP `department` | Department. |
| `company` | string |  | LDAP `company` | Company. |
| `office` | string |  | LDAP `physicalDeliveryOfficeName` | Office. |
| `phone` | string |  | LDAP `telephoneNumber` | Telephone number. |
| `mobile` | string |  | LDAP `mobile` | Mobile number. |
| `street_address` | string |  | LDAP `streetAddress` | Street address. |
| `city` | string |  | LDAP `l` | City. |
| `state` | string |  | LDAP `st` | State or province. |
| `postal_code` | string |  | LDAP `postalCode` | Postal code. |
| `country` | string |  | LDAP `c` | Two-letter country code. |
| `proxy_addresses` | list(string) |  | authoritative; LDAP `proxyAddresses` | E-mail proxy addresses. Authoritative when set. |
| `manager` | string |  | DN or sAMAccountName; LDAP `manager` | DN of the contact's manager. |
| `groups` | list(string) |  | additive; DN or sAMAccountName | Groups (DN or sAMAccountName) the object should belong to. Additive: other memberships are left alone. |
<!-- /schema -->

Also exposes `dn`.
