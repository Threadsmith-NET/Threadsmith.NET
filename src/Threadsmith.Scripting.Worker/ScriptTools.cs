namespace Threadsmith.Scripting.Worker;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Sequential access to tools admitted by the host for the current request.</summary>
public sealed class ScriptTools
{
    private readonly Stream? _input;
    private readonly Stream? _output;
    private readonly CancellationToken _cancellationToken;
    private int _busy;
    private int _sequence;
    private int _attempts;

    /// <summary>Initializes a new instance of the <see cref="ScriptTools"/> class for one execution.</summary>
    internal ScriptTools(IReadOnlyList<string> names, Stream? input, Stream? output, CancellationToken cancellationToken)
    {
        Available = Array.AsReadOnly(names.ToArray());
        _input = input;
        _output = output;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Names captured for this request. The host checks live availability again before execution.</summary>
    public IReadOnlyList<string> Available { get; }

    /// <summary>Invokes one tool using its ordinary JSON argument schema.</summary>
    public async Task<ScriptToolReply> InvokeAsync(string toolId, string argumentsJson)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _attempts) > ScriptWorkerProtocol.Attempts || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return new(false, null, false, "Limit", "Tool request limit reached or another request is outstanding.");
        }

        try
        {
            if (_input is null || _output is null || !Available.Contains(toolId, StringComparer.Ordinal) || argumentsJson is null || Encoding.UTF8.GetByteCount(argumentsJson) > ScriptWorkerProtocol.ArgumentBytes)
            {
                return new(false, null, false, "Denied", "Tool unavailable or arguments too large.");
            }

            var sequence = ++_sequence;
            await ScriptWorkerProtocol.WriteAsync(_output, ScriptFrameKind.Request, sequence, JsonSerializer.Serialize(new ScriptToolRequest(toolId, argumentsJson)), (6 * ScriptWorkerProtocol.ArgumentBytes) + 4096, _cancellationToken);
            var frame = await ScriptWorkerProtocol.ReadAsync(_input, sequence, kind => kind == ScriptFrameKind.Reply ? (6 * ScriptWorkerProtocol.ResultBytes) + 4096 : -1, _cancellationToken);
            return JsonSerializer.Deserialize<ScriptToolReply>(frame.Payload) ?? throw new InvalidDataException("Missing tool reply.");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Closes call admission and reserves the final frame sequence.</summary>
    internal int Close()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidDataException("A tool call was still pending when the script finished.");
        }

        return _sequence + 1;
    }
}
