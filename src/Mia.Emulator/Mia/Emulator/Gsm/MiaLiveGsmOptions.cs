// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

/// <summary>
/// Host configuration for the functional live GSM network attached to an
/// interactive Mia machine. The defaults are the dynamically verified cell
/// and RF values used by the firmware bring-up.
/// </summary>
internal sealed record MiaLiveGsmOptions
{
    public short Arfcn { get; init; } = -49;

    public ushort ProtocolRawSample { get; init; } = 0x0800;

    public byte RssiAdcSelector { get; init; } = 1;

    public byte DedicatedRssiAdcSelector { get; init; } = 2;

    public ushort IdleRssiRawSample { get; init; } = 0x7f80;

    public bool AutoAcceptOutgoingRequests { get; init; } = true;
}
