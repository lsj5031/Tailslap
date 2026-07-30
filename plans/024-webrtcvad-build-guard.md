# Plan 024: Derive the WebRtcVad native DLL path from the package version and fail the publish loudly when it is missing

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/TailSlap.csproj`
> If the csproj changed since this plan was written, compare the
> "Current state" excerpts against the live file before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (coordinate ordering with plan 023 — same file; either order works)
- **Category**: dx
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

The WebRTC VAD native DLL is copied into publish output via a hand-built path that hardcodes the package version (`webrtcvadsharp\1.3.0\...`) and is guarded by `Condition="Exists(...)"`. If the package version is ever bumped (or the NuGet root layout differs, e.g. non-default `NUGET_PACKAGES`), the condition silently evaluates false, the DLL is silently omitted from the publish, and at runtime `AudioRecorder` silently falls back to RMS-based VAD (`AudioRecorder.cs` ~617-623 catches the load failure) — shipped builds quietly lose ML voice-activity detection with zero build-time or runtime error. This plan derives the path from the actual `PackageReference` version and turns "DLL missing at publish" into a hard build error.

## Current state

### `TailSlap/TailSlap.csproj`

```xml
<PropertyGroup>
  <WebRtcVadNativePath>$(NuGetPackageRoot)webrtcvadsharp\1.3.0\build\x64\WebRtcVad.dll</WebRtcVadNativePath>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
  <PackageReference Include="Microsoft.Extensions.Http" Version="9.0.0" />
  <PackageReference Include="WebRtcVadSharp" Version="1.3.0" />
</ItemGroup>
...
<!-- WebRtcVadSharp only copies the native DLL on build; publish needs an explicit entry. -->
<Content Include="$(WebRtcVadNativePath)" Condition="Exists('$(WebRtcVadNativePath)')">
  <Link>WebRtcVad.dll</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <CopyToPublishDirectory>Always</CopyToPublishDirectory>
  <Visible>false</Visible>
</Content>
```

### Runtime silent fallback (context only — do not change)

`TailSlap/AudioRecorder.cs` ~617-623: when the WebRTC VAD native library cannot load, the recorder logs and falls back to RMS-based VAD. This graceful runtime degradation is fine for end users whose install got damaged; the bug is letting the BUILD produce such an install.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Publish | `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` (from `TailSlap/`) | exit 0 |
| DLL present | `Test-Path 'TailSlap\bin\Release\net9.0-windows\win-x64\publish\WebRtcVad.dll'` | True (adjust TFM if plan 023 landed) |
| Tests | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/TailSlap.csproj`
- `plans/README.md` (status row)

**Out of scope**:

- Replacing WebRtcVadSharp or making VAD cross-platform (tracked by the macOS plan 012's risk list).
- `AudioRecorder.cs` runtime fallback behavior.
- Package version bumps.

## Git workflow

- Branch: `advisor/024-webrtcvad-build-guard`
- Commit message example: `Build: derive WebRtcVad native path from package version, error on missing DLL`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Single-source the package version

In the csproj, hoist the version into a property and use it in both places:

```xml
<PropertyGroup>
  <WebRtcVadSharpVersion>1.3.0</WebRtcVadSharpVersion>
  <WebRtcVadNativePath>$(NuGetPackageRoot)webrtcvadsharp\$(WebRtcVadSharpVersion)\build\x64\WebRtcVad.dll</WebRtcVadNativePath>
</PropertyGroup>
...
<PackageReference Include="WebRtcVadSharp" Version="$(WebRtcVadSharpVersion)" />
```

A future version bump now updates the native path automatically.

**Verify**: `dotnet build -c Release` → exit 0; the DLL still lands in `bin\Release\<tfm>\win-x64\WebRtcVad.dll`.

### Step 2: Hard-fail the publish when the DLL is absent

Keep the `Exists` condition on the `Content` item (so design-time/IDE loads don't break before restore), but add a target that turns absence into an error at build/publish time, AFTER restore has run:

```xml
<Target Name="VerifyWebRtcVadNative" BeforeTargets="AssignTargetPaths">
  <Error
    Condition="!Exists('$(WebRtcVadNativePath)')"
    Text="WebRtcVad.dll not found at '$(WebRtcVadNativePath)'. The publish would silently ship without ML VAD. Check WebRtcVadSharpVersion matches the restored package, or your NuGet package root layout." />
</Target>
```

Note on target choice: `AssignTargetPaths` runs in every build after restore and before content copying. If the error fires during `dotnet restore` itself or during design-time builds, gate it: add `Condition="'$(DesignTimeBuild)' != 'true' and '$(ExcludeRestorePackageImports)' != 'true'"` refinements only if you observe a false positive — start with the simple version.

**Verify**:

1. `dotnet build -c Release` → exit 0 (DLL exists — no error).
2. Negative test: temporarily set `<WebRtcVadSharpVersion>9.9.9</WebRtcVadSharpVersion>` in ONLY the `WebRtcVadNativePath` property (i.e., break the path, not the PackageReference: change the path property to a bogus version literal), run `dotnet build -c Release` → build FAILS with the custom error text. Revert the temporary break.

### Step 3: Full pipeline check

Run the publish command and the DLL-present check from the table; run `dotnet test -c Release`.

**Verify**: publish exit 0; `WebRtcVad.dll` present next to the published exe; tests green.

## Test plan

- The negative test in Step 2 (executed and reverted, noted in the commit message) is the essential proof.
- Publish + DLL presence check.
- Full test suite (unaffected, gate only).

## Done criteria

- [ ] `rg -n "1\.3\.0" TailSlap/TailSlap.csproj` → exactly one match (the `WebRtcVadSharpVersion` property)
- [ ] `VerifyWebRtcVadNative` target exists and demonstrably fails the build on a bogus path (tested + reverted)
- [ ] `dotnet publish` output contains `WebRtcVad.dll`
- [ ] `dotnet test -c Release` exits 0
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 024 updated

## STOP conditions

- The `Error` target false-positives during `dotnet restore`, design-time builds, or the Tests project build (it references TailSlap.csproj) after trying the documented condition refinements — STOP and report the exact invocation that fails.
- `$(NuGetPackageRoot)` is empty in this environment (some CI restores use `--packages` custom dirs) — if the existing path already relies on it and works today, fine; if you discover it broken today, that IS the bug this plan guards — report it as confirmation and proceed.

## Maintenance notes

- Reviewers: confirm the negative test was actually performed (commit message).
- When plan 023 (net10.0) lands, no interaction — the native path is TFM-independent.
- The macOS port plan (012) lists this native x64-only DLL as principal risk #3; this guard makes any future platform work fail fast instead of silently degrading.
