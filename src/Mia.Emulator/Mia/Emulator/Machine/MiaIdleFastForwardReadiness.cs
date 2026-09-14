// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Machine;

internal readonly record struct MiaIdleFastForwardReadiness(
    bool CanAdvance,
    bool Candidate,
    MiaIdleFastForwardBlockReason Reason);
