namespace Threadsmith.Execution;

using System.Text;
using Threadsmith.Core;

/// <summary>A sanitized transient display fragment, never a durable event or provider replay block.</summary>
public sealed record AgentDisplayText(SessionId SessionId, RunId RunId, string Text, bool IsReasoning, bool CompletesResponse = false, Guid ResponseId = default);

/// <summary>Nonblocking bounded child display channel with explicit loss reporting.</summary>
public sealed class AgentDisplayStream
{
    /// <summary>Initializes a new instance of the <see cref="AgentDisplayStream"/> class.</summary>
    public AgentDisplayStream(ExecutionLimits? limits = null)
    {
        Limits = limits ?? new();
        Limits.Validate();
    }

    /// <summary>Immutable budgets shared with display writers.</summary>
    internal ExecutionLimits Limits { get; }

    private readonly Queue<AgentDisplayText> _pending = new();
    private readonly Lock _gate = new();
    private long _omitted;
    private int _attached;
    private int _includeReasoning;

    /// <summary>Gets or sets the user's public-reasoning visibility preference.</summary>
    public bool IncludeReasoningText
    {
        get => Volatile.Read(ref _includeReasoning) != 0;
        set => Volatile.Write(ref _includeReasoning, value ? 1 : 0);
    }

    /// <summary>Enables the single retained observer; headless operation retains no display stream.</summary>
    public void Attach()
    {
        lock (_gate)
        {
            _attached = 1;
        }
    }

    /// <summary>Disables display retention and releases queued fragments.</summary>
    public void Detach()
    {
        lock (_gate)
        {
            _attached = 0;
            _pending.Clear();
            _omitted = 0;
        }
    }

    /// <summary>Publishes bounded sanitized fragments without waiting for rendering.</summary>
    public void Publish(AgentDisplayText observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (_attached == 0)
            {
                return;
            }

            if (observation.Text.Length > Limits.MaxAgentDisplayFragmentCharacters || _pending.Count >= Limits.MaxAgentDisplayFragments)
            {
                _omitted++;
            }
            else
            {
                _pending.Enqueue(observation);
            }
        }
    }

    /// <summary>Drains at most one queue capacity, preserving accepted order.</summary>
    public IReadOnlyList<AgentDisplayText> Drain(out long omitted)
    {
        lock (_gate)
        {
            var output = _pending.ToArray();
            _pending.Clear();
            omitted = _omitted;
            _omitted = 0;
            return output;
        }
    }
}

/// <summary>Sanitizes whole lines before emitting, including credentials split across provider chunks.</summary>
internal sealed class AgentDisplayTextWriter
{
    private const int MaximumLine = 16384;
    private readonly AgentDisplayStream? _stream;
    private readonly IOutputSanitizer _sanitizer;
    private readonly SessionId _session;
    private readonly RunId _run;
    private readonly bool _reasoning;
    private readonly bool _displayEligible;
    private readonly Guid _responseId = Guid.NewGuid();
    private readonly StringBuilder _line = new();
    private bool _lineOmitted;
    private bool _holdRemainder;

    /// <summary>Initializes a new instance of the <see cref="AgentDisplayTextWriter"/> class.</summary>
    internal AgentDisplayTextWriter(AgentDisplayStream? stream, IOutputSanitizer sanitizer, SessionId session, RunId run, bool reasoning)
    {
        _stream = stream;
        _sanitizer = sanitizer;
        _session = session;
        _run = run;
        _reasoning = reasoning;
        _displayEligible = !reasoning || stream?.IncludeReasoningText == true;
    }

    /// <summary>Retains partial lines until they can be sanitized as a unit.</summary>
    internal void Append(string text)
    {
        if (_stream is null || !_displayEligible)
        {
            return;
        }

        foreach (var character in text)
        {
            if (_line.Length < _stream.Limits.MaxAgentDisplayLineCharacters && !_lineOmitted)
            {
                _line.Append(character);
            }
            else
            {
                _lineOmitted = true;
            }

            if (character == '\n' && !_holdRemainder && !_lineOmitted)
            {
                if (_sanitizer is IStreamingOutputSanitizer streaming && streaming.CanFlushStandalone(_line.ToString()))
                {
                    Flush(false);
                }
                else
                {
                    _holdRemainder = true;
                }
            }
        }
    }

    /// <summary>Publishes safe text and an optional response boundary.</summary>
    internal void Flush(bool completesResponse)
    {
        if (_stream is null || !_displayEligible)
        {
            return;
        }

        var safe = _lineOmitted ? "[Oversized sensitive display suffix omitted]\n" : _sanitizer.Sanitize(_line.ToString());
        _line.Clear();
        _lineOmitted = false;
        _holdRemainder = false;
        for (var offset = 0; offset < safe.Length;)
        {
            var length = Math.Min(_stream.Limits.MaxAgentDisplayFragmentCharacters, safe.Length - offset);
            if (offset + length < safe.Length
                && char.IsHighSurrogate(safe[offset + length - 1])
                && char.IsLowSurrogate(safe[offset + length]))
            {
                length--;
            }

            _stream.Publish(new(_session, _run, safe.Substring(offset, length), _reasoning, ResponseId: _responseId));
            offset += length;
        }

        if (completesResponse)
        {
            _stream.Publish(new(_session, _run, string.Empty, _reasoning, true, _responseId));
        }
    }
}
