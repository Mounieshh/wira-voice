# 05: Safe Input Dispatcher & Closed-Loop Verification

**What to build:**  
A hardware input dispatcher (`Win32InputActor.cs`) that verifies hit-test coordinates using `ElementFromPoint`, focuses target editors, dispatches native Win32 `SendInput` mouse clicks and scan code keystrokes, ensures key-down and key-up pairs cannot be left stuck, and re-reads the accessibility tree to verify whether the expected state change occurred.

**Blocked by:** 03: Foreground Window UI Inspector & Control Grounding

**Status:** completed

- [x] Implement `IInputActor.cs` interface
- [x] Implement `Win32InputActor.cs` using native Win32 `SendInput` P/Invoke
- [x] Implement hit-testing validation (`IUIAutomation::ElementFromPoint`) before clicking
- [x] Implement safe editor focusing (`SetFocus` / verified click) before typing text
- [x] Implement paired key press handling (`InputPermit.Pair`) so keys never stick
- [x] Implement closed-loop verification: compare snapshot before and after action
