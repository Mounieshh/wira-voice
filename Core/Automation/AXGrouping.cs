using System;
using System.Collections.Generic;
using System.Linq;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Grouping and interactive candidate extraction from accessibility snapshots.
/// Exact port of Computah's AXGrouping.swift.
/// </summary>
public static class AXGrouping
{
    private static readonly HashSet<string> Regions =
    [
        "AXToolbar", "AXMenu", "AXMenuBar", "AXTabGroup", "AXList", "AXTable",
        "AXOutline", "AXScrollArea", "AXSplitGroup", "AXSheet", "AXDialog", "AXPopover",
        "AXWebArea", "AXWindow", "AXGroup", "AXLandmark", "AXNavigation"
    ];

    private static readonly HashSet<string> Editable =
    [
        "AXTextField", "AXTextArea", "AXSearchField", "AXComboBox"
    ];

    public static List<AXCandidate> Candidates(AXSnapshot snapshot, bool includeSecondary = false)
    {
        int? EnclosingRow(AXNode node)
        {
            var parent = node.Parent;
            while (parent.HasValue && snapshot.Nodes.IndicesContains(parent.Value))
            {
                var ancestor = snapshot.Nodes[parent.Value];
                if (ancestor.Role == "AXRow") return parent.Value;
                if (ancestor.Role is "AXTable" or "AXList" or "AXOutline") return null;
                parent = ancestor.Parent;
            }
            return null;
        }

        var rowText = new Dictionary<int, List<string>>();
        var rowTextIDs = new Dictionary<int, List<int>>();

        foreach (var node in snapshot.Nodes.Where(n => n.Visible && (n.Role is "AXStaticText" or "AXHeading" or "AXLink")))
        {
            var row = EnclosingRow(node);
            if (!row.HasValue) continue;

            var text = FirstNonEmpty(node.Label, node.Value);
            if (!string.IsNullOrEmpty(text))
            {
                if (!rowText.TryGetValue(row.Value, out var list))
                {
                    list = [];
                    rowText[row.Value] = list;
                }
                if (!rowTextIDs.TryGetValue(row.Value, out var idList))
                {
                    idList = [];
                    rowTextIDs[row.Value] = idList;
                }

                if (list.Count < 6 && !list.Contains(text))
                {
                    list.Add(text);
                    idList.Add(node.Id);
                }
            }
        }

        var candidates = new List<AXCandidate>();

        foreach (var node in snapshot.Nodes)
        {
            if (!node.Enabled || !node.Visible) continue;

            var hasPointerArea = node.Frame.HasValue && node.Frame.Value.Width >= 2 && node.Frame.Value.Height >= 2;
            var actions = new List<AXOperation>();

            if (Editable.Contains(node.Role))
            {
                actions.Add(AXOperation.TypeText);
                if (!string.IsNullOrEmpty(node.Value)) actions.Add(AXOperation.ReplaceText);
                if (node.Focused && node.Subrole != "AXSecureTextField") actions.Add(AXOperation.PressReturn);
            }

            if (includeSecondary && (node.Role is "AXScrollArea" or "AXWebArea"))
            {
                actions.Add(AXOperation.ScrollUp);
                actions.Add(AXOperation.ScrollDown);
            }

            if (node.Actions.Contains("AXPress") && hasPointerArea)
            {
                actions.Add(AXOperation.PressClick);
            }

            if ((node.Role is "AXSlider" or "AXIncrementor") &&
                (node.WritableValue || (node.Actions.Contains("AXIncrement") && node.Actions.Contains("AXDecrement"))))
            {
                actions.Add(AXOperation.SetNumber);
            }

            if (node.WritableSelection && node.Actions.Count > 0)
            {
                if (includeSecondary) actions.Add(AXOperation.Select);
                else if (hasPointerArea && !actions.Contains(AXOperation.PressClick)) actions.Add(AXOperation.PressClick);
            }

            if (node.Actions.Contains("AXShowMenu") &&
                ((includeSecondary && node.Role is not ("AXToolbar" or "AXWindow" or "AXGroup" or "AXStaticText" or "AXImage")) ||
                 (actions.Count == 0 && (node.Role is "AXMenuButton" or "AXPopUpButton"))))
            {
                actions.Add(AXOperation.ShowMenu);
            }

            if (includeSecondary && node.Actions.Contains("AXIncrement")) actions.Add(AXOperation.Increment);
            if (includeSecondary && node.Actions.Contains("AXDecrement")) actions.Add(AXOperation.Decrement);

            if (actions.Count == 0 && hasPointerArea && (node.Role is "AXLink" or "AXButton"))
            {
                actions.Add(AXOperation.PressClick);
            }

            if (actions.Count == 0) continue;

            var descendants = new HashSet<int> { node.Id };
            var parts = new List<string>();
            var bindingIDs = new List<int> { node.Id };

            var ancestor = node.Parent;
            while (ancestor.HasValue && snapshot.Nodes.IndicesContains(ancestor.Value))
            {
                if (!string.IsNullOrEmpty(snapshot.Nodes[ancestor.Value].Document))
                {
                    bindingIDs.Add(ancestor.Value);
                }
                ancestor = snapshot.Nodes[ancestor.Value].Parent;
            }

            foreach (var child in snapshot.Nodes.Where(c => c.Depth > node.Depth && c.Depth <= node.Depth + 3))
            {
                if (!child.Parent.HasValue || !descendants.Contains(child.Parent.Value)) continue;
                descendants.Add(child.Id);
                var text = string.IsNullOrEmpty(child.Label) ? child.Value : child.Label;
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                    bindingIDs.Add(child.Id);
                }
                if (parts.Count == 6) break;
            }

            var childText = string.Join("; ", parts);
            var label = FirstNonEmpty(node.Label, node.Value, childText);
            var name = string.IsNullOrEmpty(label) ? node.Role : $"{node.Role}: {label}";
            var owner = RegionOwner(node, snapshot.Nodes);
            var region = snapshot.Nodes[owner];

            var context = new List<string> { $"region={region.Role} {region.Label}" };
            if (!string.IsNullOrEmpty(node.Subrole)) context.Add($"subrole={node.Subrole}");
            if (!string.IsNullOrEmpty(node.Help) && node.Help != node.Label) context.Add($"purpose={node.Help}");
            if (!string.IsNullOrEmpty(node.Value) && node.Value != label) context.Add($"value={AXSnapshot.Clipped(node.Value, 160)}");

            var nearbyNodes = snapshot.Nodes
                .Where(n => n.Parent == node.Parent && n.Id != node.Id && (n.Role is "AXStaticText" or "AXHeading"))
                .Take(4)
                .ToList();
            var nearby = nearbyNodes.Select(n => FirstNonEmpty(n.Label, n.Value)).Where(s => !string.IsNullOrEmpty(s)).ToList();
            bindingIDs.AddRange(nearbyNodes.Select(n => n.Id));
            if (nearby.Count > 0) context.Add("nearby text=" + AXSnapshot.Clipped(string.Join("; ", nearby), 240));

            var row = EnclosingRow(node);
            if (row.HasValue && rowText.TryGetValue(row.Value, out var rText) && rText.Count > 0)
            {
                context.Add("item text=" + AXSnapshot.Clipped(string.Join("; ", rText), 240));
                bindingIDs.Add(row.Value);
                if (rowTextIDs.TryGetValue(row.Value, out var rIDs))
                {
                    bindingIDs.AddRange(rIDs);
                }
            }

            if (node.Focused) context.Add("focused");
            if (node.Selected) context.Add("selected");
            if (node.Minimum.HasValue && node.Maximum.HasValue && node.Maximum.Value > node.Minimum.Value)
            {
                context.Add($"range={node.Minimum.Value}...{node.Maximum.Value}");
            }

            var contextStr = string.Join("; ", context);
            var uniqueBindingIDs = bindingIDs.Distinct().OrderBy(x => x).ToList();

            foreach (var action in actions)
            {
                var opWire = action.ToWireString();
                var candidate = new AXCandidate(
                    id: $"n{node.Id}_{opWire}",
                    nodeID: node.Id,
                    operation: action,
                    description: $"{AXSnapshot.Clipped(name, 260)} [{opWire}]; {contextStr}",
                    groupID: owner,
                    bindingNodeIDs: uniqueBindingIDs
                );
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    public static List<AXGroup> Groups(AXSnapshot snapshot, List<AXCandidate> candidates)
    {
        var byOwner = candidates.GroupBy(c => c.GroupID).ToDictionary(g => g.Key, g => g.ToList());
        var groups = new List<AXGroup>();

        foreach (var owner in byOwner.Keys.OrderBy(k => k))
        {
            var node = snapshot.Nodes[owner];
            var members = byOwner[owner];
            var name = string.IsNullOrEmpty(node.Label) ? node.Role : $"{node.Role}: {node.Label}";
            var seen = new HashSet<string>();
            var examples = new List<string>();

            foreach (var item in members)
            {
                if (seen.Add(item.Id)) examples.Add(item.Description);
                if (examples.Count == 3) break;
            }

            var title = $"{name} — {members.Count} controls: {string.Join("; ", examples)}";
            groups.Add(new AXGroup(owner, AXSnapshot.Clipped(title, 300), members));
        }

        return groups;
    }

    private static int RegionOwner(AXNode node, List<AXNode> nodes)
    {
        var cursor = node.Parent;
        while (cursor.HasValue && nodes.IndicesContains(cursor.Value))
        {
            var parent = nodes[cursor.Value];
            if (Regions.Contains(parent.Role) &&
                (parent.Role != "AXGroup" || (parent.Role == "AXGroup" && !string.IsNullOrEmpty(parent.Label))))
            {
                return cursor.Value;
            }
            cursor = parent.Parent;
        }
        return 0;
    }

    private static string FirstNonEmpty(params string[] items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item)) return item.Trim();
        }
        return string.Empty;
    }
}
