using System;
using System.Threading;

namespace WiraVoice.Core.Orchestration;

/// <summary>
/// Thread-safe authorization permit for physical input dispatch.
/// A permit is instantly revoked when a new speech turn begins or the user interrupts.
/// </summary>
public sealed class InputPermit : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _isRevoked = false;
    private readonly object _lock = new();

    public string TurnId { get; }
    public CancellationToken Token => _cts.Token;
    public bool IsValid => !_isRevoked && !_cts.IsCancellationRequested;

    public InputPermit(string turnId)
    {
        TurnId = turnId;
    }

    public void Revoke()
    {
        lock (_lock)
        {
            if (_isRevoked) return;
            _isRevoked = true;
            try
            {
                _cts.Cancel();
            }
            catch { }
        }
    }

    public void Check()
    {
        lock (_lock)
        {
            if (_isRevoked || _cts.IsCancellationRequested)
            {
                throw new OperationCanceledException($"Input permit for turn '{TurnId}' was revoked.");
            }
        }
    }

    public void Dispose()
    {
        Revoke();
        _cts.Dispose();
    }
}
