# 07: Non-Activating Floating Top-Bar Overlay

**What to build:**  
A modern Windows 11 floating island overlay (`FloatingTopBar.xaml`) docked at the top center of the screen with `WS_EX_TOPMOST` and `WS_EX_NOACTIVATE` window styles so it never steals focus from the user's active window. Displays live speech transcripts, status pills (Listening, Speculating, Executing, Verified), audio level indicators, and an interactive toggle between Cloud and Local STT modes.

**Blocked by:** 06: End-to-End Speculative Execution Coordinator

**Status:** completed

- [x] Design and implement `FloatingTopBar.xaml` with modern Windows 11 rounded floating island styling
- [x] Set Win32 extended window styles `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` via P/Invoke
- [x] Implement `TopBarViewModel.cs` with reactive bindings for Transcript, Status, and EngineMode
- [x] Connect Cloud/Local toggle to `SpeechEngineRouter`
- [x] Wire microphone audio level feedback animation
- [x] End-to-end user manual test of voice-driven control across open Windows apps
