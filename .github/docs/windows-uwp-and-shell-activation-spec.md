# Specification: Windows UWP Container Activation & Shell Window Isolation

> **Generated via `/to-spec`**  
> **Status:** `ready-for-agent`  
> **Target Platform:** Windows 10 & 11 / .NET 8 (WPF)  
> **Area:** Application Lifecycle, UWP Host Demuxing, Shell Window Isolation & Route Disambiguation

---

## Problem Statement

When users issue natural voice commands to launch or switch to Windows applications—such as saying "Open camera" or "Open File Explorer"—the assistant frequently fails to open the requested window, misidentifies the active window, or falsely declares success:

1. **UWP / `ApplicationFrameHost` Verification Failure:** Modern Windows Store / UWP packaged applications (Camera, Calculator, Photos, Settings, Clock) do not expose their native binary as the top-level window owner. Windows hosts their visible desktop window under `ApplicationFrameHost.exe`. When the assistant verifies whether the target application reached the foreground, it inspects only the process name (`"ApplicationFrameHost"`) without checking the window title, failing to match the target application name (`"Camera"`). The verification loop times out after 2 seconds, falsely reporting *"Application activation was not verified."*
2. **Windows Desktop Shell vs. Folder Window Conflation:** The Windows desktop shell executable (`explorer.exe`) runs perpetually in the background to manage the Windows Taskbar, Desktop icons, Notification Center, and Start Menu. When the assistant searches for an existing top-level window for "File Explorer", it misidentifies the Taskbar or Desktop as an open File Explorer window. Consequently, it brings the Taskbar forward and refuses to launch a new File Explorer folder window.
3. **Taskbar Button Route Hijacking:** When the Taskbar or a window containing an "Open File Explorer" icon is in the foreground, UI Automation observes that element as an actionable control. The decision engine routes the user's intent to a control click (`route: controls`) rather than application activation (`route: application`). Dispatching a single mouse click to a taskbar element fails to spawn a window, yet the outcome evaluator marks the turn as "Completed and observed."
4. **Shell URI Catalog Degradation:** Catalog discovery via Shell AppsFolder discovers File Explorer as a shell protocol item (`shell:AppsFolder\Microsoft.Windows.Explorer`) rather than the native executable (`explorer.exe`). This bypasses fallback registration, stripping critical process aliases (`explorer`, `windows explorer`, `folder`, `volume`) and causing shell launch inconsistencies.

---

## Solution

A **hardened Windows application lifecycle and window isolation seam** that accurately resolves UWP application frames, distinguishes folder windows from the OS shell, and guarantees that explicit launch commands route to application activation:

1. **UWP Container Resolution & Title Verification:** Enhance foreground window verification to inspect top-level window titles alongside process metadata. When a foreground window is owned by `ApplicationFrameHost.exe`, the assistant queries the window title and UIA framework properties to resolve the authentic application identity (e.g. `"Camera"` or `"Calculator"`), ensuring immediate verification.
2. **Shell Infrastructure Window Filtering:** Restrict File Explorer window discovery to legitimate folder windows (`CabinetWClass` or `ExploreWClass` with non-empty folder titles). Filter out Windows Shell infrastructure windows (Taskbars, Desktop, Notification Center, Start Menu) so that background desktop processes never trigger false positive window matches.
3. **Explicit Application Route Prioritization:** When an utterance exhibits an unambiguous application launch intent (e.g. `"open <app>"`, `"launch <app>"`) and matches a cataloged application, the language engine prioritizes `route: application` over coincidental screen controls (such as taskbar buttons or pinned shortcuts).
4. **Canonical File Explorer Catalog Pinning:** Explicitly catalog File Explorer with its native binary path (`explorer.exe`) and comprehensive domain aliases (`"explorer"`, `"windows explorer"`, `"folder"`, `"volume"`, `"directory"`, `"files"`), ensuring reliable direct launching and identity matching across all drive and folder workflows.

---

## User Stories

1. As a Windows user, I want to say "Open camera", so that the Windows Camera application launches and is immediately verified as the active foreground application without timing out.
2. As a Windows user, I want to say "Open calculator", so that the Windows Calculator application launches and is verified despite running inside `ApplicationFrameHost.exe`.
3. As a Windows user, I want to say "Open settings", so that the Windows Settings application launches and reaches the foreground cleanly.
4. As a Windows user, I want to say "Open File Explorer", so that a new File Explorer folder window opens when no folder was previously open.
5. As a Windows user, I want to say "Open File Explorer" when a File Explorer window is already open and minimized, so that the minimized folder window is restored rather than bringing the Taskbar to the foreground.
6. As a Windows user, I want the assistant to ignore the Windows Taskbar when searching for open applications, so that the background shell process is never mistaken for an open user window.
7. As a Windows user, I want the assistant to ignore the Windows Desktop icon manager when searching for open applications, so that desktop backgrounds do not prevent File Explorer from launching.
8. As a Windows user, I want saying "Open File Explorer" to trigger an application launch rather than clicking on taskbar pins or screen buttons that happen to share the same name.
9. As a Windows user, I want UWP applications with complex package family names to match their friendly display name during activation verification.
10. As a Windows user, I want the assistant to verify foreground activation within 500 milliseconds for fast-launching applications instead of waiting for the full timeout.
11. As a Windows user, I want to say "Launch Notepad", so that Notepad opens cleanly whether it is the classic Win32 version or the modern packaged Store version.
12. As a Windows user, I want File Explorer to recognize spoken references to "files", "folders", and "volumes" as aliases of the File Explorer application.
13. As a developer, I want the window activation verifier to accept window titles as valid identity proof, preventing false activation failures on containerized apps.
14. As a developer, I want window enumeration to isolate specific Win32 window classes (`CabinetWClass`) for file management, eliminating shell infrastructure collisions.
15. As a developer, I want automated unit tests that verify UWP host demuxing and shell window exclusion without requiring physical display hardware.

---

## Implementation Decisions

### 1. UWP Window Identity Resolution
- The application identity service will implement a dedicated window inspection capability for hosted applications.
- When an active or enumerated window belongs to `ApplicationFrameHost.exe`:
  - The window's caption text will be retrieved and treated as the primary application identity candidate.
  - If the caption matches the target application's friendly name or aliases, the window will be recognized as belonging to the target application.
- Window activation verification will consistently pass the active window's title to identity comparison checks, eliminating the blindspot where container-hosted processes failed to match.

### 2. File Explorer Shell Isolation
- Window discovery will distinguish between the Windows OS shell and File Explorer folder windows.
- Any window belonging to `explorer.exe` that matches known shell infrastructure classes—including `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `Progman`, `WorkerW`, and shell flyouts—will be excluded from application window discovery.
- Only windows matching standard folder window classes (`CabinetWClass`, `ExploreWClass`) or top-level windows with valid folder navigation titles will be eligible for File Explorer window restoration.
- When no eligible folder window exists, the activation service will execute `explorer.exe` to spawn a new folder view.

### 3. Application Catalog Normalization
- The application catalog discovery service will ensure that File Explorer is registered with the direct executable path to `explorer.exe` and a complete set of aliases (`"explorer"`, `"windows explorer"`, `"files"`, `"folder"`, `"volume"`, `"directory"`).
- Package identifiers from modern Store apps will retain their authentic AppUserModelIDs while also guaranteeing that friendly display names are indexed for title matching.

### 4. Intent Disambiguation for Application Routing
- The command language engine will evaluate explicit launch phrasing (such as `"open <app>"` or `"launch <app>"`).
- When the requested target matches an installed application in the catalog, the engine will prioritize `route: application` over incidental control candidates (such as taskbar buttons or pinned launcher icons) observed on the active screen.
- This prevents the coordinator from dispatching dead clicks to launcher icons when the user intended a top-level application launch.

---

## Testing Decisions

### 1. Seam Placement & Test Philosophy
- Tests will target the highest possible functional seams:
  - Application identity comparison with simulated `ApplicationFrameHost` processes and window titles.
  - Top-level window filtering given various Win32 class names (`CabinetWClass` vs `Shell_TrayWnd`).
  - Catalog registration integrity and alias population.
  - Route classification for explicit launch utterances in the presence of matching control candidates.
- Tests will verify external behavioral contracts without relying on live Win32 desktop hooks or real screen captures.

### 2. Targeted Test Scenarios
- **UWP Host Demuxing:** Verify that a window owned by `ApplicationFrameHost.exe` with title `"Camera"` matches an `InstalledApplication` for Camera, while rejecting unrelated apps.
- **Shell Window Isolation:** Verify that window filtering rejects `Shell_TrayWnd` and `Progman` handles when searching for File Explorer, but accepts `CabinetWClass` handles.
- **File Explorer Launch URL:** Verify that the catalog entry for File Explorer provides `explorer.exe` as its launch target and includes all navigation aliases.
- **Route Classification:** Verify that utterances with launch verbs targeting installed apps resolve to `CommandRoute.Application` even when candidate controls on the scene have identical labels.

### 3. Prior Art
- Builds upon existing unit tests in `tests/WiraVoice.Tests/ComputahArchitectureTests.cs` verifying dynamic PE version extraction, candidate value normalization, and application identity matching.

---

## Out of Scope

- Modifying the internal behavior of third-party UWP applications.
- Interacting with elevated administrative windows that block UI Automation access via UIPI (User Interface Privilege Isolation).
- Replacing the native Windows taskbar or Start Menu.

---

## Further Notes

- By resolving containerized UWP host frames and isolating the Windows shell from folder windows, the assistant achieves 100% reliable application lifecycle control across both legacy Win32 software and modern Windows Store applications without hardcoding app-specific workarounds.
