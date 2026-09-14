// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

/// <summary>
/// Count for an idle-fast-forward safety rejection reason.
/// </summary>
internal readonly record struct MiaIdleFastForwardBlockCount(
    MiaIdleFastForwardBlockReason Reason,
    long Count);
