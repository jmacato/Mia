// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Diagnostics;

internal static class AccessTraceMultipleTesting
{
    public static IReadOnlyDictionary<string, double> BenjaminiHochberg(
        IEnumerable<(string Id, double PValue)> hypotheses)
    {
        ArgumentNullException.ThrowIfNull(hypotheses);
        (string Id, double PValue)[] ordered = hypotheses
            .Select(item =>
            {
                if (string.IsNullOrWhiteSpace(item.Id) ||
                    !double.IsFinite(item.PValue) ||
                    item.PValue is < 0 or > 1)
                {
                    throw new ArgumentException("Invalid hypothesis.", nameof(hypotheses));
                }
                return item;
            })
            .OrderBy(item => item.PValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        var result = new Dictionary<string, double>(
            ordered.Length,
            StringComparer.Ordinal);
        double next = 1;
        for (var index = ordered.Length - 1; index >= 0; index--)
        {
            double adjusted = Math.Min(
                next,
                ordered[index].PValue * ordered.Length / (index + 1d));
            next = Math.Clamp(adjusted, 0, 1);
            result.Add(ordered[index].Id, next);
        }
        return result;
    }
}
