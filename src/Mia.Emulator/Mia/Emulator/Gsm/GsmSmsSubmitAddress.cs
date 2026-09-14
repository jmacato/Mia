// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly record struct GsmSmsSubmitAddress(
    byte FirstOctet,
    string Destination,
    bool International,
    int NextOffset);
