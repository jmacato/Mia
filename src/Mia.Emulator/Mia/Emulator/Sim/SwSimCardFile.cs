// SPDX-License-Identifier: BSD-3-Clause

namespace Mia.Emulator.Sim;

internal sealed record SwSimCardFile(ushort Id, ushort Parent, SwSimCardFileKind Kind, byte Structure, int RecordLength, byte[] Data);
