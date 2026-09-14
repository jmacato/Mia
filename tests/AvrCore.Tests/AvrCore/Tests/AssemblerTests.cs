// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js assembler.spec.ts)

using Xunit;

namespace AvrCore.Tests;

public class AssemblerTests
{
    static readonly string[] InvalidRegisterError = ["Line 1: Rd out of range: 16<>31"];

    static byte[] Bytes(string hex)
    {
        var result = new byte[hex.Length / 2];
        for (var i = 0; i < hex.Length; i += 2)
        {
            result[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }
        return result;
    }

    static void AssertAssembles(string source, string expectedHex)
    {
        var result = Assembler.Assemble(source);
        Assert.Empty(result.Errors);
        Assert.Equal(Bytes(expectedHex), result.Bytes);
    }

    [Fact]
    public void AssemblesAddInstructionWithFullResult()
    {
        var result = Assembler.Assemble("ADD r16, r11");
        Assert.Empty(result.Errors);
        Assert.Equal(Bytes("0b0d"), result.Bytes);
        Assert.Empty(result.Labels);
        var line = Assert.Single(result.Lines);
        Assert.Equal(1, line.Line);
        Assert.Equal("ADD r16, r11", line.Text);
        Assert.Equal(0, line.ByteOffset);
        Assert.Equal(new ushort[] { 0x0d0b }, line.Words);
    }

    [Fact]
    public void SupportsLabels()
    {
        AssertAssembles("loop: JMP loop", "0c940000");
    }

    [Fact]
    public void SupportsMultiLinePrograms()
    {
        var input = """
            start:
            LDI r16, 15
            EOR r16, r0
            BREQ start
            """;
        AssertAssembles(input, "0fe00025e9f3");
    }

    [Fact]
    public void AssemblesEmptyProgram()
    {
        var result = Assembler.Assemble("");
        Assert.Empty(result.Errors);
        Assert.Empty(result.Bytes);
        Assert.Empty(result.Lines);
        Assert.Empty(result.Labels);
    }

    [Fact]
    public void ReturnsEmptyBytesOnProgramError()
    {
        var result = Assembler.Assemble("LDI r15, 20");
        Assert.Empty(result.Bytes);
        Assert.Equal(InvalidRegisterError, result.Errors);
        Assert.Empty(result.Lines);
        Assert.Empty(result.Labels);
    }

    [Theory]
    [MemberData(
        nameof(AssemblerInstructionCases.All),
        MemberType = typeof(AssemblerInstructionCases))]
    public void AssemblesSingleInstruction(string source, string expectedHex)
    {
        AssertAssembles(source, expectedHex);
    }

    [Fact]
    public void AssemblesBreqWithForwardLabelTarget()
    {
        AssertAssembles("BREQ next \n next:", "01f0");
    }

    [Fact]
    public void AssemblesBrneWithForwardLabelTarget()
    {
        AssertAssembles("BRNE next \n next:", "01f4");
    }
}
