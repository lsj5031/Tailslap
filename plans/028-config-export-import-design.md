# Plan 028: Design a secure, portable configuration export/import contract

> **Executor instructions**: This is a design spike, not an implementation
> task. Inspect the live code, validate the proposed envelope and UX, and write
> `plans/028-decision.md`. Do not modify application source. If a required
> decision cannot be supported by evidence, record it as unresolved and STOP
> rather than implementing. Update the index status to DONE only when the
> decision document satisfies every exit criterion.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/ConfigService.cs TailSlap/SettingsForm.cs TailSlap/Dpapi.cs TailSlap/HistoryForm.cs TailSlap/TranscriptionHistoryForm.cs plans/012-macos-port.md`
> Reconcile any changed config schema or security UX before evaluating this
> design.

## Status

- **Priority**: P2
- **Effort**: S (spike)
- **Risk**: LOW for the spike; future implementation is MED security risk
- **Depends on**: none
- **Unblocks**: plan 012 macOS port compatibility/migration phase
- **Category**: direction
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

TailSlap configuration lives in `%APPDATA%\TailSlap\config.json`. API keys are
stored as Windows-user-bound DPAPI ciphertext. Copying that file to another
Windows account or macOS transfers opaque key blobs that cannot be decrypted,
while manually copying settings is error-prone. A portable export/import
feature needs a versioned contract that distinguishes ordinary settings from
secrets, makes plaintext exposure explicit, and re-encrypts imported secrets
for the destination user.

This is also a named prerequisite of `plans/012-macos-port.md`, whose
compatibility section requires config migration independently from secret
migration and explicitly rejects pretending DPAPI ciphertext is portable.

## Confirmed current state

- `ConfigService.cs:324`: config path is `%APPDATA%\TailSlap\config.json`.
- `LoadOrDefault` deserializes `AppConfig` through
  `TailSlapJsonContext.Default.AppConfig`.
- `Save` serializes the live model directly with camelCase/indented options.
- `LlmConfig.ApiKeyEncrypted` and `TranscriberConfig.ApiKeyEncrypted` are
  serialized; `[JsonIgnore] ApiKey` calls `Dpapi.Unprotect` on read and
  `Dpapi.Protect` on assignment.
- `Dpapi` uses `DataProtectionScope.CurrentUser`, so ciphertext is
  machine/user-profile specific.
- `HistoryForm.ExportVisible` and `TranscriptionHistoryForm.ExportVisible`
  already use a warning dialog stating that export writes decrypted content as
  plaintext, followed by a SaveFileDialog.
- `plans/012-macos-port.md` says: store platform-neutral configuration
  separately from secrets; accept plaintext exported settings only with
  explicit confirmation; never claim DPAPI data is portable.

## Proposed contract to validate

The decision document should approve or amend this concrete v1 envelope:

```json
{
  "schemaVersion": 1,
  "kind": "tailslap-config-export",
  "exportedAtUtc": "2026-07-30T00:00:00Z",
  "appVersion": "3.0.9",
  "settings": {
    "...": "portable AppConfig fields, excluding apiKeyEncrypted"
  },
  "secrets": {
    "format": "plaintext",
    "llmApiKey": "present only after explicit opt-in",
    "transcriberApiKey": "present only after explicit opt-in"
  }
}
```

Required design decisions:

1. `settings` must never contain `apiKeyEncrypted` fields.
2. Default export excludes the entire `secrets` object.
3. Secret export requires an unchecked-by-default `"Include API keys
   (plaintext)"` option plus a warning confirmation modeled on history export.
4. Secret-bearing filename should visibly warn the user, for example
   `tailslap-config-with-secrets-<timestamp>.json`; ordinary export uses
   `tailslap-config-<timestamp>.json`.
5. Import accepts only known `kind` and supported `schemaVersion`.
6. Import treats unknown fields as forward-compatible warnings, but rejects
   wrong types, malformed URLs/hotkeys, unsafe ranges, and structurally invalid
   envelopes before changing live config.
7. Imported plaintext keys are assigned through destination
   `LlmConfig.ApiKey` / `TranscriberConfig.ApiKey`, causing destination-local
   DPAPI protection. Imported `apiKeyEncrypted` fields are ignored/rejected,
   never copied.
8. Import with no `secrets` preserves existing destination keys. It does not
   clear them silently.
9. Import with explicit empty secret values must present a preview saying the
   corresponding destination key will be cleared and require confirmation.
10. Apply import transactionally: parse into a detached model, validate,
    preview material changes, confirm, save once. On any failure, leave current
    config untouched.
11. Never log exported/imported config JSON, API keys, prompts, endpoint query
    strings, or secret fingerprints. Log only operation outcome, schema
    version, and exception type.
12. The portable DTO must be separate from `AppConfig`, so future serialized
    DPAPI fields cannot leak into exports automatically.

## Scope

**In scope**:

- Read-only investigation of config, validation, settings, DPAPI, and existing
  export UX
- New `plans/028-decision.md`
- `plans/README.md` status row

**Out of scope**:

- Application/source/test changes
- Exporting histories, logs, audio, or prompt history
- Designing cross-platform encrypted secret files
- Password-protected exports
- Cloud sync
- Implementing macOS secure storage

## Steps

### Step 1: Inventory the portable schema

List every current `AppConfig`, `LlmConfig`, `TranscriberConfig`, and hotkey
property in `028-decision.md`, classified as:

- portable setting;
- secret;
- Windows-only setting requiring destination review;
- derived/ignored property (`JsonIgnore`);
- future migration concern.

Explicitly decide treatment of hotkeys, microphone device index, auto-start,
clipboard flags, WebSocket tuning, prompts, endpoint URLs, and provider values.
Microphone index is likely not portable across machines; recommend importing it
as `"systemDefault"` or omitting it unless code evidence supports a stable
device identifier.

### Step 2: Map validation and atomicity

Read every `ConfigService.IsValid*` method and `CreateValidatedCopy`. Document:

- which checks can be reused during import;
- which currently "repair to defaults" and therefore are too silent for import;
- which missing checks need a future implementation helper;
- how to avoid `LoadOrDefault`/watcher callbacks observing a half-imported
  config.

The chosen implementation design should validate a detached DTO, construct a
candidate `AppConfig`, assign plaintext secrets only after confirmation, then
call `Save` once. Recommend writing to a temporary file and atomic replace if
the live `Save` implementation is not atomic, but keep that as a separately
identified prerequisite rather than implementing it in this spike.

### Step 3: Specify export UX and threat messaging

Reuse the history-export confirmation style, but make the consequences specific:

```text
This export includes API keys as readable plaintext. Anyone with this file can
use those credentials. Store it securely and delete it after import. Continue?
```

Specify:

- secrets unchecked by default;
- visible warning state and changed filename when checked;
- SaveFileDialog with `.json`;
- success message saying whether secrets were included;
- no clipboard-based key export;
- no automatic upload or telemetry.

State clearly that the ordinary settings-only export can still contain
sensitive prompts and private local endpoint URLs. It is portable, not public.

### Step 4: Specify import UX and failure behavior

The decision must include a preview listing categories changed, not raw secret
values:

- settings changed;
- LLM key: unchanged / replace / clear;
- transcriber key: unchanged / replace / clear;
- machine-specific fields skipped or reset;
- warnings for unknown fields/newer schema.

Require a final confirmation before saving. Define wrong-kind, unsupported
version, malformed JSON, invalid config, DPAPI failure, and save failure
messages. On DPAPI protect failure, import must abort rather than save an empty
key. Coordinate the future implementation with plan 021's DPAPI failure
contract if it has landed.

### Step 5: Specify versioning and tests for the future implementation

Document:

- `schemaVersion` integer starts at 1;
- `kind` is mandatory;
- import supports only explicitly known versions;
- migrations are pure vN-to-vN+1 transformations over portable DTOs;
- unknown top-level kinds are rejected;
- source-generated JSON DTO registration is required.

Define a future test matrix with at least:

1. settings-only round trip;
2. secrets excluded by default;
3. secret export includes plaintext only after opt-in;
4. raw output contains no `apiKeyEncrypted`;
5. import preserves existing keys when secrets absent;
6. import replaces and destination-re-encrypts supplied keys;
7. explicit clear requires confirmation;
8. malformed/unsupported/invalid input leaves config byte-for-byte unchanged;
9. unknown fields warn but do not fail supported versions;
10. machine-specific fields are skipped/reset as decided;
11. DPAPI protect or save failure leaves live config unchanged;
12. export/import logs contain no config or secret content.

### Step 6: Write the decision

Create `plans/028-decision.md` with:

- status: Accepted, Rejected, or Needs Evidence;
- context and constraints;
- complete v1 JSON schema/example using placeholders only;
- property classification table;
- export and import UX flows;
- validation, atomicity, and error behavior;
- threat model and non-goals;
- migration/versioning strategy;
- implementation file map and test matrix;
- dependency note for plan 012;
- unresolved questions, each with an owner/evidence needed.

Never include a real endpoint credential, config dump, or DPAPI ciphertext.

## Verification

This spike modifies only plan documents:

```powershell
git diff --check -- plans/028-config-export-import-design.md plans/028-decision.md plans/README.md
git status --short
```

Manually confirm no source files changed and search the decision for
`apiKeyEncrypted`: it may appear only in prose/schema assertions explaining
that it is forbidden, never as a value.

## Done criteria

- [ ] `plans/028-decision.md` exists and chooses a concrete v1 contract
- [ ] Every current config property is classified
- [ ] Default export excludes secrets and DPAPI ciphertext
- [ ] Plaintext-secret opt-in and warning UX are explicit
- [ ] Import preserves absent secrets and re-encrypts supplied keys locally
- [ ] Atomic validation/preview/save behavior is specified
- [ ] Future implementation test matrix has at least 12 cases
- [ ] Plan 012 names this accepted decision as its config-migration prerequisite
  (update plan 012 only if the operator explicitly allows cross-plan edits;
  otherwise record the link in the index)
- [ ] No application source changed
- [ ] Plan 028 status updated in `plans/README.md`

## STOP conditions

- The live config contains another secret type not accounted for here.
- DPAPI failure cannot be distinguished from an intentionally blank key in the
  future implementation. Mark plan 021 as a prerequisite and do not approve an
  implementation contract that can silently erase credentials.
- A requirement emerges for password-protected portable secret files. That is a
  separate cryptographic design review, not an extension of plaintext opt-in.

## Maintenance notes

The portable DTO is a compatibility boundary. Adding a property to `AppConfig`
must not automatically add it to export. Review each new field for portability,
sensitivity, and migration behavior, then explicitly map it into a new or
backward-compatible envelope version.
