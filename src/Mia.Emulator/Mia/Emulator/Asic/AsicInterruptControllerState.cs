// SPDX-License-Identifier: MIT

using System.Collections.Immutable;

namespace Mia.Emulator.Asic;

internal sealed record AsicInterruptControllerState(
    ImmutableList<byte> PendingSources,
    ImmutableHashSet<byte> QueuedSources,
    ImmutableStack<byte> ActiveSources,
    byte? PresentedSource,
    bool ActivationNeeded)
{
    public static readonly AsicInterruptControllerState Empty = new(
        ImmutableList<byte>.Empty,
        ImmutableHashSet<byte>.Empty,
        ImmutableStack<byte>.Empty,
        null,
        false);
}
