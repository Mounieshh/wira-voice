# Wira Voice: Deep Module Architectural Plan & Implementation Roadmap

> **Design Methodology:** Conforms strictly to [/codebase-design](file:///d:/Vibe_/wira-voice/.agents/skills/codebase-design/SKILL.md) and [/improve-codebase-architecture](file:///d:/Vibe_/wira-voice/.agents/skills/improve-codebase-architecture/SKILL.md).  
> **Origin Architecture:** Translated and deepened from macOS Swift ([musubipapi/computah](https://github.com/musubipapi/computah)) to Windows 11 C# / .NET 8 WPF.

---

## 1. Architectural Vocabulary & Seam Discipline

Following the `/codebase-design` principles:
* **Deep Modules over Shallow Wrappers:** We avoid fragmented micro-classes that pass calls through. Each module exposes a **small, high-leverage interface** with a **deep implementation** that hides complex Windows COM, audio streams, and API serialization.
* **Seam Rule:** *"One adapter = hypothetical seam. Two adapters = real seam."*
  - **Speech Engine Seam (`ISpeechEngine`):** A genuine seam with two production adapters: `DeepgramFluxEngine` (Cloud WebSocket) and `WhisperLocalEngine` (Offline Whisper.net + VAD).
  - **Decision Engine Seam (`IDecisionEngine`):** A genuine seam supporting OpenRouter (`typesafe/jev-1.13`) and direct TypeSafe API (`jev-1.13.0`).
* **Locality & Testability:** The interface is the test surface. All complex state machines (speculative preparation, turn cancellation, token slicing) are tested through their module interface without exposing internal seams.

---

## 2. Deep Module Architecture & Directory Structure

```
d:/Vibe_/wira-voice/
├── .github/
│   └── docs/
│       ├── reference.md            # Upstream Computah architecture reference
│       └── wira-voice-plan.md       # This implementation plan
├── Core/                            # Deep Modules (Platform-agnostic & domain logic)
│   ├── Audio/                      # WASAPI audio capture & PCM buffering
│   │   ├── AudioCaptureService.cs  # Deep Module: Mic capture to 16kHz PCM16 stream
│   │   └── RingAudioBuffer.cs      # Ring buffer for local VAD/Whisper slicing
│   ├── Speech/                     # Dual STT Subsystem (ISpeechEngine Seam)
│   │   ├── ISpeechEngine.cs        # Unified Interface for turn lifecycle events
│   │   ├── DeepgramFluxEngine.cs   # Adapter 1: Cloud WebSocket with eager turn detection
│   │   ├── WhisperLocalEngine.cs   # Adapter 2: Offline Whisper.net + Silero VAD
│   │   └── SpeechEngineRouter.cs   # Deep Switch: Hot-swaps engines based on UI toggle
│   ├── Automation/                 # Windows UI Automation Subsystem
│   │   ├── UiaReader.cs            # Deep Module: Active window tree traversal & node pruning
│   │   ├── UiSceneSnapshot.cs      # Immutable scene state & candidate choices
│   │   └── Win32InputActor.cs      # Deep Module: Hit-test validation & SendInput dispatch
│   ├── Decision/                   # TypeSafe Jev AI Engine
│   │   ├── IDecisionEngine.cs      # Interface: Choice evaluation & result verification
│   │   ├── JevOpenRouterClient.cs  # Adapter 1: OpenRouter (typesafe/jev-1.13)
│   │   ├── JevChoicePayload.cs     # Prompt serializer, lexical token anchoring, choices
│   │   └── SensitiveTextFilter.cs  # Local redaction of passwords/credentials
│   └── Orchestration/              # Latency-Hiding Coordinator
│       ├── CommandCoordinator.cs   # Deep Module: Turn events -> Speculation -> Execution
│       ├── InputPermit.cs          # Thread-safe cancellation token on new speech turns
│       └── WorkflowVerification.cs # Closed-loop before/after tree diffing
├── UI/                              # Presentation Layer (WPF)
│   ├── FloatingTopBar.xaml         # Always-on-top frameless floating island
│   ├── FloatingTopBar.xaml.cs      # UI event binding (Listen, Cloud/Local toggle)
│   └── TopBarViewModel.cs          # Reactive UI state (Status, Transcript, EngineMode)
├── .env                            # Active API keys and model paths
├── .env.example                    # Template environment variables
├── .gitignore                      # Exclusions for weights, build artifacts, agents
└── WiraVoice.csproj                # .NET 8 WPF Project definition
```

---

## 3. Swift (Computah) to C# (Wira Voice) Translation Map

| macOS Swift Module (`musubipapi/computah`) | Windows C# Deep Module (`wira-voice`) | Architectural Role & Implementation Details |
| :--- | :--- | :--- |
| `AVAudioEngine` / `Voice.swift` | `AudioCaptureService.cs` (`NAudio` WASAPI) | Captures default microphone at 16kHz 16-bit mono PCM. Encapsulates device discovery and audio sample resampling. |
| Deepgram WS inside `Voice.swift` | `DeepgramFluxEngine.cs` | Connects to `wss://api.deepgram.com/v2/listen` using `ClientWebSocket`. Translates `StartOfTurn`, `EagerEndOfTurn`, and `EndOfTurn` into unified C# events. |
| *(None in Computah)* | `WhisperLocalEngine.cs` | Runs offline Whisper (`Whisper.net`) with an energy/Silero VAD. Provides zero-latency, private offline mode. |
| `AXReader.swift` (`AXUIElement`) | `UiaReader.cs` (`FlaUI.UIA3`) | Deep Module: Queries `GetForegroundWindow()`, traverses UIA3 element tree, discards invisible/layout containers, indexes interactive controls (`Button`, `Edit`, `MenuItem`). |
| `AXActor.swift` (`CGEvent`) | `Win32InputActor.cs` (`SendInput`) | Deep Module: Pre-validates physical coordinates (`ElementFromPoint`), focuses text fields, dispatches hardware mouse clicks and scan code keystrokes. |
| `JevChoice.swift` / `language.json` | `JevOpenRouterClient.cs` | Builds constrained multiple-choice prompt payloads. Anchors typed strings to exact UTF-16 lexical token offsets. Evaluates log-probabilities over candidate actions. |
| `CommandCoordinator.swift` | `CommandCoordinator.cs` | Orchestrates the entire speculative pipeline: eagerly snapshots UI and queries Jev during speech, executing instantly when speech confirms. |
| `NotchPanel.swift` (`NSPanel`) | `FloatingTopBar.xaml` (WPF) | Modern Windows 11 floating island window (`WS_EX_TOPMOST`), Acrylic/Mica styling, displays live transcription and Cloud/Local toggle without stealing focus (`WS_EX_NOACTIVATE`). |

---

## 4. Deep Module Interface Specifications

### A. Speech Subsystem (`ISpeechEngine`)
```csharp
public record SpeechTurnEvent(string Utterance, SpeechTurnType Type, CancellationToken PermitToken);

public enum SpeechTurnType
{
    StartOfTurn,      // User started speaking -> invalidate active commands
    EagerEndOfTurn,   // User about to finish -> speculative UI snapshot + Jev query
    TurnResumed,      // User paused then resumed -> cancel speculative plan
    EndOfTurn         // Speech finalized -> execute verified action plan
}

public interface ISpeechEngine : IAsyncDisposable
{
    event Action<SpeechTurnEvent> OnSpeechEvent;
    event Action<string> OnLiveTranscript;
    event Action<string> OnStatusChanged;

    Task StartListeningAsync(CancellationToken ct = default);
    Task StopListeningAsync();
    bool IsListening { get; }
}
```

### B. UI Automation Subsystem (`UiaReader`)
```csharp
public record UiControlCandidate(string Id, string Name, string ControlType, Rect BoundingBox, object NativeHandle);

public record UiSceneSnapshot(
    IntPtr WindowHandle,
    string ProcessName,
    string WindowTitle,
    IReadOnlyList<UiControlCandidate> InteractiveControls
);

public interface IUiaReader
{
    // Deep interface: walks COM tree, prunes non-interactive noise, generates deterministic IDs
    Task<UiSceneSnapshot> CaptureActiveSceneAsync(CancellationToken ct = default);
}
```

### C. AI Decision Subsystem (`IDecisionEngine`)
```csharp
public record ActionDecision(
    string SelectedControlId,
    string? TextToType,
    double Confidence,
    bool RequiresFollowupCheck
);

public interface IDecisionEngine
{
    // Deep interface: tokenizes request, formats multiple-choice criteria, queries OpenRouter/TypeSafe
    Task<ActionDecision> EvaluateIntentAsync(
        string userUtterance,
        UiSceneSnapshot scene,
        CancellationToken ct = default
    );

    // Closed-loop verification check: did the UI change as expected?
    Task<bool> VerifyOutcomeAsync(
        UiSceneSnapshot before,
        UiSceneSnapshot after,
        ActionDecision executedAction,
        CancellationToken ct = default
    );
}
```

### D. Hardware Input Subsystem (`Win32InputActor`)
```csharp
public interface IInputActor
{
    // Deep interface: hit-tests pixel coordinates, focuses control, dispatches native Win32 inputs
    Task<bool> ClickControlAsync(UiControlCandidate control, CancellationToken ct = default);
    Task<bool> TypeTextAsync(UiControlCandidate control, string text, CancellationToken ct = default);
}
```

---

## 5. Step-by-Step Implementation Roadmap

* [ ] **Phase 1: Project Dependencies & Package Integration**
  - Update `WiraVoice.csproj` with:
    - `FlaUI.UIA3` (Microsoft UI Automation COM wrapper)
    - `NAudio.Wasapi` (16kHz audio capture)
    - `Whisper.net` + `Whisper.net.Runtime.DirectML` (Local offline STT)
    - `DotNetEnv` (Secure `.env` loading)
    - `System.Net.WebSockets.ClientWebSocket` (Deepgram streaming)

* [ ] **Phase 2: Audio & Dual Speech Engine**
  - Implement `AudioCaptureService.cs` using Windows WASAPI.
  - Implement `DeepgramFluxEngine.cs` with WebSocket streaming and turn lifecycle parsing (`StartOfTurn`, `EagerEndOfTurn`, `EndOfTurn`).
  - Implement `WhisperLocalEngine.cs` wrapping `Whisper.net` and local voice activity detection.
  - Implement `SpeechEngineRouter.cs` for instant runtime switching between Cloud and Local.

* [ ] **Phase 3: Windows UI Automation (`FlaUI.UIA3`)**
  - Implement `UiaReader.cs` to capture active foreground window (`GetForegroundWindow`).
  - Prune container noise and categorize buttons, textfields, checkboxes, and menu items.
  - Generate stable IDs (`n0_click`, `n1_typeText`) with human-readable labels.

* [ ] **Phase 4: TypeSafe Jev & OpenRouter Integration**
  - Implement `JevOpenRouterClient.cs` calling `typesafe/jev-1.13` via `https://openrouter.ai/api/v1/chat/completions`.
  - Format structured choice evaluation payloads.
  - Implement lexical token slicing (UTF-16 character offsets) for string arguments.
  - Implement `SensitiveTextFilter.cs` to mask passwords and sensitive fields locally.

* [ ] **Phase 5: Win32 Input Dispatch & Verification**
  - Implement `Win32InputActor.cs` using native `SendInput` (mouse moves, clicks, scan codes).
  - Add hit-testing validation (`IUIAutomation::ElementFromPoint`) before clicking.
  - Implement closed-loop verification: compare snapshot before and after action.

* [ ] **Phase 6: Command Coordinator & Latency-Hiding Speculation**
  - Implement `CommandCoordinator.cs`:
    - On `StartOfTurn` $\rightarrow$ cancel active permits.
    - On `EagerEndOfTurn` $\rightarrow$ snapshot active UI tree and query Jev in background.
    - On `EndOfTurn` $\rightarrow$ if transcript matches speculative text, dispatch action immediately.

* [ ] **Phase 7: Floating Top-Bar Overlay (WPF)**
  - Build modern borderless floating island (`FloatingTopBar.xaml`).
  - Add Cloud vs Local STT toggle.
  - Add non-activating window flags (`WS_EX_NOACTIVATE`) so the overlay never steals focus from the user's active app.
