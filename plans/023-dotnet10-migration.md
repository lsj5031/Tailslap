# Plan 023: Migrate from .NET 9 (STS, past end-of-support) to .NET 10 LTS

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/TailSlap.csproj TailSlap.Tests/TailSlap.Tests.csproj global.json .github/workflows/build.yml .github/workflows/release.yml`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2 (execute LAST among plans 013-022 to avoid churn under active bug-fix plans)
- **Effort**: M
- **Risk**: MED (framework-dependent users must install the .NET 10 Desktop Runtime)
- **Depends on**: recommended after 013-022 land; hard dependency: none
- **Category**: migration
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

.NET 9 is an STS release whose support ended **2026-05-12** — already in the past. Users running the framework-dependent build are on an unpatched runtime, and CI builds against an out-of-support SDK line. .NET 10 is the current LTS (supported until November 2028). Blast radius is small for this codebase: WinForms, DPAPI (`ProtectedData`), WinMM/user32 P/Invoke, `System.Text.Json` source-gen, and `Channels` all carry over without API breaks; the work is TFM/SDK/package/workflow bumps plus a documented runtime-requirement change for users.

## Current state

- `TailSlap/TailSlap.csproj`: `<TargetFramework>net9.0-windows</TargetFramework>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, packages `Microsoft.Extensions.DependencyInjection` 9.0.0, `Microsoft.Extensions.Http` 9.0.0, `WebRtcVadSharp` 1.3.0 (native DLL path pinned separately — see plan 024), `FrameworkReference Microsoft.WindowsDesktop.App`.
- `TailSlap.Tests/TailSlap.Tests.csproj`: `net9.0-windows`; test packages (Microsoft.NET.Test.Sdk 17.11.1, xunit 2.9.2, runner 2.8.2, Moq 4.20.72).
- `global.json`: `{ "sdk": { "version": "9.0.100", "rollForward": "latestFeature" } }`.
- `.github/workflows/build.yml`: three jobs (`test`, `build-framework-dependent`, `build-self-contained`), each with `Setup .NET 9` / `dotnet-version: '9.0.x'`; self-contained artifact path hardcodes `TailSlap/bin/Release/net9.0-windows/win-x64/publish/TailSlap.exe`.
- `.github/workflows/release.yml`: one job, `dotnet-version: '9.0.x'`; release body text references ".NET 9 Desktop Runtime" in three places.
- Version-bearing docs that mention .NET 9: `README.md` (install/build prerequisites), `AGENTS.md` ("Build & Run", "Architecture": net9.0-windows; publish path), `knowledge.md` (Quickstart), `CONTRIBUTING.md` (prerequisites). `CHANGELOG.md` gets a new entry.
- `Directory.Build.props` — read it; it currently contains shared build settings but no TFM (verify: `rg -n "net9" Directory.Build.props` → expect no match; if it matches, include it in the bump).

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| SDK check | `dotnet --list-sdks` | a 10.0.x SDK is installed |
| Build | `dotnet build -c Release` | exit 0 |
| Tests | `dotnet test -c Release` | all pass |
| Publish (fd) | `dotnet publish -c Release --no-restore -o pub-fd` (from `TailSlap/`) | exit 0 |
| Publish (sc) | `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` (from `TailSlap/`) | exit 0, single exe under `bin\Release\net10.0-windows\win-x64\publish\` |

## Scope

**In scope**:

- `TailSlap/TailSlap.csproj`, `TailSlap.Tests/TailSlap.Tests.csproj`
- `global.json`
- `.github/workflows/build.yml`, `.github/workflows/release.yml`
- `README.md`, `AGENTS.md`, `knowledge.md`, `CONTRIBUTING.md`, `CHANGELOG.md` — ONLY the .NET-version strings and publish paths
- `TailSlap/README.md` — ONLY the .NET-version strings and publish paths
- `TailSlap.Tests/ClipboardServiceTests.cs` — replace the two .NET 10
  `WFDEV005`-obsolete `DataObject.GetData` assertions with equivalent generic
  `TryGetData<T>` assertions; no behavior change
- `plans/README.md` (status row)

**Out of scope**:

- Any C# code changes (if the compiler demands one, that's a STOP condition).
- `WebRtcVadSharp` version bump — plan 024 handles the native-path robustness; keep 1.3.0 here.
- Test-package version bumps (Test.Sdk/xunit/Moq) — only bump if `dotnet test` fails on .NET 10 with the current versions.
- Broader doc corrections — plan 026.

## Git workflow

- Branch: `advisor/023-dotnet10-migration`
- Commit message example: `Migrate to .NET 10 LTS (net10.0-windows)`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Confirm a .NET 10 SDK is available

`dotnet --list-sdks` → a `10.0.1xx` (or later) entry. If absent, install the current .NET 10 SDK (winget: `winget install Microsoft.DotNet.SDK.10`) or STOP if the environment forbids installs.

### Step 2: Bump TFMs, SDK pin, and packages

- Both csproj: `net9.0-windows` → `net10.0-windows`.
- `global.json`: `"version": "10.0.100"` (keep `"rollForward": "latestFeature"`).
- `TailSlap.csproj`: `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Http` → the latest stable `10.0.x` (check `dotnet list package --outdated` or nuget.org for the current patch).

**Verify**: `dotnet build -c Release` → exit 0, zero warnings introduced (compare warning count to a pre-change build if any appear).

The first .NET 10 build exposed `WFDEV005` in the clipboard privacy tests added
by plan 022 because `DataObject.GetData` is obsolete and warnings are errors.
Update those assertions to use `TryGetData<string>` and
`TryGetData<MemoryStream>` while preserving the exact text and DWORD payload
checks. This narrow test-source compatibility change is authorized; any other
C# compile error remains a STOP condition.

### Step 3: Full test suite + both publish flavors locally

Run all four commands from the table. For the self-contained publish, confirm `WebRtcVad.dll` sits next to the exe in the publish folder (the csproj `Content` include with `Exists` condition must still fire — the path does not depend on the TFM, but verify: `Test-Path "TailSlap\bin\Release\net10.0-windows\win-x64\publish\WebRtcVad.dll"` → True. If False, STOP — see plan 024; do not ship a publish that silently lost VAD).

**Verify**: all commands exit 0; `dotnet test -c Release` passes with the same test count as before the migration.

### Step 4: Update workflows

In both workflow files:

- Step name `Setup .NET 9` → `Setup .NET 10`; `dotnet-version: '9.0.x'` → `'10.0.x'` (4 occurrences across the two files).
- `build.yml` self-contained artifact path: `TailSlap/bin/Release/net9.0-windows/win-x64/publish/TailSlap.exe` → `net10.0-windows`.
- `release.yml` release body: three ".NET 9 Desktop Runtime" mentions → ".NET 10 Desktop Runtime"; add one line to the body under Installation: `Upgrading from a previous version? The framework-dependent build now requires the .NET 10 Desktop Runtime.`

**Verify**: `rg -n "9\.0\.x|net9\.0|\.NET 9" .github/workflows/` → no matches.

### Step 5: Update docs and changelog

- `README.md`, `AGENTS.md`, `knowledge.md`, `CONTRIBUTING.md`: replace ".NET 9" → ".NET 10" and `net9.0-windows` → `net10.0-windows` in prerequisites, build commands, and publish paths ONLY (do not fix other doc issues — plan 026 owns those).
- `CHANGELOG.md`: add an entry under a new version heading following the file's existing format (read the top of the file for the pattern): "Migrated to .NET 10 LTS. Framework-dependent builds now require the .NET 10 Desktop Runtime."

**Verify**: `rg -n "net9\.0|\.NET 9" README.md AGENTS.md knowledge.md CONTRIBUTING.md` → no matches (CHANGELOG historical entries may legitimately mention .NET 9 — exclude it from the gate).

### Step 6: Manual smoke of all four hotkey modes

Run the freshly published self-contained exe and exercise: refinement (Ctrl+Alt+R on selected text), toggle transcription (Ctrl+Alt+T ×2), push-to-talk (hold Ctrl+Win), realtime (Ctrl+Alt+Y) — each must complete a cycle without errors (transcription modes need the local backend running per AGENTS.md; if unavailable, verify at minimum: app starts, tray icon animates, hotkeys register without error balloons, settings form opens/saves). Record what was tested in the commit message.

## Test plan

- `dotnet test -c Release` — full suite, same pass count as pre-migration.
- Both publish flavors build; self-contained exe launches.
- Manual four-mode smoke (Step 6).

## Done criteria

- [ ] `rg -n "net9\.0" --glob '!CHANGELOG.md' --glob '!plans/**'` (repo root) → no matches
- [ ] `dotnet build -c Release` and `dotnet test -c Release` exit 0 on SDK 10
- [ ] Self-contained publish contains `WebRtcVad.dll`
- [ ] Workflows reference only `10.0.x` and `net10.0-windows`
- [ ] CHANGELOG entry added; release-body runtime requirement updated
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 023 updated

## STOP conditions

- Any compile error or test failure that requires a C# source change — report the exact error; source changes need review, not improvisation.
- `Microsoft.Extensions.*` 10.x introduces a behavioral break in HttpClientFactory (test failures in TextRefiner/RemoteTranscriber suites) — report rather than pinning mixed 9/10 packages.
- The .NET 10 SDK cannot be installed in the execution environment.
- `WebRtcVad.dll` missing from publish output after retarget (Step 3 check).

## Maintenance notes

- Reviewers: the user-facing cost is the Desktop Runtime requirement — confirm the release notes flag it prominently.
- .NET 10 LTS support runs to Nov 2028; schedule the next migration review mid-2028.
- If plan 024 (WebRtcVad build guard) lands first, its hard-error on a missing native DLL turns this plan's Step 3 publish check into a build-time guarantee.
