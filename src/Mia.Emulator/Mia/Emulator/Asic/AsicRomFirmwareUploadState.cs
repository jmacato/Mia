// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

internal sealed class AsicRomFirmwareUploadState
{
    internal byte[] Image = [];
    internal int Length;
    internal int UploadedBytes;
    internal int? RelocationOffset;
    internal int? ZeroLengthDestination;
    internal bool SessionOpened;
    internal bool MemoryPrepared;
    internal byte[] UploadStagingImage = [];
    internal List<AsicRomFirmwareSpan> PendingSpans { get; } = [];

    internal void Reset()
    {
        SessionOpened = false;
        MemoryPrepared = false;
        ResetPayload();
    }

    internal void ResetPayload()
    {
        Image = [];
        UploadStagingImage = [];
        Length = 0;
        UploadedBytes = 0;
        RelocationOffset = null;
        ZeroLengthDestination = null;
        PendingSpans.Clear();
    }
}
