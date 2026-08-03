// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Output;

/// <summary>
///  The stable envelope every analysis service returns its payload through: a
///  schema version, structured diagnostics and steering hints, effective query
///  context, and the typed result.
/// </summary>
/// <remarks>
///  <para>
///   The envelope is the output contract's spine. It gives every verb - across
///   every provider family - one shape the CLI and MCP heads render uniformly:
///   a machine-readable <see cref="SchemaVersion"/> so a consumer can detect
///   format changes, a <see cref="Diagnostics"/> channel for quality signals (low
///   symbol resolution, truncated output), a <see cref="Hints"/> channel for
///   next-step nudges, and the typed <see cref="Result"/> payload.
///  </para>
///  <para>
///   This type only carries the channels. Populating <see cref="Diagnostics"/> from
///   the symbol gate and emitting <see cref="Hints"/> from each verb are later
///   increments; serializing the envelope deterministically is
///   <see cref="OutputJson"/>.
///  </para>
/// </remarks>
/// <typeparam name="T">The payload type the service produces.</typeparam>
public sealed class AnalysisResult<T>
{
    /// <summary>
    ///  The current output-contract schema version. Bumped when the serialized
    ///  shape changes in a way a consumer must notice.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Version 2 renamed the ranking weight fields from <c>*Milliseconds</c> to
    ///   the metric-neutral <c>*Weight</c> (so allocation rankings no longer report
    ///   bytes under a millisecond name).
    ///  </para>
    ///  <para>
    ///   Version 3 added query-specific contributing-record counts, distinct from
    ///   metric weight, to ranking, callers, lines, and heat-map payloads.
    ///  </para>
    ///  <para>
    ///   Version 4 added the request-specific ETLX cache state to trace-info payloads.
    ///  </para>
    ///  <para>
    ///   Version 5 added per-supported-analysis capture status and observed
    ///   source-record counts to trace-info payloads.
    ///  </para>
    ///  <para>
    ///   Version 6 added sampled managed source/PDB diagnostics to trace-info
    ///   payloads.
    ///  </para>
    ///  <para>
    ///   Version 7 added PDB identity mismatch modules, sampled method
    ///   sequence-point coverage, and named managed frames without source.
    ///  </para>
    ///  <para>
    ///   Version 8 added normalized and manifest-paired ranking diffs plus compact
    ///   manifest batch rankings.
    ///  </para>
    ///  <para>
    ///   Version 9 added the effective query context: the operation, metric semantics,
    ///   and machine-readable frame/process/activity/time scope that actually ran.
    ///  </para>
    ///  <para>
    ///   Version 10 replaced serialized warning strings with stable diagnostic records
    ///   that retain the human message.
    ///  </para>
    ///  <para>
    ///   Version 11 replaced serialized hint strings with operation-neutral next-step
    ///   records that retain the human reason.
    ///  </para>
    ///  <para>
    ///   Version 12 discriminated unrelated result shapes and omitted fields that do
    ///   not apply to the selected kind.
    ///  </para>
    /// </remarks>
    public const int CurrentSchemaVersion = 12;

    /// <summary>
    ///  Initializes a new <see cref="AnalysisResult{T}"/>.
    /// </summary>
    /// <param name="result">The typed payload.</param>
    /// <param name="warnings">Quality-signal warnings, or <see langword="null"/> for none.</param>
    /// <param name="hints">Next-step steering hints, or <see langword="null"/> for none.</param>
    /// <param name="context">The effective query context, or <see langword="null"/> for legacy callers.</param>
    public AnalysisResult(
        T result,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<string>? hints = null,
        AnalysisContext? context = null)
        : this(result, warnings, hints, context, nextSteps: null)
    {
    }

    /// <summary>
    ///  Initializes an analysis result with explicit structured next steps.
    /// </summary>
    /// <param name="result">The typed payload.</param>
    /// <param name="warnings">Quality-signal warnings, or <see langword="null"/> for none.</param>
    /// <param name="hints">Next-step text for source and text-renderer compatibility.</param>
    /// <param name="context">The effective query context, or <see langword="null"/>.</param>
    /// <param name="nextSteps">
    ///  Explicit structured next steps, or <see langword="null"/> to derive reason-only
    ///  records from <paramref name="hints"/>.
    /// </param>
    public AnalysisResult(
        T result,
        IReadOnlyList<string>? warnings,
        IReadOnlyList<string>? hints,
        AnalysisContext? context,
        IReadOnlyList<AnalysisNextStep>? nextSteps = null)
    {
        Result = result;
        Warnings = warnings is null ? [] : [.. warnings];
        Diagnostics = [.. Warnings.Select(AnalysisDiagnostic.FromWarning)];
        Hints = hints is null ? [] : [.. hints];
        NextSteps = nextSteps is not null
            ? [.. nextSteps]
            : hints is SteeringHintSet steering
                ? [.. steering.NextSteps]
                : [.. Hints.Select(static hint => new AnalysisNextStep(hint))];
        Context = context;
    }

    /// <summary>
    ///  The output-contract schema version this envelope was produced under.
    /// </summary>
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>
    ///  Human warning messages retained for text renderers and source compatibility.
    ///  Empty when there are none; JSON consumers use <see cref="Diagnostics"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    ///  Machine-readable diagnostics serialized under the stable <c>warnings</c>
    ///  envelope field. Empty when there are none.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("warnings")]
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; }

    /// <summary>
    ///  Human steering hints retained for text renderers and source compatibility.
    ///  Empty when there are none; JSON consumers use <see cref="NextSteps"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Hints { get; }

    /// <summary>
    ///  Machine-readable next steps serialized under the stable <c>hints</c> envelope
    ///  field. Empty when there are none.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("hints")]
    public IReadOnlyList<AnalysisNextStep> NextSteps { get; }

    /// <summary>
    ///  The operation, metric semantics, and effective query scope, or
    ///  <see langword="null"/> for a legacy caller that did not supply it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisContext? Context { get; }

    /// <summary>
    ///  The typed payload the service produced.
    /// </summary>
    public T Result { get; }
}
