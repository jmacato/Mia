// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceInclusion
{
    public static bool[] Create(AccessTraceInclusionRequest request)
    {
        Validate(request);
        (int firstLoop, int lastLoopExclusive) = GetLoopRange(request);
        return BuildMask(request, firstLoop, lastLoopExclusive);
    }

    static void Validate(AccessTraceInclusionRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.BinCount);
        ValidatePositiveFinite(
            request.DurationSeconds,
            nameof(request.DurationSeconds));
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExcludedWholeLoops);
        ValidatePositiveFinite(
            request.LoopFrequencyHz,
            nameof(request.LoopFrequencyHz));
    }

    static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    static (int FirstLoop, int LastLoopExclusive) GetLoopRange(
        AccessTraceInclusionRequest request)
    {
        int firstLoop = request.ExcludedWholeLoops;
        int lastLoopExclusive = (int)Math.Floor(
            request.DurationSeconds * request.LoopFrequencyHz);
        int midpoint = firstLoop + (lastLoopExclusive - firstLoop) / 2;
        return request.Half switch
        {
            AccessTraceWindowHalf.First => (firstLoop, midpoint),
            AccessTraceWindowHalf.Second => (midpoint, lastLoopExclusive),
            _ => (firstLoop, lastLoopExclusive),
        };
    }

    static bool[] BuildMask(
        AccessTraceInclusionRequest request,
        int firstLoop,
        int lastLoopExclusive)
    {
        var included = new bool[request.BinCount];
        for (var index = 0; index < request.BinCount; index++)
        {
            double seconds = (index + 0.5) * request.DurationSeconds /
                request.BinCount;
            int loop = (int)Math.Floor(
                seconds * request.LoopFrequencyHz);
            included[index] = loop >= firstLoop && loop < lastLoopExclusive;
        }
        return included;
    }
}
