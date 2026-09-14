// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Rom;

internal sealed class RomOracleReport
{
    public required IReadOnlyList<RomReference> References { get; init; }

    public IEnumerable<IGrouping<(int Entry, bool IsCall), RomReference>> Entries =>
        References.GroupBy(reference => (reference.Entry, reference.IsCall));
}
