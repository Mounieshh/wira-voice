using System;

namespace WiraVoice.Core.Language;

/// <summary>
/// Attempts are counted at the HTTP boundary. Missing provider usage stays unknown.
/// Port of Computah's ModelUsage.swift.
/// </summary>
public struct ModelUsage : IEquatable<ModelUsage>
{
    public int Requests { get; set; } = 0;
    public int ReportedInputTokens { get; set; } = 0;
    public int RequestsWithoutTokenUsage { get; set; } = 0;

    public int? InputTokens => RequestsWithoutTokenUsage == 0 ? ReportedInputTokens : null;

    public ModelUsage() { }

    public ModelUsage(int requests, int reportedInputTokens, int requestsWithoutTokenUsage)
    {
        Requests = requests;
        ReportedInputTokens = reportedInputTokens;
        RequestsWithoutTokenUsage = requestsWithoutTokenUsage;
    }

    public ModelUsage Adding(ModelUsage other)
    {
        return new ModelUsage(
            Requests + other.Requests,
            ReportedInputTokens + other.ReportedInputTokens,
            RequestsWithoutTokenUsage + other.RequestsWithoutTokenUsage
        );
    }

    public ModelUsage Since(ModelUsage earlier)
    {
        return new ModelUsage(
            Requests - earlier.Requests,
            ReportedInputTokens - earlier.ReportedInputTokens,
            Math.Max(0, RequestsWithoutTokenUsage - earlier.RequestsWithoutTokenUsage)
        );
    }

    public bool Equals(ModelUsage other) =>
        Requests == other.Requests &&
        ReportedInputTokens == other.ReportedInputTokens &&
        RequestsWithoutTokenUsage == other.RequestsWithoutTokenUsage;

    public override bool Equals(object? obj) => obj is ModelUsage other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Requests, ReportedInputTokens, RequestsWithoutTokenUsage);
}

public sealed class ModelUsageTracker
{
    private readonly object _lock = new();
    private ModelUsage _usage = new();

    public ModelUsage Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _usage;
            }
        }
    }

    public void BeginRequest()
    {
        lock (_lock)
        {
            _usage.Requests++;
            _usage.RequestsWithoutTokenUsage++;
        }
    }

    public void Received(int? inputTokens)
    {
        if (inputTokens is null or < 0) return;

        lock (_lock)
        {
            _usage.ReportedInputTokens += inputTokens.Value;
            _usage.RequestsWithoutTokenUsage = Math.Max(0, _usage.RequestsWithoutTokenUsage - 1);
        }
    }
}
