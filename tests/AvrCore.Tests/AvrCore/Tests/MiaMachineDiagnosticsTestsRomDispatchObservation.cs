// SPDX-License-Identifier: MIT

using Mia.Emulator;

namespace AvrCore.Tests;

internal readonly record struct MiaMachineDiagnosticsTestsRomDispatchObservation(
    int Entry,
    AsicRomDispatchResult Result,
    bool WasStopped,
    int ThreadId);
