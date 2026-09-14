// SPDX-License-Identifier: BSD-3-Clause

namespace Mia.Emulator.Sim;

internal sealed record SimFileOverlay(ushort Parent, ushort Id, byte[] Data);
