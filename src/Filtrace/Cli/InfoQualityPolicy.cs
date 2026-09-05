// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Tracing;

namespace Filtrace.Cli;

/// <summary>
///  An opt-in trace acceptance policy applied after <c>info</c> has loaded and
///  rendered the trace's complete quality evidence.
/// </summary>
/// <param name="Strict">Whether inadequate symbol resolution fails the policy.</param>
/// <param name="RequiredEnabled">Analyses whose capture providers must be confirmed enabled.</param>
/// <param name="RequiredEvents">Analyses that must also contain at least one recorded event.</param>
internal sealed record InfoQualityPolicy(
    bool Strict,
    IReadOnlyList<string> RequiredEnabled,
    IReadOnlyList<string> RequiredEvents)
{
    private static readonly HashSet<string> s_knownAnalyses = new(
        Enum.GetValues<TraceFormat>().SelectMany(
            static format => TraceCapabilities.AnalysesFor(format)),
        StringComparer.Ordinal);

    private static readonly string s_knownAnalysisDisplay =
        string.Join(", ", s_knownAnalyses.Order(StringComparer.Ordinal));

    /// <summary>
    ///  An acceptance policy with no gates enabled.
    /// </summary>
    public static InfoQualityPolicy None { get; } = new(Strict: false, [], []);

    /// <summary>
    ///  Validates requested analysis names, removes duplicates, and creates the policy
    ///  used after the trace has loaded.
    /// </summary>
    /// <param name="strict">Whether inadequate symbol resolution should fail the policy.</param>
    /// <param name="requireEnabled">Analysis names whose provider enablement must be confirmed.</param>
    /// <param name="requireEvents">Analysis names that must also contain a recorded event.</param>
    /// <param name="policy">The normalized policy on success, or <see cref="None"/> on failure.</param>
    /// <param name="error">An actionable option error on failure; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when every supplied analysis name is supported.</returns>
    public static bool TryCreate(
        bool strict,
        string[]? requireEnabled,
        string[]? requireEvents,
        out InfoQualityPolicy policy,
        out string? error)
    {
        if (!TryNormalize("--require-enabled", requireEnabled, out IReadOnlyList<string> enabled, out error)
            || !TryNormalize("--require-events", requireEvents, out IReadOnlyList<string> events, out error))
        {
            policy = None;
            return false;
        }

        policy = new InfoQualityPolicy(strict, enabled, events);
        return true;
    }

    /// <summary>
    ///  Compares the trace's symbol and provider evidence with every enabled policy gate.
    /// </summary>
    /// <param name="info">The loaded trace metadata and observed event counts.</param>
    /// <returns>Whether any gate failed and the evidence explaining each failure.</returns>
    public InfoQualityPolicyResult Evaluate(TraceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        bool failed = Strict
            && SymbolGate.IsBelowThreshold(info.SymbolResolutionRate, info.SampleCount);

        List<string> warnings = [];
        HashSet<string> requireEvents = new(RequiredEvents, StringComparer.Ordinal);
        HashSet<string> evaluated = new(StringComparer.Ordinal);

        foreach (string analysis in RequiredEnabled.Concat(RequiredEvents))
        {
            if (!evaluated.Add(analysis))
            {
                continue;
            }

            if (!info.Analyses.TryGetValue(analysis, out AnalysisAvailability? availability))
            {
                failed = true;
                warnings.Add(info.AvailableAnalyses.Contains(analysis, StringComparer.Ordinal)
                    ? $"Required analysis '{analysis}' capture metadata is unknown; provider enablement is not established."
                    : $"Required analysis '{analysis}' is not supported by the {info.Format} trace format.");

                continue;
            }

            if (!availability.FormatSupported)
            {
                failed = true;
                warnings.Add($"Required analysis '{analysis}' is not supported by the {info.Format} trace format.");
                continue;
            }

            if (availability.CaptureStatus == CaptureStatus.Disabled)
            {
                failed = true;
                warnings.Add($"Required analysis '{analysis}' capture is disabled.");
                continue;
            }

            if (availability.CaptureStatus != CaptureStatus.Enabled)
            {
                failed = true;
                warnings.Add($"Required analysis '{analysis}' capture metadata is unknown; provider enablement is not established.");
                continue;
            }

            if (!requireEvents.Contains(analysis))
            {
                continue;
            }

            if (availability.EventCount is null)
            {
                failed = true;
                warnings.Add($"Required analysis '{analysis}' capture metadata is unknown because its event count is unavailable.");
            }
            else if (availability.EventCount <= 0)
            {
                failed = true;
                warnings.Add($"Required analysis '{analysis}' recorded 0 events; at least 1 is required.");
            }
        }

        return new InfoQualityPolicyResult(failed, warnings);
    }

    private static bool TryNormalize(
        string option,
        string[]? values,
        out IReadOnlyList<string> normalized,
        out string? error)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value) || !s_knownAnalyses.Contains(value))
            {
                normalized = [];
                error = $"{option} analysis '{value}' is unknown. Choose from: {s_knownAnalysisDisplay}.";
                return false;
            }

            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        normalized = result;
        error = null;
        return true;
    }
}
