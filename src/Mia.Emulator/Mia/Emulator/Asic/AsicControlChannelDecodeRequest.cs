// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicControlChannelDecodeRequest(
    ReadOnlyMemory<byte> Input,
    byte FirmwareState);
