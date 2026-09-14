// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class PcmWaveFileWriterTests
{
    [Fact]
    public void WritesCanonicalMonoSigned16WaveFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mia-audio-{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new PcmWaveFileWriter(path, 48_000))
            {
                writer.WriteSamples([-32768, 0, 32767]);
                Assert.Equal(3, writer.SampleCount);
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(50, bytes.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(bytes, 8, 8));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2)));
            Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)));
            Assert.Equal("data", System.Text.Encoding.ASCII.GetString(bytes, 36, 4));
            Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
            Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44, 2)));
            Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46, 2)));
            Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(48, 2)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
