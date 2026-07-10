# Plan 011: Expose ASR language (and optional session prompt) for OpenAI realtime

> **Executor instructions**: Implement config + Settings + session payload.
> Custom provider may ignore fields — document that. Update `plans/README.md`
> when done.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/OpenAIRealtimeTranscriber.cs TailSlap/ConfigService.cs TailSlap/SettingsForm.cs TailSlap/TailSlapJsonContext.cs TailSlap.Tests/OpenAIRealtimeTranscriberTests.cs`

## Status

- **Priority**: P2
- **Effort**: S–M
- **Risk**: LOW
- **Depends on**: none (pairs with 006 protocol decision but not blocked)
- **Category**: direction
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

`OpenAIRealtimeTranscriber.ConfigureSessionAsync` already sends `language` and `prompt` in `transcription_session.update`, but both are hard-coded empty strings. Non-English dictation users have no TailSlap control; the API surface is half-wired.

## Current state

```csharp
// OpenAIRealtimeTranscriber.cs ConfigureSessionAsync
input_audio_transcription = new
{
    model,
    prompt = string.Empty,
    language = string.Empty,
},
```

- `TranscriberConfig` has model, VAD, timeouts, `RealtimeProvider` — **no** language/session prompt properties.
- Settings form has realtime provider dropdown but no language field.
- Clone() on `TranscriberConfig` must be updated if properties are added (read `ConfigService.cs` Clone).

**Convention**: optional strings null/empty = auto/default; camelCase JSON; validate lightly (language as BCP-47-ish free string, max length).

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build -c Release` | exit 0 |
| OpenAI tests | `dotnet test -c Release --filter FullyQualifiedName~OpenAIRealtimeTranscriber` | pass |
| Full suite | `dotnet test -c Release` | pass |

## Scope

**In scope**:

- `TailSlap/ConfigService.cs` (`TranscriberConfig` + Clone)
- `TailSlap/OpenAIRealtimeTranscriber.cs`
- `TailSlap/SettingsForm.cs` (language TextBox; optional session prompt TextBox or single line)
- `TailSlap/TailSlapJsonContext.cs` only if new types added (prefer primitive strings — no new types)
- `TailSlap.Tests/OpenAIRealtimeTranscriberTests.cs` and/or ConfigServiceTests
- README advanced settings one-liner
- `plans/README.md`

**Out of scope**:

- Custom `RealtimeTranscriber` protocol language (unless trivial no-op field)
- Full locale picker UI with hundreds of languages (free-text + placeholder `en`, `zh`, empty=auto is enough)
- Server VAD threshold Settings exposure

## Git workflow

- Branch: `advisor/011-realtime-language`
- Commit example: `Add realtime ASR language and session prompt settings`
- Do not push/PR unless asked.

## Product rules

1. `Transcriber.Language` (`string`, default `""`) — empty means omit or send empty for provider auto-detect (keep current wire behavior for empty).
2. `Transcriber.RealtimeSessionPrompt` (`string`, default `""`) — optional vocabulary hint; empty keeps today’s empty prompt.
3. Only **OpenAI** client must apply these in `ConfigureSessionAsync`.
4. Settings: show fields when provider is openai **or** always show with helper text “Used when Realtime Provider is openai”.
5. Do not log prompt contents (fingerprint/len if logged).

## Steps

### Step 1: Config properties + Clone

Add to `TranscriberConfig`:

```csharp
public string Language { get; set; } = "";
public string RealtimeSessionPrompt { get; set; } = "";
```

Update `Clone()` copy. Ensure JSON round-trip works with existing configs (missing props → default empty).

**Verify**: `ConfigService` tests or a small round-trip test if pattern exists; else manual serialize via existing tests.

### Step 2: Session payload

In `ConfigureSessionAsync`, use config values:

```csharp
language = _config.Language ?? "",
prompt = _config.RealtimeSessionPrompt ?? "",
```

(Exact field names must match how the class stores `TranscriberConfig` — read constructor.)

**Verify**: unit test that builds/configures session JSON contains expected language when set (existing OpenAI tests may parse outbound messages — extend them). If tests only cover URL building, add a focused test for session JSON if accessible; else test via internal method reflection carefully.

### Step 3: Settings UI

- Add labeled text boxes under realtime provider section.
- Placeholder/hint: Language `en` / leave blank for auto; Session prompt optional.
- Load/save/reset-defaults paths must include new fields (mirror other transcriber fields in SettingsForm save).

**Verify**: `dotnet build -c Release`.

### Step 4: Docs + suite

- README: one bullet under transcription config.
- `dotnet test -c Release`.

## Test plan

| Case | Expected |
|------|----------|
| Default empty language | session still configures; empty language field |
| Language `en` | outbound session JSON includes `en` |
| Clone copies language/prompt | equal on clone |

## Done criteria

- [ ] Config properties exist and clone/serialize
- [ ] OpenAI session update uses config values
- [ ] Settings can edit them
- [ ] Custom provider unaffected
- [ ] `dotnet test -c Release` passes
- [ ] No prompt plaintext logging
- [ ] `plans/README.md` updated

## STOP conditions

- Session event type/shape changed upstream and anonymous object no longer matches — STOP with observed protocol notes.
- SettingsForm grid full / no row free without large layout rewrite — add fields in least-invasive place or STOP for layout help.

## Maintenance notes

- When 006 deprecates custom, these settings become always-relevant.
- Consider later: dropdown of common BCP-47 tags.
