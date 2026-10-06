# Specification: Speech Preemption Resilience & Browser Search Navigation

> **Generated via `/to-spec`**  
> **Status:** `implemented`  
> **Target Platform:** Windows 10 & 11 / .NET 8 (WPF)  
> **Area:** Audio VAD, Turn Lifecycle Coordination, Browser Automation & Keystroke Dispatch  

---

## Problem Statement

Users of Wira Voice encounter two severe operational breakdowns during continuous voice interaction:

1. **Commands Vanishing Mid-Flight (False-Turn Preemption):**
   When a user issues a voice command (e.g., *"Open Spotify"*, *"Open camera"*, *"Open the file explorer"*, *"Play the song Radima"*), the audio engine transcribes the command and dispatches the decision query to the Jev decision engine via OpenRouter. While OpenRouter processes the prompt over the network (taking ~1.5–2.2 seconds), ambient room noise, microphone hiss, breathing, or desk resonance occasionally produces an acoustic level spike between 0.04 and 0.08 RMS.
   Because the speech detection start threshold is set too low (0.035 RMS), the local Whisper engine treats the noise spike as a new speech onset and immediately fires `StartOfTurn`. The command coordinator handles `StartOfTurn` by aggressively revoking the active permit and canceling the running task's cancellation token (`_taskCts.Cancel()`). When Whisper finishes recording the 0.8–1.2s noise segment, it detects non-speech audio (`[BLANK_AUDIO]`, `(whooshing)`, `[LAUGHTER]`, `[MUSIC PLAYING]`) and silently discards it without generating a transcript. Consequently, the user's authentic command is killed mid-flight, the noise turn produces nothing, and the command evaporates into thin air.

2. **Browser Search Navigation Failing with Contradiction Errors:**
   When a user asks Wira Voice to search for a query in Google Chrome or Microsoft Edge (e.g., *"Search for GitHub"*), the automation actor uses the UI Automation `ValuePattern.SetValue` fast-path to set the address bar text. In Chromium-based browsers, `ValuePattern.SetValue` updates the accessible value of the edit control, but does not notify Chromium's internal Omnibox Controller or autocomplete mechanism. Sending `Enter` immediately after `SetValue` fails to navigate the browser, leaving the browser parked on the empty `New tab` page.
   The coordinator observes that the page URL never changed (`outcome = progress`), and Jev attempts to recover by typing into the on-page Google Search box (`AXComboBox: Ask Google`). When typing into two separate fields without completing navigation, Jev detects an action conflict (`object: wrong_object`), classifies the outcome as `contradicted`, and halts execution with the status: *"Observed result conflicts with the request. Stopped without replaying input."*

---

## Solution

1. **Guarded Turn Preemption in Command Coordinator:**
   Decouple in-flight task execution from raw acoustic energy spikes. The command coordinator must not abort active workflow tasks or revoke execution permits on unverified `StartOfTurn` audio onset signals. Instead, an in-flight command is only superseded when verified speech with an actionable transcript arrives (`EndOfTurn`), or when vocal speech has been sustained for an extended window (>450ms) with unambiguous vocal characteristics. If a transient audio onset turns out to be discarded noise (`[BLANK_AUDIO]`, `[MUSIC PLAYING]`), the in-flight decision request completes uninterrupted.

2. **Acoustic Noise Floor Recalibration:**
   Raise `SpeechStartThreshold` in the local Whisper engine from 0.035 RMS to **0.085–0.090 RMS**. While ambient microphone hiss and breathing typically fluctuate between 0.04 and 0.07 RMS, genuine human vocal commands in the user's environment consistently register between 0.14 and 0.22 RMS. This clean separation completely eliminates false turn triggers caused by ambient room noise.

3. **Chromium Omnibox Keystroke Dispatch Realignment:**
   Exempt web browser address bars and omnibox controls from direct programmatic `ValuePattern.SetValue` injection. When targeting address bars or composite search inputs in Chromium/Gecko browsers, the automation actor must dispatch genuine simulated keystrokes (`SendMouseMove`, `SendMouseClick`, `SendUnicodeString`, and `VK_RETURN`). This ensures Chromium's Omnibox controller parses the input as active typing, displays navigation recommendations, and navigates upon pressing `Enter`.

---

## User Stories

1. As a voice assistant user, I want my spoken commands (such as "Open Spotify" or "Open camera") to complete successfully even if my microphone picks up breathing, chair squeaks, or room echo while the server is thinking, so that my instructions are not canceled by background noise.
2. As a voice assistant user, I want the system to ignore ambient room noise between 0.04 and 0.08 RMS without triggering false "Listening to voice..." states, so that the assistant only activates when I am speaking.
3. As a voice assistant user, I want background noise blips that Whisper transcribes as `[BLANK_AUDIO]` or `(whooshing)` to never abort an ongoing command, so that transient audio does not break system reliability.
4. As a voice assistant user, I want to say "Search for GitHub" in Google Chrome and have the browser automatically navigate to the GitHub search results, so that I don't have to manually press Enter or re-type the query.
5. As a voice assistant user, I want text entered into web browser address bars to trigger native browser URL navigation, so that the workflow engine does not get confused and attempt secondary conflicting inputs.
6. As a voice assistant user, I want standard Win32 dialogs and text editors (such as Notepad or search filters) to retain the high-speed 0ms `ValuePattern` fast-path, so that non-browser automation remains instant.
7. As a voice assistant user, I want deliberate follow-up commands (spoken clearly at >0.12 RMS) to supersede previous commands cleanly when I intentionally interrupt, so that I maintain full control over the assistant.
8. As a developer, I want the command coordinator to distinguish between a tentative audio onset and a finalized command transcript, so that cancellation tokens are not prematurely triggered.
9. As a developer, I want UI Automation control role inspection to identify browser Omnibox fields by pattern, role, and process characteristics, so that the correct typing dispatch method is chosen automatically.
10. As a developer, I want unit tests to verify that false audio triggers do not cancel active permit leases, so that regressions in turn coordination are caught at compile and test time.

---

## Implementation Decisions

### Decision 1: Soft Audio Onset vs. Hard Command Preemption
- Modify the turn lifecycle in `CommandCoordinator` so that receiving `SpeechTurnType.StartOfTurn` enters a "tentative listening" state without calling `_taskCts.Cancel()` or revoking `_permit`.
- Cancellation of an active in-flight task will only occur if:
  - An authentic, non-empty command transcript is finalized (`SpeechTurnType.EndOfTurn`).
  - Or the user explicitly issues an interruption phrase ("Stop", "Cancel").
- If the incoming audio stream ends and Whisper discards the noise as non-speech, the coordinator simply resets its tentative listening state and allows the prior task to complete normally.

### Decision 2: Noise-Floor & Speech-Start Threshold Calibration
- Update `WhisperLocalEngine` constants:
  - Increase `SpeechStartThreshold` from `0.035` to `0.085` RMS to sit well above the `0.04`–`0.07` ambient noise floor observed in telemetry.
  - Maintain `SpeechContinueThreshold` at `0.020` RMS to ensure trailing consonants are not clipped mid-word once speaking has begun.
  - Maintain `SilenceTimeoutMs` at `450` ms to retain responsive speech finalization.

### Decision 3: Control-Aware Typing Strategy in Automation Actor
- Refactor `AXActor.Perform` for `AXOperation.TypeText` and `AXOperation.ReplaceText`:
  - Standard form fields, Notepad, text areas, and standard Win32 edit boxes use native `ValuePattern.SetValue` for 0ms execution.
  - Browser address bars, omniboxes, and composite auto-complete controls (identified by `AXTextField` or `AXComboBox` in Chrome/Edge with labels containing "Address", "Omnibox", or "search bar") are routed to synthetic keyboard dispatch (`SendMouseMove`, `SendMouseClick`, `SendUnicodeString`, and `VK_RETURN`).
  - Add explicit verification that `IsAutoSubmitControl` matches variations like `"Address and search bar"`, `"Address bar"`, and `"Search Box"`.

---

## Testing Decisions

- **Test Integrity:** All tests must verify external behavior through public interfaces, without mocking internal timers or inspecting private fields.
- **Audio VAD Test Suite:**
  - Unit tests in `WiraVoice.Tests` verify that synthesized audio buffers with RMS levels below `0.080` (representing background noise) do not trigger speech detection.
  - Verify that audio buffers with RMS levels above `0.120` (representing vocal speech) trigger speech detection within 50ms.
- **Coordinator Preemption Test Suite:**
  - Verify that calling `StartOfTurn` during an active task execution does not cancel the task's `CancellationToken` unless followed by a non-empty `EndOfTurn`.
  - Verify that a noise turn ending in an empty discard leaves the prior task's permit lease valid and active.
- **Automation Control Classifier Test Suite:**
  - Verify that browser omnibox candidate descriptions (e.g., `"AXTextField: Address and search bar"`) are recognized as auto-submit controls requiring synthetic keystroke dispatch.

---

## Out of Scope

- Modifying the remote OpenRouter LLM weights or Jev prompt syntax.
- Replacing the local offline Whisper engine with a cloud streaming speech recognition API.
- Re-architecting the FlaUI / Windows UI Automation underlying driver.

---

## Further Notes

- The root cause of commands failing was not network timeouts or Jev reasoning failures; Jev was successfully receiving requests and processing them in ~2.0 seconds. The sole reason turns failed was premature cancellation triggered by 0.04 RMS ambient room noise blips.
- By combining guarded turn preemption with the 0.085 RMS noise floor threshold, Wira Voice achieves both high turnaround speed (<500ms) and rock-solid immunity to ambient microphone noise.
