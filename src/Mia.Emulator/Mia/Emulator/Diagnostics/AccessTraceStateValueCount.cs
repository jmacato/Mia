// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceStateValueCount(
    int State,
    string ValueLabel,
    long Count);
