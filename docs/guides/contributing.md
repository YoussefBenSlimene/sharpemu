<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Contributing Workflow

This page summarizes the project's contribution rules. The canonical version
is [`CONTRIBUTING.md`](../../CONTRIBUTING.md); this page adds the practical
details (commit/PR hygiene, REUSE, coding style). If anything here conflicts
with `CONTRIBUTING.md`, the latter wins.

## Before you open a PR

- **Small and focused.** Keep PRs to a single topic. Discuss large
  architectural changes before implementing them.
- **Provide evidence.** PRs should include:
  - The affected game or application.
  - Relevant logs or failing imports.
  - Behavior before and after the change.
  - Real game testing and known limitations.
- **Real behavior, not stubs.** Changes that only return success, zero, or
  fabricated handles without implementing the expected state, output, or side
  effects will generally not be accepted. Functions that create resources,
  write output structures, register callbacks, or expose runtime state should
  model the behavior required by the guest.
- **No Sony proprietary material.** Do not introduce Sony firmware, keys,
  decrypted assets, or other copyrighted PlayStation materials. Reverse
  engineering should be based on publicly available information, clean-room
  techniques, or your own original research.
- **No game-specific hacks** unless no generic solution is possible.
- **Builds and tests pass** before you open the PR. Run `dotnet build
  SharpEmu.slnx` and `dotnet test SharpEmu.slnx`.

## AI-assisted contributions

AI-assisted development is welcome and may be used for research, reverse
engineering, code generation, or documentation. Contributors are expected to
fully understand every line of code they submit.

When submitting an AI-assisted PR:

- Clearly explain **what the change does**, **why it is needed**, and **what
  problem it solves**, in your own words.
- Describe **how you verified the change**, including the games, applications,
  or test cases used.
- Avoid excessive product-level logging. Use logging only when it provides
  meaningful diagnostic value.
- Comments should document design decisions or implementation details in your
  own words. Avoid generic AI-generated comments that merely restate what the
  code already does.
- Be prepared to answer review questions about the implementation. "The AI
  generated it" is not a sufficient explanation.

Large AI-generated changes without a clear understanding of the implementation
are unlikely to be accepted.

## Coding style

SharpEmu follows a consistent style across the project. Please match it.

- **4 spaces** for indentation (no tabs).
- **2 spaces** for XML-based files (`.csproj`, `.props`, `.targets`, `.xml`,
  `yml`, GitHub workflow files where applicable).
- Respect the project's `.editorconfig`.
- Every text file ends with a **single trailing newline**.
- Avoid formatting-only commits unless they are the purpose of the PR.
- Keep naming, formatting, and file organization consistent with the
  surrounding code.
- Prefer small, focused changes over large refactors.

Recommended editor: **VS Code** with **C#** + **C# Dev Kit**. Any editor that
respects `.editorconfig` works.

## REUSE compliance

This repository follows the **REUSE Specification**. Every new file must
contain the appropriate SPDX license header. PRs that do not comply fail CI
and will not be merged.

The convention is to put the header as the first lines of the file:

```text
<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
```

(for Markdown, XML, YAML). For C# files:

```csharp
// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
```

> Existing files inherit the license header from their original source (e.g.
> `src/SharpEmu.LibAtrac9` files originate from the VGMToolbox project under
> MIT — see the file's header for details).

CI runs `fsfe/reuse-action@v6` to verify compliance.

## Commit and PR hygiene

- Commit messages should describe **why** the change is being made, not just
  what changed.
- Keep commits logical; squash fixups before review.
- Branch names like `feature/<short-name>` or `fix/<short-name>` are fine.
- PRs target `main`.
- The PR template is **mandatory** (see
  `.github/pull_request_template.md`). PRs that leave the required checklist
  incomplete will be closed without review.

## Filing issues

Two issue templates live in `.github/ISSUE_TEMPLATE/`:

- `bug_report.yml` — for emulator bugs. Include the game, repro steps,
  relevant logs, and host details.
- `game-compatibility.yml` — for new game compatibility reports. Include the
  title, title id, what works, and what doesn't.

The `config.yml` ties them together.

## Release process

Releases use [`scripts/release.py`](../../scripts/release.py) — see
[`release-use.md`](../release-use.md) for the exact steps. The script is
the only supported way to bump versions and push tags; pushing tags manually
before the version-bump PR is merged will not trigger a release.

## Where to get help

- Open a discussion / draft PR for design questions.
- For small questions, look at the surrounding code — the project values
  self-documenting code, but the architecture pages (`docs/architecture/`)
  and per-project guides (`docs/projects/`) cover the bigger questions.

## Related

- [`CONTRIBUTING.md`](../../CONTRIBUTING.md) — the canonical version.
- [Testing guide](testing.md)
- [Debugging guide](debugging.md)
- [Architecture overview](../architecture/overview.md)
