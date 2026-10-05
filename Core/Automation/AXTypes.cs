using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace WiraVoice.Core.Automation;

public enum AXOperation
{
    PressClick,
    ShowMenu,
    Increment,
    Decrement,
    TypeText,
    ReplaceText,
    ScrollUp,
    ScrollDown,
    SetNumber,
    Select,
    PressReturn
}

public static class AXOperationExtensions
{
    public static string ToWireString(this AXOperation op) => op switch
    {
        AXOperation.PressClick => "pressClick",
        AXOperation.ShowMenu => "showMenu",
        AXOperation.Increment => "increment",
        AXOperation.Decrement => "decrement",
        AXOperation.TypeText => "typeText",
        AXOperation.ReplaceText => "replaceText",
        AXOperation.ScrollUp => "scrollUp",
        AXOperation.ScrollDown => "scrollDown",
        AXOperation.SetNumber => "setNumber",
        AXOperation.Select => "select",
        AXOperation.PressReturn => "pressReturn",
        _ => op.ToString().ToLowerInvariant()
    };

    public static AXOperation FromWireString(string s) => s switch
    {
        "pressClick" => AXOperation.PressClick,
        "showMenu" => AXOperation.ShowMenu,
        "increment" => AXOperation.Increment,
        "decrement" => AXOperation.Decrement,
        "typeText" => AXOperation.TypeText,
        "replaceText" => AXOperation.ReplaceText,
        "scrollUp" => AXOperation.ScrollUp,
        "scrollDown" => AXOperation.ScrollDown,
        "setNumber" => AXOperation.SetNumber,
        "select" => AXOperation.Select,
        "pressReturn" => AXOperation.PressReturn,
        _ => Enum.TryParse<AXOperation>(s, true, out var result) ? result : AXOperation.PressClick
    };
}

public sealed class AXNode : IEquatable<AXNode>
{
    public int Id { get; init; }
    public int? Parent { get; init; }
    public int Depth { get; init; }
    public string Role { get; init; } = "";
    public string Subrole { get; init; } = "";
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public List<string> Actions { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public Rectangle? Frame { get; init; }
    public string Help { get; init; } = "";
    public string Document { get; init; } = "";
    public string Identifier { get; init; } = "";
    public bool Focused { get; init; } = false;
    public bool Selected { get; init; } = false;
    public bool WritableValue { get; init; } = false;
    public bool WritableSelection { get; init; } = false;
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? ChildCount { get; init; }
    public string Orientation { get; init; } = "";
    public bool Visible { get; init; } = true;
    public object? NativeElement { get; init; }

    public bool Equals(AXNode? other)
    {
        if (other is null) return false;
        return Id == other.Id && Parent == other.Parent && Depth == other.Depth &&
               Role == other.Role && Label == other.Label && Value == other.Value &&
               Focused == other.Focused && Selected == other.Selected && Visible == other.Visible;
    }

    public override bool Equals(object? obj) => obj is AXNode other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Id, Role, Label, Value);
}

public enum AXCoverageReason
{
    Budget,
    VisibleCollection,
    Clipped,
    Hidden,
    Depth,
    Unreadable,
    Region,
    Unspecified
}

public readonly record struct AXCoverageGap(int? NodeID, AXCoverageReason Reason);

public sealed class AXCandidate
{
    public string Id { get; }
    public int NodeID { get; }
    public AXOperation Operation { get; }
    public string Description { get; }
    public int GroupID { get; }
    public List<int> BindingNodeIDs { get; set; } = [];

    public AXCandidate(string id, int nodeID, AXOperation operation, string description, int groupID, List<int>? bindingNodeIDs = null)
    {
        Id = id;
        NodeID = nodeID;
        Operation = operation;
        Description = description;
        GroupID = groupID;
        BindingNodeIDs = bindingNodeIDs ?? [];
    }
}

public sealed class AXGroup
{
    public int Id { get; }
    public string Title { get; }
    public List<AXCandidate> Members { get; }

    public AXGroup(int id, string title, List<AXCandidate> members)
    {
        Id = id;
        Title = title;
        Members = members;
    }
}

public class AXFailureException : Exception
{
    public AXFailureException(string message) : base(message) { }

    public static AXFailureException Permission => new("Enable UI Automation / Accessibility for Wira Voice in Windows Settings.");
    public static AXFailureException NoWindow => new("The target application has no accessible window or menu.");
    public static AXFailureException Changed => new("The target app or control changed.");
    public static AXFailureException PointerTargetUnknown => new("The pointer hit could not be bound to the selected control.");
    public static AXFailureException Unavailable(string detail) => new(detail);
}
