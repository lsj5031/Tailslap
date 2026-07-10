# Plan 001: Run `dotnet test` in CI and align docs that claim tests already run

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- .github/workflows/build.yml README.md CONTRIBUTING.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: tests / dx
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

TailSlap already has a substantial xUnit suite under `TailSlap.Tests/` (~14 classes covering controllers, TextTyper, ConfigService, history, etc.), but GitHub Actions only restores and publishes. Docs tell contributors that PRs are “automatically built and tested.” Regressions in refinement, transcription controllers, and realtime logic can merge on green CI. Running `dotnet test` on every push/PR closes that gap and makes later plans (especially 004/005) enforceable.

## Current state

- `.github/workflows/build.yml` — two jobs (`build-framework-dependent`, `build-self-contained`) that restore + publish; **no test step**.
- `.github/workflows/release.yml` — publish/package only (leave alone unless you need release-time tests; out of scope).
- `TailSlap.Tests/TailSlap.Tests.csproj` — xUnit + Moq, references `TailSlap`.
- `knowledge.md` documents: `dotnet test` from repo root.
- Docs that overclaim:

```text
README.md:~235
All commits and pull requests are automatically built and tested via GitHub Actions.

CONTRIBUTING.md:~50
Automated tests will run via GitHub Actions (build.yml)

CONTRIBUTING.md:~56
GitHub Actions will automatically build and test your PR
```

Relevant excerpt of `build.yml` today (jobs start at line 16; no test job):

```yaml
jobs:
  build-framework-dependent:
    runs-on: windows-latest
    timeout-minutes: 5
    steps:
    - uses: actions/checkout@v6
    - name: Setup .NET 9
      uses: actions/setup-dotnet@v5
      with:
        dotnet-version: '9.0.x'
    - name: Restore dependencies
      run: dotnet restore -r win-x64
      working-directory: ./TailSlap
    - name: Publish Framework-Dependent
      run: dotnet publish -c Release --no-restore -o "${{ github.workspace }}\artifacts\framework-dependent"
      working-directory: ./TailSlap
  # build-self-contained is similar — publish only
```

**Conventions**: workflows use `actions/checkout@v6`, `actions/setup-dotnet@v5`, Node 24 force flag, `windows-latest`, `permissions: contents: read`, timeout 5 minutes. Match that style. Prefer a **dedicated `test` job** so publish jobs stay independent and PRs can fail fast on tests.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Local tests | `dotnet test -c Release` (repo root) | exit 0, all tests pass |
| Build workflow YAML validity | visual review + `dotnet test` still works | — |
| List test project | `dotnet test -c Release --list-tests` | lists TailSlap.Tests cases |

## Scope

**In scope** (only these files):

- `.github/workflows/build.yml`
- `README.md` (CI claim sentence only)
- `CONTRIBUTING.md` (CI claim sentences only)
- `plans/README.md` (status row)

**Out of scope**:

- `.github/workflows/release.yml` — do not add tests here unless required for a STOP reason; release already publishes artifacts.
- Fixing any pre-existing test failures beyond documenting them if local `dotnet test` fails at plan start (see STOP).
- Code under `TailSlap/` or `TailSlap.Tests/` other than status docs.

## Git workflow

- Branch: `advisor/001-run-tests-in-ci` (or equivalent)
- Commit message example style from history: short imperative, e.g. `Run tests in CI and fix docs claims`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Confirm local suite is green

From repo root:

```powershell
dotnet test -c Release
```

**Verify**: exit code 0; summary shows all tests passed.

If this fails on current `master` with no local changes, **STOP** and report the failing tests — do not paper over by disabling tests in CI.

### Step 2: Add a `test` job to `build.yml`

Insert a new top-level job **before** or alongside the publish jobs (recommended name: `test`). Requirements:

1. `runs-on: windows-latest`
2. `timeout-minutes: 5` (or 10 if suite is slow; prefer 5 first)
3. Steps: checkout (`persist-credentials: false`), setup-dotnet `9.0.x`, then:

```yaml
    - name: Test
      run: dotnet test -c Release --verbosity normal
      working-directory: ${{ github.workspace }}
```

Using the solution/repo root is correct so `TailSlap.Tests` builds against `TailSlap`. Do **not** require `--no-restore` before the first restore; either:

- `dotnet test -c Release` alone (restores as needed), or  
- explicit `dotnet restore` on the solution then `dotnet test -c Release --no-restore`.

4. Make both publish jobs **depend on** the test job so broken tests block artifacts:

```yaml
  build-framework-dependent:
    needs: test
    ...
  build-self-contained:
    needs: test
    ...
```

Keep existing `env.FORCE_JAVASCRIPT_ACTIONS_TO_NODE24` and `permissions` at workflow level.

**Verify**: YAML still has valid structure; `test` job contains `dotnet test`; both build jobs have `needs: test`.

### Step 3: Align README / CONTRIBUTING wording

Keep claims accurate. Acceptable wording:

- README: keep “built and tested via GitHub Actions” **only after** step 2 lands (now true).
- CONTRIBUTING: “Automated tests run via GitHub Actions (`build.yml` test job)” is fine once the job exists.

If you want belt-and-suspenders, change CONTRIBUTING line ~50 to name the job explicitly:

```markdown
- Automated tests run via the `test` job in `.github/workflows/build.yml`
```

Do not claim release workflow runs tests.

**Verify**:

```powershell
Select-String -Path README.md,CONTRIBUTING.md -Pattern "built and tested|build and test|Automated tests"
```

Every hit should be consistent with a real `test` job in `build.yml`.

### Step 4: Sanity-check workflow file does not break solution locally

No GitHub API required. Re-run:

```powershell
dotnet test -c Release
```

**Verify**: exit 0.

## Test plan

- No new unit tests in this plan.
- CI is the test: the new job is the gate.
- Local gate: `dotnet test -c Release` → all pass.

## Done criteria

- [ ] `.github/workflows/build.yml` has a `test` job running `dotnet test` on `windows-latest`
- [ ] Publish jobs have `needs: test` (or equivalent so failures block the workflow)
- [ ] `README.md` / `CONTRIBUTING.md` CI claims match reality
- [ ] `dotnet test -c Release` exits 0 locally
- [ ] No files outside the in-scope list modified (`git status`)
- [ ] `plans/README.md` status for 001 set to `DONE`

## STOP conditions

Stop and report (do not improvise) if:

- Local `dotnet test -c Release` fails on clean tree before any edits.
- A substantial number of tests are flaky under Windows/STA and “fixing” them requires product code changes beyond CI wiring — report failing names instead of deleting tests.
- In-scope files drifted from excerpts and you cannot map the change.
- You feel pressured to add `continue-on-error: true` on the test job — that is out of scope and rejects the finding.

## Maintenance notes

- Reviewers: confirm `needs: test` is present so green publish artifacts imply green tests.
- If the suite grows past the job timeout, raise `timeout-minutes` rather than skipping tests.
- Follow-up (not this plan): optional TRX upload artifact; release workflow test job.
