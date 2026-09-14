// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed record AsicRomHardwareStackSnapshot(int StartAddress, byte[] Bytes);
