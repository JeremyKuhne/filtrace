// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>Pairs capture-manifest cases by exact benchmark and parameter identity.</summary>
public static class CaptureManifestPairer
{
    private const int MaxWarnings = 16;

    /// <summary>Pairs baseline and current cases without using job/display labels.</summary>
    /// <param name="before">Baseline capture manifest.</param>
    /// <param name="after">Current capture manifest.</param>
    /// <returns>Matched cases and bounded unmatched-identity warnings.</returns>
    /// <exception cref="InvalidDataException">Either manifest contains duplicate pairing keys.</exception>
    public static CaptureManifestPairResult Pair(CaptureManifest before, CaptureManifest after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<string> warnings = [];
        int omittedWarnings = 0;
        Dictionary<string, CaptureManifestCase> currentByKey = BuildKeyMap(after, "current");
        foreach (CaptureManifestCase afterCase in after.Cases)
        {
            if (afterCase.PairingKey is null)
            {
                AddWarning(
                    warnings,
                    ref omittedWarnings,
                    $"current case '{afterCase.Id}' has no benchmark identity and was not paired; analyze its trace directly");
            }
        }

        HashSet<string> baselineKeys = new(StringComparer.Ordinal);
        List<CaptureManifestCasePair> pairs = [];

        foreach (CaptureManifestCase beforeCase in before.Cases)
        {
            string? key = beforeCase.PairingKey;
            if (key is null)
            {
                AddWarning(
                    warnings,
                    ref omittedWarnings,
                    $"baseline case '{beforeCase.Id}' has no benchmark identity and was not paired; analyze its trace directly");
                continue;
            }

            if (!baselineKeys.Add(key))
            {
                throw new InvalidDataException(
                    $"Baseline manifest contains duplicate benchmark/parameter key '{DisplayKey(beforeCase)}'.");
            }

            if (currentByKey.Remove(key, out CaptureManifestCase? afterCase))
            {
                pairs.Add(new CaptureManifestCasePair(beforeCase, afterCase));
            }
            else
            {
                AddWarning(
                    warnings,
                    ref omittedWarnings,
                    $"baseline case '{DisplayKey(beforeCase)}' has no current match");
            }
        }

        foreach (CaptureManifestCase afterCase in currentByKey.Values
            .OrderBy(static captureCase => captureCase.Benchmark, StringComparer.Ordinal)
            .ThenBy(static captureCase => captureCase.Parameters, StringComparer.Ordinal))
        {
            AddWarning(
                warnings,
                ref omittedWarnings,
                $"current case '{DisplayKey(afterCase)}' has no baseline match");
        }

        if (omittedWarnings > 0)
        {
            warnings.Add($"{omittedWarnings} additional unpaired case warning(s) omitted");
        }

        return new CaptureManifestPairResult(pairs, warnings);
    }

    private static Dictionary<string, CaptureManifestCase> BuildKeyMap(
        CaptureManifest manifest,
        string side)
    {
        Dictionary<string, CaptureManifestCase> byKey = new(StringComparer.Ordinal);
        foreach (CaptureManifestCase captureCase in manifest.Cases)
        {
            string? key = captureCase.PairingKey;
            if (key is null)
            {
                continue;
            }

            if (!byKey.TryAdd(key, captureCase))
            {
                throw new InvalidDataException(
                    $"{side} manifest contains duplicate benchmark/parameter key '{DisplayKey(captureCase)}'.");
            }
        }

        return byKey;
    }

    private static string DisplayKey(CaptureManifestCase captureCase) =>
        string.IsNullOrEmpty(captureCase.Parameters)
            ? captureCase.Benchmark ?? captureCase.Id
            : $"{captureCase.Benchmark} ({captureCase.Parameters})";

    private static void AddWarning(List<string> warnings, ref int omitted, string warning)
    {
        if (warnings.Count < MaxWarnings)
        {
            warnings.Add(warning);
        }
        else
        {
            omitted++;
        }
    }
}

/// <summary>One baseline/current capture case pair.</summary>
/// <param name="Before">Baseline case.</param>
/// <param name="After">Current case.</param>
public sealed record CaptureManifestCasePair(
    CaptureManifestCase Before,
    CaptureManifestCase After);

/// <summary>Paired cases plus bounded pairing warnings.</summary>
/// <param name="Pairs">Cases matched by exact benchmark and parameters.</param>
/// <param name="Warnings">Unresolved and unmatched case diagnostics.</param>
public sealed record CaptureManifestPairResult(
    IReadOnlyList<CaptureManifestCasePair> Pairs,
    IReadOnlyList<string> Warnings);