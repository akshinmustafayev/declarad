# Building declarad releases

Needs the .NET 10 SDK (and internet on the first run, to download the runtime packs for self-contained builds).
.NET cross-compiles every target, so any one machine can build all of them.

```sh
build/build.sh                          # macOS, Linux, Git Bash, WSL
```
```powershell
.\build\build.ps1                       # Windows PowerShell / pwsh
```

| Option (sh / ps1) | Meaning |
| --- | --- |
| `--rids "linux-x64 osx-arm64"` / `-Rids linux-x64,osx-arm64` | Only these platforms. Default: `win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64` |
| `--variants portable` / `-Variants portable` | Only one variant: `portable` or `framework`. Default: both |
| `--version 0.4.0` / `-Version 0.4.0` | Version for the file names and `declarad --version`. Default: `<Version>` in `src/Declarad/Declarad.csproj` |
| `--clean` / `-Clean` | Delete `build/out` and `build/dist` first |

## What you get (`build/dist/`)

| File | Contents |
| --- | --- |
| `declarad-<version>-<rid>-portable.zip` | Self-contained: the .NET runtime is included, nothing to install. Bigger |
| `declarad-<version>-<rid>.zip` | Framework-dependent: small, needs the **.NET 10 runtime** (`Microsoft.NETCore.App`) installed |
| `SHA256SUMS` | Checksums of all zips (`sha256sum -c SHA256SUMS`, or `shasum -a 256 -c SHA256SUMS` on macOS) |

Every zip unpacks to a `declarad/` folder with a single executable (`declarad`, or `declarad.exe` on Windows) and
`README.md`. Put the executable anywhere on `PATH`. The publish folders stay in `build/out/` for inspection.

## Notes

* **macOS:** Apple Silicon only runs signed code. The executable is ad-hoc signed when built on a Mac. If you build
  macOS zips elsewhere, sign them on a Mac before shipping: `codesign --force --sign - declarad`. A downloaded binary
  may also need `xattr -d com.apple.quarantine declarad` the first time.
* **Linux/macOS LDAP:** declarad uses the system OpenLDAP client library (`libldap`). It is present on macOS and on
  most Linux distributions; on minimal images install it (`apt install libldap2`, `dnf install openldap`).
* **Permissions:** the executable is stored with mode 755 inside the zips, including zips made on Windows.
* **Verify a build:** `./declarad --version` and `./declarad schema` work without a configuration or a directory.
