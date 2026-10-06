using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Language;
using WiraVoice.Core.Speech;

namespace WiraVoice.Core.Commands;

public enum CommandTurnOutcome { Completed, Cancelled, Unconfirmed, Superseded }

public readonly record struct CommandTurnResult(string TurnID, CommandTurnOutcome Outcome, string Status);

/// <summary>
/// One active goal and one newest turn. Semantic append is explicit; submissions
/// are never placed into an automatic FIFO.
/// Exact port of Computah's CommandCoordinator.swift.
/// </summary>
public sealed class CommandCoordinator : IAsyncDisposable
{
    public Action<string, bool>? OnStatus { get; set; }
    public Action<GoalRelationship>? OnRelationship { get; set; }
    public Action<string>? OnTurnStarted { get; set; }
    public Action<CommandTurnResult>? OnTurnFinished { get; set; }
    public Action<WorkflowResult, bool>? OnResult { get; set; }
    public Func<InputPermit, Task>? BeforeObservation { get; set; }

    public List<InputAuditEvent> InputAudit => _gate.Events();
    public int InputEffectCount => _gate.EffectCount;
    public int DroppedInputAuditEvents => _gate.DroppedAuditEvents;

    public bool Running { get; private set; } = false;

    private readonly CommandEngine _engine;
    private readonly InputGate _gate;
    private InputPermit _permit;
    private CancellationTokenSource? _taskCts;
    private Task? _currentTask;
    private WorkflowCheckpoint? _checkpoint;
    private string? _turnID;
    private string? _activeSubmission;
    private readonly List<string> _finalized = [];
    private Task<PreparedAction>? _eagerTask;
    private string? _eagerText;
    private List<string> _unresolved = [];

    private readonly ISpeechEngine? _speechEngine;

    public CommandCoordinator(CommandEngine engine, int auditLimit = 0, ISpeechEngine? speechEngine = null)
    {
        _gate = new InputGate(auditLimit);
        _engine = engine;
        _permit = _gate.Revoke();
        _speechEngine = speechEngine;

        if (_speechEngine != null)
        {
            _speechEngine.OnSpeechEvent += OnSpeechEventReceived;
            _speechEngine.OnStatusChanged += status => OnStatus?.Invoke(status, false);
        }
    }

    private void OnSpeechEventReceived(SpeechTurnEvent turnEvent)
    {
        switch (turnEvent.Type)
        {
            case SpeechTurnType.StartOfTurn:
                OnStatus?.Invoke("Listening to voice…", false);
                break;
            case SpeechTurnType.EagerEndOfTurn:
                PrepareEager(turnEvent.Utterance, turnEvent.TurnId);
                break;
            case SpeechTurnType.TurnResumed:
                DiscardEager();
                break;
            case SpeechTurnType.EndOfTurn:
                Submit(turnEvent.Utterance, turnEvent.TurnId);
                break;
        }
    }

    public Task StartSpeechAsync(CancellationToken ct = default) =>
        _speechEngine != null ? _speechEngine.StartAsync(ct) : Task.CompletedTask;

    public Task StopSpeechAsync() =>
        _speechEngine != null ? _speechEngine.StopAsync() : Task.CompletedTask;

    /// <summary>
    /// A transport event suspends old authority without inferring semantic intent.
    /// </summary>
    public void BeginTurn(string id)
    {
        if (_turnID == id || _finalized.Contains(id)) return;

        FinishTurn(CommandTurnOutcome.Superseded, "A newer input turn suspended this request.");
        _turnID = id;
        DiscardEager();
        _permit = _gate.Revoke();
        try { _checkpoint?.Authorize(_permit); } catch { }

        _taskCts?.Cancel();
        _taskCts = null;
        Running = false;
        Console.WriteLine($"[Coordinator] Turn began: '{id}'");
        OnStatus?.Invoke("Listening to the new request…", false);
    }

    private async Task<PreparedAction> PrepareFreshAsync(
        string text, InputPermit ticket, Dictionary<string, object?>? relationship, Dictionary<string, object?> conversation, CancellationToken ct)
    {
        _engine.InputPermit = ticket;
        if (BeforeObservation != null)
        {
            await BeforeObservation(ticket).ConfigureAwait(false);
        }
        return await _engine.PrepareAsync(
            text, 0, relationshipContext: relationship, conversationContext: conversation, ct: ct).ConfigureAwait(false);
    }

    public void DiscardEager()
    {
        _eagerTask = null;
        _eagerText = null;
    }

    public void PrepareEager(string text, string id)
    {
        BeginTurn(id);
        if (_finalized.Contains(id) || _eagerText == text) return;

        DiscardEager();
        _eagerText = text;
        var ticket = _permit;
        var prior = _checkpoint;
        var relationship = RelationshipContext(prior);
        var conversation = ConversationContext(prior);

        var cts = new CancellationTokenSource();
        _eagerTask = Task.Run(async () =>
        {
            await _gate.DrainAsync().ConfigureAwait(false);
            ticket.Check();
            return await PrepareFreshAsync(text, ticket, relationship, conversation, cts.Token).ConfigureAwait(false);
        }, cts.Token);
    }

    public void Submit(string original, string? id = null)
    {
        var turnId = id ?? Guid.NewGuid().ToString();
        var text = original.Trim();
        if (string.IsNullOrEmpty(text) || _finalized.Contains(turnId)) return;

        BeginTurn(turnId);
        _finalized.Add(turnId);
        if (_finalized.Count > 128) _finalized.RemoveAt(0);

        Console.WriteLine($"[Coordinator] Submitting command '{turnId}': \"{text}\"");

        var ready = _eagerText == text ? _eagerTask : null;
        if (ready == null) DiscardEager(); else { _eagerTask = null; _eagerText = null; }

        var ticket = _permit;
        var prior = _checkpoint;
        var relationship = RelationshipContext(prior);
        var conversation = ConversationContext(prior);

        _unresolved.Add(text);
        if (_unresolved.Count > 8) _unresolved.RemoveAt(0);

        _activeSubmission = turnId;
        OnTurnStarted?.Invoke(turnId);
        Running = true;
        OnStatus?.Invoke("Finding an action…", true);

        _taskCts?.Cancel();
        _taskCts = new CancellationTokenSource();
        var ct = _taskCts.Token;

        _currentTask = Task.Run(async () =>
        {
            var start = DateTime.UtcNow;

            void Report(WorkflowResult res, bool current)
            {
                Console.WriteLine($"[Workflow] Complete={res.Complete}, Status=\"{res.Status}\", Events={res.Events.Count}, Elapsed={res.Elapsed:F2}s");
                foreach (var ev in res.Events)
                {
                    Console.WriteLine($"  -> Action: {ev.Action} [{ev.Outcome}]");
                }
                OnResult?.Invoke(new WorkflowResult(
                    res.Command, res.Status, res.Complete, res.Events,
                    (DateTime.UtcNow - start).TotalSeconds, _engine.Selector.Usage.Snapshot), current);
            }

            try
            {
                await _gate.DrainAsync().ConfigureAwait(false);
                ticket.Check();

                PreparedAction initial;
                if (ready != null)
                {
                    initial = await ready.ConfigureAwait(false);
                }
                else
                {
                    initial = await PrepareFreshAsync(text, ticket, relationship, conversation, ct).ConfigureAwait(false);
                }

                var prepared = initial;
                ticket.Check();

                var rel = prepared.Interpretation?.Relationship ?? GoalRelationship.Replace;
                if (prior != null && (rel is GoalRelationship.Resume or GoalRelationship.Revise or GoalRelationship.Append))
                {
                    var cursor = prior.Read().Cursor;
                    prior.Authorize(ticket);
                    if (!await _engine.ReconcileAsync(prior, ct).ConfigureAwait(false))
                    {
                        Publish("The earlier action's result is still unknown. I need that resolved before continuing it.", ticket);
                        return;
                    }

                    if (rel == GoalRelationship.Revise && prior.Read().Cursor != cursor)
                    {
                        prepared = await _engine.PrepareAsync(text, 0,
                            relationshipContext: RelationshipContext(prior), conversationContext: ConversationContext(prior), ct: ct).ConfigureAwait(false);
                        rel = prepared.Interpretation?.Relationship ?? GoalRelationship.Unclear;
                    }
                }

                ticket.Check();

                if (rel == GoalRelationship.Replace && prepared.Interpretation?.Route == null && !prepared.HasExecutablePlan)
                {
                    var res = await _engine.RunWorkflowAsync(text, preparedFirst: prepared, ct: ct).ConfigureAwait(false);
                    Report(res, true);
                    Publish(res.Status, ticket, res.Complete ? CommandTurnOutcome.Completed : CommandTurnOutcome.Unconfirmed);
                    return;
                }

                OnRelationship?.Invoke(rel);

                switch (rel)
                {
                    case GoalRelationship.Cancel:
                        _checkpoint = null;
                        _unresolved.Clear();
                        Publish("Stopped the previous task.", ticket, CommandTurnOutcome.Cancelled);
                        return;
                    case GoalRelationship.Unclear:
                        Publish("I'm paused. Should this replace the previous task or change it?", ticket);
                        return;
                    case GoalRelationship.Replace:
                        _checkpoint = new WorkflowCheckpoint(text);
                        break;
                    case GoalRelationship.Resume:
                    case GoalRelationship.Revise:
                    case GoalRelationship.Append:
                        if (prior == null)
                        {
                            Publish("There is no previous instruction to continue. Please state the next action.", ticket);
                            return;
                        }
                        _checkpoint = prior;
                        prior.Authorize(ticket);
                        if (rel == GoalRelationship.Revise)
                        {
                            prior.Update(ticket.Generation, s =>
                            {
                                if (!s.Revisions.TryGetValue(s.Cursor, out var revList))
                                {
                                    revList = [];
                                    s.Revisions[s.Cursor] = revList;
                                }
                                revList.Add(text);
                            });
                        }
                        else if (rel == GoalRelationship.Append)
                        {
                            var followupStart = prepared.Interpretation?.FollowupStartUTF16;
                            if (!followupStart.HasValue)
                            {
                                Publish("The added instruction's source boundary is unclear.", ticket);
                                return;
                            }
                            prior.Update(ticket.Generation, s =>
                            {
                                s.Followups.Add(new FollowupInstruction(text, followupStart.Value));
                            });
                        }
                        break;
                }

                _unresolved.Clear();
                if (_checkpoint == null) return;
                var active = _checkpoint;
                PreparedAction? first = rel == GoalRelationship.Replace ? prepared : null;

                while (true)
                {
                    ticket.Check();
                    var res = await _engine.RunWorkflowAsync(active.Source, preparedFirst: first, supplied: active, ct: ct).ConfigureAwait(false);
                    bool isCurrent = false;
                    try { ticket.Check(); isCurrent = true; } catch { }

                    Report(res, isCurrent);
                    if (!isCurrent) return;

                    if (!res.Complete)
                    {
                        Publish(res.Status, ticket, res.Complete ? CommandTurnOutcome.Completed : CommandTurnOutcome.Unconfirmed);
                        return;
                    }

                    var followups = active.Read().Followups;
                    if (followups.Count == 0)
                    {
                        Publish(res.Status, ticket, res.Complete ? CommandTurnOutcome.Completed : CommandTurnOutcome.Unconfirmed);
                        return;
                    }

                    var next = followups[0];
                    var newCheckpoint = new WorkflowCheckpoint(next.Source);
                    newCheckpoint.Authorize(ticket);

                    var priorProgress = active.Read().BackgroundProgress
                        .Concat([$"The preceding request completed: {active.Source}"])
                        .Concat(active.Read().Verified)
                        .ToList();

                    newCheckpoint.Update(ticket.Generation, s =>
                    {
                        s.Cursor = next.StartUTF16;
                        s.Followups = followups.Skip(1).ToList();
                        s.BackgroundProgress = priorProgress;
                        s.ReferenceContext = ConversationContext(active);
                    });

                    _checkpoint = newCheckpoint;
                    active = newCheckpoint;
                    first = null;
                }
            }
            catch (Exception ex)
            {
                bool isCurrent = false;
                try { ticket.Check(); isCurrent = true; } catch { }
                if (!isCurrent) return;

                var status = CommandEngine.FailureStatus(ex, ticket.DispatchCount > 0 || _checkpoint?.Read().Pending?.WasDispatched == true);
                OnResult?.Invoke(new WorkflowResult(text, status, false, [], (DateTime.UtcNow - start).TotalSeconds, _engine.Selector.Usage.Snapshot), true);
                Publish(status, ticket);
            }
        }, ct);
    }

    private Dictionary<string, object?>? RelationshipContext(WorkflowCheckpoint? prior)
    {
        if (prior == null) return null;
        var state = prior.Read();
        if (state.Cursor >= prior.Source.Length && state.Pending == null) return null;
        var context = prior.SemanticState;
        context["unresolved_newer_utterances"] = _unresolved;
        return context;
    }

    private Dictionary<string, object?> ConversationContext(WorkflowCheckpoint? prior)
    {
        var context = new Dictionary<string, object?> { ["unresolved_utterances"] = _unresolved };
        if (prior != null && prior.Read().Cursor >= prior.Source.Length && prior.Read().Pending == null)
        {
            context["completed_request"] = prior.Source;
            context["observed_result"] = prior.Read().Observed?.SelectionEvidence ?? "Unknown";
        }
        return context;
    }

    private void FinishTurn(CommandTurnOutcome outcome, string status)
    {
        var id = _activeSubmission;
        if (id == null) return;
        _activeSubmission = null;
        OnTurnFinished?.Invoke(new CommandTurnResult(id, outcome, status));
    }

    private void Publish(string message, InputPermit ticket, CommandTurnOutcome outcome = CommandTurnOutcome.Unconfirmed)
    {
        try { ticket.Check(); } catch { return; }
        Running = false;
        Console.WriteLine($"[Coordinator] Turn outcome: {outcome} | Status: \"{message}\"");
        FinishTurn(outcome, status: message);
        OnStatus?.Invoke(message, false);
    }

    public void Shutdown()
    {
        FinishTurn(CommandTurnOutcome.Superseded, "Execution stopped during shutdown.");
        _permit = _gate.Revoke();
        try { _checkpoint?.Authorize(_permit); } catch { }
        _taskCts?.Cancel();
        DiscardEager();
    }

    public async ValueTask DisposeAsync()
    {
        Shutdown();
        if (_speechEngine is IAsyncDisposable asyncEngine)
        {
            await asyncEngine.DisposeAsync().ConfigureAwait(false);
        }
    }
}
