using System.Threading;
using System.Threading.Tasks;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Deep module interface for synthesizing physical hardware mouse and keyboard input
/// with hit-test verification and focus management.
/// </summary>
public interface IInputActor
{
    Task<bool> ClickControlAsync(UiControlCandidate control, CancellationToken ct = default);
    Task<bool> TypeTextAsync(UiControlCandidate control, string text, CancellationToken ct = default);
    Task<bool> PressReturnAsync(UiControlCandidate control, CancellationToken ct = default);
}
