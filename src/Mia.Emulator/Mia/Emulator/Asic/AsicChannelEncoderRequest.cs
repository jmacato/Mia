// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicChannelEncoderRequest(
    ReadOnlyMemory<byte> Input,
    byte FirmwareState);
