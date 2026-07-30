# Release Readiness Standard

CtrlAgent may be described as **release-ready** only when the exact artifacts offered to users pass the automated artifact gate and the manual evidence gates below. A successful compile is necessary, but it is not a release qualification.

## Release classes

| Class | Meaning |
|---|---|
| Development build | Local or CI output. No user-facing reliability claim. |
| Prerelease | Packaged build for testing. Known limitations may remain and must be listed. |
| Release candidate | Exact installer and portable archive intended for stable publication; all automated gates pass. |
| Stable release | A release candidate that also has completed human smoke evidence and an approved rollback plan. |

The words **stable**, **production-ready**, and **release-ready** must not be used for an artifact that skipped installer rehearsal.

## Automated blocking gates

The release workflow must stop before publication unless all of these pass:

1. Version is a valid `vMAJOR.MINOR.PATCH` tag, optionally with a prerelease suffix.
2. Managed tests pass in Release configuration.
3. Console host and GUI publish self-contained for `win-x64`.
4. Native GameInput bridge builds in Release configuration.
5. Portable and installer filenames exactly match the version.
6. Portable archive has one package root and contains the GUI, console host, bridge, README, license, and docs.
7. Package contains no PDBs, `bin`/`obj` trees, IDE state, or suspicious secret-bearing filenames.
8. Every shipped executable has a Windows PE header.
9. Installer completes silently into a clean temporary directory.
10. Installed payload contains all required executables.
11. Inno Setup creates an uninstaller.
12. Silent uninstall succeeds and removes the application binaries.
13. SHA-256 checksums are generated for both artifacts.
14. A machine-readable readiness report records every check.

Run locally on Windows:

```powershell
./tools/Test-ReleaseReadiness.ps1 `
  -Version v0.1.0 `
  -PortableZip ./CtrlAgent-v0.1.0-win-x64.zip `
  -Installer ./CtrlAgent-Setup-v0.1.0.exe
```

`-SkipInstallerRehearsal` exists only for development diagnostics. A report created with it can never qualify a stable release.

## Human release-candidate evidence

Before promoting the first stable release, test the exact downloaded artifacts on a clean supported Windows installation and record:

- portable archive extracts and launches;
- installer presents correct publisher, version, icon, destination, and tasks;
- per-user install requires no elevation;
- optional desktop and startup shortcuts behave as selected;
- first-run setup completes with the mock adapter;
- controller discovery failure is understandable when no controller is connected;
- tray hide, restore, and explicit exit work;
- Mainframe opens and closes without leaving a process behind;
- uninstall removes program files and shortcuts while preserving or deliberately handling user settings;
- reinstall over the same version behaves predictably;
- upgrade from the previous stable version preserves compatible settings;
- portable archive remains a working recovery path if installation fails.

Evidence belongs under `validation/releases/<version>/` and must not contain usernames, machine names, access tokens, home-directory paths, controller serial numbers, or Bluetooth addresses.

## Rollback gate

Every stable release needs a written rollback decision before publication:

- previous known-good version and artifact checksums;
- conditions that trigger withdrawal;
- person responsible for deciding withdrawal;
- instructions for marking the GitHub release as a prerelease or removing affected assets;
- user-facing recovery instructions using the previous installer or portable archive;
- settings compatibility notes.

A release without a previous stable version must name the portable archive as the recovery path and state that there is no earlier stable build.

## Security and signing

Unsigned binaries must be labeled honestly. Code signing is strongly recommended before calling the installer commercially ready because SmartScreen reputation and publisher identity materially affect user trust. Signing status must be explicit in the release checklist; absence of a certificate must never be hidden by the workflow.

## Hardware dependency

Release readiness and hardware qualification are separate gates. A stable release may ship with experimental hardware only when the support matrix labels it accurately and the stable path remains useful on qualified hardware. No release process may upgrade a hardware claim merely because packaging passed.

## Definition of 10/10

Release readiness reaches 10/10 only after:

- the automated workflow passes on the exact release commit;
- the installer and portable archive are downloaded from GitHub and independently smoke-tested;
- install, launch, core mock workflow, uninstall, and recovery all pass;
- checksums and readiness evidence are attached to the release;
- release notes clearly separate verified, experimental, and planned capabilities;
- rollback instructions are complete;
- at least one real release rehearsal has been completed without relying on repository-local build outputs.
