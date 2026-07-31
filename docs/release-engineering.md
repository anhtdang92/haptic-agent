# Release engineering

CtrlAgent publishes two Windows x64 artifacts from one source revision:

- a self-contained deterministic portable ZIP;
- a per-user Inno Setup installer with a fixed upgrade identity.

A release is not published unless the exact installer passes install, launch, upgrade, rollback, uninstall, settings-preservation, manifest-integrity, checksum, and Authenticode checks on a fresh `windows-2025` GitHub runner.

## Release identity

The installer `AppId` in `installer/CtrlAgent.iss` is permanent. Changing it would make Windows treat an upgrade as a different product and would break automatic replacement and rollback operations.

Application binaries install under:

```text
%LocalAppData%\Programs\CtrlAgent
```

User-owned data remains outside the installation directory:

```text
%AppData%\CtrlAgent
%LocalAppData%\CtrlAgent
```

Upgrade, uninstall, and rollback preserve those directories by design.

## Signing secrets

Tagged releases require both repository secrets:

```text
WINDOWS_SIGNING_CERTIFICATE_BASE64
WINDOWS_SIGNING_CERTIFICATE_PASSWORD
```

`WINDOWS_SIGNING_CERTIFICATE_BASE64` is the Base64 representation of a code-signing PFX. The workflow writes it to a temporary file, signs with SHA-256, timestamps through DigiCert, verifies every signature, and deletes the temporary PFX.

Create the Base64 value locally without printing the password:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('ctrlagent-signing.pfx')) |
    Set-Clipboard
```

Do not commit certificates, passwords, Azure signing credentials, or exported private keys.

## Pull-request qualification

`.github/workflows/release-qualification.yml` executes on release-related changes. A fresh hosted Windows VM:

1. restores, compiles, and runs all managed tests;
2. builds a synthetic previous installer;
3. builds the current candidate installer;
4. installs the previous version silently;
5. creates an AppData sentinel;
6. upgrades to the current candidate;
7. launches the installed GUI;
8. rolls back to the previous version;
9. verifies the version and sentinel;
10. reinstalls the current version;
11. uninstalls it;
12. verifies that binaries are removed and user data remains.

The synthetic previous build validates installer mechanics. Before a stable 1.0 release, also manually test upgrading from the most recent publicly shipped version.

## Creating a release

Push a semantic-version tag:

```powershell
git tag v0.9.0-beta.1
git push origin v0.9.0-beta.1
```

or run the `release` workflow manually with the same tag format.

The workflow:

- validates the version;
- builds and tests the source;
- publishes self-contained Windows executables;
- builds the native GameInput bridge;
- generates a per-file release manifest;
- creates a deterministic ZIP;
- signs and verifies shipped binaries;
- compiles and signs the installer;
- exercises upgrade and rollback;
- generates SHA-256 checksums;
- generates GitHub build-provenance attestations;
- uploads evidence;
- creates the GitHub release last.

Any failure prevents publication.

## Rollback

Every release includes `Invoke-CtrlAgentRollback.ps1`. Keep the installer for the target prior release, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Invoke-CtrlAgentRollback.ps1 `
  -TargetInstaller .\CtrlAgent-Setup-v0.8.0-win-x64.exe `
  -ExpectedVersion v0.8.0
```

The rollback command:

- backs up roaming and local CtrlAgent data;
- stops CtrlAgent processes;
- silently removes the current installation;
- installs the requested previous version;
- restores settings and credentials if necessary;
- verifies the registry version and executable;
- launches the rolled-back application;
- writes `last-rollback-receipt.json` into the installation directory.

Rollback never relies on overwriting a newer installation with older files. It performs a clean product uninstall followed by a prior signed installer, while preserving user-owned data.

## Artifact verification

Users and maintainers can verify checksums:

```powershell
Get-FileHash .\CtrlAgent-Setup-v0.9.0-win-x64.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

Verify Authenticode:

```powershell
Get-AuthenticodeSignature .\CtrlAgent-Setup-v0.9.0-win-x64.exe | Format-List
```

The expected status for a published release is `Valid`.

## Stable-release blockers

Do not publish a stable release when any of these are true:

- signing secrets are missing;
- Authenticode verification fails;
- release manifest hashes differ;
- clean installation fails;
- the installed GUI exits immediately with an error;
- AppData is lost during upgrade, rollback, or uninstall;
- rollback installs a different version than requested;
- uninstall leaves application binaries behind;
- checksums or provenance attestations are absent;
- the release readiness report does not set `qualifiedForStableRelease` to `true`.
