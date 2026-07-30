# Plan 029 Decision: Hosted OpenAI transcription

## Status

**NEEDS LIVE EVIDENCE**

- **Operator decision:** `Do not authorize, mark NEEDS LIVE EVIDENCE`
- **Official-document access date:** 2026-07-30
- **Live smoke date:** Not run; paid API access and microphone-audio transmission were not authorized.
- **Decision:** Stop before implementation. Do not add a hosted preset, change application source, or publish README compatibility claims.

## Sources reviewed

Official OpenAI documentation accessed on 2026-07-30:

- [Realtime transcription](https://developers.openai.com/api/docs/guides/realtime-transcription)
- [Realtime client events](https://developers.openai.com/api/reference/resources/realtime/client-events)
- [Transcription overview](https://developers.openai.com/api/docs/guides/transcription)
- [GPT-4o Transcribe](https://developers.openai.com/api/docs/models/gpt-4o-transcribe)

The comparison target was the cumulative implementation in
`T:\tailslap-lite-execute-all`, including `OpenAIRealtimeTranscriber`,
`RemoteTranscriber`, `TranscriberConfig`, and the existing realtime tests.
The Plan 029 drift check showed cumulative changes in all inspected source/test
files except `README.md`; those changes were reviewed as the source of record.

## Protocol compatibility checklist

Classification meanings:

- **matches** — current code has the documented requirement in the documented location.
- **compatible extension** — current code accepts or emits an additional behavior without replacing the documented behavior.
- **drift** — current code differs from the current documented protocol.
- **unverified** — documentation review alone cannot prove hosted behavior or acceptance.

| Area | Classification | Official protocol observed 2026-07-30 | Cumulative implementation assessment |
| --- | --- | --- | --- |
| Hosted WebSocket URL and query | **unverified** | Current Realtime material uses the hosted `/v1/realtime` WebSocket service. The reviewed transcription guide establishes a transcription session with `session.update`; it does not establish that `intent=transcription` is required. | Maps `https://api.openai.com/v1` to `wss://api.openai.com/v1/realtime?intent=transcription`. Path mapping is plausible, but the query must be proved against the hosted service and checked for duplication/preservation behavior. |
| Authentication and version headers | **matches** for Bearer; **unverified** for header sufficiency | Server-side API access uses Bearer authentication. No beta/version header requirement was found in the reviewed current GA event schema. | Sends `Authorization: Bearer ...` when a key exists and does not send a beta header. A real handshake is still required to prove that no additional hosted header is required for this path/account. |
| Session-update event and nesting | **drift** | `type: "session.update"` with a nested `session` object: `session.type: "transcription"` and configuration under `session.audio.input.{format,transcription,turn_detection,noise_reduction}`. | Sends legacy flat `type: "transcription_session.update"` with `input_audio_format`, `input_audio_transcription`, `turn_detection`, and `input_audio_noise_reduction` at the root. |
| Input audio format and rate | **drift** for declaration; **matches** for bytes/rate | PCM is declared as `{ "type": "audio/pcm", "rate": 24000 }`; only 24 kHz PCM is supported for that format. | Audio is resampled from 16 kHz to 24 kHz PCM16 before append, but the session declares legacy `"pcm16"` instead of the current nested format object. |
| Context fields | **drift** | Current recommended `gpt-live-transcribe` uses `prompt`, `keywords`, and plural `languages`; it must not receive singular `language` together with `languages`. Other accepted models may still support singular `language`. | Places `prompt` and singular `language` in the legacy flat transcription object and sends blank strings. Model-specific field selection and omission behavior are not implemented. |
| VAD and noise reduction | **drift** for location; **matches** for values | `server_vad` supports threshold `0.5`, prefix padding `300 ms`, and silence duration `500 ms`; `near_field` noise reduction is supported. Both belong under `session.audio.input`. | Uses documented values and names, but places both objects in the legacy flat schema. |
| Audio append | **matches** | `input_audio_buffer.append` carries base64 audio in `audio`. | Emits that event and field after 24 kHz resampling. |
| Audio commit | **matches** event; **unverified** sequencing | `input_audio_buffer.commit` commits a non-empty buffer and triggers transcription. With Server VAD the server can commit automatically. | Emits the documented event at stop, after adding silence. Hosted behavior with Server VAD, possible prior auto-commit, and the added silence have not been observed live. |
| Audio clear | **compatible extension** event; **unverified** sequencing | `input_audio_buffer.clear` is a valid client event and produces `input_audio_buffer.cleared`. | Emits clear immediately after commit. Whether that sequencing is harmless on the hosted service must be demonstrated; it must not suppress or duplicate a final transcript. |
| Transcript delta/completed events | **matches** | Uses `conversation.item.input_audio_transcription.delta` (`item_id`, `content_index`, `delta`) and `.completed` (`item_id`, `content_index`, `transcript`). Completion ordering across turns is not guaranteed; reconcile by `item_id`. | Handles both official event names and uses `item_id`; it also maintains previous-item metadata. It ignores `content_index`, which is acceptable for the current single-input transcription use, but cross-turn ordering still needs live verification. |
| Local event aliases | **compatible extension** | The official guide does not define `transcript.text.delta` or `transcript.text.done`. | Retains these local-backend aliases in addition to official event handling; they do not replace the official names. |
| Realtime transcription model | **drift** for recommended/default choice; **compatible extension** for accepted legacy model | The current recommended starting model is `gpt-live-transcribe`; `gpt-transcribe` is documented for committed-turn WebSocket transcription. The current reference also lists `gpt-4o-transcribe` among accepted transcription models, but it is no longer the recommended starting model. | Falls back to `gpt-4o-transcribe`. A single preset model cannot be selected until one model is proved to work in both TailSlap's HTTP and realtime workflows. |
| HTTP URL | **matches** | File transcription uses `POST /v1/audio/transcriptions`. | `https://api.openai.com/v1` resolves to `/v1/audio/transcriptions`. |
| HTTP multipart fields and file | **matches** | Requires multipart audio `file` plus `model`; `language` is an optional model-dependent hint. WAV is supported. | Sends a WAV as `file`, sends configured `model`, and sends nonblank singular `language`. No unsupported mandatory field is added to the ordinary non-streaming request. |
| HTTP response handling | **matches** for ordinary JSON; **unverified** hosted | Ordinary JSON transcription responses expose transcript text. | Accepts top-level `text`, but no hosted response was received in this execution. |
| HTTP streaming extension | **unverified** | File-transcription streaming is distinct from Realtime and uses structured transcript events for supported models. | Adds `stream=true`, but its SSE path primarily treats `data:` payloads as plain text rather than proving current hosted transcript-event parsing. This does not establish hosted streaming compatibility. |
| Rate limits and billing | **unverified** account behavior | Usage is billed; limits depend on model and account tier. The reviewed `gpt-4o-transcribe` page says Free is unsupported and displays tier-specific RPM/TPM limits. Current pricing and the selected model's limits must be checked at release time. | No live account response, rate-limit headers, usage record, or billing observation was authorized. User-facing text may state only that hosted usage is billed and subject to account/model limits, not promise a price or capacity. |
| Data retention and controls | **unverified** for the intended account | OpenAI publishes API data-control and retention policies, but eligibility and effective controls can depend on endpoint, organization, and account configuration. | No account data-control state was inspected. No claim about retention duration, Zero Data Retention eligibility, training use, residency, or deletion behavior is authorized. |

## Exact live evidence still required

Obtain fresh, explicit operator consent before any of the following. The
operator must enter a restricted key through TailSlap Settings; never request a
key in chat, a shell command, a fixture, or this document.

1. **Hosted HTTP connection:** with Base URL `https://api.openai.com/v1`, run
   `Test ASR Connection` and record only date, model, HTTP status, latency, and
   pass/fail.
2. **Hosted bounded HTTP transcription:** submit one short operator-approved
   recording and prove a single nonempty final result, first with no language
   hint and then with one supported hint.
3. **Hosted realtime handshake:** prove the accepted WebSocket URL/query and
   request headers, and record only handshake status and sanitized error code if
   rejected.
4. **Current session schema:** send the current documented nested
   `session.update` shape (after a tested source change) and observe
   `session.updated` without a protocol error.
5. **Realtime audio and finalization:** append 3–5 seconds of approved audio,
   stop once, and prove that commit/clear produces deltas and exactly one final
   completion without truncation, suppression, or duplication.
6. **Ordering:** record event type names and non-content correlation fields
   (`item_id`, `previous_item_id`, and `content_index` where present) sufficient
   to prove TailSlap can reconcile turns.
7. **Language behavior:** prove blank-language behavior and one supported
   language hint using the fields required by the selected realtime model.
8. **One-model preset decision:** prove that the exact proposed preset model is
   accepted by both `/v1/audio/transcriptions` and the realtime transcription
   session. If different models are required, stop for a product decision.
9. **Operational evidence:** record sanitized rate-limit headers or errors,
   billing visibility, and the account's applicable data-control/retention
   terms without recording account identifiers.
10. **Leak check:** inspect `%APPDATA%\TailSlap\logs\app.jsonl` and prove it
    contains no API key material, Authorization values, audio, transcript
    plaintext, prompts, raw server errors, account identifiers, or private
    endpoint/configuration dumps.

## Privacy and evidence constraints

- Hosted HTTP and realtime use send microphone audio to OpenAI. Do not run
  either path without informed, explicit operator consent for that exact test.
- Use only short, non-sensitive, purpose-made test speech. Do not use customer,
  workplace, health, financial, biometric, credential, or other private data.
- Store the key only through the existing Settings/DPAPI path. Never place it
  in source, process arguments, environment dumps, screenshots, logs, tests,
  plan files, clipboard captures, or chat.
- Record only status codes, sanitized event type/correlation names, timings,
  selected model, and pass/fail. Do not record spoken or transcribed text,
  prompts, request/response bodies, key fingerprints, organization/project
  identifiers, account details, or nonpublic endpoints.
- Do not claim a fixed retention period, Zero Data Retention, residency,
  training exclusion, price, or rate limit until the applicable current
  official policy and account configuration are verified.
- If any sensitive material appears in logs or evidence, stop immediately and
  follow operator direction to secure/delete it before continuing.

## Implementation gate

The hosted preset and all README/release compatibility claims are
**prohibited** until **both** hosted HTTP transcription and hosted realtime
transcription pass the evidence requirements above using the same intentional
preset model, and any documented protocol drift is fixed and covered by
non-network tests. Passing URL construction or payload tests alone is not
sufficient.

No application source, preset, README text, or plan status index was changed in
this STOP-path execution.
