using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Decision;
using WiraVoice.Core.Orchestration;
using WiraVoice.Core.Speech;
using Xunit;

namespace WiraVoice.Tests;

public class FakeUiaReader : IUiaReader
{
    public UiSceneSnapshot ActiveScene { get; set; }

    public FakeUiaReader()
    {
        var box = new Rectangle(10, 10, 50, 20);
        var btn = new UiControlCandidate("n0_pressClick", "Submit", "Button", "", box, "pressClick", null);
        ActiveScene = new UiSceneSnapshot(IntPtr.Zero, 123, "TestApp", "Test Window", new[] { btn });
    }

    public Task<UiSceneSnapshot> CaptureActiveSceneAsync(int maxNodes = 1500, CancellationToken ct = default)
    {
        return Task.FromResult(ActiveScene);
    }

    public void Dispose() { }
}

public class FakeDecisionEngine : IDecisionEngine
{
    public ActionDecision DecisionToReturn { get; set; } = new("n0_pressClick", null, 0.95, "n0_pressClick", true);

    public Task<ActionDecision> EvaluateIntentAsync(string userUtterance, UiSceneSnapshot scene, CancellationToken ct = default)
    {
        return Task.FromResult(DecisionToReturn);
    }

    public Task<bool> VerifyOutcomeAsync(UiSceneSnapshot before, UiSceneSnapshot after, ActionDecision executedAction, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}

public class CoordinatorTests
{
    [Fact]
    public void InputPermit_RevokesCorrectly()
    {
        using var permit = new InputPermit("turn_1");
        Assert.True(permit.IsValid);

        permit.Revoke();
        Assert.False(permit.IsValid);
        Assert.True(permit.Token.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() => permit.Check());
    }

    [Fact]
    public async Task CommandCoordinator_ExecutesSpeculativeAction()
    {
        var speech = new FakeSpeechEngine(SpeechEngineMode.CloudDeepgram);
        var uia = new FakeUiaReader();
        var decision = new FakeDecisionEngine();
        var input = new FakeInputActor();

        await using var coordinator = new CommandCoordinator(speech, uia, decision, input);
        await coordinator.StartAsync();

        bool actionCompleted = false;
        coordinator.OnActionCompleted += (act, verified) =>
        {
            actionCompleted = true;
        };

        // 1. User starts speaking
        speech.SimulateSpeechTurn(string.Empty, SpeechTurnType.StartOfTurn, "turn_100");

        // 2. User about to finish speaking -> speculative step
        speech.SimulateSpeechTurn("click submit", SpeechTurnType.EagerEndOfTurn, "turn_100");
        await Task.Delay(100);

        // 3. User finishes speaking -> execute!
        speech.SimulateSpeechTurn("click submit", SpeechTurnType.EndOfTurn, "turn_100");
        await Task.Delay(250);

        Assert.True(actionCompleted);
        Assert.NotNull(input.LastClickedControl);
        Assert.Equal("Submit", input.LastClickedControl.Name);
    }
}
