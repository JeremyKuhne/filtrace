// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Filtrace.Output;

/// <summary>
///  A stable machine-readable diagnostic that retains its human message.
/// </summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Severity">The severity: <c>info</c> or <c>warning</c>.</param>
/// <param name="Message">The human-readable diagnostic message.</param>
public sealed partial record AnalysisDiagnostic(string Code, string Severity, string Message)
{
    /// <summary>
    ///  Optional structured values carried by the diagnostic.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisDiagnosticData? Data { get; init; }

    /// <summary>
    ///  Converts one legacy warning message into its stable diagnostic record.
    /// </summary>
    /// <param name="message">The warning message.</param>
    /// <returns>The classified diagnostic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public static AnalysisDiagnostic FromWarning(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Match thinScope = ThinScopeRegex().Match(message);
        if (thinScope.Success
            && TryParseInt32(thinScope.Groups["count"].Value, out int contributingRecords)
            && TryParseInt32(thinScope.Groups["minimum"].Value, out int recommendedMinimum))
        {
            return Warning(AnalysisDiagnosticCodes.ThinScope, message) with
            {
                Data = new AnalysisDiagnosticData
                {
                    ContributingRecords = contributingRecords,
                    RecommendedMinimum = recommendedMinimum
                }
            };
        }

        Match lowResolution = LowResolutionRegex().Match(message);
        if (lowResolution.Success
            && TryParseInt32(lowResolution.Groups["resolved"].Value, out int resolutionPercent)
            && TryParseInt32(lowResolution.Groups["minimum"].Value, out int minimumResolutionPercent))
        {
            return Warning(AnalysisDiagnosticCodes.LowFrameResolution, message) with
            {
                Data = new AnalysisDiagnosticData
                {
                    ResolutionPercent = resolutionPercent,
                    MinimumResolutionPercent = minimumResolutionPercent
                }
            };
        }

        if (message.StartsWith("Scoped to ", StringComparison.Ordinal))
        {
            return new AnalysisDiagnostic(
                AnalysisDiagnosticCodes.ScopeApplied,
                "info",
                message);
        }

        return Warning(ClassifyCode(message), message);
    }

    private static AnalysisDiagnostic Warning(string code, string message) =>
        new(code, "warning", message);

    private static bool TryParseInt32(string value, out int result) =>
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);

    private static string ClassifyCode(string message)
    {
        if (message.Contains("PDB identity", StringComparison.OrdinalIgnoreCase)
            && message.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisDiagnosticCodes.PdbIdentityMismatch;
        }

        if (message.StartsWith("Required analysis '", StringComparison.Ordinal)
            && message.Contains("is not supported", StringComparison.Ordinal))
        {
            return AnalysisDiagnosticCodes.RequiredAnalysisUnsupported;
        }

        if (message.StartsWith("Required analysis '", StringComparison.Ordinal)
            && message.Contains("recorded 0 events", StringComparison.Ordinal))
        {
            return AnalysisDiagnosticCodes.RequiredAnalysisEmpty;
        }

        if (message.Contains("capture metadata", StringComparison.OrdinalIgnoreCase)
            || message.Contains("enablement remains unknown", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisDiagnosticCodes.CaptureStatusUnknown;
        }

        if (message.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            && message.Contains("capture", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisDiagnosticCodes.CaptureStatusDisabled;
        }

        if (message.Contains("matched", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("multiple", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)))
        {
            return AnalysisDiagnosticCodes.AmbiguousSelector;
        }

        if (message.Contains("would exceed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("capped", StringComparison.OrdinalIgnoreCase)
            || message.Contains("truncated", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisDiagnosticCodes.TruncatedOutput;
        }

        if (message.Contains("clamped", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisDiagnosticCodes.ClampedInput;
        }

        if (message.Contains("not applied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ignored", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisDiagnosticCodes.IgnoredScope;
        }

        if (message.Contains("manifest", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("missing", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("skipped", StringComparison.OrdinalIgnoreCase)))
        {
            return AnalysisDiagnosticCodes.CaseFailure;
        }

        return AnalysisDiagnosticCodes.Warning;
    }

    [GeneratedRegex(
        @"Only (?<count>\d+) periodic CPU records contribute to this .+? result; use at least (?<minimum>\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ThinScopeRegex();

    [GeneratedRegex(
        @"Only (?<resolved>\d+)% of frames resolved to a method name \(< (?<minimum>\d+)%\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LowResolutionRegex();
}
