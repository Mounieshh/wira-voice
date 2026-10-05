using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Immutable snapshot of an application's accessibility tree and native window state.
/// Exact port of Computah's AXSnapshot (AXTypes.swift and VerificationEvidence.swift).
/// </summary>
public sealed class AXSnapshot
{
    public int Pid { get; }
    public string? BundleID { get; }
    public string AppName { get; }
    public string WindowTitle { get; }
    public List<AXNode> Nodes { get; }
    public List<AXCoverageGap> Coverage { get; }
    public bool Partial { get; }
    public double ReadSeconds { get; }
    public IntPtr? WindowHandle { get; }
    public IntPtr? MenuBarHandle { get; }
    public List<IntPtr>? ApplicationWindows { get; }
    public List<object?> Handles { get; }

    public AXSnapshot(
        int pid,
        string appName,
        string windowTitle,
        List<AXNode> nodes,
        bool partial,
        List<object?>? handles = null,
        double readSeconds = 0,
        IntPtr? windowHandle = null,
        IntPtr? menuBarHandle = null,
        string? bundleID = null,
        List<AXCoverageGap>? coverage = null,
        List<IntPtr>? applicationWindows = null)
    {
        Pid = pid;
        BundleID = bundleID;
        AppName = appName;
        WindowTitle = windowTitle;
        Nodes = nodes;
        Partial = partial;
        ReadSeconds = readSeconds;
        WindowHandle = windowHandle;
        MenuBarHandle = menuBarHandle;
        ApplicationWindows = applicationWindows;
        Handles = handles ?? nodes.Select(n => n.NativeElement).ToList();

        if (coverage == null || coverage.Count == 0)
        {
            Coverage = partial ? [new AXCoverageGap(null, AXCoverageReason.Unspecified)] : [];
        }
        else
        {
            Coverage = coverage;
        }
    }

    public bool UnfinishedVisibleRead =>
        Coverage.Any(c => c.Reason is AXCoverageReason.Budget or AXCoverageReason.Depth
                                   or AXCoverageReason.Unreadable or AXCoverageReason.Unspecified);

    public bool IsMenuOnly =>
        WindowHandle == null && (MenuBarHandle != null || Nodes.FirstOrDefault()?.Role == "AXMenuBar");

    public bool CanObserveAfter(AXSnapshot previous, int? foregroundPID)
    {
        return SameWindow(previous) ||
            (previous.IsMenuOnly && WindowHandle != null && Pid == previous.Pid && foregroundPID == Pid);
    }

    public bool SameWindow(AXSnapshot other)
    {
        if (Pid != other.Pid) return false;
        if (WindowHandle.HasValue && other.WindowHandle.HasValue)
        {
            return WindowHandle.Value == other.WindowHandle.Value;
        }
        if (IsMenuOnly || other.IsMenuOnly)
        {
            if (IsMenuOnly && other.IsMenuOnly)
            {
                if (MenuBarHandle.HasValue && other.MenuBarHandle.HasValue)
                {
                    return MenuBarHandle.Value == other.MenuBarHandle.Value;
                }
            }
        }
        return false;
    }

    public bool HasObservedChange(AXSnapshot other)
    {
        if (!SameWindow(other) || Nodes.Count != other.Nodes.Count || Handles.Count != other.Handles.Count)
        {
            return true;
        }
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (!Nodes[i].Equals(other.Nodes[i])) return true;
        }
        return false;
    }

    public List<Dictionary<string, string>> BindingEvidence
    {
        get
        {
            return Nodes
                .Where(n => !string.IsNullOrEmpty(n.Document) || n.Role == "AXWebArea" || n.Selected || n.Focused)
                .Select(n => new Dictionary<string, string>
                {
                    ["role"] = n.Role,
                    ["identifier"] = n.Identifier,
                    ["document"] = n.Document,
                    ["label"] = n.Label,
                    ["value"] = Clipped(n.Value, 500),
                    ["parent"] = n.Parent.HasValue && Nodes.IndicesContains(n.Parent.Value) ? Nodes[n.Parent.Value].Label : "",
                    ["selected"] = n.Selected.ToString().ToLowerInvariant(),
                    ["focused"] = n.Focused.ToString().ToLowerInvariant()
                })
                .ToList();
        }
    }

    public string SelectionEvidence
    {
        get
        {
            var state = string.Join("\n", Nodes
                .Where(n => !n.Enabled || n.Focused || n.Selected)
                .Select(n => $"{n.Role}: {n.Label}; value={Clipped(n.Value, 160)}; enabled={n.Enabled}; focused={n.Focused}; selected={n.Selected}"));

            var collections = Nodes
                .Where(n => n.Visible && (n.Role is "AXTable" or "AXList" or "AXOutline" or "AXGrid"))
                .Select(col => new Dictionary<string, string>
                {
                    ["role"] = col.Role,
                    ["label"] = col.Label,
                    ["total_children"] = col.ChildCount?.ToString() ?? "unknown",
                    ["observed_items"] = string.Join("; ", Nodes
                        .Where(c => c.Visible && c.Parent == col.Id)
                        .Take(4)
                        .Select(c => string.IsNullOrEmpty(c.Label) ? c.Value : c.Label))
                })
                .ToList();

            var collectionJson = JsonSerializer.Serialize(collections);
            var coverageReasons = string.Join(",", Coverage.Select(c => c.Reason.ToString().ToLowerInvariant()).Distinct().OrderBy(s => s));

            return $"app={AppName}; window={WindowTitle}; partial={Partial}; coverage={coverageReasons}\n{state}\nCurrent collections: {collectionJson}";
        }
    }

    public string Evidence
    {
        get
        {
            var entries = Nodes
                .Where(n => !string.IsNullOrEmpty(n.Label) || !string.IsNullOrEmpty(n.Value) || n.Focused || n.Selected || n.Role == "AXTextArea")
                .Select(n => $"{n.Role} | {n.Label} | help={n.Help} | value={Clipped(n.Value, 400)}" +
                             (n.Selected ? " | selected" : "") +
                             (n.Focused ? " | focused" : "") +
                             (n.Enabled ? "" : " | disabled") +
                             (n.ChildCount.HasValue ? $" | total children={n.ChildCount.Value}" : ""));

            return $"app={AppName}; window={WindowTitle}; nodes={Nodes.Count}; partial={Partial}\n{string.Join("\n", entries)}";
        }
    }

    public string DestinationEvidence
    {
        get
        {
            return string.Join("\n", Nodes
                .Where(n => n.Role == "AXWebArea" && Uri.TryCreate(n.Value, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https"))
                .Select(n => $"Loaded document: {n.Value}; title: {n.Label}"));
        }
    }

    public bool ObservesDestination(Uri url)
    {
        var expectedHost = url.Host.ToLowerInvariant();
        var expectedPath = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;

        return Nodes.Any(n =>
        {
            if (n.Role != "AXWebArea" || !Uri.TryCreate(n.Value, UriKind.Absolute, out var observed)) return false;
            var observedHost = observed.Host.ToLowerInvariant();
            var observedPath = string.IsNullOrEmpty(observed.AbsolutePath) ? "/" : observed.AbsolutePath;

            return url.Scheme.Equals(observed.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   url.Port == observed.Port &&
                   expectedHost == observedHost &&
                   expectedPath == observedPath &&
                   url.Query == observed.Query &&
                   url.Fragment == observed.Fragment;
        });
    }

    public string? ValueForSameControl(AXCandidate candidate, AXSnapshot old)
    {
        if (!old.Nodes.IndicesContains(candidate.NodeID)) return null;
        var oldNode = old.Nodes[candidate.NodeID];
        var match = Nodes.FirstOrDefault(n => n.Id == candidate.NodeID || (n.Role == oldNode.Role && n.Label == oldNode.Label));
        return match?.Value;
    }

    private string StateLine(AXNode node, int valueLimit = 2000)
    {
        var regions = new List<string>();
        var parent = node.Parent;
        while (parent.HasValue && Nodes.IndicesContains(parent.Value) && regions.Count < 4)
        {
            var pNode = Nodes[parent.Value];
            if (!string.IsNullOrEmpty(pNode.Label) && pNode.Depth > 2 && pNode.Role != "AXWebArea")
            {
                regions.Add(pNode.Label);
            }
            if (pNode.Role is "AXTable" or "AXList" or "AXOutline" or "AXGrid") break;
            parent = pNode.Parent;
        }
        regions.Reverse();
        var regionStr = string.Join(" / ", regions);

        var sb = new StringBuilder();
        sb.Append($"region={regionStr} | {node.Role} | available label={node.Label} | value={Clipped(node.Value, valueLimit)} | enabled={node.Enabled} focused={node.Focused} selected={node.Selected}");
        if (node.ChildCount.HasValue) sb.Append($" | children={node.ChildCount.Value}");
        if (node.Value.Length > valueLimit) sb.Append(" | value truncated; remaining text unknown");
        if (!string.IsNullOrEmpty(node.Document)) sb.Append($" | document={Clipped(node.Document, 500)}");
        return sb.ToString();
    }

    public string VerificationEvidence(AXSnapshot? before = null, AXCandidate? target = null, int limit = 12000)
    {
        var valueLimit = limit >= 8000 ? 2000 : Math.Max(40, limit / 8);
        var rows = new List<string> { $"AFTER app={AppName}; window={WindowTitle}; partial={Partial}" };
        var repeats = new Dictionary<string, int>();

        void AppendRow(string row)
        {
            repeats[row] = repeats.GetValueOrDefault(row, 0) + 1;
            if (repeats[row] == 1) rows.Add(row);
        }

        var included = new HashSet<int>();
        var paired = new HashSet<int>();

        void AddNode(int id, string prefix)
        {
            if (!Nodes.IndicesContains(id) || !included.Add(id)) return;
            AppendRow(prefix + StateLine(Nodes[id], valueLimit));
        }

        void AddTargetContext(int id, string prefix, AXSnapshot b)
        {
            if (!Nodes.IndicesContains(id) || included.Contains(id)) return;
            var oldIndex = b.Nodes.FindIndex(n => n.Id == id || (n.Role == Nodes[id].Role && n.Label == Nodes[id].Label));
            if (oldIndex >= 0)
            {
                AppendRow($"{prefix} BEFORE: {b.StateLine(b.Nodes[oldIndex], valueLimit)}\n{prefix} AFTER: {StateLine(Nodes[id], valueLimit)}");
                included.Add(id);
                paired.Add(id);
            }
            else
            {
                AddNode(id, $"{prefix} AFTER (prior identity unknown): ");
            }
        }

        if (before != null)
        {
            rows.Add($"BEFORE app={before.AppName}; window={before.WindowTitle}; partial={before.Partial}");
            rows.Add($"Same native window before and after: {SameWindow(before)}.");

            if (WindowHandle.HasValue && before.ApplicationWindows != null)
            {
                var existed = before.ApplicationWindows.Contains(WindowHandle.Value);
                rows.Add($"Current native window existed in the app's BEFORE window inventory: {existed}. This comparison uses native window identities, independently of tree coverage. Judge whether the resulting window contains the requested object.");
            }

            if (target != null && before.Nodes.IndicesContains(target.NodeID))
            {
                var targetBefore = "ACTION CONTROL BEFORE: " + before.StateLine(before.Nodes[target.NodeID], valueLimit);
                var matchId = Nodes.FindIndex(n => n.Id == target.NodeID || (n.Role == before.Nodes[target.NodeID].Role && n.Label == before.Nodes[target.NodeID].Label));
                if (matchId >= 0)
                {
                    AppendRow($"{targetBefore}\nSAME ACTION CONTROL AFTER: {StateLine(Nodes[matchId], valueLimit)}");
                    included.Add(matchId);
                    paired.Add(matchId);

                    foreach (var evId in target.BindingNodeIDs.Where(e => e != target.NodeID && before.Nodes.IndicesContains(e)))
                    {
                        var curId = Nodes.FindIndex(n => n.Id == evId || (n.Role == before.Nodes[evId].Role && n.Label == before.Nodes[evId].Label));
                        if (curId >= 0)
                        {
                            AddTargetContext(curId, "ACTION CONTROL CONTEXT", before);
                        }
                    }

                    if (Nodes[matchId].Parent.HasValue)
                    {
                        AddTargetContext(Nodes[matchId].Parent.Value, "ACTION CONTROL PARENT", before);
                    }
                }
                else
                {
                    AppendRow($"{targetBefore}\nDispatched control is absent from this observation. Identify the resulting object separately from the control that produced it.");
                }
            }

            // Collection changes
            foreach (var node in Nodes.Where(n => n.Visible && (n.Role is "AXTable" or "AXList" or "AXOutline" or "AXGrid")))
            {
                var oldMatch = before.Nodes.FirstOrDefault(b => b.Id == node.Id || (b.Role == node.Role && b.Label == node.Label));
                if (oldMatch != null && oldMatch.ChildCount.HasValue && node.ChildCount.HasValue && oldMatch.ChildCount != node.ChildCount)
                {
                    AppendRow($"SAME NATIVE COLLECTION BEFORE: {before.StateLine(oldMatch, valueLimit)}\nSAME NATIVE COLLECTION AFTER: {StateLine(node, valueLimit)}");
                    included.Add(node.Id);
                    paired.Add(node.Id);
                }
            }
        }

        var winNode = Nodes.FirstOrDefault(n => n.Role == "AXWindow");
        if (winNode != null) AddNode(winNode.Id, "CURRENT WINDOW: ");

        if (before != null && target != null)
        {
            var matchId = Nodes.FindIndex(n => n.Id == target.NodeID);
            if (matchId >= 0 && Nodes[matchId].Parent.HasValue)
            {
                var p = Nodes[matchId].Parent.Value;
                foreach (var neighbor in Nodes.Where(n => n.Parent == p || n.Parent == matchId).Take(12))
                {
                    AddTargetContext(neighbor.Id, "ACTION CONTROL NEIGHBOR", before);
                }
            }
        }

        foreach (var node in Nodes.Where(n => n.Focused || n.Selected))
        {
            AddNode(node.Id, "CURRENT FOCUSED/SELECTED: ");
        }

        foreach (var node in Nodes.Where(n => n.Visible && (n.Role is "AXTable" or "AXList" or "AXOutline" or "AXGrid")))
        {
            AddNode(node.Id, "CURRENT COLLECTION: ");
            foreach (var child in Nodes.Where(c => c.Visible && c.Parent == node.Id).Take(4))
            {
                AddNode(child.Id, "CURRENT COLLECTION ITEM: ");
            }
        }

        if (before != null)
        {
            AppendRow("Unpaired controls below are newly observed; their individual creation is unknown. Native window inventory comparisons are independent of this tree coverage limit.");
            foreach (var node in Nodes.Where(n => n.Visible && !paired.Contains(n.Id)))
            {
                var oldMatch = before.Nodes.FirstOrDefault(b => b.Id == node.Id || (b.Role == node.Role && b.Label == node.Label));
                if (oldMatch != null)
                {
                    if (StateLine(node, valueLimit) != before.StateLine(oldMatch, valueLimit))
                    {
                        AppendRow($"SAME NATIVE CONTROL BEFORE: {before.StateLine(oldMatch, valueLimit)}\nSAME NATIVE CONTROL AFTER: {StateLine(node, valueLimit)}");
                        included.Add(node.Id);
                    }
                }
                else if (!string.IsNullOrEmpty(node.Label) || !string.IsNullOrEmpty(node.Value))
                {
                    AddNode(node.Id, "CURRENT UNPAIRED CONTROL: ");
                }
            }
        }

        var actionable = new HashSet<int>(AXGrouping.Candidates(this).Select(c => c.NodeID));
        foreach (var node in Nodes.Where(n => n.Visible && actionable.Contains(n.Id)))
        {
            AddNode(node.Id, "CURRENT CONTROL: ");
        }

        var sbResult = new StringBuilder();
        int omitted = 0;
        int budget = Math.Max(0, limit - 100);

        foreach (var originalRow in rows)
        {
            var count = repeats.GetValueOrDefault(originalRow, 1);
            var row = originalRow + (count > 1 ? $" | equivalent evidence rows={count}" : "");
            if (sbResult.Length + row.Length + 1 > budget)
            {
                omitted++;
                continue;
            }
            sbResult.AppendLine(row);
        }

        if (omitted > 0)
        {
            sbResult.AppendLine($"Evidence budget reached; {omitted} rows omitted. Missing evidence is unknown.");
        }

        var text = sbResult.ToString();
        return text.Length > limit ? text.Substring(0, limit) : text;
    }

    public static string Clipped(string text, int limit) =>
        string.IsNullOrEmpty(text) || text.Length <= limit ? text : text.Substring(0, limit) + "…";
}

internal static class ListExtensions
{
    public static bool IndicesContains<T>(this IList<T> list, int index) => index >= 0 && index < list.Count;
}
