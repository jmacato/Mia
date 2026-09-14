// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.InteropServices;
using Mia.Browser.Audio;
using Xunit;

namespace AvrCore.Tests;

public sealed class BrowserAudioRingTests
{
    static int[] Storage => (int[])typeof(BrowserAudioRing)
        .GetField("Storage", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void StalledWorkletRetainsTheMostRecentTwoHundredMilliseconds()
    {
        using var ring = new BrowserAudioRing(48_000);
        for (int i = 0; i < 1000; i++) ring.Write(Enumerable.Repeat((short)i, 960).ToArray());
        var state = Storage;
        int read = Volatile.Read(ref state[1]);
        int write = Volatile.Read(ref state[0]);
        Assert.Equal(9600U, unchecked((uint)(write - read)));
        var samples = MemoryMarshal.Cast<int, short>(state.AsSpan(4));
        for (int i = 0; i < 9600; i++)
            Assert.Equal((short)(990 + i / 960), samples[(read + i) & (BrowserAudioRing.Capacity - 1)]);
    }

    [Fact]
    public void CounterWrapAndReplacedProducerDoNotCorruptTheCurrentStream()
    {
        using var old = new BrowserAudioRing(48_000);
        var state = Storage;
        Volatile.Write(ref state[0], -960);
        Volatile.Write(ref state[1], -960);
        old.Write(Enumerable.Repeat((short)123, 1920).ToArray());
        Assert.Equal(960, Volatile.Read(ref state[0]));
        using var current = new BrowserAudioRing(48_000);
        old.Write(new short[] { -123 });
        old.Dispose();
        current.Write(new short[] { 8192, -8192 });
        Assert.Equal(1, Volatile.Read(ref state[2]));
        Assert.Equal(962, Volatile.Read(ref state[0]));
        Assert.Equal(960, Volatile.Read(ref state[1]));
        var samples = MemoryMarshal.Cast<int, short>(state.AsSpan(4));
        Assert.Equal(8192, samples[960]);
        Assert.Equal(-8192, samples[961]);
        current.Dispose();
        Assert.False(current.IsActive);
        current.Write(new short[] { 123 });
        Assert.Equal(0, Volatile.Read(ref state[2]));
        Assert.Equal(962, Volatile.Read(ref state[0]));
    }
}
