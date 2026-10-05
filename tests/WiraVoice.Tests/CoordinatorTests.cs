using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Commands;
using WiraVoice.Core.Language;
using WiraVoice.Core.Speech;
using Xunit;

namespace WiraVoice.Tests;

public class CoordinatorTests
{
    [Fact]
    public void SpeechTurnIdentity_AcceptsInOrder_RejectsStaleOrDuplicate()
    {
        var identity = new SpeechTurnIdentity();

        // Initial turn
        var id0 = identity.Accept(0, 0);
        Assert.NotNull(id0);
        Assert.EndsWith(":0", id0);

        // Newer sequence in turn 1
        var id1 = identity.Accept(1, 1);
        Assert.NotNull(id1);
        Assert.EndsWith(":1", id1);

        // Stale sequence from Deepgram packet jitter (sequence 0 <= 1) -> rejected
        var staleSeq = identity.Accept(0, 2);
        Assert.Null(staleSeq);

        // Stale turn (turn 0 < 1) -> rejected
        var staleTurn = identity.Accept(2, 0);
        Assert.Null(staleTurn);

        // Valid forward sequence & turn
        var valid = identity.Accept(2, 2);
        Assert.NotNull(valid);
        Assert.EndsWith(":2", valid);
    }

    [Fact]
    public void InputGate_And_InputPermit_EnforcesRevocationAndLeaseSafety()
    {
        var gate = new InputGate(auditLimit: 10);
        var permit1 = gate.Revoke();
        Assert.Equal(0, permit1.DispatchCount);

        // Admitting an effect increases dispatch count
        using (var lease = permit1.Begin(effect: true))
        {
            Assert.Equal(1, permit1.DispatchCount);
            Assert.Equal(1, gate.EffectCount);
        }

        // Another permit revokes previous permit
        var permit2 = gate.Revoke();
        Assert.Throws<OperationCanceledException>(() => permit1.Check());
        permit2.Check(); // permit2 is valid
    }

    [Fact]
    public void WorkflowCheckpoint_TracksCursorAndVerifiedProgress()
    {
        var checkpoint = new WorkflowCheckpoint("Click Submit and type hello");
        var state = checkpoint.Read();
        Assert.Equal(0, state.Cursor);
        Assert.Empty(state.Verified);

        var permit = InputPermit.Standalone();
        checkpoint.Authorize(permit);

        var clause1 = new CommandClause("Click Submit", 0, 12);
        checkpoint.Update(permit.Generation, s =>
        {
            s.FinishClause(clause1, checkpoint.Source, null);
        });

        var updated = checkpoint.Read();
        Assert.Equal(12, updated.Cursor);
        Assert.Single(updated.Verified);
        Assert.Contains("0..<12", updated.Verified[0]);
    }

    [Fact]
    public void AXGrouping_ExtractsPrimaryCandidates_AndGroupsCorrectly()
    {
        var nodes = new List<AXNode>
        {
            new() { Id = 0, Role = "AXWindow", Label = "Main Window", Frame = new Rectangle(0, 0, 800, 600) },
            new() { Id = 1, Parent = 0, Role = "AXToolbar", Label = "Nav Bar", Frame = new Rectangle(0, 0, 800, 50) },
            new() { Id = 2, Parent = 1, Role = "AXTextField", Label = "Address", Value = "https://example.com", Frame = new Rectangle(10, 10, 300, 30), Enabled = true, Visible = true },
            new() { Id = 3, Parent = 1, Role = "AXButton", Label = "Go", Actions = ["AXPress"], Frame = new Rectangle(320, 10, 60, 30), Enabled = true, Visible = true }
        };

        var snapshot = new AXSnapshot(1234, "TestBrowser", "Main Window", nodes, partial: false);
        var candidates = AXGrouping.Candidates(snapshot);

        // Edit control should have typeText and replaceText
        Assert.Contains(candidates, c => c.NodeID == 2 && c.Operation == AXOperation.TypeText);
        Assert.Contains(candidates, c => c.NodeID == 2 && c.Operation == AXOperation.ReplaceText);

        // Button should have pressClick
        Assert.Contains(candidates, c => c.NodeID == 3 && c.Operation == AXOperation.PressClick);

        // Groups
        var groups = AXGrouping.Groups(snapshot, candidates);
        Assert.NotEmpty(groups);
    }
}
