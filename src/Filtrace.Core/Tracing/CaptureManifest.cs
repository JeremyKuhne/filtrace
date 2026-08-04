// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>A bounded BenchmarkDotNet capture manifest.</summary>
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

    /// <summary>Finds one case by its run-unique identifier.</summary>
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

/// <summary>One captured benchmark case from a capture manifest.</summary>
/// <param name="Id">Run-unique case identifier.</param>
/// <param name="Benchmark">Exact benchmark name, or <see langword="null"/> when unresolved.</param>
/// <param name="Parameters">Stable parameter display, empty for an unparameterized benchmark.</param>
/// <param name="BenchmarkDisplay">Human-readable BenchmarkDotNet display text.</param>
/// <param name="TracePath">Preferred raw trace path, or the speedscope path when no raw trace exists.</param>
/// <param name="SymbolsDirectory">Exact local symbol directory, when verified.</param>
/// <param name="OperationCount">Operations represented by the case, when supplied.</param>
/// <param name="OperationUnit">Operation unit, when supplied.</param>
public sealed record CaptureManifestCase(
    string Id,
    string? Benchmark,
    string Parameters,
    string BenchmarkDisplay,
    string TracePath,
    string? SymbolsDirectory,
    double? OperationCount,
    string? OperationUnit)
{
    /// <summary>
    ///  The launches this case's trace contains, in order. Empty for a case whose trace
    ///  holds a single run, which is every benchmark case.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   A short command is captured by running it repeatedly inside one session, so one
    ///   trace holds many runs. These separate them: each carries the root process id the
    ///   analysis scopes by, and the window that run occupied.
    ///  </para>
    /// </remarks>
    public IReadOnlyList<CaptureInvocation> Invocations { get; init; } = [];

    /// <summary>
    ///  Stable benchmark-and-parameter key used for cross-manifest pairing, or
    ///  <see langword="null"/> when the capture could not resolve benchmark identity.
    /// </summary>
    public string? PairingKey => Benchmark is null ? null : $"{Benchmark}\0{Parameters}";

    /// <summary>Whether count and unit are both present and usable.</summary>
    public bool HasCompleteOperationMetadata =>
        OperationCount is > 0.0 && OperationUnit is not null;
}