# State and backends

State records which directory objects belong to the configuration (see [concepts](../concepts.md#state)). A **backend** stores it. The default is a local file; teams should use a shared backend with locking.

## Configuring a backend

```hcl
declarad {
  required_version = ">= 0.4"

  backend "s3" {
    bucket = "corp-declarad-state"
    key    = "ad/prod.tfstate"
    region = "eu-west-1"
  }
}
```

Then run `declarad init`. The backend block may use `env()`, but not variables, locals or resources. Settings can come from the command line instead, which keeps environment-specific values out of the files:

```sh
declarad init --backend-config bucket=corp-state --backend-config key=ad/prod.tfstate
declarad init --backend-config prod.backend.hcl     # a file of key = value lines
```

Values given to `declarad init` are remembered in `.declarad/backend.json`; later commands use them as long as the backend block is unchanged. Passing `--backend-config` to another command overrides them for that run (and must match what `init` recorded).

## Backends

### local (default)

| Setting | Default | Description |
|---|---|---|
| `path` | `declarad.state.json` | State file, relative to the configuration directory. |

Writes are atomic, and the previous version is kept as `<path>.backup`. Lock file: `<path>.lock.info`.

### s3

| Setting | Description |
|---|---|
| `bucket`, `key` | Location of the state object (required). |
| `region` | AWS region (or `AWS_REGION`). |
| `endpoint` (or nested `endpoints { s3 = ... }`) | Custom endpoint for S3-compatible storage (MinIO, Ceph, LocalStack). Also `AWS_ENDPOINT_URL_S3`. |
| `use_path_style` | Path-style URLs (needed by most S3-compatible stores). |
| `encrypt`, `kms_key_id` | Server-side encryption (AES-256 or KMS). |
| `profile` | Named AWS profile. |
| `access_key`, `secret_key`, `token` | Static credentials. |
| `use_lockfile` | Default `true`. |

Credentials default to the AWS SDK chain: environment, profile, SSO, instance role. Locking writes `<key>.tflock` with a conditional put (If-None-Match), Terraform's `use_lockfile` mechanism; no DynamoDB table is needed (`dynamodb_table` is rejected). Enable bucket versioning to keep history.

### azurerm

| Setting | Description |
|---|---|
| `storage_account_name`, `container_name`, `key` | Blob location (container and key required). |
| `access_key` | Account key (or `ARM_ACCESS_KEY`). |
| `sas_token` | SAS token (or `ARM_SAS_TOKEN`). |
| `connection_string` | Full connection string (or `ARM_CONNECTION_STRING`); useful for Azurite. |
| `use_azuread_auth` (or `use_msi`, `use_oidc`) | Microsoft Entra ID via `DefaultAzureCredential` (environment, managed identity, Azure CLI, ...). |
| `endpoint` | Custom blob endpoint (sovereign clouds, emulators). |

Locking takes an infinite lease on the blob and records the lock info in its `terraformlockid` metadata, as Terraform does.

### http

Terraform's HTTP backend protocol, for state services such as GitLab-managed state:

| Setting | Default | Environment |
|---|---|---|
| `address` | (required) | `TF_HTTP_ADDRESS` |
| `update_method` | `POST` | `TF_HTTP_UPDATE_METHOD` |
| `lock_address`, `unlock_address` | (no locking) | `TF_HTTP_LOCK_ADDRESS`, `TF_HTTP_UNLOCK_ADDRESS` |
| `lock_method`, `unlock_method` | `LOCK`, `UNLOCK` | |
| `username`, `password` | | `TF_HTTP_USERNAME`, `TF_HTTP_PASSWORD` |
| `skip_cert_verification` | `false` | |

`GET address` returns the state (404/204 means none), the update method stores it (with `?ID=<lock id>` while locked), and a locked state answers `423` or `409` with the holder's lock info.

GitLab example:

```hcl
declarad {
  backend "http" {
    address        = "https://gitlab.corp.local/api/v4/projects/42/terraform/state/ad-prod"
    lock_address   = "https://gitlab.corp.local/api/v4/projects/42/terraform/state/ad-prod/lock"
    unlock_address = "https://gitlab.corp.local/api/v4/projects/42/terraform/state/ad-prod/lock"
    lock_method    = "POST"
    unlock_method  = "DELETE"
    username       = "declarad-ci"
  }
}
```

## Locking

`apply`, `destroy`, `import`, `state rm`, `state mv` and `state push` lock the state. A second run fails immediately with the holder's details, or waits up to `--lock-timeout`. `--lock=false` disables locking.

A lock left by a crashed run is removed with `declarad force-unlock <id>`.

Whenever no lock is held (`--lock=false`, or a backend configured without locking such as `http` without `lock_address` or `s3` with `use_lockfile = false`), every save first checks that nobody wrote newer state since it was loaded (same lineage, serial not newer). A concurrent writer is refused instead of silently overwritten.

## Changing backends

1. Edit the backend block (or `--backend-config`).
2. Run `declarad init --migrate-state`. The current state is copied to the new backend; `--force-copy` overwrites state that already exists there. When the old backend was a local file, it is renamed to `<path>.migrated` so it can't be picked up by mistake later.

Until you do, other commands refuse to run. `declarad init --reconfigure` switches without copying. The working directory remembers the initialized backend in `.declarad/backend.json`, which may contain credentials: don't commit `.declarad/`.

## Inspecting and repairing state

```sh
declarad state list
declarad state show 'user.staff["jdoe"]'
declarad state pull > backup.json
declarad state push backup.json          # refuses a different lineage or older serial without --force
declarad state rm group.legacy           # stop managing without deleting
declarad state mv user.john user.jdoe
```

The format is the same for every backend: JSON with `version`, `serial`, `lineage`, `outputs` and `resources`.
