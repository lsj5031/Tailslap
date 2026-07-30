# Port TailSlap to macOS

## Objective

Ship a signed, notarized macOS menu-bar application that preserves TailSlap's core refinement and transcription workflows while keeping the existing Windows application stable. Reuse protocol, configuration, and workflow code; implement operating-system integration separately for each platform.

## Product scope

### First macOS release

- Menu-bar application with settings and history windows.
- Toggle recording, remote transcription, and optional LLM enhancement.
- Text refinement using selected text or clipboard fallback.
- Configurable global shortcuts that include a normal key.
- Clipboard paste into the previously focused application.
- Secure API-key and encrypted-history storage.
- Microphone and Accessibility permission onboarding.
- Apple Silicon support; add Intel only if user demand justifies the extra release artifact.

### Follow-up releases

- Push-to-talk, including modifier-only shortcuts.
- Incremental HTTP transcription and simulated typing.
- Realtime WebSocket transcription.
- Recording overlay and richer native notifications.
- Linux support, if a cross-platform UI makes it economical.

### Explicit non-goals for the first release

- Pixel-identical WinForms and macOS interfaces.
- Sharing platform-native implementations through conditional branches in one class.
- Migrating Windows DPAPI ciphertext directly to macOS.
- Sandboxed Mac App Store distribution. Global input monitoring and text injection make direct signed distribution the practical first target.

## Architecture

Split the current application into platform-neutral orchestration and thin platform hosts:

```diagram
┌──────────────────────────────────────────────────────┐
│ TailSlap.Core                                        │
│ controllers, HTTP/WebSocket clients, config models, │
│ history model, validation, serialization             │
└───────────────────────┬──────────────────────────────┘
                        │ platform contracts
             ┌──────────┴──────────┐
             ▼                     ▼
┌────────────────────────┐  ┌─────────────────────────┐
│ TailSlap.Windows       │  │ TailSlap.Mac           │
│ WinForms, Win32,       │  │ menu bar/UI, AppKit,   │
│ WinMM, DPAPI, Registry │  │ AVFoundation, Keychain │
└────────────────────────┘  └─────────────────────────┘
```

Create platform contracts only where the existing workflows need them:

- `IAudioRecorder` and `IAudioRecorderFactory`
- `IGlobalHotkeyService`
- `IClipboardService` and a text-insertion service
- `ISecureStorage`
- `INotificationService`
- `IAutoStartService`
- UI-dispatching abstraction only if controllers currently depend on WinForms synchronization

Do not extract wrappers speculatively. Move a class to the core project only after its platform dependencies are absent or injected.

## Technology decisions to make during the spike

### UI host

Evaluate these choices with a small menu-bar/settings prototype:

1. **Avalonia**: preferred if shared future macOS/Linux UI is valuable. Verify menu-bar support, native dialogs, accessibility behavior, packaging, and trimming before committing.
2. **AppKit through .NET for macOS**: preferred if native menu-bar behavior and lower macOS integration risk outweigh shared UI code.

Do not use WinForms: it is Windows-only. Avoid Mac Catalyst unless a spike proves that its desktop global-hotkey and accessibility integrations are adequate.

### Native integrations

| Capability | Windows implementation | macOS implementation |
|---|---|---|
| Audio capture | WinMM | AVFoundation/CoreAudio |
| Global shortcut | `RegisterHotKey`, `WH_KEYBOARD_LL` | Carbon hotkey API for ordinary shortcuts; Quartz event tap for push-to-talk |
| Clipboard | Win32/WinForms clipboard | `NSPasteboard` |
| Text insertion | `SendInput`/paste | Accessibility API and paste fallback |
| Secrets | DPAPI | Keychain Services |
| History encryption | DPAPI | Keychain-held key plus authenticated file encryption, or Keychain records if size permits |
| Auto-start | Registry Run key | Login Items or LaunchAgent |
| Notifications | WinForms tray balloons | UserNotifications |
| Tray UI | `NotifyIcon` | `NSStatusItem` or framework equivalent |
| VAD | Windows `WebRtcVad.dll` package asset | universal native build or a maintained cross-platform replacement |

## Delivery phases

### Phase 0: Feasibility spike

- Build a minimal macOS menu-bar process on Apple Silicon.
- Request and explain microphone and Accessibility permissions.
- Register one global shortcut.
- Record a WAV file and paste fixed text into another application.
- Read/write one Keychain secret.
- Publish, code-sign, notarize, and run the artifact on a clean Mac.
- Decide Avalonia versus AppKit and document the decision before restructuring production code.

**Exit criteria:** every high-risk OS capability works in a signed build outside the development environment.

### Phase 1: Establish project boundaries without behavior changes

- Add a platform-neutral core target and a Windows host target.
- Move HTTP clients, protocol DTOs, config models, serialization, and pure controller logic incrementally.
- Keep WinForms forms and every P/Invoke in the Windows host.
- Replace direct static platform calls only where they block extraction.
- Run existing Windows tests after each extraction and manually verify all four Windows workflows.

**Exit criteria:** the Windows release behaves unchanged and the core assembly builds without a Windows target framework.

### Phase 2: macOS minimum viable host

- Implement menu-bar lifecycle, settings, logging, and user notifications.
- Implement AVFoundation audio recording and microphone selection.
- Wire toggle transcription and LLM enhancement to reused core clients.
- Implement Keychain-backed API-key storage and encrypted local history.
- Add ordinary global shortcuts and clipboard-based text delivery.
- Add permission onboarding and actionable recovery UI when permission is denied.

**Exit criteria:** a clean Mac can install the signed build, configure an endpoint, record, transcribe, and paste the result.

### Phase 3: Refinement and history parity

- Capture selected text through Accessibility where available, with an explicit clipboard fallback.
- Add refinement workflow and history windows.
- Preserve the rule that logs never contain source or transcription plaintext.
- Define config migration independently from secret migration; never write plaintext API keys during conversion.

**Exit criteria:** refinement and both history types meet the Windows behavior contract, allowing platform-appropriate UI differences.

### Phase 4: Push-to-talk and realtime parity

- Implement Quartz event-tap key-down/key-up tracking.
- Handle permission revocation, keyboard layouts, left/right modifiers, auto-repeat, app focus changes, sleep/wake, and forced maximum duration.
- Add realtime audio streaming and incremental text insertion.
- Port or replace VAD and test on Apple Silicon.

**Exit criteria:** push-to-talk and realtime sessions recover cleanly from cancellation, network loss, permission loss, and device changes.

### Phase 5: Release engineering

- Produce deterministic `osx-arm64` release artifacts and optional `osx-x64` artifacts.
- Configure Developer ID signing, hardened runtime entitlements, and notarization in CI.
- Add update and rollback strategy before automatic updates.
- Test upgrade behavior, first-run permissions, uninstall cleanup, and crash diagnostics on supported macOS versions.
- Document the support matrix and differences from Windows.

## Testing strategy

- Keep controller and protocol tests in the platform-neutral test project.
- Add contract tests that run against both Windows and macOS platform implementations where practical.
- Unit-test shortcut state machines independently from native callbacks.
- Add integration tests for config round trips, encrypted history, HTTP/SSE/WebSocket behavior, and cancellation.
- Maintain a manual macOS matrix covering Apple Silicon, supported macOS versions, common keyboard layouts, multiple microphones, permission denial/revocation, sleep/wake, and focus changes.
- Treat signing/notarization installation on a clean machine as a release gate, not a post-release check.

## Compatibility and migration

- Keep the JSON property names and endpoint semantics stable where possible.
- Store platform-neutral configuration separately from secrets.
- On import from Windows, accept plaintext exported settings only with explicit user confirmation; do not claim DPAPI data is portable.
- Version encrypted-history envelopes so algorithms and key storage can evolve.
- Preserve existing Windows defaults unless a separately reviewed product change intentionally migrates them.

## Principal risks

1. **Accessibility approval and behavior:** selection capture and text insertion vary by target application. Design explicit fallbacks and make permission state visible.
2. **Global key monitoring:** modifier-only push-to-talk needs Quartz and Input Monitoring, and can behave differently across keyboard layouts.
3. **Audio/VAD native dependencies:** the current VAD package publishes a Windows DLL. Resolve this in the feasibility phase.
4. **Distribution:** unsigned development success does not prove hardened, notarized runtime success.
5. **Premature abstraction:** broad interfaces can destabilize Windows. Extract one tested workflow at a time.
6. **Secret/history migration:** Windows DPAPI ciphertext cannot be decrypted on macOS; define export/import UX instead of weakening encryption.

## Definition of done

- Windows behavior and release pipeline remain supported.
- macOS release is signed, notarized, and installable without development tools.
- Toggle transcription and refinement work end-to-end with clear permission handling.
- Secrets and histories are encrypted using platform-appropriate facilities.
- Logs contain no selected, refined, or transcribed plaintext.
- Automated core tests pass on Windows and macOS CI agents.
- Known parity gaps are documented in the UI and release notes.
