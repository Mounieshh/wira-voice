# 06: End-to-End Speculative Execution Coordinator

**What to build:**  
The central orchestration engine (`CommandCoordinator.cs`) connecting `ISpeechEngine`, `IUiaReader`, `IDecisionEngine`, and `IInputActor`. Implements the latency-hiding speculative pipeline: `StartOfTurn` revokes active `InputPermit` tokens; `EagerEndOfTurn` triggers a background UI snapshot and Jev evaluation; `EndOfTurn` executes the action instantly (<10ms perceived latency) if the final transcript matches the speculative plan.

**Blocked by:** 02: Live Speech Ingestion with Cloud/Local Engine Toggle, 04: TypeSafe Jev Choice Evaluator via OpenRouter, 05: Safe Input Dispatcher & Closed-Loop Verification

**Status:** completed

- [x] Implement `InputPermit.cs` thread-safe cancellation and execution authorization
- [x] Implement `CommandCoordinator.cs` state machine
- [x] Handle `StartOfTurn`: cancel pending permits and active executions
- [x] Handle `EagerEndOfTurn`: speculative UI capture and background Jev query
- [x] Handle `TurnResumed`: discard speculative plan
- [x] Handle `EndOfTurn`: verify transcript match and execute action immediately
- [x] Integration test the coordinator with `FakeSpeechEngine` and `FakeDecisionEngine`
