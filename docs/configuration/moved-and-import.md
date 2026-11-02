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

### Renaming a block

As in Terraform, a block label is only the resource's address in declarad. It is never used as a value: the object's name comes from `name` (and a user's logon name from `sam_account_name`), which you always set explicitly. So renaming a label (`user "alice"` → `user "alice2"`) changes nothing about the object, and declarad finds it again even without a `moved` block. The account keeps its objectGUID, SID, group memberships and every value it had:

- Nothing is written to Active Directory: the apply only moves the entry in the state. As in Terraform, the plan shows

  ```
    # user.alice has moved to user.alice2
      user.alice2 {
          # dn = "CN=alice,CN=Users,DC=corp,DC=local"
      }

  Plan: 0 to add, 0 to change, 0 to destroy.
  ```

  If the moved resource also has changes, it is shown as `will be updated in-place` with a `# (moved from user.alice)` line. References to the new address (`user.alice2.dn`) work as usual.
- This works for every resource type, for `for_each`/`count` blocks (`user.staff["alice"]` → `user.people["alice"]`) and for renamed `module` calls.
- Several blocks can be renamed in one apply.
- Attachments (`group_membership`, `gpo_link`, `dns_record`) are recognised by their target and identity, so a rename never detaches what they attached.

A renamed block may change the object in the same edit: a new `path` moves it to another OU, and a new `name` (or `given_name`/`surname`, which make up a user's CN) renames it, both in place. A renamed block is recognised as the same object when its DN, its **name (CN) or its logon name** matches an object that left the configuration. If none matches, it describes a different object: the plan deletes the old one and creates the new one, with a warning. If it really is the same object, add a `moved` block, or rename the block and the object in separate applies.

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
