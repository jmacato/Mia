// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

/// <summary>
/// Immutable encoded LAPDm burst observed at the dedicated uplink boundary.
/// This is diagnostic evidence from the firmware-visible channel, not a
/// reconstructed over-the-air waveform.
/// </summary>
internal readonly record struct GsmDedicatedUplinkFrame(
    byte FirmwareState,
    ReadOnlyMemory<byte> Bytes);
