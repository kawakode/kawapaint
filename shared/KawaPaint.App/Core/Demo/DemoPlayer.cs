// KawaPaint - plays a recorded input stream back against the live editor.
//
// The player owns the clock and nothing else: it decides *when* each event is due and hands it to
// an apply callback, which is where MainView turns a DemoOp back into a real editor action. That
// split is what lets the same stream drive a real-time demo, a 4x fast-forward, or (later) an
// offline render, without the player knowing anything about tools or documents.
//
// Timing is virtual-clock based rather than one-tick-per-event: the tick rate is fixed and every
// event that has come due since the last tick is applied in order. A slow frame therefore makes
// playback catch up rather than drift, and a speed change mid-playback is just a change to how
// fast the virtual clock advances.

using System;
using System.Diagnostics;
using Avalonia.Threading;

namespace KawaPaint.App.Core.Demo;

public sealed class DemoPlayer
{
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;

    private DemoFile? _demo;
    private Action<DemoEvent>? _apply;
    private int _next;

    /// <summary>Virtual milliseconds banked before the current run of the stopwatch.</summary>
    private double _bankedMs;

    private double _speed = 1.0;

    public DemoPlayer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Pump();
    }

    public bool IsPlaying => _demo is not null && _clock.IsRunning;

    /// <summary>True while a demo is loaded, whether or not the clock is running.</summary>
    public bool IsActive => _demo is not null;

    public int PositionMs => (int)(_bankedMs + _clock.Elapsed.TotalMilliseconds * _speed);

    public int DurationMs => _demo?.DurationMs ?? 0;

    public double Speed
    {
        get => _speed;
        set
        {
            value = Math.Clamp(value, 0.1, 32.0);
            if (Math.Abs(value - _speed) < 0.0001) return;

            // Bank what the old speed earned before switching, so the position doesn't jump.
            if (_clock.IsRunning)
            {
                _bankedMs += _clock.Elapsed.TotalMilliseconds * _speed;
                _clock.Restart();
            }
            _speed = value;
            SpeedChanged?.Invoke();
        }
    }

    /// <summary>Fires on every tick that advanced the clock, for a progress readout.</summary>
    public event Action? Progressed;

    public event Action? SpeedChanged;

    /// <summary>Fires once the last event has been applied, or when Stop() is called.</summary>
    public event Action? Finished;

    /// <summary>
    /// Starts playing. The caller is responsible for having already restored the demo's starting
    /// document - the player replays inputs, it does not reset state.
    /// </summary>
    public void Play(DemoFile demo, Action<DemoEvent> apply)
    {
        _demo = demo;
        _apply = apply;
        _next = 0;
        _bankedMs = 0;
        _clock.Restart();
        _timer.Start();
    }

    public void Pause()
    {
        if (_demo is null || !_clock.IsRunning) return;
        _bankedMs += _clock.Elapsed.TotalMilliseconds * _speed;
        _clock.Stop();
        _timer.Stop();
        Progressed?.Invoke();
    }

    public void Resume()
    {
        if (_demo is null || _clock.IsRunning) return;
        _clock.Restart();
        _timer.Start();
    }

    public void TogglePause()
    {
        if (_clock.IsRunning) Pause(); else Resume();
    }

    public void Stop()
    {
        if (_demo is null) return;
        _timer.Stop();
        _clock.Reset();
        _demo = null;
        _apply = null;
        Finished?.Invoke();
    }

    private void Pump()
    {
        if (_demo is null || _apply is null) return;

        int now = PositionMs;
        var events = _demo.Events;

        // Bounded per tick: a demo fast-forwarded to 32x could otherwise apply thousands of
        // events in one go and lock the UI thread. Anything left over is picked up next tick,
        // which slows playback down under load instead of freezing it.
        int budget = 4000;
        while (_next < events.Count && events[_next].TimeMs <= now && budget-- > 0)
            _apply(events[_next++]);

        Progressed?.Invoke();

        if (_next >= events.Count)
        {
            _timer.Stop();
            _clock.Reset();
            _demo = null;
            _apply = null;
            Finished?.Invoke();
        }
    }
}
