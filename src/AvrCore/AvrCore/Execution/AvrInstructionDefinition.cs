namespace AvrCore.Execution;

internal readonly struct AvrInstructionDefinition
{
    internal AvrInstructionDefinition(
        int mask,
        int value,
        Action<Cpu, int> handler,
        bool requiresNonzeroDisplacement = false)
    {
        Mask = mask;
        Value = value;
        Handler = handler;
        RequiresNonzeroDisplacement = requiresNonzeroDisplacement;
    }

    int Mask { get; }

    int Value { get; }

    internal Action<Cpu, int> Handler { get; }

    bool RequiresNonzeroDisplacement { get; }

    internal bool Matches(int opcode) =>
        (opcode & Mask) == Value &&
        (!RequiresNonzeroDisplacement || DecodeDisplacement(opcode) != 0);

    static int DecodeDisplacement(int opcode) =>
        (opcode & 7) |
        ((opcode & 0xc00) >> 7) |
        ((opcode & 0x2000) >> 8);
}
