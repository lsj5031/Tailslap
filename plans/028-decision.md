# Plan 028 Decision: Secure configuration export/import

## Status

**Accepted**

The v1 contract is accepted with the amendments and implementation
prerequisites below. In particular, the implementation must add a
failure-reporting atomic save path; the current `ConfigService.Save` is `void`,
uses `File.WriteAllText`, and swallows failures, so it cannot satisfy this
contract as written.

## Evidence reviewed

- Live cumulative source in `T:\tailslap-lite-execute-all`, including the
  landed effects of plans 018, 021, 022, and 027.
- Drift from `f3016ac`: config caching, one-shot DPAPI failure notifications
  with `notifyOnFailure` overloads, `ExcludeFromClipboardHistory`, and the
  broadened HTTP/realtime meaning of `TranscriberConfig.Language`.
- Existing plaintext history-export warning and `SaveFileDialog` flow.
- Plan 012's requirement to separate portable configuration from secrets and
  never represent Windows DPAPI ciphertext as portable.

## Context and binding constraints

- The live file is `%APPDATA%\TailSlap\config.json`.
- `ApiKeyEncrypted` is Windows-user-bound DPAPI ciphertext under
  `DataProtectionScope.CurrentUser`; it is not portable.
- The transfer model must be a dedicated DTO. It must not serialize
  `AppConfig` or inherit future properties automatically.
- Settings-only export is the default. Plaintext key export is an explicit,
  unchecked opt-in with a second confirmation.
- Imports are detached, strict, previewed, confirmed, and committed once.
- Prompts, HTTP metadata, language/vocabulary hints, and endpoint URLs can be
  sensitive even when they are classified as portable. “Portable” does not
  mean “safe to publish.”
- No export or import step uses the clipboard. The
  `ExcludeFromClipboardHistory` setting protects TailSlap-originated delivery
  writes on Windows; it does not make clipboard-based configuration transfer
  safe.

## V1 envelope

The property names and nesting below are the concrete v1 compatibility
boundary. All example values are placeholders, not live configuration.

```json
{
  "schemaVersion": 1,
  "kind": "tailslap-config-export",
  "exportedAtUtc": "<UTC ISO-8601 timestamp>",
  "appVersion": "<TailSlap semantic version>",
  "settings": {
    "autoPaste": true,
    "excludeFromClipboardHistory": true,
    "useClipboardFallback": true,
    "hotkeys": {
      "refinement": {
        "modifiers": "<portable modifier names>",
        "key": "<portable logical key name>",
        "rightAltOnly": false
      },
      "toggleTranscription": {
        "modifiers": "<portable modifier names>",
        "key": "<portable logical key name>",
        "rightAltOnly": false
      },
      "pushToTalk": {
        "modifiers": "<portable modifier names>",
        "key": "<portable logical key name or null for modifier-only>",
        "rightAltOnly": true
      },
      "realtimeTranscription": {
        "modifiers": "<portable modifier names>",
        "key": "<portable logical key name>",
        "rightAltOnly": false
      }
    },
    "llm": {
      "enabled": true,
      "baseUrl": "<HTTP-or-HTTPS endpoint>",
      "model": "<model identifier>",
      "temperature": 0.2,
      "maxTokens": null,
      "refinementPrompt": "<user prompt text>",
      "httpReferer": "<optional HTTP Referer value or null>",
      "xTitle": "<optional X-Title value or null>"
    },
    "transcriber": {
      "enabled": true,
      "baseUrl": "<HTTP-or-HTTPS endpoint>",
      "model": "<model identifier>",
      "timeoutSeconds": 30,
      "autoPaste": true,
      "enableVAD": true,
      "silenceThresholdMs": 2000,
      "microphone": "systemDefault",
      "streamResults": false,
      "webSocketConnectionTimeoutSeconds": 10,
      "webSocketReceiveTimeoutSeconds": 30,
      "webSocketSendTimeoutSeconds": 10,
      "webSocketHeartbeatIntervalSeconds": 10,
      "webSocketHeartbeatTimeoutSeconds": 15,
      "vadActivationThreshold": 900,
      "vadSustainThreshold": 550,
      "vadSilenceThreshold": 120,
      "useWebRtcVad": true,
      "webRtcVadSensitivity": 2,
      "enableAutoEnhance": true,
      "autoEnhanceThresholdChars": 100,
      "realtimeProvider": "openai",
      "language": "<BCP-47 hint or empty string for provider auto-detect>",
      "realtimeSessionPrompt": "<optional vocabulary/domain prompt>"
    }
  },
  "secrets": {
    "format": "plaintext",
    "llmApiKey": "<plaintext key only after explicit opt-in>",
    "transcriberApiKey": "<plaintext key only after explicit opt-in>"
  }
}
```

`secrets` is absent by default. Within an opted-in `secrets` object, absence of
an individual key property means preserve that destination key. A present
nonblank string means replace it. A present `null`, empty string, or
whitespace-only string means clear it and requires an explicit clear warning.
`format` is mandatory when `secrets` exists and v1 accepts only `"plaintext"`.

The hotkey representation deliberately does not export WinForms `Keys` numeric
values or Win32 modifier bitmasks. V1 uses an allow-listed canonical vocabulary
such as modifier names (`alt`, `control`, `shift`, `meta`) and logical key names.
The Windows importer maps `meta` to Win and previews every imported shortcut
for destination review. Unsupported key names or combinations are rejected,
not silently defaulted.

The microphone field is always `"systemDefault"` in v1. The live numeric
`PreferredMicrophoneIndex` is an enumeration-order index and is never exported
or applied from another file.

## Complete live-property classification

The “future” column records migration or compatibility handling even for
properties whose current disposition is settled.

### `AppConfig`

| Property | Classification and v1 treatment | Future concern |
|---|---|---|
| `AutoPaste` | Portable setting; export/import. | None beyond normal version review. |
| `ExcludeFromClipboardHistory` | Windows-only setting requiring destination review. Export as a named setting; apply on Windows, warn and ignore on platforms without equivalent clipboard-history controls. Default remains privacy-preserving. | A macOS host may map it only after defining equivalent pasteboard semantics. |
| `UseClipboardFallback` | Portable setting; export/import with preview because it changes clipboard exposure. | Permission/fallback behavior is platform-specific. |
| `Hotkey` | Windows-only runtime setting represented portably as `hotkeys.refinement`; destination review required. | Translate canonical names to each host's native shortcut model. |
| `TranscriberHotkey` | Same, as `hotkeys.toggleTranscription`. | Same. |
| `TypelessHotkey` | Same, as `hotkeys.pushToTalk`; modifier-only form is allowed. | Plan 012 defers modifier-only push-to-talk on macOS. |
| `StreamingTranscriberHotkey` | Same, as `hotkeys.realtimeTranscription`. | Realtime is deferred in plan 012. |
| `Llm` | Structural container, not a scalar setting; explicitly map the fields below. | Never serialize the live type as the transfer DTO. |
| `Transcriber` | Structural container, not a scalar setting; explicitly map the fields below. | Same. |

Auto-start is **not** an `AppConfig` property. It is Windows Registry state
behind `AutoStartService`; v1 neither exports nor imports it. A future
cross-platform preference needs its own reviewed setting and permission UX.

### `HotkeyConfig` (applies to all four hotkey properties)

| Property | Classification and v1 treatment | Future concern |
|---|---|---|
| `Modifiers` | Windows-only native representation; export canonical modifier names, never the numeric bitmask. Destination maps and reviews. | Keyboard layouts and macOS Command/Option mappings need host contract tests. |
| `Key` | Windows-only native representation; export canonical logical key name, or `null` only for push-to-talk. Never export a WinForms enum number. | Some OEM/layout-specific keys may not map and must be rejected or skipped with confirmation. |
| `RightAltOnly` | Windows-only setting requiring destination review. Valid only for modifier-only push-to-talk with Alt as the sole modifier; otherwise reject an inconsistent import. | macOS has no approved equivalent in plan 012; warn and ignore there. |

### `LlmConfig`

| Property | Classification and v1 treatment | Future concern |
|---|---|---|
| `Enabled` | Portable setting. | None beyond normal version review. |
| `BaseUrl` | Portable setting; sensitive because it can reveal a private/local endpoint. | Import only HTTP/HTTPS; do not log query strings. |
| `Model` | Portable setting. | Provider-specific model availability is not validated offline. |
| `Temperature` | Portable setting; require `0..2`. | None. |
| `MaxTokens` | Portable nullable setting; null or `1..32768`. | None. |
| `RefinementPrompt` | Portable setting; sensitive user-authored content. | Add an explicit size limit before implementation. |
| `ApiKeyEncrypted` | Secret storage field and Windows-only ciphertext; forbidden from every export DTO and rejected wherever encountered in an import. | A new secret-storage field must never join export automatically. |
| `HttpReferer` | Portable setting; potentially sensitive metadata. | Add length/control-character validation. |
| `XTitle` | Portable setting; potentially sensitive metadata. | Add length/control-character validation. |
| `ApiKey` (`JsonIgnore`) | Derived secret accessor; plaintext appears only in opted-in `secrets.llmApiKey`. Import through destination-local protection. | Replace DPAPI with `ISecureStorage` on non-Windows hosts. |
| `DefaultRefinementPrompt` | Derived/static default, not a serialized property; never exported separately. | Default changes do not rewrite explicit exported prompts. |
| `GetEffectiveRefinementPrompt()` | Derived behavior, not exported. | None. |

### `TranscriberConfig`

| Property | Classification and v1 treatment | Future concern |
|---|---|---|
| `Enabled` | Portable setting. | None. |
| `BaseUrl` | Portable setting; sensitive private/local endpoint. | Import only HTTP/HTTPS; do not log query strings. |
| `Model` | Portable setting. | Provider availability is not validated offline. |
| `ApiKeyEncrypted` | Secret storage field and Windows-only ciphertext; forbidden from export DTOs and rejected on import. | Same secure-storage boundary as LLM. |
| `TimeoutSeconds` | Portable setting; require `1..300`. | None. |
| `AutoPaste` | Portable setting. | Destination text-delivery capability may differ. |
| `EnableVAD` | Portable setting. | VAD implementation availability differs by host. |
| `SilenceThresholdMs` | Portable setting; require `100..5000`, matching `ConfigService`. | Settings UI currently uses `500..10000`; implementation must reconcile this drift rather than silently repair. |
| `PreferredMicrophoneIndex` | Windows/machine-specific and unstable; do not export the integer. Emit `microphone: "systemDefault"` and reset destination to its system default (`-1` on Windows). | A future schema may use a stable device identifier plus explicit fallback. |
| `StreamResults` | Portable setting. | Plan 012 defers incremental HTTP typing on macOS. |
| `WebSocketConnectionTimeoutSeconds` | Portable setting; require `1..120`. | None. |
| `WebSocketReceiveTimeoutSeconds` | Portable setting; require `1..120`. | None. |
| `WebSocketSendTimeoutSeconds` | Portable setting; require `1..120`. | None. |
| `WebSocketHeartbeatIntervalSeconds` | Portable setting; require `5..60`. | None. |
| `WebSocketHeartbeatTimeoutSeconds` | Portable setting; require `10..120`. | Add relational checks if the runtime requires timeout greater than interval. |
| `VadActivationThreshold` | Portable advanced setting; require a future explicit numeric range and consistency checks. | Settings currently writes presets but `CreateValidatedCopy` does not validate it. |
| `VadSustainThreshold` | Portable advanced setting; require a future explicit numeric range and consistency checks. | Same. |
| `VadSilenceThreshold` | Portable advanced setting; require a future explicit numeric range and consistency checks. | Same. |
| `UseWebRtcVad` | Portable setting, but destination capability warning is required. | Plan 012 must port or replace the native VAD dependency. |
| `WebRtcVadSensitivity` | Portable setting; require integer `0..3`. | `CreateValidatedCopy` currently omits this check. |
| `EnableAutoEnhance` | Portable setting. | Requires usable LLM configuration at runtime, not import time. |
| `AutoEnhanceThresholdChars` | Portable setting; require `10..10000`. | `CreateValidatedCopy` currently omits this check. |
| `RealtimeProvider` | Portable setting; v1 allow-list is `custom` or `openai`, case-normalized. | New providers require an explicit schema compatibility decision. |
| `Language` | Portable setting; applies to HTTP transcription and OpenAI-protocol realtime. Empty means provider auto-detect. | Add BCP-47 syntax/length validation; do not regress it to realtime-only semantics. |
| `RealtimeSessionPrompt` | Portable setting; sensitive vocabulary/domain content, currently realtime-only. | Add size limit; do not forward it to HTTP without a separate privacy/provider decision. |
| `ApiKey` (`JsonIgnore`) | Derived secret accessor; plaintext appears only in opted-in `secrets.transcriberApiKey`. | Replace DPAPI with host secure storage on macOS. |
| `WebSocketUrl` (`JsonIgnore`) | Derived from base URL/provider; never export. | Recompute on destination. |
| `TranscriptionEndpoint` (`JsonIgnore`) | Derived from base URL; never export. | Recompute on destination. |

## Export UX

1. Open **Export configuration** from Settings.
2. Show a summary noting that settings-only exports may contain prompts,
   private endpoint URLs, HTTP metadata, language hints, and vocabulary.
3. Show `Include API keys (plaintext)` unchecked by default.
4. When checked, show a persistent warning state and require:

   > This export includes API keys as readable plaintext. Anyone with this
   > file can use those credentials. Store it securely and delete it after
   > import. Continue?

5. If secret decryption is requested, decrypt only in memory. Because DPAPI's
   one-argument API delegates to `notifyOnFailure: true`, a nonempty encrypted
   field that produces an empty plaintext result is a decryption failure, not
   an absent key: abort the export and show the existing one-shot DPAPI
   notification plus “API keys could not be decrypted; no file was written.”
   Do not fall back to settings-only without asking.
6. Use a `.json` `SaveFileDialog`.
   - Settings only: `tailslap-config-<UTC timestamp>.json`
   - With keys: `tailslap-config-with-secrets-<UTC timestamp>.json`
7. Build a dedicated DTO, serialize it to a temp file in the selected
   directory, flush, then atomically move/replace the destination where the
   filesystem supports it. On failure, remove only the temp file created by
   this operation and leave any destination file unchanged.
8. Success text states “Settings exported without API keys” or “Settings and
   plaintext API keys exported.” No clipboard copy, upload, telemetry, raw JSON
   logging, endpoint logging, or secret fingerprinting is allowed.

## Import UX

1. Open a `.json` file with an `OpenFileDialog`. Enforce a documented maximum
   file size before reading.
2. Parse with duplicate-property detection and presence tracking for optional
   secret properties. Require the exact `kind`, integer `schemaVersion`, and
   required structure.
3. Migrate the detached portable DTO if needed, then validate strictly. Do not
   call a repair-to-default path.
4. Construct a candidate from a clone of the current config so fields absent
   by contract, especially secrets, remain unchanged. Map portable settings;
   reset the microphone to system default; translate hotkeys for the current
   host.
5. Show a preview containing no values for secrets:
   - categories of settings changed;
   - LLM key: unchanged / replace / clear;
   - transcriber key: unchanged / replace / clear;
   - machine-specific fields skipped or reset;
   - unknown-field and destination-capability warnings.
6. Require final confirmation. Any explicit key clear gets an additional
   conspicuous statement that the destination credential will be removed.
7. Only after confirmation, apply each nonblank plaintext key to the detached
   candidate through `LlmConfig.ApiKey` or `TranscriberConfig.ApiKey`. The
   setters use destination-local DPAPI protection and notify on failure. For a
   nonblank input, an empty resulting `ApiKeyEncrypted` means protection
   failed: abort before save. Clearing uses the same property with `null`.
8. Commit through the new atomic, failure-reporting config-save API once.
   Refresh the cache and notify `ConfigChanged` only after the replacement
   succeeds.
9. Show success and remind the user to delete a plaintext-secret source file.

Unknown fields in an otherwise supported v1 envelope produce warnings and are
ignored. Unknown `kind`, unsupported version, duplicate known properties,
wrong JSON types, non-finite numbers, forbidden ciphertext property names, and
invalid values are hard failures.

## Validation and atomicity

### Reusable checks

- `IsValidUrl`: nonblank absolute HTTP/HTTPS URL.
- `IsValidTemperature`: `0..2`.
- `IsValidMaxTokens`: `1..32768` when present.
- `IsValidModelName`: nonblank.
- `IsValidTimeout`: `1..300`.
- `IsValidSilenceThreshold`: `100..5000`.
- `IsValidWebSocketTimeout`: `1..120`.
- `IsValidWebSocketHeartbeatInterval`: `5..60`.
- `IsValidWebSocketHeartbeatTimeout`: `10..120`.
- Settings hotkey rules: at least one modifier; a normal key for all except
  push-to-talk; no duplicates; only push-to-talk may be modifier-only.

### Checks that must be added or reconciled

- Canonical hotkey allow-list, numeric Win32 values forbidden, `RightAltOnly`
  consistency, and destination availability checks.
- `RealtimeProvider` allow-list.
- BCP-47 language syntax/length.
- Prompt and metadata size/control-character limits.
- VAD threshold ranges and cross-field consistency.
- `WebRtcVadSensitivity` `0..3`.
- `AutoEnhanceThresholdChars` `10..10000`.
- Maximum envelope/file/string sizes and duplicate JSON property rejection.
- The silence range mismatch between `ConfigService` (`100..5000`) and
  Settings (`500..10000`) must be resolved before implementation; v1 uses the
  service's existing public validator until a separate product decision
  changes it.

`CreateValidatedCopy` is not an import validator. It silently substitutes
defaults for invalid URLs, temperature, tokens, models, timeout, silence,
WebSocket values, and an empty toggle-transcription hotkey. Import must instead
return all validation errors and make no changes.

The current `Save` implementation is also insufficient: it returns no result,
swallows exceptions, writes in place, invalidates the cache on failure, and can
let the watcher observe a partial write. Future implementation must add a
separate `TrySaveAtomic`/throwing equivalent that serializes to a sibling temp
file, flushes it, atomically replaces the live file, updates `_cache` only
after success, and suppresses/debounces watcher events until commit. Failure
must preserve both the live file byte-for-byte and the prior in-memory config.

## Error behavior

| Failure | User-visible behavior | State/log behavior |
|---|---|---|
| Malformed JSON, duplicate property, wrong type, oversized input | “This file is not a valid TailSlap configuration export.” Include safe field paths, not values. | No mutation; log outcome and exception type only. |
| Wrong or missing `kind` | “This file is not a TailSlap configuration export.” | No mutation. |
| Unsupported schema version | “This export version is not supported by this TailSlap version.” | No mutation; log numeric version only. |
| Unknown fields in supported v1 | Preview warnings listing field paths only. | Continue only after confirmation. |
| Invalid URL, hotkey, provider, language, or range | List safe field paths and rules. | No repair, no mutation. |
| Forbidden encrypted-key field | “Encrypted API-key data is not portable and cannot be imported.” | Reject entire import; do not log its value. |
| DPAPI unprotect during secret export | “API keys could not be decrypted; no file was written.” | One-shot notification is allowed; no output file. |
| DPAPI protect during import | “API keys could not be securely stored; configuration was not imported.” | Detached candidate discarded; existing config and keys unchanged. |
| Save/replace failure | “Configuration could not be saved; existing settings were not changed.” | Existing bytes/cache retained; exception type only. |
| User cancels preview/clear warning | Close without changes. | Optional outcome-only log. |

Logs may contain only operation name, success/cancel/failure outcome, supported
schema version, counts of warnings/changed categories, and exception type.
They must not contain JSON, setting values, key values, key fingerprints,
prompts, endpoint URLs or query strings, language/session prompts, or source
file contents.

## Threat model

### Threats addressed

- Accidental transfer of Windows-user-bound DPAPI ciphertext.
- Accidental plaintext-key export through a default or auto-serialized path.
- Silent destination-key clearing when secrets are absent or DPAPI fails.
- Malformed or hostile JSON changing live configuration partially.
- Private values leaking through logs, clipboard history, telemetry, or
  filenames.
- Machine-specific microphone indices and native hotkey numbers being applied
  as though portable.
- Future `AppConfig` additions leaking because the transfer DTO mirrors the
  storage model.

### Residual risks users must see

- A secret-bearing file is readable plaintext at rest and usable by anyone who
  obtains it.
- A settings-only file can reveal private endpoints, prompts, HTTP metadata,
  workflow preferences, language, and domain vocabulary.
- Importing endpoints/prompts from an untrusted file can redirect future
  requests or alter generated text; preview must identify those categories.
- Atomic replacement depends on destination filesystem support; unsupported
  filesystems must fail safely rather than fall back to in-place writes.

### Non-goals

- Password-encrypted or public-key-encrypted export files.
- Cloud sync, automatic upload, telemetry, clipboard export, histories, logs,
  audio, prompt history, or cross-platform encrypted secret storage.
- Importing DPAPI ciphertext, Windows Registry auto-start state, or microphone
  device indices.
- Proving endpoint reachability, credentials, provider model availability, or
  trustworthiness during offline validation.

## Versioning and migrations

- `kind` is mandatory and exactly `"tailslap-config-export"`.
- `schemaVersion` is a mandatory integer beginning at `1`.
- Import supports only explicitly implemented versions. A numerically newer
  version is rejected, not treated as v1 merely because fields look familiar.
- Unknown fields are warnings only after a supported version has been chosen.
- Migrations are pure `PortableConfigExportVn -> PortableConfigExportVn+1`
  transformations. They do not read or write live config, decrypt secrets,
  access devices, or silently invent machine-specific values.
- Each version has dedicated source-generated JSON registrations. Export always
  emits the latest supported version.
- New `AppConfig` properties require explicit classification and mapping.
  Backward-compatible optional additions may remain v1 only after review;
  changed meaning, representation, or required data requires a new version.

## Future implementation file map

| File | Change |
|---|---|
| `TailSlap/ConfigTransferDtos.cs` (new) | V1 envelope, settings, canonical hotkey, secret-presence DTOs, and migration DTOs. No dependency on `AppConfig`. |
| `TailSlap/ConfigTransferService.cs` (new) | Export mapping, strict parse, unknown-field collection, migration, validation, preview model, secret handling, and no-sensitive-log policy. |
| `TailSlap/IConfigTransferService.cs` (new) | UI-independent export/import orchestration contract. |
| `TailSlap/ConfigService.cs` | Add atomic, failure-reporting commit path with coherent cache/watcher behavior; keep ordinary load behavior unchanged. |
| `TailSlap/IConfigService.cs` | Expose the atomic commit result/exception contract needed by transfer service. |
| `TailSlap/TailSlapJsonContext.cs` | Register every versioned transfer DTO for source-generated JSON. |
| `TailSlap/SettingsForm.cs` | Export/import entry points and settings-only sensitivity copy. |
| `TailSlap/ConfigImportPreviewForm.cs` (new) | Category-only preview, unknown/machine-specific warnings, and replace/clear/unchanged key states. |
| `TailSlap/Program.cs` | Register transfer service in DI. |
| `TailSlap.Tests/ConfigTransferServiceTests.cs` (new) | Contract, validation, failure, leak, migration, and atomicity tests. |
| `TailSlap.Tests/ConfigServiceTests.cs` | Atomic save/cache/watcher tests using an isolated config path seam. |

Do not put export DTOs on `AppConfig`, and do not implement transfer by
serializing then deleting `apiKeyEncrypted` properties.

## Future test matrix

At minimum, implementation must cover:

1. Settings-only v1 export/import round trip preserves every mapped portable
   field.
2. Default export omits the entire `secrets` object.
3. Secret export cannot proceed without the unchecked option and warning
   confirmation.
4. Opted-in secret export contains plaintext placeholders in the secret DTO
   only and uses the warning filename.
5. Raw output contains no forbidden encrypted-key property name or ciphertext.
6. Secret export aborts without a file when a nonempty encrypted key cannot be
   unprotected.
7. Import with no `secrets` preserves both destination keys.
8. Import with one secret property absent preserves that key while replacing
   the present key.
9. Imported nonblank keys are DPAPI-protected for the destination and plaintext
   is absent from live `config.json`.
10. Explicit null, empty, and whitespace key values preview “clear” and cannot
    commit without clear confirmation.
11. Canceling preview or clear confirmation leaves file, cache, and keys
    unchanged.
12. Malformed JSON, duplicate known fields, wrong types, wrong kind, and each
    unsupported version leave config byte-for-byte unchanged.
13. Unknown fields on supported v1 warn by field path and can be confirmed
    without applying the unknown data.
14. Any imported encrypted-key property is rejected, with no value in logs or
    messages.
15. Invalid URLs, models, temperature, token counts, timeouts, provider,
    language, VAD values, and prompt/metadata sizes report errors without
    repair.
16. Canonical hotkeys round-trip; duplicates, unsupported names, invalid
    modifier-only uses, and inconsistent `rightAltOnly` are rejected.
17. Microphone always exports/imports as system default; numeric device indices
    are never transferred.
18. DPAPI protection failure on either key discards the detached candidate and
    leaves existing config and both keys unchanged.
19. Temp-write, flush, replace, and post-replace cache failure paths are tested;
    pre-commit failures leave file and cache unchanged and no half-written
    config is observable.
20. Concurrent `LoadOrDefault` and watcher callbacks observe only the old or
    fully committed config, never the temp/partial candidate.
21. Export/import logs and notifications contain no config values, JSON,
    secrets, fingerprints, prompts, endpoints, query strings, or vocabulary.
22. Source-generated serialization emits the exact v1 names and import rejects
    non-integer/non-finite schema or numeric values.
23. `ExcludeFromClipboardHistory` retains its Windows privacy semantics after
    round trip, while configuration transfer never writes keys or config JSON
    to the clipboard.
24. `Language` round-trips as the shared HTTP and OpenAI-realtime ASR hint;
    blank remains provider auto-detect.

## Plan 012 dependency

This accepted decision is the configuration-migration contract required by
plan 012. Plan 012 must depend on implementation and tests of this contract
before its config-migration work, while macOS secret persistence still uses
Keychain rather than DPAPI. The importer/exporter keeps configuration migration
independent from secret migration: absent secrets preserve destination secure
storage, and opted-in plaintext secrets are immediately re-protected by the
destination host. This document does not itself complete plan 012's migration
implementation.

## Unresolved questions

These do not block acceptance of the v1 contract, but they must be resolved
before implementation:

| Question | Owner | Evidence needed |
|---|---|---|
| What exact canonical logical-key vocabulary covers TailSlap's supported OEM and layout-sensitive keys? | Windows/macOS hotkey implementers | Mapping table and contract tests on supported keyboard layouts. |
| What maximum file, prompt, metadata, language, and session-prompt sizes should v1 enforce? | Security/product owner | Existing backend/UI limits and memory-abuse review; choose fixed bounds before coding. |
| Should atomic commit reject filesystems without same-directory atomic replace, or offer a clearly non-atomic user-approved fallback? | ConfigService owner/security reviewer | Windows filesystem behavior tests. Default recommendation is reject safely. |
| What is the authoritative application-version source for `appVersion`? | Release engineering | Confirm assembly/package version used by published artifacts. |
| Should unsupported machine-specific hotkeys be skipped individually or reject the whole import on macOS? | macOS product owner | Phase 0/1 shortcut capability results from plan 012. Windows v1 rejects unsupported mappings. |

## Decision summary

Accept v1 as a dedicated, versioned, settings-first envelope. It excludes
secrets by default, never transfers DPAPI ciphertext, makes plaintext key
exposure explicit, preserves absent destination keys, requires confirmation for
clear operations, and commits only after strict detached validation and
preview. Implementation is gated on an atomic failure-reporting config save and
the additional validators listed above.
