// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Filtrace.Output;

/// <summary>A stable machine-readable diagnostic that retains its human message.</summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Severity">The severity: <c>info</c> or <c>warning</c>.</param>
/// <param name="Message">The human-readable diagnostic message.</param>
public sealed partial record AnalysisDiagnostic(string Code, string Severity, string Message)
{
    /// <summary>Optional structured values carried by the diagnostic.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisDiagnosticData? Data { get; init; }

    /// <summary>Converts one legacy warning message into its stable diagnostic record.</summary>
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

/// <summary>Stable diagnostic code vocabulary for the output contract.</summary>
public static class AnalysisDiagnosticCodes
{
    /// <summary>A warning that has not yet been assigned a narrower stable code.</summary>
    public const string Warning = "warning";

    /// <summary>Aggregate frame-name resolution is below the quality threshold.</summary>
    public const string LowFrameResolution = "low_frame_resolution";

    /// <summary>Managed source-line mapping is below the quality threshold.</summary>
    public const string LowSourceMapping = "low_source_mapping";

    /// <summary>A local PDB did not match the module identity recorded in the trace.</summary>
    public const string PdbIdentityMismatch = "pdb_identity_mismatch";

    /// <summary>Capture-provider enablement could not be established.</summary>
    public const string CaptureStatusUnknown = "capture_status_unknown";

    /// <summary>A required capture provider was known to be disabled.</summary>
    public const string CaptureStatusDisabled = "capture_status_disabled";

    /// <summary>Too few contributing records support a directional conclusion.</summary>
    public const string ThinScope = "thin_scope";

    /// <summary>A frame or process selector matched more than one definition.</summary>
    public const string AmbiguousSelector = "ambiguous_selector";

    /// <summary>Rows, payload, or another bounded result dimension was shortened.</summary>
    public const string TruncatedOutput = "truncated_output";

    /// <summary>A caller-supplied limit was clamped to the supported range.</summary>
    public const string ClampedInput = "clamped_input";

    /// <summary>A requested scope axis did not apply to the selected format.</summary>
    public const string IgnoredScope = "ignored_scope";

    /// <summary>A requested process, activity, or time scope was applied.</summary>
    public const string ScopeApplied = "scope_applied";

    /// <summary>One manifest case or another isolated sub-operation failed.</summary>
    public const string CaseFailure = "case_failure";
}

/// <summary>Optional numeric values carried by known diagnostics.</summary>
public sealed record AnalysisDiagnosticData
{
    /// <summary>The number of records that contributed to the scoped result.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContributingRecords { get; init; }

    /// <summary>The directional minimum recommended for the scoped result.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RecommendedMinimum { get; init; }

    /// <summary>The integer percentage of frames that resolved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResolutionPercent { get; init; }

    /// <summary>The minimum integer frame-resolution percentage expected.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinimumResolutionPercent { get; init; }
}
