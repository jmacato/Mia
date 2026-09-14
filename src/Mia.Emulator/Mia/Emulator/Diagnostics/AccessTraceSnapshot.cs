// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal sealed record AccessTraceSnapshot(
    string Schema,
    AccessTraceWindow Window,
    AccessTraceClockInfo Clock,
    AccessTraceCaptureInfo Capture,
    AccessTraceStorageMetrics Storage,
    IReadOnlyList<AccessTraceReconciliationSnapshot> Reconciliation,
    IReadOnlyList<AccessTraceAddressSnapshot> Addresses)
{
    public const string CurrentSchema = "mia-access-aggregate-v1";
}
