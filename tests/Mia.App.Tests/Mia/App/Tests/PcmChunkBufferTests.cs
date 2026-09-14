// SPDX-License-Identifier: MIT

using Mia.Desktop.Runtime;
using Xunit;

namespace Mia.App.Tests;

public sealed class PcmChunkBufferTests
{
    [Fact]
    public void PublishesOnlyCompleteChunks()
    {
        var published = new List<short[]>();
        var buffer = new PcmChunkBuffer(
            4,
            samples => published.Add(samples.ToArray()));

        buffer.Write([1, 2, 3]);

        Assert.Empty(published);

        buffer.Write([4, 5, 6, 7, 8, 9]);

        Assert.Equal(2, published.Count);
        Assert.Equal([1, 2, 3, 4], published[0]);
        Assert.Equal([5, 6, 7, 8], published[1]);
    }
}
