# gpo_link

Links an existing Group Policy Object to an OU or to the domain root. The GPO itself (its settings in SYSVOL) is created and edited in GPMC; declarad manages where it is linked.

```hcl
data "gpo" "baseline" {
  name = "Security Baseline"
}

data "gpo" "workstations" {
  name = "Workstation Settings"
}

gpo_link "baseline" {
  gpo      = data.gpo.baseline.id
  target   = ou.corp.dn
  enforced = true
  order    = 1
}

gpo_link "workstations" {
  gpo    = data.gpo.workstations.id
  target = ou.workstations.dn
}
```

## Behaviour

- Edits the target's `gPLink` attribute and leaves other links alone. Several `gpo_link` resources can target the same OU.
- `order` is the link order shown in GPMC: 1 is the highest precedence. Omit it to leave the position alone; a new link is added with the lowest precedence, as GPMC does.
- `enabled = false` keeps the link but disables it; `enforced = true` sets "Enforced".
- Changing `target` or `gpo` removes the old link and creates the new one.
- Removing the resource removes the link.
- `gpo` accepts `{GUID}`, a bare GUID, or the GPO's DN.

## Attributes

<!-- schema:gpo_link -->
| Attribute | Type | Default | Notes | Description |
|---|---|---|---|---|
| `gpo` | string |  | required | GPO id ({GUID}) or its DN, e.g. data.gpo.baseline.id. |
| `target` | string |  | required; DN | DN of the OU (or the domain root) to link the GPO to. |
| `enabled` | bool | `true` |  | Whether the link is enabled. |
| `enforced` | bool | `false` |  | Whether the link is enforced (No Override). |
| `order` | number |  |  | Link order on the target (1 = highest precedence). Unmanaged when omitted. |
<!-- /schema -->
