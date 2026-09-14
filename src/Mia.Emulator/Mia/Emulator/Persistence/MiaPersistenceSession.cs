// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Persistence;

internal sealed record MiaPersistenceSession(
    string Key,
    IMiaPersistenceStore Store,
    MiaPersistenceSnapshot InitialSnapshot);
