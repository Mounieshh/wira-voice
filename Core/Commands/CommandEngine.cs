using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Language;

namespace WiraVoice.Core.Commands;

public sealed class PreparedAction
{
    public AXSnapshot? Snapshot { get; }
    public InstalledApplication? Launch { get; }
    public AXCandidate? Candidate { get; }
    public string? Literal { get; }
    public string? Description { get; }
    public string EmptyStatus { get; }
    public string Observation { get; }
    public int Requests => Usage.Requests;
    public InputPermit Permit { get; set; } = InputPermit.Standalone();
    public Uri? Navigation { get; set; }
    public InstalledApplication? NavigationBrowser { get; set; }
    public InterpretedCommand? Interpretation { get; set; }
    public double CaptureSeconds { get; set; } = 0;
    public double ModelSeconds { get; set; } = 0;
    public double PreparationSeconds { get; set; } = 0;
    public int RecoveryReads { get; set; } = 0;
    public Dictionary<string, object?> ReferenceContext { get; set; } = [];
    public ModelUsage Usage { get; set; } = new();

    public bool HasExecutablePlan =>
        Launch != null || Navigation != null || Candidate != null || Interpretation?.ActionID == "already_satisfied";

    public PreparedAction(
        AXSnapshot? snapshot,
        InstalledApplication? launch,
        AXCandidate? candidate,
        string? literal,
        string? description,
        string emptyStatus,
        string observation)
    {
        Snapshot = snapshot;
        Launch = launch;
        Candidate = candidate;
        Literal = literal;
        Description = description;
        EmptyStatus = emptyStatus;
        Observation = observation;
    }

    public Task<string> ExecuteAsync(bool physicalActivation = true)
    {
        if (Snapshot == null || Candidate == null)
        {
            throw AXFailureException.Unavailable("The selected action has no observed control.");
        }
        return Task.Run(() => AXActor.Perform(Candidate, Snapshot, Literal, Permit, physicalActivation));
    }
}

public enum ObservationKind { Initial, Recovery, Expanded, Region, Verification }

public readonly record struct ObservationRequest(ObservationKind Kind, int Pid = 0, AXSnapshot? OldSnapshot = null, int RegionId = 0);

/// <summary>
/// Prepares and dispatches actions, reconciles in-flight work, and interfaces with Jev.
/// Port of Computah's CommandEngine.swift.
/// </summary>
public partial class CommandEngine
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public InputPermit InputPermit { get; set; } = InputPermit.Standalone();
    public JevSelector Selector { get; set; }
    public Func<PreparedAction, Task<string>> ExecutePrepared { get; set; }
    public Func<int?> ObservationForegroundPID { get; set; }
    public Func<ObservationRequest, Task<AXSnapshot>> ReadObservation { get; set; }

    private readonly UiaReader _reader = new();

    public CommandEngine(JevSelector selector)
    {
        Selector = selector;
        ExecutePrepared = action => action.ExecuteAsync(true);
        ObservationForegroundPID = () =>
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            return (int)pid;
        };

        ReadObservation = async request =>
        {
            switch (request.Kind)
            {
                case ObservationKind.Initial:
                    return await _reader.CaptureAsync(maxNodes: 900, seconds: 0.35).ConfigureAwait(false);
                case ObservationKind.Verification:
                    return await _reader.CaptureAsync(maxNodes: 900, seconds: 0.35, targetPid: request.Pid).ConfigureAwait(false);
                case ObservationKind.Recovery:
                    return await _reader.CaptureAsync(maxNodes: 1200, seconds: 0.45, targetPid: request.Pid).ConfigureAwait(false);
                case ObservationKind.Expanded:
                    return await _reader.CaptureAsync(maxNodes: 2000, seconds: 0.65, targetPid: request.Pid).ConfigureAwait(false);
                case ObservationKind.Region:
                    return await _reader.CaptureAsync(maxNodes: 900, seconds: 0.35, targetPid: request.OldSnapshot?.Pid).ConfigureAwait(false);
                default:
                    return await _reader.CaptureAsync().ConfigureAwait(false);
            }
        };
    }

    public Task<PreparedAction> PrepareAsync(
        string command,
        AXSnapshot? observation = null,
        List<string>? progress = null,
        HashSet<string>? excluded = null,
        CancellationToken ct = default)
    {
        return PrepareAsync(command, 0, observation, progress, excluded, ct: ct);
    }

    public async Task<PreparedAction> PrepareAsync(
        string command,
        int offset,
        AXSnapshot? observation = null,
        List<string>? progress = null,
        HashSet<string>? excluded = null,
        InterpretedCommand? existing = null,
        List<string>? revisions = null,
        Dictionary<string, object?>? relationshipContext = null,
        Dictionary<string, object?>? conversationContext = null,
        AXSnapshot? continuation = null,
        HashSet<string>? activatedApps = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var usageStart = Selector.Usage.Snapshot;
        var snapshot = observation;
        double captureSeconds = 0.0;
        double modelSeconds = 0.0;
        int recoveryReads = 0;
        string? captureFailure = null;

        if (snapshot == null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                snapshot = await ReadObservation(new ObservationRequest(ObservationKind.Initial)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { captureFailure = ex.Message; }
            captureSeconds += sw.Elapsed.TotalSeconds;
        }

        ct.ThrowIfCancellationRequested();
        var apps = AppRouting.Installed();
        var defaultBrowser = AppRouting.DefaultBrowser();
        if (defaultBrowser != null)
        {
            var idx = apps.FindIndex(a => string.Equals(a.BundleID, defaultBrowser.BundleID, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                var existingApp = apps[idx];
                apps[idx] = new InstalledApplication(existingApp.Name, existingApp.BundleID, existingApp.Url,
                    existingApp.Aliases.Concat(["Registered default browser"]).ToList());
            }
            else
            {
                apps.Add(defaultBrowser);
            }
        }

        activatedApps ??= [];
        apps.RemoveAll(a => activatedApps.Contains(a.BundleID));

        if (continuation != null)
        {
            if (snapshot == null || !snapshot.SameWindow(continuation))
            {
                throw AXFailureException.Changed;
            }
        }

        var language = new CommandLanguage(Selector);
        excluded ??= [];
        var scope = snapshot != null ? AvailableCandidates(snapshot, excluded) : [];

        if (existing != null && (existing.Source != command || existing.Clause.StartUTF16 != offset))
        {
            throw new JevException.Invalid("Prepared interpretation belongs to a different instruction.");
        }

        var swInterpret = Stopwatch.StartNew();
        var interpreted = await language.InterpretAsync(
            command,
            offset,
            apps,
            progress ?? [],
            scope,
            snapshot,
            existing?.Clause,
            revisions ?? [],
            relationshipContext,
            conversationContext ?? [],
            continuation,
            ct).ConfigureAwait(false);
        modelSeconds += swInterpret.Elapsed.TotalSeconds;

        bool Eligible(AXCandidate candidate)
        {
            if (excluded.Contains(candidate.Description)) return false;
            return candidate.Operation switch
            {
                AXOperation.TypeText or AXOperation.ReplaceText => interpreted.CanType && interpreted.Value != null,
                AXOperation.SetNumber => interpreted.CanSetNumber && interpreted.Value != null,
                _ => true
            };
        }

        PreparedAction MakePrepared(
            AXCandidate? candidate = null,
            InstalledApplication? launch = null,
            Uri? navigation = null,
            string status = "",
            string? description = null)
        {
            var details = $"{snapshot?.AppName ?? "No AX observation"}; nodes={snapshot?.Nodes.Count ?? 0}; partial={snapshot?.Partial ?? true}; options={scope.Count}; selected={candidate?.Id ?? interpreted.ActionID ?? "none"}; recovery reads={recoveryReads}; capture={captureSeconds:F2}s; model={modelSeconds:F2}s";
            var usage = Selector.Usage.Snapshot.Since(usageStart);

            var result = new PreparedAction(
                interpreted.Route == CommandRoute.Controls || interpreted.Relationship != null ? snapshot : null,
                launch,
                candidate,
                interpreted.Value,
                description ?? candidate?.Description,
                status,
                details)
            {
                Usage = usage,
                Permit = InputPermit,
                Navigation = navigation,
                NavigationBrowser = navigation != null ? (interpreted.App ?? AppRouting.DefaultBrowser()) : null,
                Interpretation = interpreted,
                ReferenceContext = conversationContext ?? [],
                CaptureSeconds = captureSeconds,
                ModelSeconds = modelSeconds,
                PreparationSeconds = (DateTime.UtcNow - started).TotalSeconds,
                RecoveryReads = recoveryReads
            };
            return result;
        }

        ct.ThrowIfCancellationRequested();

        if (interpreted.Relationship.HasValue && interpreted.Relationship.Value != GoalRelationship.Replace)
        {
            return MakePrepared(status: "Goal relationship: " + interpreted.Relationship.Value.ToString().ToLowerInvariant());
        }

        if (interpreted.ActivatesApp)
        {
            if (interpreted.App == null)
            {
                return MakePrepared(status: "The requested app is unclear or unavailable.");
            }
            return MakePrepared(launch: interpreted.App, description: $"Open {interpreted.App.Name}");
        }

        if (interpreted.Route == null)
        {
            return MakePrepared(status: "No clear action requested.");
        }

        if (interpreted.Route == CommandRoute.Controls)
        {
            var selected = interpreted.ActionID;
            var missingFromIncompleteRead = selected == null && snapshot?.UnfinishedVisibleRead == true;

            if (selected is "reobserve" or "inspect_secondary" || missingFromIncompleteRead)
            {
                if (snapshot != null)
                {
                    var recovery = await RecoverAsync(command, offset, interpreted, snapshot, scope,
                        progress ?? [], excluded, revisions ?? [], conversationContext ?? [], continuation, ct).ConfigureAwait(false);
                    snapshot = recovery.Snapshot;
                    scope = recovery.Candidates;
                    interpreted = recovery.Interpretation;
                    selected = interpreted.ActionID;
                    captureSeconds += recovery.CaptureSeconds;
                    modelSeconds += recovery.ModelSeconds;
                    recoveryReads += recovery.Reads;
                }
            }

            if (selected == "already_satisfied" && snapshot != null)
            {
                return MakePrepared(status: "Requested state needs fresh confirmation.", description: "Already satisfied — verify without input");
            }

            var candidate = scope.FirstOrDefault(c => c.Id == selected);
            if (candidate == null)
            {
                return MakePrepared(status: captureFailure ?? "No matching control found after bounded observation; no further input sent.");
            }

            if (!Eligible(candidate))
            {
                throw new JevException.Invalid("The selected action and source value disagree.");
            }

            if (candidate.Operation is AXOperation.TypeText or AXOperation.ReplaceText or AXOperation.SetNumber)
            {
                var swResolve = Stopwatch.StartNew();
                interpreted = await language.ResolveValueAsync(interpreted, ct).ConfigureAwait(false);
                modelSeconds += swResolve.Elapsed.TotalSeconds;
            }

            return MakePrepared(candidate: candidate);
        }

        // Navigation
        var swNav = Stopwatch.StartNew();
        interpreted = await language.ResolveValueAsync(interpreted, ct).ConfigureAwait(false);
        modelSeconds += swNav.Elapsed.TotalSeconds;

        var url = interpreted.Value != null ? CommandLanguage.ValidatedUrl(interpreted.Value) : null;
        if (url == null)
        {
            return MakePrepared(status: "The requested address is incomplete or unclear.");
        }

        return MakePrepared(navigation: url, description: $"Visit {url.AbsoluteUri}");
    }

    public List<AXCandidate> AvailableCandidates(AXSnapshot scene, HashSet<string> excluded, bool includeSecondary = false)
    {
        return AXGrouping.Candidates(scene, includeSecondary: includeSecondary)
            .Where(c => !excluded.Contains(c.Description))
            .ToList();
    }
}
