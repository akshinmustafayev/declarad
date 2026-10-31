# moved and import blocks

## moved

Renaming a resource or moving it into a module changes its address. Without help, declarad would plan to delete the object at the old address and create a new one. A `moved` block tells it that the object simply has a new address:

```hcl
moved {
  from = user.john
  to   = user.jdoe
}

moved {
  from = ou.finance
  to   = module.department["finance"].ou.department
}

moved {
  from = module.old_name
  to   = module.new_name          # moves every resource inside
}
```

The plan shows `(moved from …)` and no changes for the object itself. After everyone has applied, the block can be removed (or kept as documentation; once the old address no longer exists it does nothing).

Instance keys must be literals: `user.staff["jdoe"]`, `computer.ws[0]`. `from` matching an address prefix moves all instances below it.

Renaming only the block label of a resource whose DN stays the same is detected automatically, even without a `moved` block.

`declarad state mv <from> <to>` does the same immediately, without review. Prefer `moved` blocks in shared configurations.

## import

An existing object has to be explicitly taken under management. Otherwise declarad refuses to create a resource whose DN is already taken. The `import` block puts the adoption into the plan, so it is reviewed like any other change:

```hcl
group "legacy_vpn" {
  name        = "VPN Users"
  description = "Remote access"
}

import {
  to = group.legacy_vpn
  id = "CN=VPN Users,OU=Groups,DC=corp,DC=local"   # DN or objectGUID
}
```

The plan shows `(imported from …)` plus any attribute differences, which are applied. Import blocks belong to the root module; `to` may point into modules (`module.x.user.y["k"]`). An import block for an address that is already in state is ignored.

## Other ways to adopt

| Method | When |
|---|---|
| `declarad import <address> <dn-or-guid>` | One-off, immediately, without a plan. |
| `declarad plan --adopt` / `apply --adopt` | Many objects whose DNs match the configuration exactly, typically after `declarad export`. |

Once adopted, an object is managed like any other: removing it from the configuration deletes it from the directory. Use `declarad state rm` to stop managing an object without deleting it.
