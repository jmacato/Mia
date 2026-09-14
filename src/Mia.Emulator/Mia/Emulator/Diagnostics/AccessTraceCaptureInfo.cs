// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceCaptureInfo(
    bool Stopped,
    long? StoppedAtCycle);
