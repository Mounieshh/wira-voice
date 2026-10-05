using System;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;
using WiraVoice.Core.Decision;
using WiraVoice.Core.Speech;

namespace WiraVoice.Core.Orchestration;

/// <summary>
/// Central orchestration coordinator connecting Speech, UI Automation, Jev AI, and Input Dispatch.
/// Implements latency-hiding speculative execution during speech turns and closed-loop verification.
/// </summary>
public sealed class CommandCoordinator : IAsyncDisposable
{
    private readonly ISpeechEngine _speechEngine;
    private readonly IUiaReader _uiaReader;
    private readonly IDecisionEngine _decisionEngine;
    private readonly IInputActor _inputActor;

    private InputPermit? _activePermit;
    private (string Transcript, UiSceneSnapshot Scene, ActionDecision Decision)? _speculativePlan;
    private readonly object _planLock = new();

    public event Action<string>? OnStatusChanged;
    public event Action<string, bool, string>? OnTranscript;
    public event Action<ActionDecision, bool>? OnActionCompleted;

    public CommandCoordinator(
        ISpeechEngine speechEngine,
        IUiaReader uiaReader,
        IDecisionEngine decisionEngine,
        IInputActor inputActor)
    {
        _speechEngine = speechEngine;
        _uiaReader = uiaReader;
        _decisionEngine = decisionEngine;
        _inputActor = inputActor;

        _speechEngine.OnSpeechEvent += OnSpeechEventReceived;
        _speechEngine.OnTranscript += (text, isFinal, turnId) => OnTranscript?.Invoke(text, isFinal, turnId);
        _speechEngine.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
    }

    public Task StartAsync(CancellationToken ct = default) => _speechEngine.StartAsync(ct);

    public Task StopAsync()
    {
        _activePermit?.Revoke();
        _speculativePlan = null;
        return _speechEngine.StopAsync();
    }

    private void OnSpeechEventReceived(SpeechTurnEvent turnEvent)
    {
        switch (turnEvent.Type)
        {
            case SpeechTurnType.StartOfTurn:
                HandleStartOfTurn(turnEvent.TurnId);
                break;

            case SpeechTurnType.EagerEndOfTurn:
                _ = HandleEagerEndOfTurnAsync(turnEvent.Utterance, turnEvent.TurnId);
                break;

            case SpeechTurnType.TurnResumed:
                HandleTurnResumed();
                break;

            case SpeechTurnType.EndOfTurn:
                _ = HandleEndOfTurnAsync(turnEvent.Utterance, turnEvent.TurnId);
                break;
        }
    }

    private void HandleStartOfTurn(string turnId)
    {
        lock (_planLock)
        {
            _activePermit?.Revoke();
            _activePermit = new InputPermit(turnId);
            _speculativePlan = null;
        }
        OnStatusChanged?.Invoke("Listening…");
    }

    private async Task HandleEagerEndOfTurnAsync(string utterance, string turnId)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return;

        InputPermit? currentPermit;
        lock (_planLock)
        {
            currentPermit = _activePermit;
            if (currentPermit == null || currentPermit.TurnId != turnId || !currentPermit.IsValid)
                return;
        }

        try
        {
            OnStatusChanged?.Invoke("Speculating next action…");

            // 1. Snapshot active window while user is ending their speech
            var scene = await _uiaReader.CaptureActiveSceneAsync(1500, currentPermit.Token);
            currentPermit.Check();

            // 2. Query TypeSafe Jev in background
            var decision = await _decisionEngine.EvaluateIntentAsync(utterance, scene, currentPermit.Token);
            currentPermit.Check();

            lock (_planLock)
            {
                if (currentPermit.IsValid)
                {
                    _speculativePlan = (utterance, scene, decision);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Speculation notice: {ex.Message}");
        }
    }

    private void HandleTurnResumed()
    {
        lock (_planLock)
        {
            _speculativePlan = null;
        }
        OnStatusChanged?.Invoke("Speech resumed…");
    }

    private async Task HandleEndOfTurnAsync(string utterance, string turnId)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return;

        InputPermit? currentPermit;
        (string Transcript, UiSceneSnapshot Scene, ActionDecision Decision)? planToExecute = null;

        lock (_planLock)
        {
            currentPermit = _activePermit;
            if (currentPermit == null || currentPermit.TurnId != turnId || !currentPermit.IsValid)
                return;

            // Check if our eager speculative plan matches the finalized transcript
            if (_speculativePlan != null &&
                string.Equals(_speculativePlan.Value.Transcript.Trim(), utterance.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                planToExecute = _speculativePlan;
                _speculativePlan = null;
            }
        }

        try
        {
            UiSceneSnapshot scene;
            ActionDecision decision;

            if (planToExecute != null)
            {
                // INSTANT SPECULATIVE EXECUTION (<10ms perceived latency!)
                scene = planToExecute.Value.Scene;
                decision = planToExecute.Value.Decision;
                OnStatusChanged?.Invoke("Executing speculative action…");
            }
            else
            {
                // Fresh evaluation if speculative transcript diverged
                OnStatusChanged?.Invoke("Evaluating intent…");
                scene = await _uiaReader.CaptureActiveSceneAsync(1500, currentPermit.Token);
                currentPermit.Check();

                decision = await _decisionEngine.EvaluateIntentAsync(utterance, scene, currentPermit.Token);
                currentPermit.Check();
            }

            if (decision.SelectedControlId == "none_here")
            {
                OnStatusChanged?.Invoke("No applicable action found.");
                OnActionCompleted?.Invoke(decision, false);
                return;
            }

            // Dispatch the physical input
            bool executed = await DispatchActionAsync(decision, scene, currentPermit.Token);
            if (!executed)
            {
                OnStatusChanged?.Invoke("Action could not be dispatched.");
                OnActionCompleted?.Invoke(decision, false);
                return;
            }

            // Closed-loop verification: re-read active scene to confirm state change
            Thread.Sleep(100); // Allow OS UI animation to complete
            var afterScene = await _uiaReader.CaptureActiveSceneAsync(1500, currentPermit.Token);
            bool verified = await _decisionEngine.VerifyOutcomeAsync(scene, afterScene, decision, currentPermit.Token);

            OnStatusChanged?.Invoke(verified ? $"Verified: {decision.SelectedControlId}" : "Executed (unverified)");
            OnActionCompleted?.Invoke(decision, verified);
        }
        catch (OperationCanceledException)
        {
            OnStatusChanged?.Invoke("Command canceled.");
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Execution error: {ex.Message}");
        }
    }

    private async Task<bool> DispatchActionAsync(ActionDecision decision, UiSceneSnapshot scene, CancellationToken ct)
    {
        UiControlCandidate? target = null;
        foreach (var c in scene.InteractiveControls)
        {
            if (c.Id == decision.SelectedControlId)
            {
                target = c;
                break;
            }
        }

        if (target == null) return false;

        switch (target.SupportedOperation)
        {
            case "typeText":
                string text = decision.TextToType ?? string.Empty;
                return await _inputActor.TypeTextAsync(target, text, ct);

            case "select":
            case "pressClick":
            default:
                return await _inputActor.ClickControlAsync(target, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _uiaReader.Dispose();
        if (_speechEngine is IAsyncDisposable asyncEngine)
        {
            await asyncEngine.DisposeAsync();
        }
    }
}
