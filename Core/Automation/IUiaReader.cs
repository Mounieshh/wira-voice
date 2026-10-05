using System;
using System.Threading;
using System.Threading.Tasks;

namespace WiraVoice.Core.Automation;

/// <summary>
/// Deep module interface for inspecting and snapshotting the foreground window's accessibility tree.
/// </summary>
public interface IUiaReader : IDisposable
{
    /// <summary>
    /// Captures the current active foreground window, prunes non-interactive layout elements,
    /// and extracts interactive control candidates.
    /// </summary>
    Task<UiSceneSnapshot> CaptureActiveSceneAsync(int maxNodes = 1500, CancellationToken ct = default);
}
