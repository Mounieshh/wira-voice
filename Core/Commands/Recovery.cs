using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Language;

namespace WiraVoice.Core.Commands;

public sealed class RecoveryResult
{
    public AXSnapshot Snapshot { get; set; }
    public List<AXCandidate> Candidates { get; set; }
    public InterpretedCommand Interpretation { get; set; }
    public int Reads { get; set; } = 0;
    public double CaptureSeconds { get; set; } = 0.0;
    public double ModelSeconds { get; set; } = 0.0;

    public RecoveryResult(AXSnapshot snapshot, List<AXCandidate> candidates, InterpretedCommand interpretation)
    {
        Snapshot = snapshot;
        Candidates = candidates;
        Interpretation = interpretation;
    }
}

public partial class CommandEngine
{
    public async Task<RecoveryResult> RecoverAsync(
        string source,
        int offset,
        InterpretedCommand interpretation,
        AXSnapshot snapshot,
        List<AXCandidate> primary,
        List<string> progress,
        HashSet<string> excluded,
        List<string> revisions,
        Dictionary<string, object?>? conversationContext = null,
        AXSnapshot? continuation = null,
        CancellationToken ct = default)
    {
        var result = new RecoveryResult(snapshot, primary, interpretation);
        var observationBinding = snapshot;
        var language = new CommandLanguage(Selector);

        List<AXCandidate> Candidates(AXSnapshot scene, bool secondary = false) =>
            AvailableCandidates(scene, excluded, includeSecondary: secondary);

        async Task Choose(List<AXCandidate> choices, AXSnapshot scene, string stage)
        {
            InputPermit.Check();
            var sw = Stopwatch.StartNew();
            var prog = new List<string>(progress)
            {
                $"Recovery scope: {stage}. Missing choices here do not prove global absence. Use a directly offered action when it advances the instruction."
            };
            var answer = await language.InterpretAsync(
                source, offset, apps: [], progress: prog,
                controls: choices, observation: scene, fixedClause: interpretation.Clause,
                revisions: revisions, conversationContext: conversationContext ?? [],
                continuation: continuation, ct: ct).ConfigureAwait(false);

            result.ModelSeconds += sw.Elapsed.TotalSeconds;
            result.Snapshot = scene;
            result.Candidates = choices;
            result.Interpretation = answer;
        }

        bool Resolved()
        {
            return result.Interpretation.Route != CommandRoute.Controls ||
                   result.Interpretation.ActionID == "already_satisfied" ||
                   result.Candidates.Any(c => c.Id == result.Interpretation.ActionID);
        }

        async Task<AXSnapshot> Read(ObservationRequest request)
        {
            InputPermit.Check();
            var sw = Stopwatch.StartNew();
            var scene = await ReadObservation(request).ConfigureAwait(false);
            result.CaptureSeconds += sw.Elapsed.TotalSeconds;
            result.Reads += 1;

            if (!scene.CanObserveAfter(observationBinding, ObservationForegroundPID()))
            {
                throw AXFailureException.Changed;
            }
            if (observationBinding.IsMenuOnly && scene.WindowHandle != null)
            {
                observationBinding = scene;
            }
            return scene;
        }

        // Expanded read if traversal was incomplete
        if (interpretation.ActionID == null && snapshot.UnfinishedVisibleRead)
        {
            var expanded = await Read(new ObservationRequest(ObservationKind.Expanded, snapshot.Pid)).ConfigureAwait(false);
            await Choose(Candidates(expanded), expanded, "one completed broader read after primary traversal ran out of budget").ConfigureAwait(false);
            return result;
        }

        var primaryIDs = new HashSet<string>(primary.Select(c => c.Id));
        var secondary = Candidates(snapshot, secondary: true).Where(c => !primaryIDs.Contains(c.Id)).ToList();

        if (interpretation.ActionID == "inspect_secondary" && secondary.Count > 0 && secondary.Count <= 251)
        {
            await Choose(secondary, snapshot, "new capabilities already captured; no native reread").ConfigureAwait(false);
            if (Resolved()) return result;
        }
        else if (interpretation.ActionID == "inspect_secondary" && secondary.Count > 251)
        {
            var byRegion = Candidates(snapshot, secondary: true).GroupBy(c => c.GroupID).ToDictionary(g => g.Key, g => g.ToList());
            var owners = byRegion.Keys.OrderBy(k => k).ToList();

            var options = owners.Select(id =>
            {
                var node = snapshot.Nodes[id];
                var members = byRegion.GetValueOrDefault(id, []);
                var examples = string.Join("; ", members.Take(4).Select(m => m.Description));
                return new JevOption($"region{id}", $"{node.Role}: {node.Label}; {members.Count} capabilities; examples: {examples}");
            }).ToList();

            options.Add(new JevOption("fresh_visible", "No observed region has sufficient relevant evidence; read the current visible surface again."));

            var swJev = Stopwatch.StartNew();
            var answer = await Selector.JudgeAsync(new Dictionary<string, object?>
            {
                ["instruction"] = interpretation.Clause.ModelText(source),
                ["revisions"] = revisions,
                ["scene"] = snapshot.SelectionEvidence,
                ["verified_progress"] = progress,
                ["reference_context"] = conversationContext ?? []
            }, [new JevQuestion(LanguagePrompts.Text("recovery_region"), options)], ct).ConfigureAwait(false);
            result.ModelSeconds += swJev.Elapsed.TotalSeconds;

            var chosenId = answer.Ids.FirstOrDefault();
            var matchOwner = owners.FirstOrDefault(o => $"region{o}" == chosenId);
            if (matchOwner != 0 || chosenId == "region0")
            {
                await Choose(byRegion.GetValueOrDefault(matchOwner, []), snapshot, "one observed structural region").ConfigureAwait(false);
                if (Resolved()) return result;

                var region = await Read(new ObservationRequest(ObservationKind.Region, snapshot.Pid, snapshot, matchOwner)).ConfigureAwait(false);
                await Choose(Candidates(region), region, "fresh bounded region; primary controls").ConfigureAwait(false);
                if (Resolved()) return result;
            }
        }

        // Fresh visible reread
        var fresh = await Read(new ObservationRequest(ObservationKind.Recovery, snapshot.Pid)).ConfigureAwait(false);
        await Choose(Candidates(fresh), fresh, "fresh visible surface; primary controls").ConfigureAwait(false);
        if (Resolved()) return result;

        if (fresh.UnfinishedVisibleRead)
        {
            var expanded = await Read(new ObservationRequest(ObservationKind.Expanded, snapshot.Pid)).ConfigureAwait(false);
            await Choose(Candidates(expanded), expanded, "one broader bounded fallback; primary controls only").ConfigureAwait(false);
        }

        return result;
    }
}
