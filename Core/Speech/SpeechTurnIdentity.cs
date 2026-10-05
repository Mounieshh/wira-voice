using System;

namespace WiraVoice.Core.Speech;

/// <summary>
/// Provider metadata orders speech messages; text never determines turn identity.
/// Port of Computah's SpeechTurnIdentity.swift.
/// </summary>
public sealed class SpeechTurnIdentity
{
    private readonly Guid _session;
    private int _lastSequence = -1;
    private int _latestTurn = -1;

    public SpeechTurnIdentity(Guid? session = null)
    {
        _session = session ?? Guid.NewGuid();
        _lastSequence = -1;
        _latestTurn = -1;
    }

    public string? Accept(int? sequence, int turn)
    {
        if (turn < _latestTurn)
        {
            return null;
        }

        if (sequence.HasValue)
        {
            if (sequence.Value <= _lastSequence)
            {
                return null;
            }
            _lastSequence = sequence.Value;
        }

        _latestTurn = turn;
        return $"{_session}:{turn}";
    }
}
