// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicTonePcmWorker : IDisposable
{
    const int MaximumPendingStates = 256;
    const long MaximumBufferedCycles = MiaSystemClock.AsicCyclesPerSecond / 5;
    const int ChunkSamples = AsicTonePcmRenderer.DefaultSampleRate / 50;

    readonly object _gate = new();
    readonly Queue<AsicToneState> _states = new(MaximumPendingStates);
    readonly AsicToneState[] _batch = new AsicToneState[MaximumPendingStates];
    readonly Action<ReadOnlyMemory<short>> _output;
    readonly MiaWorker _worker;
    AsicTonePcmRenderer _renderer;
    AsicToneState? _discardedState;
    short[] _samples = new short[ChunkSamples];
    int _length;
    long _targetCycle;
    long _renderedCycle;
    bool _drainQueued;
    int _disposed;

    public AsicTonePcmWorker(
        Action<ReadOnlyMemory<short>> output,
        long startCycle = 0,
        bool dedicatedThread = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _renderer = new(startCycle: startCycle);
        _targetCycle = _renderedCycle = startCycle;
        _worker = new("audio", dedicatedThread);
    }

    public event Action<Exception>? Faulted
    {
        add => _worker.Faulted += value;
        remove => _worker.Faulted -= value;
    }

    internal int PendingStateCount
    {
        get { lock (_gate) return _states.Count; }
    }

    public void Enqueue(AsicToneState state)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (_states.Count == MaximumPendingStates)
                _discardedState = _states.Dequeue();
            _states.Enqueue(state);
        }
    }

    public void AdvanceTo(long cycle)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            ArgumentOutOfRangeException.ThrowIfLessThan(cycle, _targetCycle);
            _targetCycle = cycle;
            if (_drainQueued) return;
            _drainQueued = true;
            // One coalesced drain owns all rendering. Advancing the core does
            // not enqueue a task or allocate a PCM block for every batch.
            _worker.Post(Drain);
        }
    }

    void Drain()
    {
        while (true)
        {
            int count = 0;
            long target;
            AsicToneState? discarded;
            lock (_gate)
            {
                target = _targetCycle;
                if (_disposed != 0 || target <= _renderedCycle ||
                    _discardedState is { } lost && lost.Cycle > target)
                {
                    _drainQueued = false;
                    return;
                }
                discarded = _discardedState;
                _discardedState = null;
                while (_states.TryPeek(out var state) && state.Cycle <= target)
                    _batch[count++] = _states.Dequeue();
            }

            int first = 0;
            long start = Math.Max(_renderedCycle, target - MaximumBufferedCycles);
            if (discarded is { } previous) start = Math.Max(start, previous.Cycle);
            if (start > _renderedCycle || discarded.HasValue)
            {
                // After a stalled consumer, retain the most recent 200 ms
                // and the state at its boundary instead of replaying stale
                // audio or allowing the state mailbox to grow indefinitely.
                AsicToneState state = discarded ?? _renderer.CurrentState;
                while (first < count && _batch[first].Cycle <= start)
                    state = _batch[first++];
                _renderer = new(startCycle: start);
                _renderer.RenderTo(state with { Cycle = start }, Span<short>.Empty);
                _length = 0;
            }
            for (; first < count && Volatile.Read(ref _disposed) == 0; first++)
                RenderState(_batch[first]);
            RenderCycle(target);
            _renderedCycle = target;
        }
    }

    void RenderState(AsicToneState state)
    {
        AsicTonePcmRenderResult result;
        do
        {
            result = _renderer.RenderTo(state, _samples.AsSpan(_length));
            Commit(result.SamplesWritten);
        } while (!result.ReachedTarget && Volatile.Read(ref _disposed) == 0);
    }

    void RenderCycle(long cycle)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            var result = _renderer.RenderTo(cycle, _samples.AsSpan(_length));
            Commit(result.SamplesWritten);
            if (result.ReachedTarget) return;
        }
    }

    void Commit(int count)
    {
        _length += count;
        if (_length != _samples.Length) return;
        short[] completed = _samples;
        _samples = new short[ChunkSamples];
        _length = 0;
        if (Volatile.Read(ref _disposed) == 0) _output(completed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            _states.Clear();
        }
        _worker.Dispose();
    }
}
