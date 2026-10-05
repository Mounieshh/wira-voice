# 02: Live Speech Ingestion with Cloud/Local Engine Toggle

**What to build:**  
A unified speech subsystem satisfying the `ISpeechEngine` seam. Captures live microphone audio using Windows WASAPI (`NAudio`) as 16kHz PCM16, and feeds either:
1. `DeepgramFluxEngine` (Cloud streaming WebSocket with conversational turn events: `StartOfTurn`, `EagerEndOfTurn`, `TurnResumed`, `EndOfTurn`), or
2. `WhisperLocalEngine` (Offline in-process Whisper.net with cuBLAS GPU acceleration and Silero/energy VAD).
Includes a `SpeechEngineRouter` to hot-swap engines at runtime without restarting.

**Blocked by:** 01: Project Foundation & Environment Bootstrap

**Status:** completed

- [x] Implement `ISpeechEngine.cs` interface and `SpeechTurnEvent` records
- [x] Implement `AudioCaptureService.cs` using Windows WASAPI to stream 16kHz mono PCM16
- [x] Implement `DeepgramFluxEngine.cs` connecting to Deepgram Flux WebSocket and parsing turn-taking lifecycle
- [x] Implement `WhisperLocalEngine.cs` loading `models/ggml-base.bin` using cuBLAS GPU acceleration with VAD chunking
- [x] Implement `SpeechEngineRouter.cs` allowing runtime switching between Cloud and Local
- [x] Unit test turn event emissions across the `ISpeechEngine` seam
