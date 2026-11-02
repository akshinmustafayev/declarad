# container

A generic `CN=` container, for layouts that use containers rather than OUs. Containers can't have GPOs linked and are rarely needed. Prefer [`ou`](ou.md).

```hcl
container "service_objects" {
  name        = "Service Objects"
  description = "Application-owned objects"
}
```

DN: `CN=<name>,<path>`; `path` defaults to the domain root. Export skips containers unless you ask for them (`--type container`), because AD has many system containers.

## Attributes

<!-- schema:container -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `name` | string |  | required | Container name (the CN). |
| `path` | string | computed | DN | Distinguished name of the parent OU or container. |
| `description` | string |  | LDAP `description` | Free-text description. |
<!-- /schema -->

Also exposes `dn`.
