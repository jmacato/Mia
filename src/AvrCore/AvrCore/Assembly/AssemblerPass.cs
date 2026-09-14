// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace AvrCore.Assembly;

internal sealed class AssemblerPass
{
    readonly string[] _sourceLines;
    readonly AssembleResult _result = new();
    readonly Dictionary<string, string?> _replacements = [];
    int _byteOffset;

    public AssemblerPass(string source)
    {
        _sourceLines = source.Split('\n');
    }

    public AssembleResult Assemble()
    {
        for (var index = 0; index < _sourceLines.Length; index++)
        {
            AssembleSourceLine(_sourceLines[index], index + 1);
        }

        if (_result.Errors.Count != 0)
        {
            _result.Lines.Clear();
            _result.Labels.Clear();
            return _result;
        }

        Assembler.ResolveAndEmit(_result);
        return _result;
    }

    void AssembleSourceLine(string sourceLine, int lineNumber)
    {
        string sourceText = sourceLine.Trim();
        if (sourceText.Length == 0)
        {
            return;
        }

        string codeText = Assembler.CommentPattern.Replace(sourceText, "").Trim();
        if (codeText.Length == 0)
        {
            return;
        }

        ReadLabel(ref codeText);
        if (codeText.Length == 0)
        {
            return;
        }

        var assembledLine = new AssemblerLine
        {
            Line = lineNumber,
            Text = sourceText,
        };
        try
        {
            AssembleCode(codeText, assembledLine);
        }
        catch (AssemblerException error)
        {
            _result.Errors.Add($"Line {lineNumber}: {error.Message}");
        }
    }

    void ReadLabel(ref string codeText)
    {
        Match labelMatch = Assembler.LabelPattern.Match(codeText);
        if (!labelMatch.Success)
        {
            return;
        }

        _result.Labels[labelMatch.Groups[1].Value] = _byteOffset;
        codeText = Assembler.LabelPattern.Replace(codeText, "").Trim();
    }

    void AssembleCode(string codeText, AssemblerLine assembledLine)
    {
        Match instructionMatch = Assembler.CodePattern.Match(codeText);
        if (!instructionMatch.Success)
        {
            throw new AssemblerException("doesn't match as code!");
        }

        string mnemonic = instructionMatch.Groups[1].Value.ToUpperInvariant();
        string? firstOperand = ReadOperand(instructionMatch, 2);
        string? secondOperand = ReadOperand(instructionMatch, 3);
        if (TryAssembleDirective(
                mnemonic,
                firstOperand,
                secondOperand,
                assembledLine))
        {
            return;
        }

        AssembleInstruction(
            mnemonic,
            Assembler.ApplyReplacement(firstOperand, _replacements),
            Assembler.ApplyReplacement(secondOperand, _replacements),
            assembledLine);
    }

    static string? ReadOperand(Match instruction, int groupIndex) =>
        instruction.Groups[groupIndex].Success
            ? instruction.Groups[groupIndex].Value
            : null;

    bool TryAssembleDirective(
        string mnemonic,
        string? firstOperand,
        string? secondOperand,
        AssemblerLine assembledLine)
    {
        if (mnemonic == "_REPLACE")
        {
            ApplyReplacementDirective(firstOperand, secondOperand);
            return true;
        }
        if (mnemonic == "_LOC")
        {
            ApplyLocationDirective(firstOperand);
            return true;
        }
        if (mnemonic == "_IW")
        {
            ApplyImmediateWordDirective(firstOperand, assembledLine);
            return true;
        }
        return false;
    }

    void ApplyReplacementDirective(string? name, string? replacement)
    {
        if (name is null)
        {
            throw new AssemblerException("_REPLACE needs a name.");
        }

        _replacements[name] = replacement;
    }

    void ApplyLocationDirective(string? operand)
    {
        if (!Assembler.TryParseNumber(operand, out int location))
        {
            throw new AssemblerException("Location is not a number.");
        }
        if ((location & 1) != 0)
        {
            throw new AssemblerException("Location is odd");
        }

        _byteOffset = location;
    }

    void ApplyImmediateWordDirective(
        string? operand,
        AssemblerLine assembledLine)
    {
        if (!Assembler.TryParseNumber(operand, out int word))
        {
            throw new AssemblerException("Immediate Word is not a number.");
        }

        assembledLine.Words = [(ushort)word];
        assembledLine.SizeWords = 1;
        AddLine(assembledLine);
    }

    void AssembleInstruction(
        string mnemonic,
        string? firstOperand,
        string? secondOperand,
        AssemblerLine assembledLine)
    {
        if (!Assembler.InstructionHandlers.TryGetValue(mnemonic, out var handler))
        {
            throw new AssemblerException("No such instruction: " + mnemonic);
        }

        OpResult encoded = handler(
            firstOperand,
            secondOperand,
            _byteOffset,
            _result.Labels);
        assembledLine.Words = encoded.Words;
        assembledLine.Deferred = encoded.Deferred;
        assembledLine.SizeWords = encoded.Words?.Count ?? encoded.SizeWords;
        AddLine(assembledLine);
    }

    void AddLine(AssemblerLine line)
    {
        line.ByteOffset = _byteOffset;
        _byteOffset += line.SizeWords * 2;
        _result.Lines.Add(line);
    }
}
