# Environment

Environment variables, external dependencies, and setup notes.

**What belongs here:** Required env vars, external API keys/services, dependency quirks, platform-specific notes.
**What does NOT belong here:** Service ports/commands (use `.factory/services.yaml`).

---

## Platform

- **Windows 10/11** (required for WinForms, DPAPI, WinMM audio, low-level keyboard hooks)
- **.NET 9 SDK** (version 9.0.100, roll-forward: latestFeature)
- **Architecture**: x64 only

## External Dependencies

- **Transcription server**: Must be running at configured `Transcriber.BaseUrl` (default `http://localhost:18000/v1`). Must support SSE streaming with `stream: true` parameter and `data: <text>\n\n` format.
- **LLM server** (optional, for auto-enhancement): Configured `Llm.BaseUrl` (default `http://localhost:11434/v1`)

## Config Location

- `%APPDATA%\TailSlap\config.json` — main configuration file
- `%APPDATA%\TailSlap\app.log` — log file
- `%APPDATA%\TailSlap\history.jsonl.encrypted` — encrypted refinement history
- `%APPDATA%\TailSlap\transcription-history.jsonl.encrypted` — encrypted transcription history

## Build Notes

- `TreatWarningsAsErrors=true` — zero warnings required
- `EnforceCodeStyleInBuild=true` — code style enforced
- Only 3 NuGet packages: Microsoft.Extensions.DependencyInjection 9.0.0, Microsoft.Extensions.Http 9.0.0, WebRtcVadSharp 1.3.0
- No new packages should be added
