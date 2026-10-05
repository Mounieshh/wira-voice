# 03: Foreground Window UI Inspector & Control Grounding

**What to build:**  
A deep Windows UI Automation module (`UiaReader.cs`) that attaches to the foreground window (`GetForegroundWindow`), recursively traverses interactive controls using `FlaUI.UIA3`, prunes layout and invisible noise, and produces a clean `UiSceneSnapshot` with deterministic candidate IDs (`n0_pressClick`, `n1_typeText`) and exact bounding rectangles.

**Blocked by:** 01: Project Foundation & Environment Bootstrap

**Status:** completed

- [x] Implement `UiSceneSnapshot.cs` and `UiControlCandidate.cs` domain records
- [x] Implement `IUiaReader.cs` deep interface
- [x] Implement `UiaReader.cs` using `FlaUI.UIA3`
- [x] Implement control pruning: skip invisible, clipped, or layout-only wrappers
- [x] Group interactive controls by control type (Button, Edit, CheckBox, MenuItem, TabItem)
- [x] Test active window capture and control categorization
