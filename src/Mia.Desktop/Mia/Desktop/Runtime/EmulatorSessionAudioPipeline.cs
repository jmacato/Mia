// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Mia.Desktop.Runtime;

internal sealed class EmulatorSessionAudioPipeline : IDisposable
{
    const int OutputChunkMilliseconds = 20;

    readonly MiaMachine _machine;
    readonly AsicTonePcmRenderer _renderer;
    readonly PcmChunkBuffer _output;
    readonly ConcurrentQueue<AsicToneState> _pendingStates = new();
    readonly Action<AsicToneState> _toneStateChanged;
    readonly short[] _renderBuffer = new short[2_048];

    public EmulatorSessionAudioPipeline(
        MiaMachine machine,
        Action<ReadOnlyMemory<short>> publish)
    {
        _machine = machine;
        _renderer = new(startCycle: machine.Cycles);
        _output = new(
            _renderer.SampleRate * OutputChunkMilliseconds / 1_000,
            publish);
        _toneStateChanged = _pendingStates.Enqueue;
        machine.ToneGenerator.StateChanged += _toneStateChanged;
    }

    public int SampleRate => _renderer.SampleRate;

    public void Drain(long targetCycle)
    {
        while (_pendingStates.TryDequeue(out AsicToneState state))
        {
            RenderTo(state);
        }
        RenderTo(targetCycle);
    }

    void RenderTo(AsicToneState state)
    {
        AsicTonePcmRenderResult result;
        do
        {
            result = _renderer.RenderTo(state, _renderBuffer);
            Publish(result.SamplesWritten);
        }
        while (!result.ReachedTarget);
    }

    void RenderTo(long targetCycle)
    {
        AsicTonePcmRenderResult result;
        do
        {
            result = _renderer.RenderTo(targetCycle, _renderBuffer);
            Publish(result.SamplesWritten);
        }
        while (!result.ReachedTarget);
    }

    void Publish(int length)
    {
        if (length > 0)
        {
            _output.Write(_renderBuffer.AsSpan(0, length));
        }
    }

    public void Dispose()
    {
        _machine.ToneGenerator.StateChanged -= _toneStateChanged;
    }
}
