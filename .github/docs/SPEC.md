# Specification: Wira Voice (Windows Hands-Free Accessibility Voice Control)

> **Generated via `/to-spec`**  
> **Status:** `ready-for-agent`  
> **Target Framework:** C# / .NET 8 (Windows Presentation Foundation)  
> **Reference Architecture:** Translated & Deepened from macOS [musubipapi/computah](https://github.com/musubipapi/computah)

---

## Problem Statement

Users who wish to navigate, command, and control their Windows 11 computers hands-free using natural voice commands currently face significant hurdles:
* **Conventional vision-based AI computer-use agents** take full-screen screenshots, resulting in 2-4 seconds of latency per command, high financial cost ($0.05–$0.20 per action), and frequent pixel misclicks due to coordinate guessing and DPI scaling errors.
* **Standard voice assistants (Cortana, Siri, etc.)** only support rigid, hardcoded voice syntax (e.g. "open notepad") and cannot intelligently understand the contextual state of in-focus applications or operate third-party software buttons, text areas, and menus.
* **Cloud-only speech services** introduce network latency and compromise privacy for users who want sensitive workflows kept entirely on their local device without sending speech or credentials over the internet.

---

## Solution

**Wira Voice** is a low-latency, hands-free voice assistant for Windows 11 that operates the operating system through its native **Accessibility Tree (UI Automation)** rather than computer vision screenshots.

1. **Accessibility-Driven Semantics:** Instead of guessing pixel coordinates, the assistant reads the active foreground window's native UI hierarchy (buttons, text fields, menu items, checkboxes) using Microsoft UI Automation (`FlaUI.UIA3`).
2. **Constrained Decision Making:** It compiles interactive controls into a structured multiple-choice question evaluated by **TypeSafe's Jev model** (`typesafe/jev-1.13` via OpenRouter), selecting the precise control ID and slicing user-spoken text with sub-100ms inference.
3. **Latency-Hiding Speculative Pipeline:** Using speech turn detection (e.g., Deepgram Flux's `EagerEndOfTurn`), it snapshots the UI and queries Jev *while the user is finishing their sentence*, achieving near-instant (<10ms perceived latency) execution upon speech completion.
4. **Dual Speech Architecture:** Provides a real-time in-app toggle between:
   - **Cloud Mode (Deepgram Flux):** For conversational turn-taking and maximum speculative speed.
   - **Local Offline Mode (Whisper.cpp / cuBLAS GPU):** For 100% private, offline, zero-cost local transcription accelerated by NVIDIA GPU tensor cores.
5. **Closed-Loop Safety:** Protects user state by revoking active command permits whenever new speech begins (`StartOfTurn`), validating hit-test coordinates before clicking, and re-reading the UI tree post-action to verify that the desired state change actually succeeded.
6. **Non-Activating Floating Island UI:** A modern Windows 11 floating top-bar overlay that displays live transcripts and status without ever stealing keyboard focus from the active foreground window.

---

## User Stories

1. As a computer user, I want to say "Click Submit" in any active window, so that the application clicks the exact Submit button without me touching the mouse.
2. As a computer user, I want to say "Type hello world into search", so that the assistant focuses the search field and types the text without guessing coordinates.
3. As a developer, I want to say "Open Chrome", so that the assistant launches or switches to Google Chrome.
4. As a privacy-conscious user, I want to toggle to Local Whisper mode, so that all my speech is processed locally on my machine without sending audio to the cloud.
5. As an NVIDIA GPU owner, I want the local Whisper engine to leverage my GPU via cuBLAS, so that local offline transcription runs in milliseconds.
6. As a cloud user, I want to toggle to Deepgram Flux mode, so that I get real-time conversational turn-taking and speculative execution.
7. As a user, I want the assistant to evaluate the UI *while* I am speaking the end of my command, so that the perceived delay between speech completion and action execution is near zero.
8. As a user, I want to interrupt a running command by speaking again, so that the previous command's permission to act is instantly revoked.
9. As a user, I want the floating overlay to stay always-on-top without stealing keyboard focus from my current app, so that my typing is never interrupted.
10. As a user, I want the assistant to verify that an action succeeded after clicking or typing, so that it doesn't falsely assume an action worked when a dialog failed to open.
11. As a user, I want the assistant never to repeat an uncertain action, so that it doesn't accidentally double-click destructive buttons like "Delete".
12. As a user, I want my sensitive environment keys and passwords to be redacted locally, so that credentials are never leaked to external AI models.
13. As a user, I want the assistant to recognize when a requested state is already satisfied (e.g. asking to pause when playback is already paused), so that unnecessary inputs are not sent.
14. As a user, I want the assistant to handle multi-step actions (such as opening a menu to find a button), so that commands that require revealing hidden controls can be completed sequentially.
15. As a developer, I want to use my standard OpenRouter API key to query TypeSafe Jev, so that I don't need a separate proprietary account.
16. As a user, I want to see a live visual indicator of speech volume and status (Listening, Speculating, Executing, Done) in the top floating bar, so that I always know what the assistant is doing.

---

## Implementation Decisions

### 1. Seams & Architecture
* **The Speech Engine Seam (`ISpeechEngine`):**
  - Defines the interface for audio ingestion and turn event emissions (`StartOfTurn`, `EagerEndOfTurn`, `TurnResumed`, `EndOfTurn`, `OnTranscript`, `OnStatusChanged`).
  - Supported by two production adapters: `DeepgramFluxEngine` (Cloud WebSocket) and `WhisperLocalEngine` (Local in-process `Whisper.net` with cuBLAS GPU acceleration and VAD).
  - Managed by a `SpeechEngineRouter` that allows hot-swapping engines at runtime without restarting the app.
* **The Decision Engine Seam (`IDecisionEngine`):**
  - Defines the interface for multiple-choice intention classification and result verification.
  - Production adapter: `JevOpenRouterClient` querying `typesafe/jev-1.13` via `https://openrouter.ai/api/v1/chat/completions`.
  - Encapsulates lexical token slicing (`source_tokens` with UTF-16 character offsets) and option grouping.
* **The UI Automation Driver:**
  - Encapsulates Microsoft UI Automation 3 (`FlaUI.UIA3`) to inspect the foreground process (`GetForegroundWindow`).
  - Implements intelligent noise pruning: skips invisible controls, bounds-clipped nodes, and layout-only containers.
  - Normalizes interactive elements into standardized candidate IDs (`n0_pressClick`, `n1_typeText`, `n2_select`).
* **The Win32 Input Actor:**
  - Employs native Win32 `SendInput` for synthesizing mouse clicks and hardware scan-code keystrokes.
  - Enforces pre-click hit-testing via `IUIAutomation::ElementFromPoint(x, y)` to ensure the targeted element is not occluded by tooltips or modal sheets.
  - Pairs key-down and key-up events under a checked input permit so keys are never left stuck down.
* **The Speculative Coordinator:**
  - Translates `musubipapi/computah`'s core coordinator logic into a thread-safe C# state machine.
  - Issues cancellation tokens (`InputPermit`) upon `StartOfTurn`.
  - Triggers pre-emptive snapshotting and Jev evaluation upon `EagerEndOfTurn`.
  - Finalizes and dispatches execution upon `EndOfTurn` if the transcript matches the speculative plan.
* **Prompt Rule Retention:**
  - Directly adopts the battle-tested prompt guidelines from `musubipapi/computah/Sources/ComputahCore/Prompts/language.json` for Jev's `premise`, `boundary`, `route`, `action`, and `verify` questions.

---

## Testing Decisions

### What Makes a Good Test
* Tests must test **external observable behavior across genuine seams**, never internal private state or implementation details.
* Tests should survive internal refactors without modification as long as the contract at the seam is preserved.

### Testable Seams & Strategies
1. **Speculative Execution Coordinator (`CommandCoordinator`):**
   - Tested using a `FakeSpeechEngine` (which emits controlled sequences of `StartOfTurn` $\rightarrow$ `EagerEndOfTurn` $\rightarrow$ `EndOfTurn`) and a `FakeDecisionEngine`.
   - Verifies:
     - Speculative preparation starts during eager events.
     - Action executes instantly when final transcript matches eager text.
     - Speculative preparation is discarded when `TurnResumed` fires.
     - Active input permits are invalidated when a new `StartOfTurn` arrives.
2. **Lexical Token Slicing & Prompt Serializer:**
   - Pure in-memory unit tests verifying that user utterances are tokenized into exact UTF-16 bounds and sliced verbatim without character corruption.
3. **UI Automation Pruning & Categorization:**
   - Tested against synthetic UI Automation trees to verify that non-interactive containers are stripped while buttons, text fields, and menu items receive deterministic IDs.

---

## Out of Scope

* Vision-based screenshot models (no OpenCV, screenshot LLMs, or OCR).
* Voice authentication or multi-speaker voiceprint identification.
* Direct arbitrary shell script execution (the assistant operates exclusively through public OS UI controls and app routing).
* Cross-platform support for macOS or Linux (this is a Windows 11-native C# implementation).

---

## Further Notes

* **NVIDIA cuBLAS Acceleration:** The project has verified the local `models/ggml-base.bin` model with `whisper.cpp` cuBLAS GPU acceleration, providing an instant fallback when running offline.
* **OpenRouter Economics:** TypeSafe Jev on OpenRouter is priced at ~$0.042 per million input tokens with free output tokens, making cloud decisions cost negligible fractions of a cent.
