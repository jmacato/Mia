// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicTimeGeneratorPendingTransaction(
    int Address,
    byte Selector,
    AsicTimeGeneratorDescriptor Descriptor,
    AsicTimeGeneratorActionProgram? ActionProgram,
    AsicTimeGeneratorActionDefinition? ActionDefinition)
{
    public static AsicTimeGeneratorPendingTransaction ForDescriptor(
        int address,
        AsicTimeGeneratorDescriptor descriptor) =>
        new(address, 0, descriptor, null, null);

    public static AsicTimeGeneratorPendingTransaction ForAction(
        byte selector,
        AsicTimeGeneratorActionProgram program) =>
        new(0, selector, default, program, null);

    public static AsicTimeGeneratorPendingTransaction ForActionDefinition(
        byte selector,
        AsicTimeGeneratorActionDefinition definition) =>
        new(0, selector, default, null, definition);
}
