# Getting started

## Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/download) to build, or a published `declarad` binary.
- Network access to a domain controller on LDAPS (636) or LDAP with StartTLS (389).
- An account allowed to create and change the objects you manage. A dedicated service account with delegated rights on the OUs you manage is recommended over Domain Admins.

declarad runs on Windows, Linux and macOS. It needs no RSAT, PowerShell or WinRM.

## Build

```sh
git clone <this repository>
cd declarad
dotnet publish src/Declarad -c Release -o ~/.local/declarad
export PATH="$HOME/.local/declarad:$PATH"
declarad --help
```

## Your first configuration

```sh
mkdir corp-ad && cd corp-ad
declarad init
```

`init` creates `main.dad` when the directory is empty. Edit its connection block:

```hcl
connection {
  server   = "dc01.corp.local"
  domain   = "corp.local"
  username = "svc-declarad@corp.local"
  password = env("DECLARAD_PASSWORD")
}
```

Keep secrets out of files: `password = env("...")`, or omit `password` entirely and declarad reads `DECLARAD_PASSWORD` or prompts for it.

Now describe something small:

```hcl
ou "lab" {
  name                               = "declarad-lab"
  description                        = "Playground for declarad"
  protected_from_accidental_deletion = false
}

group "lab_admins" {
  name    = "Lab Admins"
  path    = ou.lab.dn
  members = [user.alice.dn]
}

user "alice" {
  sam_account_name         = "alice"
  given_name               = "Alice"
  surname                  = "Example"
  path                     = ou.lab.dn
  initial_password         = env("ALICE_PASSWORD")
  change_password_at_logon = true
}
```

Check it without touching the directory:

```sh
declarad validate
declarad fmt
```

## Plan and apply

```sh
export DECLARAD_PASSWORD='...'
export ALICE_PASSWORD='S0me-Initial-Passw0rd!'
declarad plan
```

The plan lists every object declarad would create, change or delete, and why. Nothing is changed yet. When it looks right:

```sh
declarad apply
```

`apply` shows the plan again and asks for `yes`. Afterwards `declarad.state.json` records which directory objects belong to the configuration (see [concepts](concepts.md#state)).

Run `declarad plan` again: it reports **No changes**. Now edit something in Active Directory Users and Computers (for example, Alice's title) and plan again: declarad reports the drift and `apply` reverts it.

## Clean up

```sh
declarad destroy
```

## Next steps

- Learn how planning, ownership and safety work in [Core concepts](concepts.md).
- Bring existing objects under management: [Adopting an existing domain](guides/adopting-existing-domain.md).
- Share state with your team: [State and backends](configuration/backends.md).
