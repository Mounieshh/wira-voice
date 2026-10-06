# Specification: Windows-Native Application Identity & File Navigation

> **Generated via `/to-spec`**  
> **Status:** `ready-for-agent`  
> **Target Platform:** Windows 11 / .NET 8 (WPF)  
> **Area:** Application Modeling, Windows AUMID, Process Identity & Multi-Step Execution

---

## Problem Statement

When users issue natural voice commands to control Windows 11—such as opening applications, creating browser tabs, navigating drives and folders, or searching for files—the assistant frequently misinterprets the target or stalls mid-action:

1. **Mac-Centric Identity Mismatch:** The upstream architecture was built for macOS and expects reverse-DNS Bundle Identifiers (`com.google.Chrome`) and Mac window structures. On Windows, executables have process binary names (`chrome.exe`), while user-facing catalog names come from Start Menu shortcuts (`"Google Chrome"`). Comparing these directly caused the assistant to falsely conclude that active windows did not match the target app, discarding in-app control actions and spawning redundant duplicate instances.
2. **Hardcoded Process Dictionaries:** Attempting to bridge this gap with hardcoded alias maps (e.g. mapping `"chrome"` to `"Google Chrome"`) is brittle, fails on renamed or uncataloged applications, and violates clean modular design.
3. **Incomplete Text Submission in Windows Shell:** In Windows File Explorer and browsers, typing a folder path or search term into an address or search bar does not trigger navigation until the Enter key (`VK_RETURN`) is pressed. Incomplete submission left text sitting idle in the input field while the workflow timed out.
4. **Premature Multi-Step Workflow Aborts:** When a multi-step interaction (such as clicking a breadcrumb bar to reveal an address field) produced intermediate progress, the decision engine marked the overall goal as pending. The execution loop interpreted this as an unknown outcome and aborted immediately instead of continuing to the next step.

---

## Solution

A **pure Windows-native application identity and execution seam** that completely eliminates macOS legacy artifacts:

1. **Dynamic Windows Application Identity:** Replace synthetic bundle IDs with native Windows application model primitives:
   - **AppUserModelID (AUMID):** The canonical Windows application identifier (e.g. `Chrome`, `MSEdge`, `electron.app.Antigravity`, or `<PackageFamilyName>!<AppId>`), queried dynamically via Windows Shell and Start Application APIs.
   - **PE Version Resource Extraction (`FileDescription`):** Query the embedded PE `VERSIONINFO` metadata directly from active process modules to dynamically retrieve the authentic friendly name (e.g. `chrome.exe` $\rightarrow$ `"Google Chrome"`, `explorer.exe` $\rightarrow$ `"Windows Explorer"`) with **zero hardcoded dictionaries**.
2. **Unified Scene & Catalog Semantics:** Ensure that both the active scene observation and the application catalog present matching, human-readable application names and AUMIDs to the AI decision model.
3. **Native Window Activation & Restoration:** For existing application windows, restore minimized windows (`SW_RESTORE`) and bring them to the foreground via Win32 `SetForegroundWindow` without launching duplicate processes.
4. **Auto-Submitting Navigation & Search Fields:** Automatically synthesize a trailing `VK_RETURN` keystroke when typing into Address Bars or Search Fields, completing navigation and search in a single responsive turn.
5. **Intermediate Multi-Step Progression:** Recognize intermediate UI state transitions and resolved progress effects as forward progress, enabling sequential tasks (such as focusing an address bar, entering a path, and confirming navigation) to advance across consecutive attempts.
6. **Smart Drive & Path Normalization:** Automatically normalize spoken drive and volume requests (e.g. `"volume D"`, `"D drive"`) into standard Windows root paths (`D:\`).

---

## User Stories

1. As a Windows user, I want to say "Open Chrome" when Chrome is closed, so that Google Chrome launches cleanly via its native Windows application identity.
2. As a Windows user, I want to say "Open Chrome" when Chrome is already running in the background, so that its existing window is restored and brought forward without spawning a second window.
3. As a Windows user, I want to say "Open a new tab" while Chrome is active, so that the assistant clicks the New Tab button inside the existing browser instead of misidentifying the active window as an unknown app.
4. As a Windows user, I want the assistant to identify modern Windows Store and MSIX packaged apps by their AppUserModelID (AUMID), so that apps like Windows Terminal or Calculator are recognized natively.
5. As a developer, I want the assistant to dynamically discover application names from the executable's PE version resource table (`FileDescription`), so that new and third-party software is supported without hardcoded lists.
6. As a Windows user, I want to say "Go to volume D" or "Open D drive", so that the assistant normalizes the request to `D:\` and navigates File Explorer straight to that drive.
7. As a Windows user, I want to say "Search for the file resume" in File Explorer, so that the assistant types the query and presses Enter to run the search in a single action.
8. As a Windows user, I want to say "Go to folder Intention", so that the assistant clicks the breadcrumb bar, types the folder name, and presses Enter across sequential steps without giving up.
9. As a Windows user, I want the top floating notch to display strictly the clean transcribed sentence I spoke, so that technical debug tags and parenthetical status notes never clutter the screen.
10. As a Windows user, I want multi-step UI actions to treat visible UI updates as progress, so that sequential interactions continue smoothly until the goal is achieved.
11. As a Windows user, I want the AI decision prompts to be free of macOS-specific terminology like "bundle", so that decisions are evaluated against native Windows concepts.
12. As a Windows user, I want the assistant to verify that an action succeeded before declaring completion, so that failed or blocked interactions are caught reliably.

---

## Implementation Decisions

### 1. Unified Application Identity Module
- Introduce a Windows-native application identity service that resolves metadata for both installed applications and running processes.
- For installed applications: Query the Windows Start Application registry and Shell APIs to extract the friendly display name, executable path, and `AppUserModelID` (AUMID).
- For running processes: Inspect the process main module's `FileVersionInfo.FileDescription` to obtain the friendly application name dynamically. If unavailable, fall back to the process name.
- Eliminate synthetic `bundle=` tags in AI prompt options, replacing them with standard Windows application metadata (`name` and `aumid`).

### 2. Scene Observation & Equality Contract
- The observation snapshot service will populate its application name using the dynamically resolved friendly application name and AUMID.
- Application equality checks will compare canonical friendly names and AUMIDs, eliminating false negatives caused by differences between process binary names (`chrome`) and catalog names (`Google Chrome`).

### 3. Window Activation Seam
- Window activation will inspect running processes for matching AUMIDs or friendly names with top-level window handles (`HWND`).
- When a matching window is found, it will be restored from minimized state using the Win32 `SW_RESTORE` command and focused using `SetForegroundWindow`.
- New processes will only be launched via `Process.Start` if no existing top-level window is found.

### 4. Automated Field Submission
- The native input execution actor will inspect the role and description of target controls.
- When typing text into controls identified as an Address Bar, Search Box, or search field, a trailing Return key event will be dispatched automatically after a brief debounce period.

### 5. Multi-Step Execution & Progress Promotion
- The workflow execution loop will evaluate intermediate progress effects: if an action successfully updates an intermediate target (such as focusing an editor or entering text) and changes the UI state, it will be promoted to progress status.
- This allows the workflow loop to advance to subsequent attempts rather than halting prematurely on pending outcomes.

---

## Testing Decisions

### 1. Seam Placement & Test Philosophy
- Tests must verify external observable behavior across the highest possible seams:
  - Application identity resolution given process metadata and executable paths.
  - Value normalization for spoken volume and path commands.
  - Multi-step outcome judgment promotion from raw decision outputs.
  - Top bar presentation text purity.
- Tests will not mock internal private implementation details or rely on brittle hardcoded process lists.

### 2. Targeted Modules
- **Application Identity Resolution:** Verify that process executable descriptions and AUMIDs resolve correctly for standard Windows applications (e.g. Chrome, Edge, File Explorer, VS Code).
- **Candidate Value Normalization:** Verify that spoken drive letters (`"D"`, `"d"`, `"volume D"`, `"D:"`) normalize to `"D:\"` when targeting Address Bars, and search queries strip conversational prefixes.
- **Workflow Progress Evaluation:** Verify that actions producing visible UI state changes or resolved progress effects transition the workflow into progress rather than halting.
- **Top Bar View Model:** Verify that display text strictly reflects the transcribed utterance without status tag noise.

### 3. Prior Art
- Builds upon existing unit tests in the test suite covering speculative turn coordination, credential redaction, and speech engine switching.

---

## Out of Scope

- Support for legacy non-Windows operating systems (macOS, Linux).
- Direct file system manipulation bypassing the GUI (Wira Voice is an accessibility and UI automation assistant; it navigates through native Windows UI rather than running silent background shell commands).
- Custom virtual keyboard injection for games or DirectInput exclusive-mode applications.

---

## Further Notes

- By removing the legacy macOS bundle abstraction and adopting Windows AUMID and PE `FileDescription` metadata, the architecture becomes deeply modular, fully automated, and resilient to third-party application updates without manual maintenance.
