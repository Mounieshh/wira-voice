# Specification: Low-Latency Voice Pipeline & Complete Windows-Native Decoupling

> **Generated via `/to-spec`**  
> **Status:** `implemented`  
> **Target Platform:** Windows 10 & 11 / .NET 8 (WPF)  
> **Area:** Audio Capture, VAD, Speech Engine Latency, Windows Native Decoupling & UI Automation Fast-Path

---

## Problem Statement

Users interacting with Wira Voice experience noticeable delays and frequent recognition failures during voice commands, along with subtle logic faults originating from upstream macOS architectural remnants:

1. **High Turnaround Latency (4–7 Seconds):**
   - **Excessive Silence Timeout:** When a user stops speaking, the local speech engine waits an unconditional 1,100 milliseconds (`SilenceTimeoutMs = 1100`) before finalizing the recording, adding more than 1 second of dead wait time to every single interaction.
   - **Noise-Floor Hang Trap:** The threshold to continue recording speech (`SpeechContinueThreshold = 0.008`) sits directly in the typical ambient noise floor (fan noise, microphone hiss, air conditioning). Normal background noise repeatedly resets the silence timer, causing the engine to record 4.5 to 5.7 seconds of dead air for a simple 1-second phrase.
   - **Sequential CPU Batch Inference:** Transcription only begins *after* the prolonged recording terminates, processing a large 5-second PCM audio buffer on the CPU and adding another 1 to 2 seconds of latency before intent evaluation can even start.

2. **Clipped Speech Onsets & Hallucinated Discards:**
   - **Consonant Attack Truncation:** The rolling pre-roll buffer stores only 250 milliseconds (`MaxPreRollBytes = 8000`). Initial plosives and soft consonants (such as *"C"*, *"P"*, *"T"*, *"F"*, *"S"*, *"O"*) take 300 to 500 milliseconds before vocal energy crosses the start threshold. As a result, the first syllable of user commands is regularly clipped (e.g., *"Open camera"* is heard as *"-pen camera"* resulting in `", open camera."`, or *"Resume"* is truncated to *"-zume"* resulting in `"So much."`).
   - **Hallucinatory Noise Discard:** When 3 to 5 seconds of predominantly silent or noisy audio is fed to Whisper's autoregressive decoder, it generates hallucinated non-speech tokens (such as `[MUSIC PLAYING]`, `[BLANK_AUDIO]`, `[SOUND]`, `[clears throat]`). The cleaner regex strips these to empty strings and drops the user's turn entirely, ignoring spoken commands.

3. **Missing Speech Lifecycle Synchronization:**
   - When vocal energy is first detected, the speech engine does not emit a `StartOfTurn` event to the coordinator. The coordinator is therefore unaware that the user has started speaking until the final transcript arrives seconds later, preventing early permit revocation and stalling speculative planning.

4. **Upstream macOS Identity & Automation Remnants:**
   - **Application Matching Inversion:** In the verification workflow, the friendly application name (e.g. `"Google Chrome"`) is passed into a parameter expecting an executable name, while friendly matching is omitted. This causes window verification to fail even after the correct window is active in the foreground.
   - **Reverse-DNS Bundle ID Assumption:** Upstream macOS logic assumes every application has a unique, globally consistent `BundleIdentifier` (such as `com.apple.finder`). On Windows, Win32 and packaged applications have disparate AUMIDs, process names, and executable paths; strict string equality against a single identifier fails across different discovery channels.
   - **Synthetic Input Sluggishness in Text Fields:** The automation actor relies exclusively on physical mouse moves, mouse clicks, `Ctrl+A`, `Backspace`, character-by-character key presses with sleep delays, and Return presses. For native Windows controls, this adds hundreds of milliseconds of delay and risks focus drops, ignoring fast native UI Automation value injection.

---

## Solution

A **high-sensitivity, low-latency Windows-native speech and automation pipeline** that reduces end-to-end response latency to under 500 milliseconds, preserves complete vocal onsets, eliminates ambient noise traps, and replaces legacy macOS artifacts with native Windows contracts:

1. **Sub-500ms Voice Activity Detection & Adaptive Endpointing:**
   - Reduce the natural silence timeout to a responsive 450–500 milliseconds while dynamically distinguishing true vocal continuation from background noise.
   - Raise the continuation threshold above the ambient microphone floor (to ~0.020) so fan noise and air hiss do not keep recording buffers artificially open.
   - Cap maximum turn recording time to prevent runaway capture.

2. **Expanded Pre-Roll Buffer (400ms):**
   - Increase the circular pre-roll buffer to 400 milliseconds (12,800 bytes of 16kHz 16-bit mono PCM). The entire consonant attack of the opening word is preserved without clipping.

3. **Immediate Turn Lifecycle Notification (`StartOfTurn`):**
   - Instantly dispatch a `StartOfTurn` event to the command coordinator as soon as valid vocal energy triggers recording. Stale execution permits and background tasks are suspended immediately without waiting for speech completion.

4. **Whisper Hallucination Guard & Trimming:**
   - Trim low-energy trailing silence from the audio buffer before sending it to the model. Whisper receives only active vocal audio, preventing decoder loop hallucinations (`[MUSIC PLAYING]`, `[BLANK_AUDIO]`).

5. **Windows-Native Application Identity & Verification Seam:**
   - Unify the application matching interface across workflow verification and window inspection, resolving friendly display names, process names, and window captions symmetrically.
   - Deprecate rigid single-string bundle identifier comparisons in favor of composite Windows application matching (`InstalledApplication.MatchesName`, `MatchesWindowTitle`, and AUMID resolution).

6. **Native UIA `ValuePattern` Fast-Path for Automation:**
   - In the automation execution module, check whether targeted editable controls support UI Automation `ValuePattern` or `TextPattern`. If supported, inject text immediately in 0 milliseconds; fallback to synthetic mouse/keyboard dispatch only when native patterns are unavailable.

---

## User Stories

1. As a voice user, I want the system to begin processing my command within 500 milliseconds of me finishing speaking, so that interaction feels instant and conversational.
2. As a voice user, I want saying "Open camera" to never cut off the initial "O" sound, so that the assistant does not transcribe leading commas or clipped words.
3. As a voice user, I want saying "Go to new volume D" in a room with background fan noise to stop recording promptly when I finish speaking, rather than recording 5 seconds of room noise.
4. As a voice user, I want quiet room noise to never cause Whisper to output `[MUSIC PLAYING]` or `[BLANK_AUDIO]`, so that my spoken commands are never silently dropped.
5. As a voice user, I want the top bar to show "Listening..." the instant I start speaking, so that I know the system is actively hearing my voice.
6. As a voice user, I want previous actions to be cancelled immediately when I speak a new command, preventing obsolete actions from completing.
7. As a voice user, I want text entry in address bars and search fields to populate instantly without waiting for simulated character-by-character typing.
8. As a voice user, I want File Explorer and browser navigation verification to succeed reliably on Windows without false negatives caused by mismatched process names.
9. As a voice user, I want the assistant to work reliably on Windows without depending on macOS-specific bundle identifier structures.
10. As a developer, I want the speech engine to expose clean lifecycle events (`StartOfTurn`, `EndOfTurn`) through the `ISpeechEngine` seam for modular testing.
11. As a developer, I want speech activity thresholds to be resilient to varying microphone hardware and gain levels.
12. As a developer, I want automated unit tests that verify pre-roll preservation and silence timeout behavior using simulated PCM audio feeds.
13. As a developer, I want automation action execution to fall back gracefully to physical keystrokes if an editable control does not support native UIA patterns.
14. As a developer, I want application verification in the workflow engine to correctly evaluate friendly names and window captions across Win32, UWP, and shell windows.

---

## Implementation Decisions

### 1. Voice Activity Detection (VAD) & Timing Calibration
- **Adaptive Silence Threshold:** Lower natural pause timeout from 1,100ms down to a responsive window between 400ms and 500ms.
- **Ambient Floor Rejection:** Elevate vocal continuation threshold above ambient microphone hiss (calibrated from 0.008 to ~0.020). Low-energy noise will not reset the silence deadline.
- **Extended Pre-Roll Ring Buffer:** Expand the rolling pre-roll storage from 250ms (8,000 bytes) to 400ms (12,800 bytes). When speech is triggered, the entire 400ms history is prepended, capturing unvoiced consonant onsets.
- **Audio Trimming:** Prior to inference, trailing audio below vocal energy thresholds will be trimmed off, eliminating long silent tails that trigger Whisper autoregressive hallucinations.

### 2. Speech Turn Lifecycle Synchronization
- When vocal energy crosses the speech start threshold, the speech engine will immediately emit `SpeechTurnType.StartOfTurn` with the generated turn identifier.
- The coordinator will receive this event, immediately call `BeginTurn()`, revoke the active input permit, and cancel any in-flight actions from preceding turns.
- When the silence timeout expires, the speech engine will emit `SpeechTurnType.EndOfTurn` accompanied by the clean transcript.

### 3. Windows Application Verification & Identity Consolidation
- The application verification interface will harmonize parameter order and matching semantics. Both friendly display name and process name will be passed to identity resolvers.
- Deprecate reverse-DNS bundle identifier checks across workflow completion and navigation verifiers. Verification will evaluate composite Windows identity: matching friendly name, process binary name, window title, or AUMID.

### 4. Native UI Automation Text Entry Fast-Path
- The automation actor will inspect whether target controls support the native UI Automation `ValuePattern` interface.
- For controls supporting `ValuePattern`, text replacement will execute directly via native pattern methods, completing in 0ms with zero reliance on mouse repositioning or artificial thread sleeps.
- Controls lacking `ValuePattern` will seamlessly fall back to synthetic mouse focusing and keyboard dispatch.

---

## Testing Decisions

### 1. Test at Highest Seams
- **Speech Engine Seam (`ISpeechEngine`):** Test voice activity detection, pre-roll buffer retention, silence timing, and lifecycle events (`StartOfTurn`, `EndOfTurn`) by injecting deterministic raw 16kHz PCM audio buffers.
- **Application Routing Seam (`AppRouting` / `WindowsAppIdentity`):** Test application verification and identity matching with mock foreground snapshots containing various combinations of friendly names, process names, and window captions.
- **Automation Execution Seam (`AXActor`):** Verify that native UIA patterns take precedence over synthetic dispatch when supported.
- **Coordinator Seam (`CommandCoordinator`):** Verify that incoming `StartOfTurn` events immediately revoke permits and supersede prior turns.

### 2. Good Test Characteristics
- Tests must assert on observable behavior across public module interfaces, avoiding reflection or internal state inspection.
- Audio tests must be fast and deterministic (using synthesized PCM sine waves and silence frames) rather than relying on real microphone hardware or wall-clock sleep delays.

---

## Out of Scope

1. Replacing Whisper with cloud-only alternatives when local mode is explicitly chosen by the user.
2. Training custom acoustic models or fine-tuning Whisper weights.
3. Multi-monitor physical display recalibration for the floating UI.
4. Support for non-Windows operating systems (macOS, Linux).

---

## Further Notes

- The existing test suite in `tests/WiraVoice.Tests/` provides a comprehensive foundation for audio capture calculation, prompt judgment, and architecture verification.
- All timing adjustments preserve clean user feedback on the floating top bar without regressing speech waveform visualization.
