// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

/// <summary>
/// Progress made by a bounded PCM render operation.
/// </summary>
/// <param name="SamplesWritten">Number of samples written to the destination.</param>
/// <param name="ReachedTarget">
/// True when the renderer reached the requested ASIC cycle. For the
/// state-taking overload, this also means that the new state was applied.
/// </param>
internal readonly record struct AsicTonePcmRenderResult(
    int SamplesWritten,
    bool ReachedTarget);
