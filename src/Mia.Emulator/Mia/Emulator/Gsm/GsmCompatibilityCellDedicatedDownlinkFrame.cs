// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal readonly record struct GsmCompatibilityCellDedicatedDownlinkFrame(
    byte[] Bytes,
    GsmCompatibilityCellDedicatedDownlinkFrameKind Kind);
