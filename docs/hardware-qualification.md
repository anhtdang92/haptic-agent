# Hardware Qualification Policy

CtrlAgent distinguishes **implemented**, **experimental**, and **qualified** hardware. Code coverage or a successful build does not qualify a physical device. A device is production-qualified only when a repeatable evidence report passes the repository's automated gate.

## Qualification levels

| Level | Meaning | Product wording |
|---|---|---|
| Implemented | Protocol and adapter code exist and are covered by software tests. | Implemented; hardware verification pending |
| Experimental | At least one real device/transport has been exercised, but one or more release gates are blocked or incomplete. | Experimental |
| Qualified | Every required test passes for a specific device model, firmware, transport, Windows build, driver stack, and CtrlAgent version. | Supported |
| Unsupported | A required behavior is impossible or unsafe on the tested stack. | Unsupported; fallback documented |

Qualification is **per transport**. Passing an Xbox controller over USB does not qualify Bluetooth or the Xbox Wireless Adapter. Passing a DualSense does not qualify DualSense Edge.

## Release gates

A transport may be marked `qualified` only when all of these are true:

1. Discovery works both before and after application launch.
2. Disconnect and reconnect recover without restarting CtrlAgent.
3. Every advertised standard control is independently verified.
4. Required simultaneous inputs do not create missed presses or stuck releases.
5. At least two haptic cues are consistently distinguishable.
6. Haptics stop after completion, interruption, disconnect, process exit, and reconnect.
7. Foreground, background, tray, overlay, and lock-screen behavior is documented.
8. A 30-minute soak includes at least 100 haptic cues and one reconnect.
9. Reported capabilities exactly match observed hardware behavior.
10. The report contains no serial number, Bluetooth address, account name, or machine name.
11. The machine-readable report passes `tools/Test-HardwareQualification.ps1` without `-AllowExperimental`.

A capability that was not physically observed must be `false`, even when protocol documentation or unit tests indicate it should exist.

## Evidence workflow

Copy the template once per device and transport:

```powershell
Copy-Item validation/hardware-report.template.json `
  validation/2026-07-30-elite-series-2-usb.json
```

Fill every field from the actual validation session, then run:

```powershell
./tools/Test-HardwareQualification.ps1 `
  -ReportPath validation/2026-07-30-elite-series-2-usb.json
```

Development-only reports may be checked with:

```powershell
./tools/Test-HardwareQualification.ps1 `
  -ReportPath validation/2026-07-30-elite-series-2-usb.json `
  -AllowExperimental
```

`-AllowExperimental` must never be used as evidence for changing product documentation from experimental to supported.

## Required qualification matrix

### Xbox-family

| Device | USB | Bluetooth | Xbox Wireless Adapter |
|---|---|---|---|
| Standard Xbox controller | Required | Required | Required |
| Elite Series 2 | Required | Required | Required |

The Elite Series 2 matrix must explicitly report whether independent paddles, trigger rumble, and Guide-button input are available on each transport. Firmware-mapped paddles are not independent paddles.

### PlayStation

| Device | USB | Bluetooth |
|---|---|---|
| DualSense | Required | Required |
| DualSense Edge | Required | Required |

The DualSense matrix must verify main rumble, lightbar, adaptive triggers, touchpad click, PS button, and clean output shutdown. The Edge matrix additionally requires independent rear paddles and Fn buttons.

## Current blockers to 10/10 maturity

The repository cannot honestly claim full hardware maturity until all of the following evidence exists:

- Elite Series 2 over USB, Bluetooth, and Xbox Wireless Adapter;
- standard Xbox controller over the same three transports;
- DualSense over USB and Bluetooth;
- DualSense Edge over USB and Bluetooth;
- background/tray haptic behavior characterized for every Windows transport;
- repeatable soak reports with no stuck state, runaway rumble, crash, or reconnect failure;
- product copy generated from the evidence rather than maintained as optimistic prose.

## Failure policy

A failed gate does not automatically block the entire controller family. Instead:

- disable or stop advertising the failed capability;
- activate a validated fallback when one exists;
- mark only that device/transport combination experimental or unsupported;
- retain the raw observation in the report;
- open an issue linking the report and describing the next technical path.

Safety-related failures—stuck approval input, stale held state after reconnect, or rumble/adaptive-trigger output that does not stop—are release blockers for that transport.
