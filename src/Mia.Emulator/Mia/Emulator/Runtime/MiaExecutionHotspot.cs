// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Runtime;

/// <summary>
/// A sampled execution location recorded by the machine without attaching an
/// execution observer. <see cref="BackwardBranches"/> identifies locations
/// that repeatedly transfer control to an earlier program word; it is an
/// investigation signal, not proof that a loop is safe to skip.
/// </summary>
internal readonly record struct MiaExecutionHotspot(
    int ProgramCounter,
    long Executions,
    long BackwardBranches);
