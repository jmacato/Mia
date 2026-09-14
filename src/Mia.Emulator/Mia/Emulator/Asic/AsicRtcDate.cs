// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal readonly record struct AsicRtcDate(
    int Day,
    int Month,
    int Year,
    int Century,
    int FullYear);
