using System;

namespace WiraVoice.Core.Language;

/// <summary>
/// Aggregate accounting only. Never retains commands, app content, or credentials.
/// Port of Computah's JevCosts.swift.
/// </summary>
public sealed class JevCosts
{
    public const string PricedModel = "jev-1.13.0";
    public const double InputUsdPerMillion = 0.042;

    public struct Total
    {
        public bool Enabled { get; set; } = false;
        public DateTime Since { get; set; } = DateTime.UtcNow;
        public int Requests { get; set; } = 0;
        public int MissingUsage { get; set; } = 0;
        public int InputTokens { get; set; } = 0;
        public int UnpricedTokens { get; set; } = 0;
        public double EstimatedUsd { get; set; } = 0.0;

        public Total() { }
    }

    private readonly object _lock = new();
    private Total _total;
    private Guid _generation = Guid.NewGuid();
    private Action? _changed;

    public JevCosts(Total? total = null)
    {
        _total = total ?? new Total();
    }

    public Total Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    public void OnChange(Action callback)
    {
        lock (_lock)
        {
            _changed = callback;
        }
    }

    public void SetEnabled(bool enabled)
    {
        Action? callback;
        lock (_lock)
        {
            if (enabled && !_total.Enabled && _total.Requests == 0)
            {
                _total.Since = DateTime.UtcNow;
            }
            _total.Enabled = enabled;
            callback = _changed;
        }
        callback?.Invoke();
    }

    public void Reset()
    {
        Action? callback;
        lock (_lock)
        {
            var enabled = _total.Enabled;
            _total = new Total { Enabled = enabled };
            _generation = Guid.NewGuid();
            callback = _changed;
        }
        callback?.Invoke();
    }

    public Guid? BeginRequest()
    {
        Action? callback;
        Guid? ticket;
        lock (_lock)
        {
            if (!_total.Enabled) return null;
            _total.Requests++;
            _total.MissingUsage++;
            ticket = _generation;
            callback = _changed;
        }
        callback?.Invoke();
        return ticket;
    }

    public void Received(Guid? ticket, string model, int? inputTokens)
    {
        if (!ticket.HasValue || inputTokens is null or < 0) return;

        Action? callback;
        lock (_lock)
        {
            if (ticket.Value != _generation) return;
            _total.MissingUsage--;
            _total.InputTokens += inputTokens.Value;
            if (model == PricedModel)
            {
                _total.EstimatedUsd += (double)inputTokens.Value * InputUsdPerMillion / 1_000_000.0;
            }
            else
            {
                _total.UnpricedTokens += inputTokens.Value;
            }
            callback = _changed;
        }
        callback?.Invoke();
    }
}
