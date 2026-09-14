// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Asic;

/// <summary>
/// Deterministic power source for one proven band-zero channel. This models
/// only the analog ADC level; synchronization bursts and decoded GSM data are
/// separate hardware boundaries.
/// </summary>
internal sealed class AsicSingleChannelRfSource(
    short arfcn,
    ushort rawSample) : IAsicRfSignalSource
{
    public short Arfcn { get; } = arfcn is >= AsicRfFrontend.BandZeroFirstArfcn
        and <= AsicRfFrontend.BandZeroLastArfcn
        ? arfcn
        : throw new ArgumentOutOfRangeException(nameof(arfcn));

    public ushort RawSample { get; } = rawSample;

    public long DecodedSampleCount { get; private set; }

    public long MatchedSampleCount { get; private set; }

    public ushort ReadRawSample(
        byte adcSelector,
        AsicRfTransaction transaction,
        AsicTimeGeneratorActionExecution action)
    {
        if (!AsicRfFrontend.TryDecodeBandZeroArfcn(
                action.ProgramSelector,
                transaction,
                out var tunedArfcn))
        {
            return 0;
        }

        DecodedSampleCount++;
        if (tunedArfcn != Arfcn)
        {
            return 0;
        }

        MatchedSampleCount++;
        return RawSample;
    }
}
