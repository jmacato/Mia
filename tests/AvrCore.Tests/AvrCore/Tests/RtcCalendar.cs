// SPDX-License-Identifier: MIT

namespace AvrCore.Tests;

internal readonly record struct RtcCalendar(
    byte Second,
    byte Minute,
    byte Hour,
    byte Day,
    byte Month,
    byte Year,
    byte Century);
