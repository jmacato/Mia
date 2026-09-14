// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Persistence;

internal readonly record struct MiaPersistenceCapture(
    long Version,
    MiaPersistenceSnapshot Snapshot);
