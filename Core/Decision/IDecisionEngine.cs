using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;

namespace WiraVoice.Core.Decision;

/// <summary>
/// The action decision made by TypeSafe Jev model.
/// </summary>
public record ActionDecision(
    string SelectedControlId,      // e.g. "n0_pressClick", "none_here"
    string? TextToType,            // Literal text sliced from user utterance
    double Confidence,             // Model confidence probability (0.0 to 1.0)
    string RawChoice,              // The raw choice string returned by Jev
    bool RequiresFollowupCheck     // True if closed-loop verification is needed
);

/// <summary>
/// Deep module interface for evaluating user intent against observed UI scenes.
/// </summary>
public interface IDecisionEngine
{
    Task<ActionDecision> EvaluateIntentAsync(
        string userUtterance,
        UiSceneSnapshot scene,
        CancellationToken ct = default
    );

    Task<bool> VerifyOutcomeAsync(
        UiSceneSnapshot before,
        UiSceneSnapshot after,
        ActionDecision executedAction,
        CancellationToken ct = default
    );
}
