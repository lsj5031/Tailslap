# Plan 026: Correct contributor, architecture, logging, and release documentation

> **Executor instructions**: Follow this plan exactly. This is a factual
> correction pass, not a documentation rewrite. Run all verification commands.
> If a claim cannot be confirmed from code or CI, STOP and report it. Update
> this plan's status in `plans/README.md` when complete unless a reviewer owns
> the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- AGENTS.md CONTRIBUTING.md README.md .github/ISSUE_TEMPLATE/bug_report.md TailSlap/TailSlap.csproj .github/workflows/release.yml`
> Recheck every factual claim below if these files changed. Plan 023 is expected
> to alter .NET version strings and publish paths; preserve its live values.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none; execute after plan 023 if possible to avoid conflicts
- **Category**: docs
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Current contributor and architecture docs contain actionable inaccuracies:
bug reports point to an obsolete log file, contributors are told to branch from
nonexistent `main`, dependency claims contradict the project file, the P/Invoke
inventory is incomplete, and README names a release asset that CI never
produces. These errors waste debugging time and can cause users to download the
wrong artifact or omit the logs maintainers actually need.

## Confirmed current-state mismatches

| Location | Current claim | Source of truth |
|----------|---------------|-----------------|
| `AGENTS.md:67` | Logger writes `%APPDATA%\TailSlap\app.log` | `Logger` writes JSONL under `%APPDATA%\TailSlap\logs\app.jsonl`; `AGENTS.md:127` and `README.md:160` already state this |
| `CONTRIBUTING.md:16` | Attach `app.log` | Same JSONL path above |
| `.github/ISSUE_TEMPLATE/bug_report.md:32` | Attach `app.log` | Same JSONL path above |
| `CONTRIBUTING.md:20` | Branch from `main` | Remote HEAD and workflows use `master` |
| `CONTRIBUTING.md:26` | No external packages beyond built-in .NET | `TailSlap.csproj` references DependencyInjection, Http, and WebRtcVadSharp |
| `AGENTS.md:123` | Only DependencyInjection, via `Microsoft.AspNetCore.App` | Project references three packages and `Microsoft.WindowsDesktop.App`; no AspNetCore framework reference |
| `AGENTS.md:118` | P/Invokes declared only in MainForm, ClipboardService, AudioRecorder | Repo also has `NativeMethods`, KeyboardHook, TextTyper/realtime/native handle declarations; verify exact current list |
| `README.md:36` | Self-contained release asset is `TailSlap-self-contained-win-x64.exe` | `release.yml` creates `TailSlap-self-contained-win-x64.zip` |

Plan 023 may change `.NET 9` to `.NET 10` and `net9.0-windows` to
`net10.0-windows`. This plan must not reverse those changes.

## Scope

**In scope**:

- `AGENTS.md`
- `CONTRIBUTING.md`
- `.github/ISSUE_TEMPLATE/bug_report.md`
- `README.md`
- `plans/README.md`

**Out of scope**:

- Source code, project files, workflows, or package upgrades
- General prose/style rewrites
- New user guides or feature documentation
- Historical statements in CHANGELOG
- The .NET migration itself (plan 023)

## Git workflow

- Branch: `advisor/026-docs-correction-pass`
- Commit example: `Docs: correct logs, dependencies, branch, and release asset`
- Do not push or open a PR unless instructed.

## Steps

### Step 1: Reconfirm facts from live sources

Run:

```powershell
git symbolic-ref refs/remotes/origin/HEAD
rg -n "logs|app\.jsonl|app\.log" TailSlap/Logger.cs AGENTS.md README.md
rg -n "PackageReference|FrameworkReference" TailSlap/TailSlap.csproj
rg -n "DllImport|LibraryImport" TailSlap --glob "*.cs"
rg -n "DestinationPath|gh release create" .github/workflows/release.yml
```

Expected:

- default branch is `master`;
- active log path is `logs\app.jsonl`;
- three PackageReferences and `Microsoft.WindowsDesktop.App` are present at
  commit `f3016ac`;
- P/Invoke declarations occur beyond the three files named in AGENTS;
- release workflow packages both variants as zip files.

If plan 023 changed package versions or TFM, use its current values without
changing the meaning of this correction.

### Step 2: Correct logging guidance

Replace obsolete `%APPDATA%\TailSlap\app.log` references in:

- `AGENTS.md` Logger service bullet;
- `CONTRIBUTING.md` issue-reporting step;
- `.github/ISSUE_TEMPLATE/bug_report.md` Logs section.

Use exactly:

```text
%APPDATA%\TailSlap\logs\app.jsonl
```

In the issue template, ask users to redact endpoint URLs, account identifiers,
and any unexpected sensitive content before attaching logs. Do not ask them to
paste API keys or config files.

Remove or revise `AGENTS.md:127`'s phrase calling `app.log` a preferred fallback
only if the legacy file is no longer written anywhere. Search first. If legacy
reading/writing still exists, keep the note but clearly identify `app.jsonl` as
the current source.

**Verify**:

```powershell
rg -n "%APPDATA%\\TailSlap\\app\.log" AGENTS.md CONTRIBUTING.md .github/ISSUE_TEMPLATE/bug_report.md
```

Expected: at most one explicitly labeled legacy-history mention in AGENTS;
none in contributor instructions or issue template.

### Step 3: Correct contributor workflow

In `CONTRIBUTING.md`:

- `main` → `master`;
- update the dependency/style bullet to say contributors should avoid adding
  dependencies unnecessarily and must justify new ones, rather than falsely
  claiming there are none;
- list the current production dependencies at a useful level:
  `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Http`, and
  `WebRtcVadSharp`, plus the `Microsoft.WindowsDesktop.App` framework reference;
- add `dotnet test -c Release` to the mandatory test commands before publish;
- preserve the live .NET SDK/TFM values if plan 023 has landed.

Do not change command shell examples solely from Bash to PowerShell. The commands
shown are portable enough.

### Step 4: Correct AGENTS architecture guidance

In `AGENTS.md`:

- replace the dependency bullet with the same accurate package/framework
  summary;
- replace the closed P/Invoke file list with maintainable wording:
  `"P/Invoke declarations are centralized where practical in NativeMethods,
  with subsystem-local declarations in native integration classes (hotkeys,
  clipboard/input, audio, and realtime window handling). Search DllImport /
  LibraryImport before adding duplicates."`
- ensure the Logger bullet points at the JSONL path.

Do not inventory every P/Invoke file. Such a list would immediately drift again.

### Step 5: Correct release asset name

In `README.md:36`, change:

```text
TailSlap-self-contained-win-x64.exe
```

to:

```text
TailSlap-self-contained-win-x64.zip
```

Keep the instruction to extract and run `TailSlap.exe`. Confirm the
framework-dependent asset name also exactly matches `release.yml`.

### Step 6: Validate links, searches, build, and tests

Run:

```powershell
rg -n "\bmain\b|TailSlap-self-contained-win-x64\.exe|%APPDATA%\\TailSlap\\app\.log|Microsoft\.AspNetCore\.App" AGENTS.md CONTRIBUTING.md README.md .github/ISSUE_TEMPLATE/bug_report.md
dotnet build -c Release
dotnet test -c Release
```

Expected for the search:

- no `main` branch instruction;
- no `.exe` release-asset name;
- no current-log instruction using `app.log`;
- no `Microsoft.AspNetCore.App` dependency claim.

An explicitly labeled legacy `app.log` mention in AGENTS is allowed. Inspect any
remaining match manually.

## Test plan

This is documentation-only. The search gates are primary, while build and full
tests ensure no accidental non-doc change or malformed repo edit affected the
project.

## Done criteria

- [ ] All three user/contributor log references point to `logs\app.jsonl`
- [ ] Contributor branch base is `master`
- [ ] Dependency and framework claims match `TailSlap.csproj`
- [ ] P/Invoke guidance no longer claims an incomplete closed file list
- [ ] README self-contained asset name matches release workflow zip
- [ ] `dotnet build -c Release` and `dotnet test -c Release` pass
- [ ] Only in-scope documentation files changed
- [ ] Plan 026 status updated in `plans/README.md`

## STOP conditions

- `Logger.cs` no longer clearly establishes one current log location.
- The default remote branch differs from `master`.
- Release workflow asset names changed and README already matches them.
- Resolving a mismatch requires changing code or CI. Report it as a separate
  implementation issue rather than expanding this docs plan.

## Maintenance notes

Prefer claims tied to stable concepts over exhaustive inventories. Whenever a
workflow changes release asset names, runtime requirements, or default branch,
its PR should update README and CONTRIBUTING in the same change.
