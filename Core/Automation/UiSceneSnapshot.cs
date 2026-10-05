using System;
using System.Collections.Generic;
using System.Drawing;

namespace WiraVoice.Core.Automation;

/// <summary>
/// An observed interactive UI control candidate grounded from the native Windows Accessibility tree.
/// </summary>
public record UiControlCandidate(
    string Id,                  // e.g. "n0_pressClick", "n1_typeText"
    string Name,                // e.g. "Submit", "Search or enter address"
    string Role,                // e.g. "Button", "Edit", "MenuItem", "CheckBox"
    string Help,                // e.g. "Clicks the submit button"
    Rectangle BoundingBox,      // Screen coordinates for hit testing and clicks
    string SupportedOperation,  // "pressClick", "typeText", "select", "setNumber"
    object? NativeElement       // FlaUI.Core.AutomationElements.AutomationElement
);

/// <summary>
/// An immutable snapshot of the active application's scene and interactive controls.
/// </summary>
public record UiSceneSnapshot(
    IntPtr WindowHandle,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    IReadOnlyList<UiControlCandidate> InteractiveControls
);
