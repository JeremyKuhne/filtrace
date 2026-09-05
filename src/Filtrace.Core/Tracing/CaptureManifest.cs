// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  A bounded BenchmarkDotNet capture manifest.
/// </summary>
/// <param name="Path">Canonical manifest path.</param>
/// <param name="Process">Optional process selector recorded by the capture.</param>
/// <param name="Cases">Captured benchmark cases in manifest order.</param>
public sealed record CaptureManifest(
    string Path,
    string? Process,
    IReadOnlyList<CaptureManifestCase> Cases)
{
    /// <summary>
    ///  What the capture recorded. Defaults to <see cref="CaptureKind.Benchmark"/>, which
    ///  is what a manifest written before the kind existed contains.
    /// </summary>
    public CaptureKind Kind { get; init; } = CaptureKind.Benchmark;

    /// <summary>
    ///  Finds one case by its run-unique identifier.
    /// </summary>
    /// <param name="caseId">The exact case identifier.</param>
    /// <returns>The matching case.</returns>
    /// <exception cref="ArgumentException">
    ///  <paramref name="caseId"/> is empty, too long, or contains control characters.
    /// </exception>
    /// <exception cref="InvalidDataException">The manifest has no case with that identifier.</exception>
    public CaptureManifestCase GetCase(string caseId)
    {
        ArgumentException.ThrowIfNullOrEmpty(caseId);
        if (caseId.Length > CaptureManifestReader.MaxCaseIdLength || caseId.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Case id must contain 1-{CaptureManifestReader.MaxCaseIdLength} non-control characters.",
                nameof(caseId));
        }

        foreach (CaptureManifestCase captureCase in Cases)
        {
            if (string.Equals(captureCase.Id, caseId, StringComparison.Ordinal))
            {
                return captureCase;
            }
        }

        throw new InvalidDataException($"Capture manifest has no case with id '{caseId}'.");
    }

    /// <summary>
    ///  Resolves the process scope for <paramref name="captureCase"/>: an explicit
    ///  caller override wins, then the case's recorded invocation ids, then the
    ///  manifest's legacy process-name scope.
    /// </summary>
    /// <param name="captureCase">The manifest case whose trace is being loaded.</param>
    /// <param name="requested">
    ///  The caller's scope request, or <see langword="null"/> for automatic scope.
    ///  When supplied, its descendant, activity, and time-window choices survive a
    ///  fallback to recorded invocation ids or the manifest process name.
    /// </param>
    /// <returns>The scope to pass to the trace loader.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="captureCase"/> is <see langword="null"/>.</exception>
    public ScopeRequest? ResolveCaseScope(CaptureManifestCase captureCase, ScopeRequest? requested)
    {
        ArgumentNullException.ThrowIfNull(captureCase);

        if (requested is { Selector: not null } or { IncludeAll: true })
        {
            return requested;
        }

        bool includeChildren = requested?.IncludeChildren ?? true;
        ScopeRequest? resolved = captureCase.Invocations.Count > 0
            ? ScopeRequest.ForProcessIds(
                captureCase.Invocations.Select(static invocation => invocation.ProcessId),
                includeChildren)
            : Process is null
                ? requested
                : ScopeRequest.ForProcess(Process, includeChildren);

        if (resolved is null || requested is null)
        {
            return resolved;
        }

        if (requested.ActivityName is string activityName)
        {
            resolved = resolved.WithActivity(activityName);
        }

        return requested.Window is TimeWindow window
            ? resolved.WithTimeWindow(window.StartMSec, window.EndMSec)
            : resolved;
    }
}
