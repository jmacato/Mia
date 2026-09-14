// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors; Michael Conrad Tadpol Tilstra (ported from avr8js assembler.ts / Avrian-Jump)

namespace AvrCore.Assembly;

public sealed class AssemblerException : Exception
{
    public AssemblerException()
    {
    }

    public AssemblerException(string message) : base(message)
    {
    }

    public AssemblerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
