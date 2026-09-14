// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class LiveFramebufferFileTests
{
    [Fact]
    public void PublishAtomicallyReplacesCompleteFrame()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"mia-live-frame-{Guid.NewGuid():N}");
        var path = System.IO.Path.Combine(directory, "lcd.rgb332");
        try
        {
            using var publisher = new LiveFramebufferFile(path);

            publisher.Publish([0x01, 0x02, 0x03]);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, File.ReadAllBytes(path));

            publisher.Publish([0xa0, 0xb0]);
            Assert.Equal(new byte[] { 0xa0, 0xb0 }, File.ReadAllBytes(path));
            Assert.Equal(2, publisher.PublishedFrameCount);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
