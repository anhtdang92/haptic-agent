# CtrlAgent Documentation

Use this page as the starting point for product, operations, development, validation, and release documentation. Documentation is organized by task so a user does not need to understand the repository structure before finding an answer.

## Start here

| Goal | Document |
|---|---|
| Understand and install CtrlAgent | [`../README.md`](../README.md) |
| Configure controller profiles | [`profiles.md`](profiles.md) |
| Understand supported agent adapters | [`adapters.md`](adapters.md) |
| Understand the system design | [`architecture.md`](architecture.md) |
| Validate controllers and haptics | [`controller-validation.md`](controller-validation.md) |
| Review accessibility support and test requirements | [`accessibility.md`](accessibility.md) |
| Qualify a release | [`release-readiness.md`](release-readiness.md) |

## User journey

1. Read the root README for system requirements, installation choices, first launch, and known limitations.
2. Start with the mock adapter to verify the GUI without credentials or an external agent.
3. Select a workspace and agent only after the mock workflow succeeds.
4. Review the controller profile and keyboard shortcuts before enabling approval actions.
5. Use troubleshooting guidance before filing an issue; include the application version and Windows build, but never tokens, private paths, controller serial numbers, or Bluetooth addresses.

## Documentation standards

Every user-facing feature change must update the relevant documentation in the same pull request. Documentation must:

- state whether behavior is verified, experimental, or planned;
- name prerequisites before instructions;
- use copyable commands and show the expected outcome;
- include recovery or reversal steps for destructive or persistent actions;
- avoid hard-coded test counts and other facts that become stale automatically;
- define acronyms on first use;
- provide meaningful link text rather than “click here”;
- keep headings in a logical hierarchy without skipping levels;
- describe images in surrounding text and provide alternative text when images are embedded;
- distinguish keyboard keys, controller controls, commands, paths, and UI labels consistently.

## Audience map

### End users

Use the root README, profiles guide, adapter guide, accessibility guide, and troubleshooting sections. Internal architecture should not be required to install or operate the application.

### Contributors

Start with the architecture and adapter contracts, then inspect the tests and headless UI-render harness. A behavior claim is not complete until code, tests, documentation, and support language agree.

### Release maintainers

Use the release-readiness standard and the evidence templates under `validation/`. Stable publication requires exact-artifact qualification, human smoke evidence, accessibility evidence, accurate hardware claims, and a rollback decision.

## Change review checklist

Before merging documentation changes, confirm:

- every internal link resolves from its source file;
- command names, file paths, switches, and screenshots match the current product;
- the happy path and at least one failure/recovery path are documented;
- limitations are explicit rather than implied;
- accessibility information is updated when navigation, focus, motion, color, audio, text, or input behavior changes;
- release and support claims are backed by evidence rather than implementation status alone.

## Reporting a documentation problem

Open an issue with the page, section heading, expected information, and what was confusing or incorrect. Do not include credentials, private workspace contents, local usernames, machine names, device identifiers, or full private paths.
