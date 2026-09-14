// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class AsicTonePcmRendererTests
{
    [Fact]
    public async Task AudioWorkerMatchesSynchronousMixingOnItsOwnThread()
    {
        var chunks = System.Threading.Channels.Channel.CreateUnbounded<(int Thread, short[] Samples)>();
        int callerThread = Environment.CurrentManagedThreadId;
        using var worker = new AsicTonePcmWorker(samples =>
            chunks.Writer.TryWrite((Environment.CurrentManagedThreadId, samples.ToArray())));
        var reference = new AsicTonePcmRenderer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int batch = 0; batch < 8; batch++)
        {
            long start = batch * 260_000L;
            var first = new AsicToneState(start, 0x80, 1, (byte)(100 + batch), 0);
            var second = first with { Cycle = start + 130_000, DtmfCode = (byte)(batch + 1) };
            worker.Enqueue(first);
            worker.Enqueue(second);
            worker.AdvanceTo(start + 260_000);

            Apply(reference, first);
            var expected = RenderFully(reference, second.Cycle).ToList();
            Apply(reference, second);
            expected.AddRange(RenderFully(reference, start + 260_000));
            var actual = await chunks.Reader.ReadAsync(timeout.Token);

            Assert.NotEqual(callerThread, actual.Thread);
            Assert.Equal(expected, actual.Samples);
        }
    }

    [Fact]
    public async Task StalledAudioWorkerBoundsEventsAndDiscardsOldAudioBeforeResuming()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = System.Threading.Channels.Channel.CreateUnbounded<short[]>();
        int calls = 0;
        using var worker = new AsicTonePcmWorker(samples =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                release.Wait();
            }
            chunks.Writer.TryWrite(samples.ToArray());
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            worker.AdvanceTo(260_000);
            await entered.Task.WaitAsync(timeout.Token);
            for (int i = 0; i < 1_000; i++)
                worker.Enqueue(new(260_000 + i * 13_000L, 0x80, 1, (byte)(i % 200), 0));
            worker.AdvanceTo(13_260_000);
            Assert.Equal(256, worker.PendingStateCount);
        }
        finally
        {
            release.Set();
        }
        for (int i = 0; i < 11; i++)
            Assert.Equal(960, (await chunks.Reader.ReadAsync(timeout.Token)).Length);
        worker.Dispose();
        Assert.Equal(11, calls);
        Assert.Equal(0, worker.PendingStateCount);
        worker.Enqueue(new(14_000_000, 0x80, 1, 100, 0));
        worker.AdvanceTo(14_000_000);
        Assert.Equal(0, worker.PendingStateCount);
        Assert.False(chunks.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(1, 697, 1209)]
    [InlineData(2, 697, 1336)]
    [InlineData(3, 697, 1477)]
    [InlineData(4, 770, 1209)]
    [InlineData(5, 770, 1336)]
    [InlineData(6, 770, 1477)]
    [InlineData(7, 852, 1209)]
    [InlineData(8, 852, 1336)]
    [InlineData(9, 852, 1477)]
    [InlineData(10, 941, 1336)]
    [InlineData(11, 941, 1209)]
    [InlineData(12, 941, 1477)]
    [InlineData(13, 697, 1633)]
    [InlineData(14, 770, 1633)]
    [InlineData(15, 852, 1633)]
    [InlineData(16, 941, 1633)]
    public void KeyTonesContainOnlyTheSelectedFrequencyPair(byte code, int low, int high)
    {
        var renderer = new AsicTonePcmRenderer();
        Apply(renderer, new(0, 0, 0, 0, 0, code));
        short[] samples = RenderFully(renderer, 13_000_000);
        foreach (int frequency in new[] { 697, 770, 852, 941, 1209, 1336, 1477, 1633 })
        {
            double sine = 0, cosine = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double phase = 2 * Math.PI * frequency * i / renderer.SampleRate;
                sine += samples[i] * Math.Sin(phase);
                cosine += samples[i] * Math.Cos(phase);
            }
            double amplitude = 2 * Math.Sqrt(sine * sine + cosine * cosine) / samples.Length;
            Assert.InRange(amplitude,
                frequency == low || frequency == high ? 4095 : 0,
                frequency == low || frequency == high ? 4097 : 1);
        }
        Apply(renderer, new(13_000_000, 0, 0, 0, 0));
        Assert.All(RenderFully(renderer, 13_013_000), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void KeyToneMixingIsIndependentOfChunkSizeAndDoesNotClip()
    {
        var combined = new AsicTonePcmRenderer(peakAmplitude: short.MaxValue);
        var chunked = new AsicTonePcmRenderer(peakAmplitude: short.MaxValue);
        var state = new AsicToneState(0, 0x41, 6, 0xca, 0, 1);
        Apply(combined, state);
        Apply(chunked, state);
        short[] expected = RenderFully(combined, 130000);
        var actual = new List<short>();
        var buffer = new short[7];
        AsicTonePcmRenderResult result;
        do
        {
            result = chunked.RenderTo(130000, buffer);
            actual.AddRange(buffer.Take(result.SamplesWritten));
        } while (!result.ReachedTarget);
        Assert.Equal(expected, actual);
        Assert.Contains(short.MaxValue, actual);
        Assert.Contains(short.MinValue, actual);
    }

    [Fact]
    public void ZeroControlAndUnknownModesRenderSilence()
    {
        var renderer = new AsicTonePcmRenderer(sampleRate: 48_000);
        Apply(renderer, new(0, 0x00, 0x30, 0x60, 0x00));

        var controlMuted = RenderFully(renderer, 13_000);

        Apply(renderer, new(13_000, 0x80, 0x30, 0x60, 0x01));
        var unknownMuted = RenderFully(renderer, 26_000);

        Assert.All(controlMuted, sample => Assert.Equal(0, sample));
        Assert.All(unknownMuted, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void ActiveStateRendersSymmetricSignedPcm()
    {
        var renderer = new AsicTonePcmRenderer(
            sampleRate: 48_000,
            peakAmplitude: 4_096);
        Apply(renderer, new(0, 0x4a, 0x30, 0x61, 0x00));

        var samples = RenderFully(renderer, 130_000);

        Assert.Contains((short)4_096, samples);
        Assert.Contains((short)-4_096, samples);
        Assert.All(
            samples,
            sample => Assert.True(sample is 4_096 or -4_096));
    }

    [Fact]
    public void RecoveredDividerProducesExpectedFrequency()
    {
        const int sampleRate = 384_000;
        const byte reload = 24;
        var renderer = new AsicTonePcmRenderer(sampleRate);
        Apply(renderer, new(0, 0x80, 12, reload, 0));

        var samples = RenderFully(
            renderer,
            MiaSystemClock.AsicCyclesPerSecond);
        var risingEdges = CountRisingEdges(samples);
        var expectedFrequency =
            26_000_000d / (256 * (reload + 1));

        Assert.InRange(risingEdges, expectedFrequency - 1, expectedFrequency + 1);
    }

    [Fact]
    public void WidthUsesProvisionalInclusiveCounterDuty()
    {
        const int sampleRate = 384_000;
        var renderer = new AsicTonePcmRenderer(sampleRate);
        Apply(renderer, new(0, 0x4a, 0x04, 0x8f, 0));

        var samples = RenderFully(
            renderer,
            MiaSystemClock.AsicCyclesPerSecond);
        var positiveSamples = samples.Count(sample => sample > 0);
        var measuredDuty = (double)positiveSamples / samples.Length;
        var expectedDuty = 5d / 144d;

        Assert.InRange(
            measuredDuty,
            expectedDuty - 0.0001,
            expectedDuty + 0.0001);
        Assert.InRange(Math.Abs(samples.Average(sample => (double)sample)), 0, 2);
    }

    [Fact]
    public void StateAtSampleCycleAffectsThatSample()
    {
        var renderer = new AsicTonePcmRenderer(sampleRate: 100_000);
        var start = new AsicToneState(130, 0x80, 0x30, 0x61, 0);

        var before = RenderStateFully(renderer, start);
        var after = RenderFully(renderer, 260);

        Assert.Equal(new short[] { 0 }, before);
        Assert.Equal(new short[] { renderer.PeakAmplitude }, after);
    }

    [Fact]
    public void ReloadChangePreservesNormalizedOscillatorPhase()
    {
        var renderer = new AsicTonePcmRenderer(sampleRate: 100_000);
        Apply(renderer, new(0, 0x80, 4, 9, 0));
        RenderStateFully(renderer, new(640, 0x80, 9, 19, 0));

        var afterReloadChange = RenderFully(renderer, 780);

        Assert.Equal(
            new short[] { (short)-renderer.PeakAmplitude },
            afterReloadChange);
    }

    [Fact]
    public void SmallBuffersAndIntermediateCycleBoundariesAreChunkInvariant()
    {
        var oneShot = new AsicTonePcmRenderer(sampleRate: 48_000);
        Apply(oneShot, new(0, 0x80, 0x30, 0x60, 0));
        var expected = RenderFully(oneShot, 130_000);

        var chunked = new AsicTonePcmRenderer(sampleRate: 48_000);
        Apply(chunked, new(0, 0x80, 0x30, 0x60, 0));
        var actual = new List<short>();
        RenderInChunks(chunked, 30_001, 17, actual);
        RenderInChunks(chunked, 65_007, 17, actual);
        RenderInChunks(chunked, 130_000, 17, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StartCycleAvoidsRenderingEarlierMachineHistory()
    {
        var renderer = new AsicTonePcmRenderer(
            sampleRate: 48_000,
            startCycle: 400_000_000);
        Apply(renderer, new(400_000_000, 0x80, 0x30, 0x60, 0));

        Assert.Equal(480, renderer.GetRemainingSampleCount(400_130_000));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => renderer.GetRemainingSampleCount(399_999_999));
    }

    static int CountRisingEdges(short[] samples)
    {
        var count = samples.Length != 0 && samples[0] > 0 ? 1 : 0;
        for (var index = 1; index < samples.Length; index++)
        {
            if (samples[index - 1] < 0 && samples[index] > 0)
            {
                count++;
            }
        }

        return count;
    }

    static void Apply(
        AsicTonePcmRenderer renderer,
        AsicToneState state)
    {
        var result = renderer.RenderTo(state, Span<short>.Empty);
        Assert.Equal(new AsicTonePcmRenderResult(0, true), result);
    }

    static short[] RenderStateFully(
        AsicTonePcmRenderer renderer,
        AsicToneState state)
    {
        var samples = new short[renderer.GetRemainingSampleCount(state.Cycle)];
        var result = renderer.RenderTo(state, samples);
        Assert.Equal(samples.Length, result.SamplesWritten);
        Assert.True(result.ReachedTarget);
        return samples;
    }

    static short[] RenderFully(
        AsicTonePcmRenderer renderer,
        long targetCycle)
    {
        var samples = new short[renderer.GetRemainingSampleCount(targetCycle)];
        var result = renderer.RenderTo(targetCycle, samples);
        Assert.Equal(samples.Length, result.SamplesWritten);
        Assert.True(result.ReachedTarget);
        return samples;
    }

    static void RenderInChunks(
        AsicTonePcmRenderer renderer,
        long targetCycle,
        int chunkSize,
        List<short> output)
    {
        var buffer = new short[chunkSize];
        AsicTonePcmRenderResult result;
        do
        {
            result = renderer.RenderTo(targetCycle, buffer);
            output.AddRange(buffer.AsSpan(0, result.SamplesWritten).ToArray());
        }
        while (!result.ReachedTarget);
    }
}
