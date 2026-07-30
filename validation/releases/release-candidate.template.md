# CtrlAgent Release Candidate Evidence

## Identity

- Version: `v0.0.0`
- Commit:
- GitHub Actions run:
- Tester:
- Test date (UTC):
- Windows edition/build:
- Clean VM or physical machine:
- Binary signing status: `unsigned` / `signed`

Do not record usernames, machine names, tokens, controller serial numbers, Bluetooth addresses, or private paths.

## Artifact integrity

| Artifact | Downloaded from GitHub release | SHA-256 matches | Result |
|---|---:|---:|---|
| Portable ZIP |  |  |  |
| Installer |  |  |  |
| Readiness report |  | n/a |  |

## Portable smoke test

| Check | Result | Notes |
|---|---|---|
| Extracts without warnings |  |  |
| GUI launches |  |  |
| Console host launches |  |  |
| Mock-agent first-run setup completes |  |  |
| Mainframe opens and exits |  |  |
| Tray hide/restore/exit works |  |  |
| No process remains after explicit exit |  |  |

## Installer smoke test

| Check | Result | Notes |
|---|---|---|
| Correct product name/version/icon |  |  |
| Per-user install needs no elevation |  |  |
| Default install completes |  |  |
| Optional desktop shortcut works |  |  |
| Optional startup shortcut follows selection |  |  |
| Installed GUI launches |  |  |
| Hardware validation shortcut launches |  |  |
| Repair/reinstall same version is predictable |  |  |
| Uninstaller completes |  |  |
| Program files and shortcuts are removed |  |  |
| User-settings behavior matches release notes |  |  |

## Upgrade and recovery

- Previous stable version:
- Upgrade test result:
- Settings preserved or migrated:
- Portable recovery path verified:
- Rollback artifact and checksum:
- Withdrawal trigger:
- Withdrawal decision owner:

## Support claims

- Qualified controller/transport combinations:
- Experimental controller/transport combinations:
- Agent adapters verified live:
- Agent adapters marked experimental:
- Known limitations included in release notes:

## Decision

- Automated artifact gate: `PASS` / `FAIL`
- Human smoke gate: `PASS` / `FAIL`
- Rollback gate: `PASS` / `FAIL`
- Hardware/support language accurate: `PASS` / `FAIL`
- Stable publication approved: `YES` / `NO`

Approval notes:
