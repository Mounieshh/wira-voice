using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Diagnostics;

namespace WiraVoice.Core.Language;

public static class LanguagePrompts
{
    private sealed class Document
    {
        public Dictionary<string, string> Shared { get; set; } = [];
        public Dictionary<string, string> Prompts { get; set; } = [];
    }

    private static readonly Dictionary<string, string> Prompts;

    static LanguagePrompts()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "Prompts", "language.json");
        if (!File.Exists(path))
        {
            // Try relative to workspace
            path = Path.Combine(Directory.GetCurrentDirectory(), "Core", "Prompts", "language.json");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing language prompt resource at {path}");
        }

        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<Document>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to parse language.json");

        Prompts = new Dictionary<string, string>();
        foreach (var (key, template) in doc.Prompts)
        {
            var expanded = template;
            foreach (var (fragKey, fragVal) in doc.Shared)
            {
                expanded = expanded.Replace("{{" + fragKey + "}}", fragVal);
            }
            Prompts[key] = expanded;
        }
    }

    public static string Text(string key) =>
        Prompts.TryGetValue(key, out var val) ? val : throw new KeyNotFoundException($"Missing prompt: {key}");

    public static string? TryGetText(string key) =>
        Prompts.TryGetValue(key, out var val) ? val : null;
}

public readonly record struct SourceToken(string Text, SensitiveText.TextRange Range, string ModelText)
{
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{M}\p{N}]+|[^\s]", RegexOptions.Compiled);

    public static List<SourceToken> Read(string source, int offset = 0)
    {
        if (offset < 0 || offset > source.Length)
        {
            throw new JevException.Invalid("Invalid source boundary.");
        }

        var protectedSpans = SensitiveText.ProtectedRanges(source);
        var matches = TokenRegex.Matches(source.Substring(offset));
        var tokens = new List<SourceToken>();

        foreach (Match match in matches)
        {
            var range = new SensitiveText.TextRange(offset + match.Index, match.Length);
            var isProtected = protectedSpans.Any(p => p.Intersects(range));
            tokens.Add(new SourceToken(match.Value, range, isProtected ? "[REDACTED CREDENTIAL]" : match.Value));
        }

        return tokens;
    }
}

public enum GoalRelationship { Replace, Revise, Resume, Append, Cancel, Unclear }

public enum CommandRoute { Application, Navigation, Controls }

public enum ValueFormat { Absent, Literal, Address, Number, Percent }

public sealed class CommandClause : IEquatable<CommandClause>
{
    public string Text { get; }
    public int StartUTF16 { get; }
    public int EndUTF16 { get; }

    public CommandClause(string text, int startUTF16, int endUTF16)
    {
        Text = text;
        StartUTF16 = startUTF16;
        EndUTF16 = endUTF16;
    }

    public string ModelText(string source) =>
        SensitiveText.Redact(source, new SensitiveText.TextRange(StartUTF16, EndUTF16 - StartUTF16));

    public bool Equals(CommandClause? other) =>
        other != null && StartUTF16 == other.StartUTF16 && EndUTF16 == other.EndUTF16 && Text == other.Text;

    public override bool Equals(object? obj) => obj is CommandClause other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(StartUTF16, EndUTF16, Text);
}

public sealed class InterpretedCommand
{
    public string Source { get; }
    public CommandClause Clause { get; }
    public CommandRoute? Route { get; }
    public InstalledApplication? App { get; }
    public ValueFormat Format { get; }
    public SensitiveText.TextRange? ValueRange { get; }
    public string? Value { get; set; }
    public int? FollowupStartUTF16 { get; set; }
    public GoalRelationship? Relationship { get; set; }
    public string? ValueSource { get; set; }
    public string? ActionID { get; set; }
    public bool ActivatesApp { get; set; }
    public bool ValueIsNormalized { get; set; } = true;

    public bool CanType => Format is ValueFormat.Literal or ValueFormat.Address;
    public bool CanSetNumber => Format is ValueFormat.Number or ValueFormat.Percent;

    public string? ModelValue
    {
        get
        {
            if (Value == null || !ValueRange.HasValue) return Value;
            var original = ValueSource ?? Source;
            var protectedSpans = SensitiveText.ProtectedRanges(original);
            return protectedSpans.Any(p => p.Intersects(ValueRange.Value))
                ? SensitiveText.Redact(original, ValueRange.Value)
                : Value;
        }
    }

    public InterpretedCommand(
        string source,
        CommandClause clause,
        CommandRoute? route,
        InstalledApplication? app,
        ValueFormat format,
        SensitiveText.TextRange? valueRange,
        string? value)
    {
        Source = source;
        Clause = clause;
        Route = route;
        App = app;
        Format = format;
        ValueRange = valueRange;
        Value = value;
    }
}

public sealed class CommandLanguage
{
    public JevSelector Selector { get; }

    public CommandLanguage(JevSelector selector)
    {
        Selector = selector;
    }

    private struct ValueSelection
    {
        public string? Format;
        public string? First;
        public string? Last;

        public ValueSelection(JevJudgments judgments)
        {
            Format = judgments.Answer("format");
            First = judgments.Answer("value_start");
            Last = judgments.Answer("value_end");
        }
    }

    public async Task<InterpretedCommand> InterpretAsync(
        string source,
        int offset = 0,
        List<InstalledApplication>? apps = null,
        List<string>? progress = null,
        List<AXCandidate>? controls = null,
        AXSnapshot? observation = null,
        CommandClause? fixedClause = null,
        List<string>? revisions = null,
        Dictionary<string, object?>? relationshipContext = null,
        Dictionary<string, object?>? conversationContext = null,
        AXSnapshot? continuation = null,
        CancellationToken ct = default)
    {
        apps ??= [];
        progress ??= [];
        controls ??= [];
        revisions ??= [];
        conversationContext ??= [];

        if (fixedClause != null)
        {
            if (fixedClause.StartUTF16 != offset || fixedClause.EndUTF16 > source.Length ||
                fixedClause.EndUTF16 <= offset ||
                source.Substring(offset, fixedClause.EndUTF16 - offset) != fixedClause.Text)
            {
                throw new JevException.Invalid("Invalid active source binding.");
            }
        }

        var tokens = SourceToken.Read(source, offset)
            .Where(t => fixedClause == null || t.Range.Max <= fixedClause.EndUTF16)
            .ToList();

        if (tokens.Count == 0) throw new JevException.Invalid("No command.");
        if (tokens.Count > 254)
        {
            throw new JevException.Invalid("This utterance exceeds the source-selection limit. Please use a shorter request.");
        }

        var endOffset = fixedClause?.EndUTF16 ?? source.Length;
        var state = new Dictionary<string, object?>
        {
            ["original_request"] = source,
            ["consumed_utf16"] = offset,
            ["verified_progress"] = progress,
            ["remaining_request"] = SensitiveText.Redact(source, new SensitiveText.TextRange(offset, endOffset - offset)),
            ["source_tokens"] = tokens.Select((t, i) => new Dictionary<string, object?>
            {
                ["id"] = $"t{i}",
                ["text"] = t.ModelText,
                ["start_utf16"] = t.Range.Location,
                ["end_utf16"] = t.Range.Max
            }).ToList()
        };

        if (conversationContext.Count > 0)
        {
            state["conversation_context"] = conversationContext;
        }

        var positions = tokens.Select((t, i) => new JevOption($"t{i}", $"Source token {i}: {t.ModelText}")).ToList();
        var valueTokens = new Dictionary<string, (string Source, string SourceID, SourceToken Token, int? OriginalIndex)>();

        for (int i = 0; i < tokens.Count; i++)
        {
            valueTokens[$"t{i}"] = (source, "original", tokens[i], i);
        }

        var revisionState = new List<Dictionary<string, object?>>();
        for (int r = 0; r < revisions.Count; r++)
        {
            var revText = revisions[r];
            var revTokens = SourceToken.Read(revText);
            revisionState.Add(new Dictionary<string, object?>
            {
                ["source_id"] = $"revision{r}",
                ["original_text"] = revText,
                ["tokens"] = revTokens.Select((t, idx) => new Dictionary<string, object?>
                {
                    ["id"] = $"r{r}t{idx}",
                    ["text"] = t.ModelText
                }).ToList()
            });

            for (int i = 0; i < revTokens.Count; i++)
            {
                var id = $"r{r}t{i}";
                positions.Add(new JevOption(id, $"Revision {r} token {i}: {revTokens[i].ModelText}"));
                valueTokens[id] = (revText, $"revision{r}", revTokens[i], null);
            }
        }

        if (positions.Count > 254)
        {
            throw new JevException.Invalid("Source and revision spans exceed the selection limit.");
        }

        if (revisions.Count > 0)
        {
            state["instruction_revisions"] = revisionState;
        }

        var ends = new List<JevOption>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (fixedClause != null && i != tokens.Count - 1) continue;
            var nextInfo = (i + 1 < tokens.Count) ? $"; next: {tokens[i + 1].ModelText}" : "; end of request";
            ends.Add(new JevOption($"t{i}", $"After token {i}: {tokens[i].ModelText}{nextInfo}"));
        }

        JevQuestion Question(string key, List<JevOption> options)
        {
            var instructions = (key == "action" ? "" : LanguagePrompts.Text("premise") + "\n") + LanguagePrompts.Text(key);
            var noneDesc = key == "action" ? LanguagePrompts.Text("action_none") : null;
            var budget = key == "action" ? 64 : 254;
            return new JevQuestion(instructions, options, noneDesc, budget, key: key);
        }

        List<JevQuestion> ValueQuestions() =>
        [
            Question("format", Enum.GetValues<ValueFormat>().Select(f =>
            {
                var raw = f.ToString().ToLowerInvariant();
                return new JevOption(raw, LanguagePrompts.Text("format_" + raw));
            }).ToList()),
            Question("value_start", positions),
            Question("value_end", positions)
        ];

        JevOption OperationOption(AXCandidate candidate)
        {
            var opWire = candidate.Operation.ToWireString();
            var desc = LanguagePrompts.TryGetText("operation_" + opWire);
            var prefix = !string.IsNullOrEmpty(desc) ? desc + "\n" : "";
            return new JevOption(candidate.Id, prefix + candidate.Description);
        }

        Dictionary<string, object?> ControlFields(AXNode node) => new()
        {
            ["role"] = node.Role,
            ["label"] = node.Label,
            ["current_value"] = node.Value,
            ["focused"] = node.Focused
        };

        if (fixedClause != null)
        {
            state["active_instruction"] = SensitiveText.Redact(source, new SensitiveText.TextRange(fixedClause.StartUTF16, fixedClause.EndUTF16 - fixedClause.StartUTF16));
        }

        state["observed_scene"] = observation?.SelectionEvidence ?? "No AX observation available.";

        if (observation != null)
        {
            state["coverage"] = observation.Coverage
                .GroupBy(c => c.Reason.ToString().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => (object?)g.Count());

            var primaryIds = new HashSet<string>(controls.Select(c => c.Id));
            var secondary = AXGrouping.Candidates(observation, includeSecondary: true)
                .Where(c => !primaryIds.Contains(c.Id))
                .ToList();

            state["secondary_capabilities"] = secondary
                .GroupBy(c => c.GroupID)
                .OrderBy(g => g.Key)
                .Select(g => new Dictionary<string, object?>
                {
                    ["region"] = observation.Nodes.IndicesContains(g.Key) ? observation.Nodes[g.Key].Label : "",
                    ["count"] = g.Count(),
                    ["operations"] = g.Select(c => c.Operation.ToWireString()).Distinct().OrderBy(x => x).ToList()
                })
                .ToList();
        }

        var appOptions = apps.Select((app, i) =>
        {
            var aliasDiff = app.Aliases.Where(a => !string.Equals(a, app.Name, StringComparison.OrdinalIgnoreCase));
            return new JevOption($"app{i}", $"Activate installed app: {app.Name}; aliases={string.Join(", ", aliasDiff)}; bundle={app.BundleID}");
        }).ToList();

        var byNode = controls.GroupBy(c => c.NodeID).ToDictionary(g => g.Key, g => g.ToList());
        var targetGroups = byNode.Where(kvp => kvp.Value.Count > 1)
            .ToDictionary(kvp => $"target_n{kvp.Key}", kvp => kvp.Value);

        var seenNodes = new HashSet<int>();
        var controlOptions = new List<JevOption>();

        foreach (var c in controls)
        {
            if (!seenNodes.Add(c.NodeID)) continue;
            var members = byNode[c.NodeID];
            if (members.Count == 1)
            {
                controlOptions.Add(new JevOption(c.Id, c.Description));
            }
            else
            {
                controlOptions.Add(new JevOption($"target_n{c.NodeID}",
                    $"Interact with this observed control. Available operations: {string.Join("; ", members.Select(m => m.Description))}"));
            }
        }

        controlOptions.Add(new JevOption("inspect_secondary", LanguagePrompts.Text("action_inspect_secondary")));
        controlOptions.Add(new JevOption("reobserve", LanguagePrompts.Text("action_reobserve")));
        controlOptions.Add(new JevOption("already_satisfied", LanguagePrompts.Text("action_already_satisfied")));

        var questions = new List<JevQuestion>();
        if (fixedClause == null) questions.Add(Question("boundary", ends));

        questions.Add(Question("route", [
            new JevOption("application", LanguagePrompts.Text("route_application")),
            new JevOption("navigation", LanguagePrompts.Text("route_navigation")),
            new JevOption("controls", LanguagePrompts.Text("route_controls"))
        ]));

        questions.AddRange(ValueQuestions());

        questions.Add(Question("application", appOptions.Count == 0
            ? [new JevOption("unavailable", "No application catalog available")]
            : appOptions));

        var actionQ = Question("action", controlOptions);
        var observedTargetIDs = new HashSet<string>(controls.Select(c => byNode[c.NodeID].Count > 1 ? $"target_n{c.NodeID}" : c.Id));
        actionQ.ObservedControls = controlOptions
            .Where(o => observedTargetIDs.Contains(o.Id))
            .ToDictionary(o => o.Id, o => o.Description);

        questions.Add(actionQ);

        var operationQuestions = new Dictionary<string, string>();
        var operationTargets = new Dictionary<string, object?>();

        foreach (var opt in controlOptions.Where(o => targetGroups.ContainsKey(o.Id)).Take(2))
        {
            if (!targetGroups.TryGetValue(opt.Id, out var members) || members.Count == 0 ||
                observation == null || !observation.Nodes.IndicesContains(members[0].NodeID))
            {
                continue;
            }

            var node = observation.Nodes[members[0].NodeID];
            operationTargets[opt.Id] = new Dictionary<string, object?>
            {
                ["selected_target"] = members.Select(m => m.Description).ToList(),
                ["selected_control"] = ControlFields(node)
            };

            var premise = LanguagePrompts.Text("operation_premise").Replace("{target}", opt.Id);
            operationQuestions[opt.Id] = "operation_" + opt.Id;
            questions.Add(new JevQuestion(
                premise + "\n" + LanguagePrompts.Text("operation"),
                members.Select(OperationOption),
                key: "operation_" + opt.Id));
        }

        if (operationTargets.Count > 0)
        {
            state["operation_targets"] = operationTargets;
        }

        var relationshipQuestions = new List<JevQuestion>();
        if (relationshipContext != null)
        {
            relationshipQuestions.Add(new JevQuestion(
                LanguagePrompts.Text("relationship"),
                Enum.GetValues<GoalRelationship>().Select(r =>
                {
                    var raw = r.ToString().ToLowerInvariant();
                    return new JevOption(raw, LanguagePrompts.Text("relationship_" + raw));
                }),
                key: "relationship"));

            relationshipQuestions.Add(new JevQuestion(
                LanguagePrompts.Text("append_start"),
                tokens.Select((t, i) => new JevOption($"t{i}", $"Start new work at source token {i}: {t.ModelText}")),
                key: "append_start"));
        }

        if (continuation != null)
        {
            state["prior_binding"] = continuation.BindingEvidence;
            state["current_binding"] = observation?.BindingEvidence ?? [];
            questions.Add(new JevQuestion(
                LanguagePrompts.Text("continuation_binding"),
                new[] { "app_scope", "fresh_target", "same_document", "changed_or_unknown" }
                    .Select(s => new JevOption(s, LanguagePrompts.Text("continuation_" + s))),
                key: "continuation_binding"));
        }

        var relationState = new Dictionary<string, object?>
        {
            ["original_request"] = source,
            ["source_tokens"] = state.GetValueOrDefault("source_tokens"),
            ["prior_goal"] = relationshipContext ?? [],
            ["observed_scene"] = observation?.SelectionEvidence ?? "Unknown"
        };

        using var actionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var actionTask = Selector.JudgeAsync(state, questions, actionCts.Token);

        JevJudgments relationAnswers;
        try
        {
            relationAnswers = relationshipQuestions.Count > 0
                ? await Selector.JudgeAsync(relationState, relationshipQuestions, ct).ConfigureAwait(false)
                : new JevJudgments([]);
        }
        catch
        {
            actionCts.Cancel();
            throw;
        }

        GoalRelationship? relationship = null;
        if (relationshipContext != null)
        {
            var rawRel = relationAnswers.Answer("relationship") ?? "unclear";
            relationship = Enum.TryParse<GoalRelationship>(rawRel, true, out var gr) ? gr : GoalRelationship.Unclear;
        }

        if (relationship.HasValue && relationship.Value != GoalRelationship.Replace)
        {
            actionCts.Cancel();
            try { await actionTask.ConfigureAwait(false); } catch { }

            int? followupStart = null;
            if (relationship.Value == GoalRelationship.Append)
            {
                var appendAnswer = relationAnswers.Answer("append_start");
                if (appendAnswer == null)
                {
                    var boundState = new Dictionary<string, object?>(relationState) { ["selected_relationship"] = "append" };
                    var repair = await Selector.JudgeAsync(boundState, relationshipQuestions.Where(q => q.Key == "append_start").ToList(), ct).ConfigureAwait(false);
                    appendAnswer = repair.Answer("append_start");
                }
                var tokenIdx = tokens.FindIndex(t => $"t{tokens.IndexOf(t)}" == appendAnswer);
                if (tokenIdx >= 0) followupStart = tokens[tokenIdx].Range.Location;
            }

            var end = fixedClause?.EndUTF16 ?? source.Length;
            return new InterpretedCommand(source,
                new CommandClause(source.Substring(offset, end - offset), offset, end),
                null, null, ValueFormat.Absent, null, null)
            {
                Relationship = relationship,
                FollowupStartUTF16 = followupStart
            };
        }

        var answers = await actionTask.ConfigureAwait(false);

        if (continuation != null)
        {
            var contBinding = answers.Answer("continuation_binding") ?? "";
            if (contBinding is not ("app_scope" or "fresh_target" or "same_document"))
            {
                throw AXFailureException.Unavailable("The earlier document or item cannot be uniquely identified. Please identify the target before continuing.");
            }
        }

        int endIndex;
        if (fixedClause != null)
        {
            endIndex = tokens.Count - 1;
        }
        else
        {
            var bAnswer = answers.Answer("boundary");
            endIndex = tokens.FindIndex(t => $"t{tokens.IndexOf(t)}" == bAnswer);
            if (endIndex < 0) throw new JevException.Invalid("The instruction boundary is unclear.");
        }

        var clauseEnd = fixedClause?.EndUTF16 ?? (endIndex + 1 < tokens.Count ? tokens[endIndex + 1].Range.Location : source.Length);
        var clause = new CommandClause(source.Substring(offset, clauseEnd - offset), offset, clauseEnd);

        var routeStr = answers.Answer("route");
        CommandRoute? route = Enum.TryParse<CommandRoute>(routeStr, true, out var cr) ? cr : null;

        var appAnswer = answers.Answer("application");
        var appIndex = appOptions.FindIndex(o => o.Id == appAnswer);
        var app = appIndex >= 0 ? apps[appIndex] : null;

        var needsActivation = route == CommandRoute.Application || (route == CommandRoute.Controls && app != null && (observation == null ||
            (!string.IsNullOrEmpty(observation.BundleID) ? !string.Equals(observation.BundleID, app.BundleID, StringComparison.OrdinalIgnoreCase) : !string.Equals(observation.AppName, app.Name, StringComparison.OrdinalIgnoreCase))));

        string? actionID = route == null ? null : needsActivation ? (appIndex >= 0 ? $"app{appIndex}" : null) :
            route == CommandRoute.Navigation ? "navigate" : answers.Answer("action");

        var valueAnswers = new ValueSelection(answers);
        var conditionalState = new Dictionary<string, object?>(state)
        {
            ["active_instruction"] = SensitiveText.Redact(source, new SensitiveText.TextRange(clause.StartUTF16, clause.EndUTF16 - clause.StartUTF16))
        };

        var selectedTarget = actionID;
        if (actionID != null && operationQuestions.TryGetValue(actionID, out var opKey))
        {
            actionID = answers.Answer(opKey);
            if (operationTargets.TryGetValue(selectedTarget!, out var tgt) && tgt is Dictionary<string, object?> dict)
            {
                conditionalState["selected_control"] = dict.GetValueOrDefault("selected_control");
                conditionalState["selected_target"] = dict.GetValueOrDefault("selected_target");
            }
        }
        else if (actionID != null && targetGroups.TryGetValue(actionID, out var members))
        {
            conditionalState["selected_target"] = members.Select(m => m.Description).ToList();
            if (observation != null && members.Count > 0 && observation.Nodes.IndicesContains(members[0].NodeID))
            {
                conditionalState["selected_control"] = ControlFields(observation.Nodes[members[0].NodeID]);
            }
            var followUpQuestions = new List<JevQuestion>
            {
                new(LanguagePrompts.Text("operation"), members.Select(OperationOption), key: "operation")
            };
            followUpQuestions.AddRange(ValueQuestions());

            var followup = await Selector.JudgeAsync(conditionalState, followUpQuestions, ct).ConfigureAwait(false);
            actionID = followup.Answer("operation");
            valueAnswers = new ValueSelection(followup);
        }

        // Retry if selected target has no applicable operation
        if (actionID == null && selectedTarget != null && targetGroups.TryGetValue(selectedTarget, out var rejected))
        {
            var rejectedNodes = new HashSet<int>(rejected.Select(r => r.NodeID));
            foreach (var (tgt, key) in operationQuestions)
            {
                if (answers.Answer(key) == null && targetGroups.TryGetValue(tgt, out var rList))
                {
                    foreach (var m in rList) rejectedNodes.Add(m.NodeID);
                }
            }

            var alternatives = controls.Where(c => !rejectedNodes.Contains(c.NodeID)).ToList();
            if (alternatives.Count > 0)
            {
                conditionalState.Remove("selected_control");
                conditionalState.Remove("selected_target");
                conditionalState.Remove("operation_targets");

                var repaired = await Selector.JudgeAsync(conditionalState, [
                    Question("action", alternatives.Select(c => new JevOption(c.Id, c.Description)).ToList())
                ], ct).ConfigureAwait(false);

                actionID = repaired.Answer("action");
                valueAnswers = default;
            }
        }

        var format = ValueFormat.Absent;
        SensitiveText.TextRange? valueRange = null;
        string? value = null;

        var selectedControl = controls.FirstOrDefault(c => c.Id == actionID);
        var requiresValue = route == CommandRoute.Navigation || (selectedControl != null &&
            (selectedControl.Operation is AXOperation.TypeText or AXOperation.ReplaceText or AXOperation.SetNumber));

        if (requiresValue)
        {
            if (string.IsNullOrEmpty(valueAnswers.Format) || valueAnswers.Format == "absent")
            {
                conditionalState["selected_target"] = selectedControl?.Description ?? "Navigate to requested address";
                var resolved = await Selector.JudgeAsync(conditionalState, ValueQuestions(), ct).ConfigureAwait(false);
                valueAnswers = new ValueSelection(resolved);
            }

            if (string.IsNullOrEmpty(valueAnswers.Format) ||
                !Enum.TryParse<ValueFormat>(valueAnswers.Format, true, out format))
            {
                throw new JevException.Invalid("The instruction's value is unclear.");
            }

            if (format != ValueFormat.Absent)
            {
                if (string.IsNullOrEmpty(valueAnswers.First) || string.IsNullOrEmpty(valueAnswers.Last) ||
                    !valueTokens.TryGetValue(valueAnswers.First, out var first) ||
                    !valueTokens.TryGetValue(valueAnswers.Last, out var last) ||
                    first.Source != last.Source ||
                    first.Token.Range.Location > last.Token.Range.Location ||
                    (first.OriginalIndex.HasValue && first.OriginalIndex.Value > endIndex) ||
                    (last.OriginalIndex.HasValue && last.OriginalIndex.Value > endIndex))
                {
                    throw new JevException.Invalid("The selected value crosses a source or instruction boundary.");
                }

                if (first.SourceID != last.SourceID)
                {
                    throw new JevException.Invalid("Value bounds belong to different source versions.");
                }

                var range = new SensitiveText.TextRange(first.Token.Range.Location, last.Token.Range.Max - first.Token.Range.Location);
                valueRange = range;
                value = first.Source.Substring(range.Location, range.Length);
            }

            if (route == CommandRoute.Navigation && format != ValueFormat.Address)
            {
                throw new JevException.Invalid("Navigation has no complete address.");
            }
        }

        return new InterpretedCommand(source, clause, route, app, format, valueRange, value)
        {
            Relationship = relationship,
            ValueSource = valueAnswers.First != null && valueTokens.TryGetValue(valueAnswers.First, out var vt) ? vt.Source : null,
            ActionID = actionID,
            ActivatesApp = needsActivation,
            ValueIsNormalized = format is ValueFormat.Literal or ValueFormat.Absent
        };
    }

    public async Task<InterpretedCommand> ResolveValueAsync(InterpretedCommand command, CancellationToken ct = default)
    {
        if (command.ValueIsNormalized || string.IsNullOrEmpty(command.Value)) return command;

        var raw = command.Value;
        if (command.ModelValue != raw)
        {
            var locallyRepresentable = command.Format == ValueFormat.Address
                ? ValidatedUrl(raw) != null
                : double.TryParse(raw, out _);

            if (!locallyRepresentable)
            {
                throw new JevException.Invalid("A protected value needs a literal address or decimal representation before it can be used.");
            }
        }

        string normalized;
        switch (command.Format)
        {
            case ValueFormat.Address:
                normalized = await AddressAsync(raw, ct).ConfigureAwait(false);
                break;
            case ValueFormat.Number:
            case ValueFormat.Percent:
                var num = await NumberAsync(raw, ct).ConfigureAwait(false);
                normalized = num + (command.Format == ValueFormat.Percent ? "%" : "");
                break;
            case ValueFormat.Literal:
            case ValueFormat.Absent:
            default:
                return command;
        }

        command.Value = normalized;
        command.ValueIsNormalized = true;
        return command;
    }

    public async Task<string> NumberAsync(string source, CancellationToken ct = default)
    {
        if (double.TryParse(source, out var direct) && !double.IsNaN(direct) && !double.IsInfinity(direct))
        {
            return direct.ToString();
        }

        // Try basic spoken word replacements
        var words = source.ToLowerInvariant().Trim();
        var numbers = new List<double>();

        var wordMap = new Dictionary<string, double>
        {
            ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
            ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
            ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
            ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
            ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
            ["hundred"] = 100, ["thousand"] = 1000
        };

        if (wordMap.TryGetValue(words, out var directWord))
        {
            numbers.Add(directWord);
        }

        if (numbers.Count > 0)
        {
            var options = numbers.Select((n, i) => new JevOption($"v{i}", n.ToString())).ToList();
            var result = await Selector.JudgeAsync(new Dictionary<string, object?> { ["value_source"] = source }, [
                new JevQuestion(LanguagePrompts.Text("number"), options, key: "number")
            ], ct).ConfigureAwait(false);

            var ans = result.Answer("number");
            var matchIdx = options.FindIndex(o => o.Id == ans);
            if (matchIdx >= 0) return numbers[matchIdx].ToString();
        }

        throw new JevException.Invalid("The numeric expression could not be confirmed. Please state the value as a decimal number.");
    }

    private async Task<string> AddressAsync(string source, CancellationToken ct = default)
    {
        if (ValidatedUrl(source) != null) return source;

        var tokens = SourceToken.Read(source);
        var symbols = new[] { ".", "/", ":", "-", "_", "?", "=", "&", "#", "%", "+", "@", "~" };

        var options = new List<JevOption> { new("copy", "Copy this source token exactly") };
        options.AddRange(symbols.Select((s, i) => new JevOption($"s{i}", $"Address punctuation: {s}")));

        var questions = tokens.Select((t, i) => new JevQuestion(
            $"{LanguagePrompts.Text("address_symbol")}\ntarget_token={i}",
            options,
            key: $"address_{i}")).ToList();

        var state = new Dictionary<string, object?>
        {
            ["address_source"] = source,
            ["tokens"] = tokens.Select((t, i) => new Dictionary<string, object?>
            {
                ["id"] = i,
                ["text"] = t.ModelText
            }).ToList()
        };

        var result = await Selector.JudgeAsync(state, questions, ct).ConfigureAwait(false);
        var output = new System.Text.StringBuilder();

        for (int i = 0; i < tokens.Count; i++)
        {
            var ans = result.Ids.IndicesContains(i) ? result.Ids[i] : null;
            if (ans == "copy")
            {
                output.Append(tokens[i].Text);
            }
            else if (ans != null && ans.StartsWith('s') && int.TryParse(ans.Substring(1), out var symIdx) && symIdx < symbols.Length)
            {
                output.Append(symbols[symIdx]);
            }
            else
            {
                throw new JevException.Invalid("The spoken address is unclear.");
            }
        }

        var resUrl = output.ToString();
        if (ValidatedUrl(resUrl) == null)
        {
            throw new JevException.Invalid("The address is incomplete or invalid.");
        }
        return resUrl;
    }

    public static Uri? ValidatedUrl(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsWhiteSpace)) return null;

        var urlStr = source.Contains("://") ? source : "https://" + source;
        if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri)) return null;

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.')) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;

        var labels = uri.Host.Split('.');
        foreach (var label in labels)
        {
            if (string.IsNullOrEmpty(label) || label.StartsWith('-') || label.EndsWith('-'))
            {
                return null;
            }
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-'))
            {
                return null;
            }
        }

        return uri;
    }
}
