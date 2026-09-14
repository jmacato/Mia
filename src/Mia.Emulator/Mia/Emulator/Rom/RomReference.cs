// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Rom;

internal sealed record RomReference(int Entry, bool IsCall, int Site, int? CleanupBytes);
