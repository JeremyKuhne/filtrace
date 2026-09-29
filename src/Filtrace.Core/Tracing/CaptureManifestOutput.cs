// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing.Readers;

namespace Filtrace.Tracing;

/// <summary>
///  Applies deterministic size and character-safety bounds to manifest analysis output.
/// </summary>
internal static class CaptureManifestOutput
{
    /// <summary>
    ///  The maximum number of warnings retained for one manifest case.
    /// </summary>
    public const int MaxWarningsPerCase = 4;

    /// <summary>
    ///  The maximum number of UTF-16 characters retained in one case warning.
    /// </summary>
    public const int MaxWarningLength = 240;

    /// <summary>
    ///  The maximum number of UTF-16 characters retained in a frame name.
    /// </summary>
    public const int MaxFrameLength = 160;

    /// <summary>
    ///  Appends a sanitized, bounded warning when the per-case warning budget has room.
    /// </summary>
    /// <param name="warnings">The warnings already retained for the case.</param>
    /// <param name="warning">The warning text to sanitize and append.</param>
    public static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaxWarningsPerCase)
        {
            warnings.Add(Bound(warning, MaxWarningLength));
        }
    }

    /// <summary>
    ///  Reserves a bounded case warning for contributing SampleProfiler records.
    /// </summary>
    /// <param name="warnings">The case warnings to append to.</param>
    /// <param name="info">The analyzed trace and its CPU sample provenance.</param>
    /// <param name="side">The diff arm label, or <see langword="null"/> for batch.</param>
    public static void AddCpuSampleWarning(List<string> warnings, TraceInfo info, string? side = null)
    {
        if (CpuSampleEvidence.WarningFor(info.CpuSampling) is string sampleWarning)
        {
            AddWarning(warnings, side is null ? sampleWarning : $"{side}: {sampleWarning}");
        }
    }

    /// <summary>
    ///  Reserves a bounded case warning for incomplete capture evidence.
    /// </summary>
    /// <param name="warnings">The case warnings to append to.</param>
    /// <param name="info">The trace and its lost-event count.</param>
    /// <param name="side">The diff arm label, or <see langword="null"/> for batch.</param>
    public static void AddEventLossWarning(List<string> warnings, TraceInfo info, string? side = null)
    {
        if (TraceLogReader.EventLossWarning(info.EventsLost) is string lossWarning)
        {
            AddWarning(warnings, side is null ? lossWarning : $"{side}: {lossWarning}");
        }
    }

    /// <summary>
    ///  Adds other trace-quality warnings after the reserved SampleProfiler warning.
    /// </summary>
    /// <param name="warnings">The case warnings to append to.</param>
    /// <param name="info">The analyzed trace and its quality warnings.</param>
    /// <param name="side">The diff arm label, or <see langword="null"/> for batch.</param>
    public static void AddOtherTraceWarnings(List<string> warnings, TraceInfo info, string? side = null)
    {
        string? sampleWarning = CpuSampleEvidence.WarningFor(info.CpuSampling);
        string? lossWarning = TraceLogReader.EventLossWarning(info.EventsLost);
        foreach (string warning in info.Warnings.Take(MaxWarningsPerCase))
        {
            if (string.Equals(warning, sampleWarning, StringComparison.Ordinal)
                || string.Equals(warning, lossWarning, StringComparison.Ordinal))
            {
                continue;
            }

            AddWarning(warnings, side is null ? warning : $"{side}: {warning}");
            if (warnings.Count == MaxWarningsPerCase)
            {
                break;
            }
        }
    }

    /// <summary>
    ///  Replaces control characters and shortens a frame name without splitting a surrogate pair.
    /// </summary>
    /// <param name="frame">The frame name to make safe for bounded output.</param>
    /// <returns>The original frame when already safe and within budget; otherwise a bounded copy.</returns>
    public static string BoundFrame(string frame) => Bound(frame, MaxFrameLength);

    private static string Bound(string value, int maxLength)
    {
        int length = Math.Min(value.Length, maxLength);
        if (length < value.Length
            && length > 0
            && char.IsHighSurrogate(value[length - 1])
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        int firstControl = -1;
        for (int index = 0; index < length; index++)
        {
            if (char.IsControl(value[index]))
            {
                firstControl = index;
                break;
            }
        }

        if (firstControl < 0)
        {
            return length == value.Length ? value : value[..length];
        }

        char[] sanitized = value[..length].ToCharArray();
        for (int index = firstControl; index < sanitized.Length; index++)
        {
            if (char.IsControl(sanitized[index]))
            {
                sanitized[index] = ' ';
            }
        }

        return new string(sanitized);
    }
}
