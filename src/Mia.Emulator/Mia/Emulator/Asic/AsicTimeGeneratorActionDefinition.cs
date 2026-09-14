// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicTimeGeneratorActionDefinition(
    ReadOnlyMemory<byte> Bytes);
