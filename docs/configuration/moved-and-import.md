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

Renaming a block label (`user "alice"` → `user "alice2"`) never replaces the object, even without a `moved` block. The account keeps its objectGUID, SID, group memberships and every value it had:

- The label is also the default for one attribute: a user's or service account's `sam_account_name`, and the `name` of other types. Other defaults follow from it (CN, UPN, display names). After a rename these keep coming from the **old** label, so `alice2` still logs on as `alice`. The state remembers the old label (`label_default`), so later plans stay clean, and renaming the block again or back works the same way.
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
- To really change the logon name or CN, set `sam_account_name` or `name` explicitly. That updates the same account in place.
- Several blocks can be renamed in one apply. A block is matched with a removed block of the same type in the same module and connection.
- Attachments (`group_membership`, `gpo_link`, `dns_record`) are recognised by their target and identity, so a rename never detaches what they attached.
- `moved` blocks and `declarad state mv` keep label-derived values the same way.

A rename is recognised only while the renamed block still describes the same object. If you rename a block **and** change what its DN is made of in the same edit (for example `given_name`/`surname`, which make up a user's CN, or `path`), the plan deletes the old object and creates a new one, and warns about it. To keep the object, rename first and change the attributes in a second apply, or add a `moved` block.

Because two blocks with the same type and module are paired, removing one bare block and adding another bare block in the same edit (`user "bob" {}` → `user "carol" {}`) is treated as a rename of bob. Check the plan: it says `user.bob has moved to user.carol`. To replace bob with a new account, give the new block its own `sam_account_name`, or remove and add in separate applies.

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
