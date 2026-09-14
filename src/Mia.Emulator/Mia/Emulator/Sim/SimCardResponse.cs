// SPDX-License-Identifier: BSD-3-Clause

namespace Mia.Emulator.Sim;

internal readonly record struct SimCardResponse(byte[] Data, bool Complete);
