// SPDX-License-Identifier: MIT

using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public class RomCallOracleTests
{
    [Fact]
    public void FindsPairedEntriesAndEpilogueCleanupValue()
    {
        var assembled = Assembler.Assemble("CALL 0x7e0014\nLDI r30, 10\nJMP 0x7e0058");
        Assert.Empty(assembled.Errors);
        var firmware = new byte[0x100];
        assembled.Bytes.CopyTo(firmware, 0);

        var report = RomCallOracle.Analyze(firmware);

        var prologue = Assert.Single(report.References, reference => reference.Entry == 0x3f000a);
        Assert.True(prologue.IsCall);
        Assert.Null(prologue.CleanupBytes);
        var epilogue = Assert.Single(report.References, reference => reference.Entry == 0x3f002c);
        Assert.False(epilogue.IsCall);
        Assert.Equal(10, epilogue.CleanupBytes);
    }

    [Fact]
    public void PrintNamesTheRecoveredAbiRegisterFrame()
    {
        var report = new RomOracleReport { References = [] };
        using var output = new StringWriter();

        RomCallOracle.Print(report, output);

        Assert.Contains("r25,r26,r27,r24,r4", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("r0-r4", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x000200)]
    [InlineData(0x010000)]
    public void IgnoresCallLikeWordsInKnownDataSegments(int byteOffset)
    {
        var assembled = Assembler.Assemble("CALL 0x7e0098");
        Assert.Empty(assembled.Errors);
        var firmware = new byte[byteOffset + assembled.Bytes.Length];
        assembled.Bytes.CopyTo(firmware, byteOffset);

        var report = RomCallOracle.Analyze(firmware);

        Assert.Empty(report.References);
    }
}
