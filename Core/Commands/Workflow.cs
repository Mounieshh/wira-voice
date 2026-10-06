using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Diagnostics;
using WiraVoice.Core.Language;

namespace WiraVoice.Core.Commands;

public sealed class WorkflowEvent
{
    public CommandClause Clause { get; }
    public string Action { get; }
    public string Before { get; }
    public string After { get; set; }
    public string Outcome { get; set; }
    public double SelectionSeconds { get; }
    public double VerificationSeconds { get; set; }
    public int Requests { get; set; }
    public double CaptureSeconds { get; set; } = 0;
    public string? SelectionDetails { get; set; }
    public int? InputTokens { get; set; }
    public double? ModelSeconds { get; set; }
    public double? PreparationSeconds { get; set; }
    public int? RecoveryReads { get; set; }

    public WorkflowEvent(
        CommandClause clause,
        string action,
        string before,
        string after,
        string outcome,
        double selectionSeconds,
        double verificationSeconds,
        int requests,
        string? selectionDetails = null)
    {
        Clause = clause;
        Action = action;
        Before = before;
        After = after;
        Outcome = outcome;
        SelectionSeconds = selectionSeconds;
        VerificationSeconds = verificationSeconds;
        Requests = requests;
        SelectionDetails = selectionDetails;
    }
}

public sealed class WorkflowResult
{
    public string Command { get; }
    public string Status { get; }
    public bool Complete { get; }
    public List<WorkflowEvent> Events { get; }
    public double Elapsed { get; }
    public ModelUsage? Usage { get; set; }
    public int Requests => Usage?.Requests ?? Events.Sum(e => e.Requests);
    public int? InputTokens => Usage?.InputTokens ?? (Events.All(e => e.InputTokens.HasValue) ? Events.Sum(e => e.InputTokens!.Value) : null);

    public WorkflowResult(string command, string status, bool complete, List<WorkflowEvent> events, double elapsed, ModelUsage? usage = null)
    {
        Command = command;
        Status = status;
        Complete = complete;
        Events = events;
        Elapsed = elapsed;
        Usage = usage;
    }
}

public readonly record struct OutcomeJudgment(string? Id, int Requests, double Elapsed, string? ProgressEffect = null);

public partial class CommandEngine
{
    public static string FailureStatus(Exception error, bool didDispatch) =>
        $"Stopped: {error.Message} " + (didDispatch
            ? "Input was dispatched; some effects may be partial or unconfirmed. Check the app before repeating."
            : "No input was sent for this attempt.");

    public static string RevisionContext(List<string> revisions) =>
        revisions.Count == 0
            ? ""
            : "\nUser revisions to this instruction (latest overrides conflicting earlier values):\n" + string.Join("\n", revisions);

    /// <summary>
    /// Advance only through semantically selected source intervals after verified completion.
    /// Multi-step autonomous loop port of Computah's Workflow.swift.
    /// </summary>
    public async Task<WorkflowResult> RunWorkflowAsync(
        string command,
        PreparedAction? preparedFirst = null,
        WorkflowCheckpoint? supplied = null,
        CancellationToken ct = default)
    {
        var checkpoint = supplied ?? new WorkflowCheckpoint(command);
        if (checkpoint.Source != command)
        {
            throw new JevException.Invalid("Checkpoint source mismatch.");
        }

        checkpoint.Authorize(InputPermit);
        var started = DateTime.UtcNow;
        var usageStart = Selector.Usage.Snapshot;

        if (checkpoint.Read().Pending != null && !(await ReconcileAsync(checkpoint, ct).ConfigureAwait(false)))
        {
            var stopStatus = "Earlier input remains unconfirmed. No replay was sent.";
            checkpoint.Update(InputPermit.Generation, s =>
            {
                s.ExecutionStatus = "stopped";
                s.LastStopReason = stopStatus;
            });

            return new WorkflowResult(command, stopStatus, false, [],
                (DateTime.UtcNow - started).TotalSeconds, Selector.Usage.Snapshot.Since(usageStart));
        }

        var prior = checkpoint.Read();
        var usePreparedFirst =
            prior.Cursor == 0 && prior.Active == null && prior.Pending == null && prior.Observed == null &&
            prior.NoInputReference == null && preparedFirst?.Interpretation?.Source == command &&
            preparedFirst?.Interpretation?.Clause.StartUTF16 == 0;

        checkpoint.Update(InputPermit.Generation, s =>
        {
            s.ExecutionStatus = "running";
            if (usePreparedFirst && preparedFirst != null)
            {
                s.ReferenceContext = preparedFirst.ReferenceContext;
            }
        });

        var resume = checkpoint.Read();
        var dispatchStart = InputPermit.DispatchCount;
        var events = new List<WorkflowEvent>();
        Action? measureCurrentStep = null;
        AXSnapshot? current = null;
        int? expectedPID = (resume.NoInputBinding ?? resume.Observed)?.Pid;

        WorkflowResult MakeResult(string status, bool complete)
        {
            measureCurrentStep?.Invoke();
            checkpoint.Update(InputPermit.Generation, s =>
            {
                s.ExecutionStatus = complete ? "completed" : "stopped";
                s.LastStopReason = complete ? "" : status;
            });

            var totalUsage = Selector.Usage.Snapshot.Since(usageStart);
            if (usePreparedFirst && preparedFirst != null)
            {
                totalUsage = totalUsage.Adding(preparedFirst.Usage);
            }

            return new WorkflowResult(
                command,
                status,
                complete,
                events,
                (DateTime.UtcNow - started).TotalSeconds,
                totalUsage);
        }

        var cursor = resume.Cursor;
        var completed = resume.Verified.Count;
        var verifiedProgress = new List<string>(resume.Verified);

        try
        {
            while (cursor < command.Length)
            {
                InputPermit.Check();
                ct.ThrowIfCancellationRequested();

                if (completed >= 32)
                {
                    return MakeResult("Stopped at the instruction limit.", false);
                }

                PreparedAction initial;
                if (usePreparedFirst && cursor == 0 && preparedFirst?.Interpretation?.Source == command &&
                    preparedFirst.Interpretation?.Clause.StartUTF16 == 0)
                {
                    initial = preparedFirst;
                }
                else
                {
                    initial = await PrepareAsync(
                        command,
                        cursor,
                        observation: current,
                        progress: resume.BackgroundProgress.Concat(verifiedProgress).ToList(),
                        revisions: checkpoint.Read().Revisions.GetValueOrDefault(cursor, []),
                        conversationContext: resume.ReferenceContext,
                        continuation: cursor == resume.Cursor ? (resume.NoInputBinding ?? resume.Observed) : null,
                        activatedApps: checkpoint.Read().ActivatedApps,
                        ct: ct).ConfigureAwait(false);
                }

                if (initial.Interpretation == null || initial.Interpretation.Clause.EndUTF16 <= cursor)
                {
                    return MakeResult("No valid instruction boundary was selected.", false);
                }

                var clause = initial.Interpretation.Clause;
                checkpoint.Update(InputPermit.Generation, s => s.Active = clause);

                var history = cursor == resume.Cursor ? new List<string>(resume.History) : [];
                var excluded = cursor == resume.Cursor ? new HashSet<string>(resume.Excluded) : [];
                bool satisfied = false;
                bool retriedSatisfaction = false;
                string? satisfactionReferenceEvidence = null;
                bool recoveredBeforeDispatch = false;
                var recoveryBinding = checkpoint.Read().NoInputBinding;

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    var attemptStart = DateTime.UtcNow;
                    PreparedAction prepared;
                    if (attempt == 0)
                    {
                        prepared = initial;
                    }
                    else
                    {
                        prepared = await PrepareAsync(
                            command,
                            cursor,
                            observation: current,
                            progress: resume.BackgroundProgress.Concat(verifiedProgress).Concat(history).ToList(),
                            excluded: excluded,
                            existing: initial.Interpretation,
                            revisions: checkpoint.Read().Revisions.GetValueOrDefault(cursor, []),
                            conversationContext: resume.ReferenceContext,
                            continuation: recoveryBinding,
                            activatedApps: checkpoint.Read().ActivatedApps,
                            ct: ct).ConfigureAwait(false);
                    }

                    InputPermit.Check();
                    ct.ThrowIfCancellationRequested();

                    var prepareStart = attempt == 0
                        ? DateTime.UtcNow.AddSeconds(-prepared.PreparationSeconds)
                        : attemptStart;

                    var eventStart = events.Count;
                    var verificationUsageStart = Selector.Usage.Snapshot;
                    double verificationModelSeconds = 0.0;

                    measureCurrentStep = () =>
                    {
                        var usage = prepared.Usage.Adding(Selector.Usage.Snapshot.Since(verificationUsageStart));
                        for (int idx = eventStart; idx < events.Count; idx++)
                        {
                            events[idx].Requests = usage.Requests;
                            events[idx].InputTokens = usage.InputTokens;
                            events[idx].ModelSeconds = prepared.ModelSeconds + verificationModelSeconds;
                            events[idx].CaptureSeconds = prepared.CaptureSeconds;
                            events[idx].PreparationSeconds = prepared.PreparationSeconds;
                            events[idx].RecoveryReads = prepared.RecoveryReads;
                        }
                    };

                    if (expectedPID.HasValue && prepared.Snapshot != null && prepared.Snapshot.Pid != expectedPID.Value)
                    {
                        return MakeResult("The target application changed. Stopped before input.", false);
                    }

                    // Already satisfied check
                    if (prepared.Interpretation?.ActionID == "already_satisfied" && prepared.Snapshot != null)
                    {
                        var before = prepared.Snapshot;
                        if (satisfactionReferenceEvidence == null)
                        {
                            var reference = checkpoint.Read().NoInputBinding ?? before;
                            checkpoint.Update(InputPermit.Generation, s =>
                            {
                                if (s.NoInputBinding == null)
                                {
                                    s.NoInputReference = new NoInputReference(cursor, reference);
                                }
                            });
                            recoveryBinding ??= reference;
                            var ev = reference.Evidence;
                            satisfactionReferenceEvidence =
                                "Original observation before no-input confirmation; historical object reference only, not proof of current completion:\n" +
                                (ev.Length > 6000 ? ev.Substring(0, 6000) + "\nOriginal observation truncated; missing evidence is unknown." : ev);
                        }

                        var verifyStart = DateTime.UtcNow;
                        var confirmation = await ConfirmSatisfiedAsync(
                            before,
                            clause.ModelText(command) + RevisionContext(checkpoint.Read().Revisions.GetValueOrDefault(cursor, [])),
                            history,
                            checkpoint.Read().VerificationContext,
                            satisfactionReferenceEvidence,
                            ct).ConfigureAwait(false);

                        var fresh = confirmation.Snapshot;
                        var judgment = confirmation.Judgment;
                        verificationModelSeconds = judgment.Elapsed;
                        var complete = judgment.Id == "complete";

                        events.Add(new WorkflowEvent(
                            clause,
                            "Already satisfied — no input",
                            before.Evidence,
                            fresh.Evidence,
                            complete ? "complete" : "unknown",
                            (verifyStart - prepareStart).TotalSeconds,
                            (DateTime.UtcNow - verifyStart).TotalSeconds,
                            prepared.Requests + judgment.Requests,
                            $"Judge={judgment.Id ?? "none"}; evidence:\n{confirmation.Evidence}"));

                        if (!complete)
                        {
                            if (retriedSatisfaction)
                            {
                                return MakeResult("Current state did not confirm completion. Stopped without sending another action.", false);
                            }
                            retriedSatisfaction = true;
                            recoveryBinding ??= before;
                            current = fresh;
                            history.Add("The proposed already-satisfied state was not confirmed by a fresh read. No input was sent. Select from this refreshed observation for the same instruction.");
                            continue;
                        }

                        current = fresh;
                        expectedPID = fresh.Pid;
                        satisfied = true;
                        break;
                    }

                    if (prepared.Launch == null && prepared.Candidate == null && prepared.Navigation == null)
                    {
                        events.Add(new WorkflowEvent(
                            clause,
                            "No input",
                            prepared.Snapshot?.Evidence ?? prepared.Observation,
                            "",
                            prepared.EmptyStatus,
                            (DateTime.UtcNow - prepareStart).TotalSeconds,
                            0,
                            prepared.Requests,
                            prepared.Observation));

                        return MakeResult(prepared.EmptyStatus, false);
                    }

                    // Navigation
                    if (prepared.Navigation != null)
                    {
                        checkpoint.Update(InputPermit.Generation, s =>
                        {
                            s.Pending = new PendingInstruction(clause, prepared, InputPermit.DispatchCount);
                        });

                        if (prepared.NavigationBrowser == null)
                        {
                            return MakeResult("No browser was selected and none is registered for HTTPS.", false);
                        }

                        var nav = await NavigateAsync(prepared.Navigation, prepared.NavigationBrowser, prepared, clause, prepareStart, ct).ConfigureAwait(false);
                        current = nav.Snapshot;
                        expectedPID = nav.Pid;
                        verificationModelSeconds = nav.ModelSeconds;
                        events.Add(nav.Event);

                        if (!nav.Verified)
                        {
                            return MakeResult("Navigation requested; the destination document is not yet observed.", false);
                        }
                        satisfied = true;
                        break;
                    }

                    // Application Launch
                    if (prepared.Launch != null)
                    {
                        checkpoint.Update(InputPermit.Generation, s =>
                        {
                            s.Pending = new PendingInstruction(clause, prepared, InputPermit.DispatchCount);
                        });

                        var activation = await ActivateAsync(prepared.Launch, prepared, clause, prepareStart, checkpoint.Read().VerificationContext, ct).ConfigureAwait(false);
                        verificationModelSeconds = activation.ModelSeconds;
                        events.Add(activation.Event);

                        if (!activation.Verified)
                        {
                            return MakeResult("Application activation was not verified.", false);
                        }

                        checkpoint.Update(InputPermit.Generation, s =>
                        {
                            s.Pending = null;
                            s.Observed = null;
                        });

                        expectedPID = ObservationForegroundPID();
                        current = null;

                        if (!activation.Complete)
                        {
                            history.Add($"The target application {prepared.Launch.Name} is now foreground. Activation is only a prerequisite; the instruction remains unfinished.");
                            checkpoint.Update(InputPermit.Generation, s =>
                            {
                                s.History = history;
                                s.ActivatedApps.Add(prepared.Launch.BundleID);
                            });
                            continue;
                        }

                        satisfied = true;
                        break;
                    }

                    // Control interaction
                    var candidateBefore = prepared.Snapshot;
                    var candidate = prepared.Candidate;
                    if (candidateBefore == null || candidate == null)
                    {
                        return MakeResult("No observed target.", false);
                    }

                    expectedPID = candidateBefore.Pid;
                    InputPermit.Check();

                    // Check if value already present
                    bool valueAlreadyPresent = false;
                    if (prepared.Literal != null && candidateBefore.Nodes.IndicesContains(candidate.NodeID))
                    {
                        var n = candidateBefore.Nodes[candidate.NodeID];
                        valueAlreadyPresent =
                            (candidate.Operation == AXOperation.ReplaceText && n.Value == prepared.Literal) ||
                            (candidate.Operation == AXOperation.SetNumber && NumericTargetMatches(n.Value, prepared.Literal, n));
                    }

                    if (valueAlreadyPresent)
                    {
                        events.Add(new WorkflowEvent(
                            clause,
                            "No input — requested value already present",
                            candidateBefore.Evidence,
                            candidateBefore.Evidence,
                            "Value observed; selecting the next action",
                            (DateTime.UtcNow - prepareStart).TotalSeconds,
                            0,
                            prepared.Requests));

                        excluded.Add(candidate.Description);
                        history.Add("The selected control already has the requested value. No input was sent. This proves only the value; select a next action or verify the whole goal and intended object.");
                        current = candidateBefore;
                        checkpoint.Update(InputPermit.Generation, s =>
                        {
                            s.History = history;
                            s.Excluded = excluded;
                        });
                        continue;
                    }

                    var eventIndex = events.Count;
                    events.Add(new WorkflowEvent(
                        clause,
                        candidate.Description,
                        candidateBefore.Evidence,
                        "",
                        "Input intended; outcome unknown until observed",
                        (DateTime.UtcNow - prepareStart).TotalSeconds,
                        0,
                        prepared.Requests,
                        prepared.Observation));

                    checkpoint.Update(InputPermit.Generation, s =>
                    {
                        s.Pending = new PendingInstruction(clause, prepared, InputPermit.DispatchCount);
                    });

                    var dispatchCount = InputPermit.DispatchCount;
                    string receipt;
                    try
                    {
                        Console.WriteLine($"[Automation] Executing action: \"{candidate.Description}\" ({candidate.Operation})");
                        receipt = await ExecutePrepared(prepared).ConfigureAwait(false);
                        Console.WriteLine($"[Automation] Action executed successfully: {receipt}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Automation] Action preflight failed: {ex.Message}");
                        InputPermit.Check();
                        if (recoveredBeforeDispatch || InputPermit.DispatchCount != dispatchCount)
                        {
                            throw;
                        }

                        recoveredBeforeDispatch = true;
                        var fresh = await ReadObservation(new ObservationRequest(ObservationKind.Recovery, candidateBefore.Pid)).ConfigureAwait(false);
                        if (!fresh.CanObserveAfter(candidateBefore, ObservationForegroundPID()))
                        {
                            throw AXFailureException.Changed;
                        }

                        recoveryBinding = candidateBefore.IsMenuOnly && fresh.WindowHandle != null ? fresh : candidateBefore;
                        current = fresh;
                        history.Add($"The selected operation failed preflight; no input was dispatched. The old target is invalid. Original selected target: {candidate.Description}. Re-establish that intended item or a permitted fresh named target from the original instruction.");

                        events[eventIndex] = new WorkflowEvent(
                            clause,
                            candidate.Description,
                            candidateBefore.Evidence,
                            fresh.Evidence,
                            "notDispatched; refreshed for reselection",
                            (DateTime.UtcNow - prepareStart).TotalSeconds,
                            0,
                            prepared.Requests,
                            ex.Message);

                        checkpoint.Update(InputPermit.Generation, s =>
                        {
                            s.Pending = null;
                            s.Observed = fresh;
                            s.History = history;
                        });
                        continue;
                    }

                    events[eventIndex].SelectionDetails = prepared.Observation + "\n" + receipt;
                    var selSeconds = (DateTime.UtcNow - prepareStart).TotalSeconds;
                    var verifyStartControl = DateTime.UtcNow;

                    var verification = await VerifyControlAsync(
                        prepared,
                        candidateBefore,
                        candidate,
                        clause.ModelText(command) + RevisionContext(checkpoint.Read().Revisions.GetValueOrDefault(cursor, [])),
                        history,
                        checkpoint.Read().VerificationContext,
                        ct).ConfigureAwait(false);

                    var after = verification.After;
                    var verdict = verification.Verdict;
                    verificationModelSeconds += verification.ModelSeconds;

                    events[eventIndex] = new WorkflowEvent(
                        clause,
                        candidate.Description,
                        candidateBefore.Evidence,
                        after.Evidence,
                        verdict,
                        selSeconds,
                        (DateTime.UtcNow - verifyStartControl).TotalSeconds,
                        prepared.Requests,
                        prepared.Observation + "\n" + receipt)
                    {
                        CaptureSeconds = candidateBefore.ReadSeconds
                    };

                    current = after;
                    checkpoint.Update(InputPermit.Generation, s => s.Observed = after);

                    if (verdict == "complete")
                    {
                        satisfied = true;
                        break;
                    }

                    // Intermediate progress: either verdict is explicitly progress, or the UI observed meaningful state change
                    // and was not contradicted, allowing multi-step workflows to advance to the next step.
                    bool isIntermediateProgress = verdict == "progress" ||
                        (verdict != "contradicted" && after.HasObservedChange(candidateBefore) && attempt < 4);

                    if (!isIntermediateProgress)
                    {
                        return MakeResult(
                            verdict == "contradicted"
                                ? "Observed result conflicts with the request. Stopped without replaying input."
                                : "Action sent; its outcome is still unknown. Stopped without replaying it.",
                            false);
                    }

                    excluded.Clear();
                    history.Add($"Observed intermediate progress after {candidate.Description}. This effect is resolved. Choose a new intentional action for the remaining goal from the current state.\n" +
                        after.VerificationEvidence(candidateBefore, candidate, 2000));

                    if (candidate.Operation == AXOperation.ReplaceText && prepared.Literal != null)
                    {
                        history.Add($"Verified exact field readback after replacement: {prepared.Interpretation?.ModelValue ?? "[private value]"}. The previous field value was replaced, not retained. This establishes the field value only; check the remaining required effects.");
                    }

                    checkpoint.Update(InputPermit.Generation, s =>
                    {
                        s.Pending = null;
                        s.History = history;
                        s.Excluded = excluded;
                    });
                }

                if (!satisfied)
                {
                    return MakeResult("The step needs further interaction; stopped at the action limit.", false);
                }

                checkpoint.Update(InputPermit.Generation, s =>
                {
                    s.FinishClause(clause, command, current);
                    cursor = s.Cursor;
                    completed = s.Verified.Count;
                    verifiedProgress = s.Verified;
                });
            }
        }
        catch (Exception ex)
        {
            var didDispatch = InputPermit.DispatchCount > dispatchStart || checkpoint.Read().Pending?.WasDispatched == true;
            var failure = FailureStatus(ex, didDispatch);
            if (events.Count > 0 && events[^1].Outcome == "Input intended; outcome unknown until observed")
            {
                events[^1].Outcome = failure;
            }
            return MakeResult(ex is OperationCanceledException ? "Superseded by a newer request. Earlier input remains subject to observation." : failure, false);
        }

        return MakeResult(completed == 0 ? "No command." : "Completed and observed.", completed > 0);
    }

    public async Task<bool> ReconcileAsync(WorkflowCheckpoint checkpoint, CancellationToken ct = default)
    {
        var pending = checkpoint.Read().Pending;
        if (pending == null) return true;
        if (!pending.WasDispatched)
        {
            checkpoint.Update(InputPermit.Generation, s => s.Pending = null);
            return true;
        }

        InputPermit.Check();
        var fresh = await ReadObservation(new ObservationRequest(ObservationKind.Initial)).ConfigureAwait(false);
        var prepared = pending.Prepared;
        string verdict;

        if (prepared.Launch != null)
        {
            if (!AppRouting.IsSameApp(fresh, prepared.Launch) && !AppRouting.IsSameApp(fresh.AppName, fresh.WindowTitle, prepared.Launch)) return false;
            var answer = await ActivationJudgmentAsync(prepared.Launch.Name, pending.Clause.ModelText(checkpoint.Source), checkpoint.Read().VerificationContext, ct).ConfigureAwait(false);
            verdict = answer.Id ?? "unknown";
        }
        else if (prepared.Navigation != null)
        {
            if (prepared.NavigationBrowser == null || (!AppRouting.IsSameApp(fresh, prepared.NavigationBrowser) && !AppRouting.IsSameApp(fresh.AppName, fresh.WindowTitle, prepared.NavigationBrowser))) return false;
            var ans = await DestinationJudgmentAsync(prepared.Navigation, fresh, ct).ConfigureAwait(false);
            verdict = ans.Id ?? "unknown";
        }
        else if (prepared.Snapshot != null && prepared.Candidate != null)
        {
            var before = prepared.Snapshot;
            if (fresh.Pid != before.Pid || ObservationForegroundPID() != before.Pid) return false;
            var revs = checkpoint.Read().Revisions.GetValueOrDefault(pending.Clause.StartUTF16, []);
            var answer = await JudgeControlOutcomeAsync(prepared, before, fresh, pending.Clause.ModelText(checkpoint.Source) + RevisionContext(revs),
                checkpoint.Read().History, checkpoint.Read().VerificationContext, ct).ConfigureAwait(false);
            verdict = answer.Id ?? "unknown";
        }
        else
        {
            return false;
        }

        InputPermit.Check();
        if (verdict is not ("complete" or "progress")) return false;

        checkpoint.Update(InputPermit.Generation, state =>
        {
            state.Pending = null;
            state.Observed = fresh;
            if (verdict == "complete")
            {
                state.FinishClause(pending.Clause, checkpoint.Source, fresh);
            }
            else
            {
                if (prepared.Launch != null) state.ActivatedApps.Add(prepared.Launch.BundleID);
                state.Excluded.Clear();
                var progress = "The interrupted action has now been observed as intermediate progress. Its effect is resolved; choose a new intentional action for the remaining goal.";
                if (prepared.Snapshot != null && prepared.Candidate != null)
                {
                    progress += "\n" + fresh.VerificationEvidence(prepared.Snapshot, prepared.Candidate, 2000);
                }
                state.History.Add(progress);
            }
        });

        return true;
    }

    private async Task<(AXSnapshot? Snapshot, int Pid, bool Verified, double ModelSeconds, WorkflowEvent Event)> NavigateAsync(
        Uri url, InstalledApplication browser, PreparedAction prepared, CommandClause clause, DateTime prepareStart, CancellationToken ct)
    {
        var pid = await OpenUrlAsync(url, browser, ct).ConfigureAwait(false);
        var sent = DateTime.UtcNow;
        AXSnapshot? current = null;
        bool verified = false;
        string? lastEvidence = null;
        double modelSeconds = 0.0;

        for (int i = 0; i < 15; i++)
        {
            var snapshot = await ObserveAfterInputAsync(pid, ct: ct).ConfigureAwait(false);
            current = snapshot;
            verified = (AppRouting.IsSameApp(snapshot, browser) || AppRouting.IsSameApp(snapshot?.AppName, snapshot?.WindowTitle, browser)) && (snapshot?.ObservesDestination(url) ?? false);
            if (verified) break;

            var evidence = snapshot?.DestinationEvidence;
            if (snapshot != null && (AppRouting.IsSameApp(snapshot, browser) || AppRouting.IsSameApp(snapshot.AppName, snapshot.WindowTitle, browser)) && evidence != lastEvidence)
            {
                lastEvidence = evidence;
                var answer = await DestinationJudgmentAsync(url, snapshot, ct).ConfigureAwait(false);
                modelSeconds += answer.Elapsed;
                verified = answer.Id == "complete";
            }
            if (verified) break;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        var ev = new WorkflowEvent(
            clause,
            $"Visit {url.AbsoluteUri}",
            prepared.Observation,
            current?.Evidence ?? "No browser observation yet.",
            verified ? "complete" : "unknown",
            (sent - prepareStart).TotalSeconds,
            (DateTime.UtcNow - sent).TotalSeconds,
            prepared.Requests);

        return (current, pid, verified, modelSeconds, ev);
    }

    public async Task<OutcomeJudgment> DestinationJudgmentAsync(Uri url, AXSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.ObservesDestination(url)) return new OutcomeJudgment("complete", 0, 0);
        var evidence = snapshot.DestinationEvidence;
        if (string.IsNullOrEmpty(evidence)) return new OutcomeJudgment(null, 0, 0);

        return await JudgeOutcomeAsync(
            url.AbsoluteUri,
            evidence,
            [
                new JevOption("complete", LanguagePrompts.Text("verify_complete")),
                new JevOption("pending", LanguagePrompts.Text("verify_pending"))
            ],
            LanguagePrompts.Text("verify_destination"),
            ct: ct).ConfigureAwait(false);
    }

    private async Task<(bool Verified, bool Complete, double ModelSeconds, WorkflowEvent Event)> ActivateAsync(
        InstalledApplication launch, PreparedAction prepared, CommandClause clause, DateTime prepareStart, Dictionary<string, object?> referenceContext, CancellationToken ct)
    {
        var verified = await AppRouting.ActivateAsync(launch, InputPermit).ConfigureAwait(false);
        var verifyStart = DateTime.UtcNow;
        bool complete = false;
        double modelSeconds = 0.0;
        int requests = prepared.Requests;

        if (verified)
        {
            var goal = prepared.Interpretation != null ? clause.ModelText(prepared.Interpretation.Source) : SensitiveText.Redact(clause.Text);
            var judgment = await ActivationJudgmentAsync(launch.Name, goal, referenceContext, ct).ConfigureAwait(false);
            InputPermit.Check();
            complete = judgment.Id == "complete";
            modelSeconds = judgment.Elapsed;
            requests += judgment.Requests;
        }

        var ev = new WorkflowEvent(
            clause,
            $"Open {launch.Name}",
            prepared.Observation,
            $"Foreground verified={verified}",
            verified ? (complete ? "complete" : "progress") : "unknown",
            (verifyStart - prepareStart).TotalSeconds,
            (DateTime.UtcNow - verifyStart).TotalSeconds,
            requests);

        return (verified, complete, modelSeconds, ev);
    }

    public Task<OutcomeJudgment> ActivationJudgmentAsync(string appName, string goal, Dictionary<string, object?> referenceContext, CancellationToken ct)
    {
        return JudgeOutcomeAsync(
            goal,
            $"Observed foreground application: {appName}. No document, item, setting, or content change has been established.",
            [
                new JevOption("complete", "The instruction only requests this application in the foreground; that is now observed."),
                new JevOption("progress", "The instruction requires interaction inside the application; foregrounding is only a prerequisite.")
            ],
            LanguagePrompts.Text("verify_activation"),
            referenceContext: referenceContext,
            ct: ct);
    }

    private async Task<(AXSnapshot Snapshot, string Evidence, OutcomeJudgment Judgment)> ConfirmSatisfiedAsync(
        AXSnapshot before, string goal, List<string> history, Dictionary<string, object?> referenceContext, string? beforeEvidence, CancellationToken ct)
    {
        var fresh = await ObserveAfterInputAsync(before.Pid, boundTo: before, ct: ct).ConfigureAwait(false);
        if (!fresh.SameWindow(before))
        {
            throw AXFailureException.Unavailable($"Window binding changed during confirmation: {before.WindowTitle} -> {fresh.WindowTitle}.");
        }

        var evidence = fresh.VerificationEvidence();
        var judgment = await JudgeOutcomeAsync(
            goal,
            evidence,
            [
                new JevOption("complete", "Positive current evidence establishes the requested state; no input needed."),
                new JevOption("pending", "The requested state is not positively established.")
            ],
            LanguagePrompts.Text("verify_satisfied"),
            history: history,
            checkObject: true,
            referenceContext: referenceContext,
            beforeEvidence: beforeEvidence,
            ct: ct).ConfigureAwait(false);

        return (fresh, evidence, judgment);
    }

    private async Task<(AXSnapshot After, string Verdict, double ModelSeconds)> VerifyControlAsync(
        PreparedAction prepared, AXSnapshot before, AXCandidate candidate, string goal, List<string> history, Dictionary<string, object?> referenceContext, CancellationToken ct)
    {
        var after = await ObserveAfterInputAsync(before.Pid, ct: ct).ConfigureAwait(false);
        var verdict = "pending";
        double modelSeconds = 0.0;
        string? lastJudged = null;

        for (int check = 0; check < 4; check++)
        {
            var evidence = after.VerificationEvidence(before, candidate);
            if (evidence != lastJudged)
            {
                lastJudged = evidence;
                var judgment = await JudgeControlOutcomeAsync(prepared, before, after, goal, history, referenceContext, ct).ConfigureAwait(false);
                modelSeconds += judgment.Elapsed;
                verdict = judgment.Id ?? "pending";
            }
            if (verdict != "pending") break;
            if (check >= 1 && after.HasObservedChange(before))
            {
                // UI has visibly changed and stabilized
                break;
            }
            if (check < 3)
            {
                var delayMs = (int)(Math.Min(0.6, 0.15 * Math.Pow(2, check)) * 1000);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                after = await ObserveAfterInputAsync(before.Pid, ct: ct).ConfigureAwait(false);
            }
        }

        return (after, verdict, modelSeconds);
    }

    public async Task<OutcomeJudgment> JudgeControlOutcomeAsync(
        PreparedAction prepared, AXSnapshot before, AXSnapshot after, string goal, List<string> history, Dictionary<string, object?> referenceContext, CancellationToken ct)
    {
        var candidate = prepared.Candidate ?? throw AXFailureException.Changed;
        var evidence = $"DISPATCHED (not proof): {candidate.Description}\n" + after.VerificationEvidence(before, candidate);
        var windowFacts = new Dictionary<string, bool> { ["same_native_window"] = after.SameWindow(before) };

        if (after.WindowHandle.HasValue && before.ApplicationWindows != null)
        {
            windowFacts["current_window_existed_before_action"] = before.ApplicationWindows.Contains(after.WindowHandle.Value);
        }

        bool? numericMatched = null;
        if (candidate.Operation == AXOperation.SetNumber && prepared.Literal != null)
        {
            var curVal = after.ValueForSameControl(candidate, before);
            if (curVal != null && before.Nodes.IndicesContains(candidate.NodeID))
            {
                numericMatched = NumericTargetMatches(curVal, prepared.Literal, before.Nodes[candidate.NodeID]);
            }
            evidence += $"\nExact numeric readback on the same native control matches requested value: {numericMatched ?? false}. This establishes only the setting, not other required effects or semantic target identity.";
        }

        var options = new[] { "complete", "progress", "contradicted", "pending" }
            .Select(s => new JevOption(s, LanguagePrompts.Text("verify_" + s)))
            .ToList();

        var answer = await JudgeOutcomeAsync(
            goal,
            evidence,
            options,
            LanguagePrompts.Text("verify"),
            action: new Dictionary<string, string>
            {
                ["operation"] = candidate.Operation.ToWireString(),
                ["target"] = candidate.Description,
                ["source_value"] = prepared.Interpretation?.ModelValue ?? ""
            },
            history: history,
            checkObject: true,
            referenceContext: referenceContext,
            windowFacts: windowFacts,
            ct: ct).ConfigureAwait(false);

        if ((numericMatched == false && answer.Id == "complete") ||
            (answer.Id == "progress" && !after.HasObservedChange(before) && answer.ProgressEffect != "resolved"))
        {
            return new OutcomeJudgment(null, answer.Requests, answer.Elapsed, answer.ProgressEffect);
        }

        return answer;
    }

    public async Task<OutcomeJudgment> JudgeOutcomeAsync(
        string goal,
        string evidence,
        List<JevOption> options,
        string instructions,
        Dictionary<string, string>? action = null,
        List<string>? history = null,
        bool checkObject = false,
        Dictionary<string, object?>? referenceContext = null,
        string? beforeEvidence = null,
        Dictionary<string, bool>? windowFacts = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var questions = new List<JevQuestion>
        {
            new(instructions, options, noneDescription: "The evidence is insufficient to determine the observed outcome; keep it unknown.", key: "outcome")
        };

        if (checkObject)
        {
            questions.Add(new JevQuestion(
                LanguagePrompts.Text("verify_object"),
                [
                    new JevOption("same_intended_object", "The observed result applies to the intended object, preserving the prior object when required or using the explicitly requested new one."),
                    new JevOption("wrong_object", "The observed result applies to a different object than intended."),
                    new JevOption("unknown", "Insufficient relevant identity evidence.")
                ],
                key: "object"));

            if (options.Any(o => o.Id == "progress"))
            {
                questions.Add(new JevQuestion(
                    LanguagePrompts.Text("verify_progress_effect"),
                    new[] { "resolved", "conflicting", "unknown" }.Select(s => new JevOption(s, LanguagePrompts.Text("progress_effect_" + s))),
                    key: "progress_effect"));
            }
        }

        var state = new Dictionary<string, object?>
        {
            ["user_goal"] = goal,
            ["observed_evidence"] = evidence,
            ["attempted_action"] = action ?? [],
            ["instruction_history"] = history ?? [],
            ["reference_context"] = referenceContext ?? []
        };

        if (beforeEvidence != null) state["before_evidence"] = beforeEvidence;
        if (windowFacts != null && windowFacts.Count > 0) state["native_window_facts"] = windowFacts;

        var judgment = await Selector.JudgeAsync(state, questions, ct).ConfigureAwait(false);
        var outcome = judgment.Answer("outcome");

        if (checkObject && outcome == "complete" && judgment.Answer("object") != "same_intended_object")
        {
            outcome = (judgment.Answer("object") == "wrong_object" && options.Any(o => o.Id == "contradicted"))
                ? "contradicted" : null;
        }

        if (checkObject && outcome == "progress" && judgment.Answer("progress_effect") != "resolved")
        {
            outcome = judgment.Answer("progress_effect") == "conflicting" ? "contradicted" : null;
        }

        // If the overarching goal is still pending or unclear, but the action's specific intermediate progress effect
        // was positively resolved (e.g. text entered, control clicked open), promote to progress!
        if (checkObject && (outcome == "pending" || outcome == null) && judgment.Answer("progress_effect") == "resolved")
        {
            outcome = "progress";
        }

        var effect = checkObject ? judgment.Answer("progress_effect") : null;
        return new OutcomeJudgment(outcome, judgment.Requests, (DateTime.UtcNow - started).TotalSeconds, effect);
    }

    public static bool NumericTargetMatches(string value, string literal, AXNode node)
    {
        if (!double.TryParse(value, out var observed) || double.IsNaN(observed) || double.IsInfinity(observed)) return false;
        var cleanLit = literal.Replace("%", "").Trim();
        if (!double.TryParse(cleanLit, out var numeric) || double.IsNaN(numeric) || double.IsInfinity(numeric)) return false;
        if (!node.Minimum.HasValue || !node.Maximum.HasValue) return false;

        var low = node.Minimum.Value;
        var high = node.Maximum.Value;
        if (double.IsNaN(low) || double.IsNaN(high) || high <= low) return false;

        var expected = literal.Contains('%') ? low + (high - low) * numeric / 100.0 : numeric;
        return expected >= low && expected <= high && Math.Abs(observed - expected) <= Math.Max(0.00001, (high - low) * 0.001);
    }

    private async Task<AXSnapshot> ObserveAfterInputAsync(int pid, AXSnapshot? boundTo = null, CancellationToken ct = default)
    {
        await Task.Delay(60, ct).ConfigureAwait(false);
        InputPermit.Check();
        var snapshot = await ReadObservation(new ObservationRequest(ObservationKind.Verification, pid)).ConfigureAwait(false);
        InputPermit.Check();

        if (snapshot.Pid != pid || ObservationForegroundPID() != pid)
        {
            throw AXFailureException.Unavailable("The target app changed after input; outcome unknown.");
        }

        if (boundTo != null && !snapshot.CanObserveAfter(boundTo, ObservationForegroundPID()))
        {
            throw AXFailureException.Unavailable("The target window changed after input; outcome unknown. No input replayed.");
        }

        return snapshot;
    }

    private Task<int> OpenUrlAsync(Uri url, InstalledApplication browser, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var lease = InputPermit.Begin(effect: true);
            var psi = new ProcessStartInfo
            {
                FileName = browser.Url,
                Arguments = url.AbsoluteUri,
                UseShellExecute = true
            };
            var proc = Process.Start(psi) ?? throw AXFailureException.Unavailable("Browser launch did not return a process.");
            return proc.Id;
        }, ct);
    }
}
