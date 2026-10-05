using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WiraVoice.Core.Automation;

namespace WiraVoice.Core.Commands;

/// <summary>
/// One input lane. Revocation and admission share a lock; in-flight work drains
/// without holding that lock, so a new utterance can revoke authority promptly.
/// Exact port of Computah's InputControl.swift.
/// </summary>
public readonly record struct InputAuditEvent(Guid Generation, string Event, DateTime At);

public sealed class InputGate
{
    private readonly object _lock = new();
    private Guid _generation = Guid.NewGuid();
    private readonly List<InputAuditEvent> _audit = [];
    private int _inFlight = 0;
    private readonly List<TaskCompletionSource<bool>> _waiters = [];
    private readonly int _auditLimit;
    private int _dropped = 0;
    private int _effects = 0;

    public InputGate(int auditLimit = 0)
    {
        _auditLimit = Math.Max(0, auditLimit);
    }

    private void Record(InputAuditEvent ev)
    {
        if (_auditLimit <= 0) return;
        if (_audit.Count == _auditLimit)
        {
            _audit.RemoveAt(0);
            _dropped++;
        }
        _audit.Add(ev);
    }

    public int EffectCount
    {
        get
        {
            lock (_lock) return _effects;
        }
    }

    public int DroppedAuditEvents
    {
        get
        {
            lock (_lock) return _dropped;
        }
    }

    public InputPermit Revoke()
    {
        lock (_lock)
        {
            _generation = Guid.NewGuid();
            var id = _generation;
            Record(new InputAuditEvent(id, "revokedPrevious", DateTime.UtcNow));
            return new InputPermit(this, id, new InputCounter());
        }
    }

    internal void Check(Guid id)
    {
        lock (_lock)
        {
            if (_generation != id)
            {
                throw new OperationCanceledException("Input permit was revoked.");
            }
        }
    }

    internal InputLease Begin(Guid id, InputCounter counter, bool effect)
    {
        lock (_lock)
        {
            if (_generation != id)
            {
                throw new OperationCanceledException("Input permit was revoked.");
            }
            if (_inFlight != 0)
            {
                throw AXFailureException.Unavailable("Another native input is still finishing.");
            }
            _inFlight++;
            Record(new InputAuditEvent(id, effect ? "admittedEffect" : "admittedPointerMove", DateTime.UtcNow));
            if (effect)
            {
                counter.Value++;
                _effects++;
            }
            return new InputLease(this);
        }
    }

    internal void End()
    {
        List<TaskCompletionSource<bool>> ready = [];
        lock (_lock)
        {
            _inFlight--;
            if (_inFlight == 0)
            {
                ready.AddRange(_waiters);
                _waiters.Clear();
            }
        }
        foreach (var waiter in ready)
        {
            waiter.TrySetResult(true);
        }
    }

    internal int Count(InputCounter counter)
    {
        lock (_lock)
        {
            return counter.Value;
        }
    }

    public List<InputAuditEvent> Events()
    {
        lock (_lock)
        {
            return [.. _audit];
        }
    }

    public Task DrainAsync()
    {
        lock (_lock)
        {
            if (_inFlight == 0) return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(tcs);
            return tcs.Task;
        }
    }
}

public sealed class InputLease : IDisposable
{
    private readonly InputGate _gate;
    private readonly object _lock = new();
    private bool _finished = false;

    internal InputLease(InputGate gate)
    {
        _gate = gate;
    }

    public void Finish()
    {
        bool needsFinish;
        lock (_lock)
        {
            needsFinish = !_finished;
            _finished = true;
        }
        if (needsFinish)
        {
            _gate.End();
        }
    }

    public void Dispose() => Finish();
}

internal sealed class InputCounter
{
    public int Value = 0;
}

public readonly struct InputPermit
{
    private readonly InputGate _gate;
    public Guid Generation { get; }
    private readonly InputCounter _counter;

    internal InputPermit(InputGate gate, Guid generation, InputCounter counter)
    {
        _gate = gate;
        Generation = generation;
        _counter = counter;
    }

    public static InputPermit Standalone() => new InputGate().Revoke();

    public int DispatchCount => _gate.Count(_counter);

    public void Check()
    {
        _gate.Check(Generation);
    }

    public InputLease Begin(bool effect = true)
    {
        return _gate.Begin(Generation, _counter, effect);
    }

    public T Perform<T>(Func<T> operation, bool effect = true)
    {
        using var lease = Begin(effect);
        return operation();
    }

    public void Perform(Action operation, bool effect = true)
    {
        using var lease = Begin(effect);
        operation();
    }

    public void Pair(Action down, Action up, Action? hold = null)
    {
        Perform(() =>
        {
            down();
            try
            {
                hold?.Invoke();
            }
            finally
            {
                up();
            }
        });
    }
}
