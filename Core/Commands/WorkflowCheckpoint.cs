using System;
using System.Collections.Generic;
using System.Linq;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Diagnostics;
using WiraVoice.Core.Language;

namespace WiraVoice.Core.Commands;

public sealed class PendingInstruction
{
    public CommandClause Clause { get; }
    public PreparedAction Prepared { get; }
    public int DispatchCount { get; }
    public bool WasDispatched => Prepared.Permit.DispatchCount > DispatchCount;

    public PendingInstruction(CommandClause clause, PreparedAction prepared, int dispatchCount)
    {
        Clause = clause;
        Prepared = prepared;
        DispatchCount = dispatchCount;
    }
}

public readonly record struct FollowupInstruction(string Source, int StartUTF16);

public sealed class NoInputReference
{
    public int StartUTF16 { get; }
    public AXSnapshot Snapshot { get; }

    public NoInputReference(int startUTF16, AXSnapshot snapshot)
    {
        StartUTF16 = startUTF16;
        Snapshot = snapshot;
    }
}

public sealed class CheckpointState
{
    public string ExecutionStatus { get; set; } = "planning";
    public string LastStopReason { get; set; } = "";
    public List<string> BackgroundProgress { get; set; } = [];
    public List<FollowupInstruction> Followups { get; set; } = [];
    public Dictionary<int, List<string>> Revisions { get; set; } = [];
    public int Cursor { get; set; } = 0;
    public List<string> Verified { get; set; } = [];
    public List<string> History { get; set; } = [];
    public Dictionary<string, object?> ReferenceContext { get; set; } = [];
    public HashSet<string> ActivatedApps { get; set; } = [];
    public HashSet<string> Excluded { get; set; } = [];
    public CommandClause? Active { get; set; }
    public PendingInstruction? Pending { get; set; }
    public AXSnapshot? Observed { get; set; }
    public NoInputReference? NoInputReference { get; set; }

    public Dictionary<string, object?> VerificationContext
    {
        get
        {
            var context = new Dictionary<string, object?>(ReferenceContext)
            {
                ["verified_instructions"] = BackgroundProgress.Concat(Verified).ToList()
            };
            return context;
        }
    }

    public AXSnapshot? NoInputBinding =>
        NoInputReference != null && NoInputReference.StartUTF16 == Cursor ? NoInputReference.Snapshot : null;

    private static string CompletionNote(CommandClause clause, string source) =>
        $"Completed and observed source interval {clause.StartUTF16}..<{clause.EndUTF16}: {clause.ModelText(source)}";

    public void FinishClause(CommandClause clause, string source, AXSnapshot? observed)
    {
        Cursor = clause.EndUTF16;
        NoInputReference = null;
        Verified.Add(CompletionNote(clause, source));
        Active = null;
        History = [];
        Excluded = [];
        ActivatedApps = [];
        Pending = null;
        if (observed != null) Observed = observed;
    }
}

/// <summary>
/// Original source and observed progress survive task cancellation.
/// Exact port of Computah's WorkflowCheckpoint.swift.
/// </summary>
public sealed class WorkflowCheckpoint
{
    public string Source { get; }
    private readonly object _lock = new();
    private CheckpointState _state = new();
    private Guid? _generation;

    public WorkflowCheckpoint(string source)
    {
        Source = source;
    }

    public CheckpointState Read()
    {
        lock (_lock)
        {
            return _state;
        }
    }

    public void Authorize(InputPermit permit)
    {
        lock (_lock)
        {
            permit.Check();
            _generation = permit.Generation;
        }
    }

    public void Update(Guid expectedGeneration, Action<CheckpointState> mutate)
    {
        lock (_lock)
        {
            if (_generation != expectedGeneration) return;
            mutate(_state);
        }
    }

    public Dictionary<string, object?> SemanticState
    {
        get
        {
            var value = Read();
            var remainingLen = Source.Length - value.Cursor;
            var remaining = remainingLen > 0
                ? SensitiveText.Redact(Source, new SensitiveText.TextRange(value.Cursor, remainingLen))
                : "";

            return new Dictionary<string, object?>
            {
                ["execution_status"] = value.ExecutionStatus,
                ["last_stop_reason"] = value.LastStopReason,
                ["has_unfinished_work"] = value.Cursor < Source.Length,
                ["phase"] = value.Pending != null ? "dispatched_or_pending" : value.Active != null ? "active_clause" : "between_clauses",
                ["previous_request"] = Source,
                ["remaining_request"] = remaining,
                ["active_instruction"] = value.Active?.ModelText(Source) ?? "",
                ["revisions"] = value.Revisions.GetValueOrDefault(value.Cursor, []),
                ["verified_progress"] = value.BackgroundProgress.Concat(value.Verified).ToList(),
                ["intermediate_progress"] = value.History,
                ["pending_action"] = value.Pending != null
                    ? new Dictionary<string, object?>
                    {
                        ["action"] = value.Pending.Prepared.Description ?? "",
                        ["dispatched"] = value.Pending.WasDispatched,
                        ["outcome"] = "unknown until observed"
                    }
                    : new Dictionary<string, object?>(),
                ["bound_scene"] = (value.NoInputBinding ?? value.Observed)?.SelectionEvidence ?? "Unknown"
            };
        }
    }
}
