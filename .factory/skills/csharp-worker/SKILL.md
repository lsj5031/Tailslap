---
name: csharp-worker
description: General-purpose C# implementation worker for the TailSlap WinForms project
---

# C# Worker

NOTE: Startup and cleanup are handled by `worker-base`. This skill defines the WORK PROCEDURE.

## When to Use This Skill

All implementation features for the Typeless Mode mission. This worker handles C# code changes to the TailSlap WinForms desktop application, including creating new files, modifying existing files, and writing tests.

## Required Skills

None.

## Work Procedure

### 1. Read Context

Read these files FIRST before writing any code:
- `AGENTS.md` in the mission directory for boundaries and conventions
- `T:\tailslap-lite\TailSlap\Program.cs` for DI registration patterns
- `T:\tailslap-lite\TailSlap\MainForm.cs` for hotkey handling patterns
- `T:\tailslap-lite\.factory\library\architecture.md` for system architecture
- `T:\tailslap-lite\.factory\services.yaml` for build/test commands

### 2. Write Tests First (Red)

Write failing tests BEFORE implementing. Follow existing patterns in `TailSlap.Tests/`:
- xUnit `[Fact]` attributes
- Moq for mocking (`Mock<T>`)
- Manual dependency injection in test constructors
- `CreateMockConfigService()` helper pattern
- Reflection for private member testing when needed (`BindingFlags.NonPublic | BindingFlags.Instance`)
- Test naming: `MethodName_Condition_ExpectedBehavior`

Create test files in `T:\tailslap-lite\TailSlap.Tests\`.

### 3. Implement (Green)

Write the minimum code to make tests pass. Follow conventions:
- File-scoped namespaces
- Nullable reference types enabled
- PascalCase public, `_camelCase` private
- Sealed classes where appropriate
- Interface-driven design with factory pattern for stateful services
- `async/await` with `ConfigureAwait(false)` for library code
- Explicit try-catch with graceful fallbacks
- SHA256 fingerprints in logs only — never log sensitive text

Create implementation files in `T:\tailslap-lite\TailSlap\`.

### 4. Build and Verify

```powershell
# Build (must pass with 0 warnings)
cd T:\tailslap-lite; dotnet build -c Release

# Run ALL tests (existing 81 + new must pass)
cd T:\tailslap-lite; dotnet test TailSlap.Tests -c Release --no-build
```

If the build fails, fix ALL warnings and errors before proceeding. TreatWarningsAsErrors=true is enforced.

### 5. Manual Verification

For features involving keyboard hooks or UI interaction, verify the code logic manually:
- Trace the key-down → recording → key-up → transcription → typing flow
- Check that the finally block always cleans up (temp files, CTS, state reset)
- Verify thread safety (hook callback on correct thread, UI operations marshaled)
- Check that cancellation is properly propagated through all async calls

### 6. Commit and Handoff

Commit all changes with a descriptive message. Return a thorough handoff.

## Example Handoff

```json
{
  "salientSummary": "Implemented TypelessController with push-to-talk recording, SSE streaming transcription, and incremental text typing. Created KeyboardHook abstraction for WH_KEYBOARD_LL. Extracted TextTyper from RealtimeTranscriptionController. All 81 existing tests + 23 new tests pass. Build succeeds with 0 warnings.",
  "whatWasImplemented": "TypelessController (replaces TranscriptionController): key-down starts AudioRecorder, key-up stops and sends to RemoteTranscriber.TranscribeStreamingAsync, SSE chunks typed via TextTyper hybrid approach. KeyboardHook: low-level keyboard hook with auto-repeat suppression and modifier tracking. TextTyper: extracted text typing logic with backspace corrections, foreground window monitoring, and clipboard/SendKeys hybrid. Updated MainForm to wire TypelessController, updated Program.cs DI, removed old TranscriptionController toggle paths.",
  "whatWasLeftUndone": "Auto-enhancement for typeless mode results is deferred (text is already on screen when enhancement would run). SettingsForm UI for typeless-specific settings not updated (uses existing transcriber config).",
  "verification": {
    "commandsRun": [
      { "command": "cd T:\\tailslap-lite; dotnet build -c Release", "exitCode": 0, "observation": "Build succeeded with 0 warnings, 0 errors" },
      { "command": "cd T:\\tailslap-lite; dotnet test TailSlap.Tests -c Release --no-build", "exitCode": 0, "observation": "Passed 104 tests (81 existing + 23 new), 0 failed" }
    ],
    "interactiveChecks": []
  },
  "tests": {
    "added": [
      { "file": "TailSlap.Tests/TypelessControllerTests.cs", "cases": [
        { "name": "TriggerTranscribeAsync_DisabledTranscriber_ReturnsFalse", "verifies": "VAL-CONF-003" },
        { "name": "OnKeyDown_StartsRecording_WhenIdle", "verifies": "VAL-REC-001" },
        { "name": "OnKeyUp_StopsRecording_AndStartsTranscription", "verifies": "VAL-REC-002" },
        { "name": "OnKeyUp_ShortRecording_SkipsTranscription", "verifies": "VAL-REC-003" },
        { "name": "OnKeyDown_IgnoresRepeat_WhenRecording", "verifies": "VAL-REC-007" },
        { "name": "OnKeyDown_MicFailure_ReturnsToIdle", "verifies": "VAL-REC-005" }
      ]},
      { "file": "TailSlap.Tests/KeyboardHookTests.cs", "cases": [
        { "name": "Hook_FiresKeyDown_OnMatchingKey", "verifies": "VAL-LIFE-001" },
        { "name": "Hook_FiresKeyUp_OnMatchingKeyRelease", "verifies": "VAL-REC-002" },
        { "name": "Hook_SuppressesAutoRepeat", "verifies": "VAL-REC-007" },
        { "name": "Hook_ContinuesDespiteModifierRelease", "verifies": "VAL-REC-009" }
      ]},
      { "file": "TailSlap.Tests/TextTyperTests.cs", "cases": [
        { "name": "TypeText_UsesClipboard_ForLongText", "verifies": "VAL-TYPE-002" },
        { "name": "TypeText_UsesSendKeys_ForShortText", "verifies": "VAL-TYPE-003" },
        { "name": "CorrectText_BackspaceAndRetype", "verifies": "VAL-TYPE-004" },
        { "name": "WindowChange_ResetsBaseline", "verifies": "VAL-TYPE-005" }
      ]}
    ]
  },
  "discoveredIssues": []
}
```

## When to Return to Orchestrator

- The feature depends on an interface or service that doesn't exist yet and isn't part of this feature's scope
- Requirements are ambiguous or contradictory (e.g., unclear interaction with existing features)
- Existing bugs in unrelated code block this feature (report but do not fix)
- The keyboard hook conflicts with the existing `RegisterHotKey` mechanism in unexpected ways
- Build fails due to code analysis warnings that can't be resolved without changing existing interfaces
