// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: Uri Shaked and contributors (ported from avr8js cpu.ts)

namespace AvrCore.Execution;

public delegate byte CpuMemoryReadHook(int addr);
