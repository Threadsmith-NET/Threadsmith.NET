namespace Threadsmith.Tui.TuiKit;

/// <summary>Rotates a shuffled deck of tips on the UI owner without repeating within a cycle.</summary>
internal sealed class StartupTips
{
    private readonly string[] _tips;
    private readonly TimeProvider _timeProvider;
    private int _index;
    private long? _shownAt;

    /// <summary>Initializes a new instance of the <see cref="StartupTips"/> class using the supplied tips.</summary>
    internal StartupTips(IReadOnlyList<string> tips, TimeProvider? timeProvider = null)
    {
        _tips = [.. tips];
        _timeProvider = timeProvider ?? TimeProvider.System;
        Random.Shared.Shuffle(_tips);
    }

    /// <summary>Gets the current tip, advancing only after five seconds of display.</summary>
    internal string? Current
    {
        get
        {
            if (_tips.Length == 0)
            {
                return null;
            }

            var now = _timeProvider.GetTimestamp();
            if (_shownAt is null)
            {
                _shownAt = now;
            }
            else if (_timeProvider.GetElapsedTime(_shownAt.Value, now) >= TimeSpan.FromSeconds(5))
            {
                var previous = _tips[_index];
                _index++;
                if (_index == _tips.Length)
                {
                    Random.Shared.Shuffle(_tips);
                    _index = 0;
                    if (_tips.Length > 1 && _tips[0] == previous)
                    {
                        var next = Random.Shared.Next(1, _tips.Length);
                        (_tips[0], _tips[next]) = (_tips[next], _tips[0]);
                    }
                }

                _shownAt = now;
            }

            return _tips[_index];
        }
    }
}
