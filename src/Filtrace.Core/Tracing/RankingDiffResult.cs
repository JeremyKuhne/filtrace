// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.Tracing;

/// <summary>
///  The change between two rankings of the same metric: the per-frame deltas
///  ordered by the size of the change, plus the scope totals on each side.
/// </summary>
/// <remarks>
///  <para>
///   This is the engine's <c>diff</c> verb. It is purely a comparison of two
///   rankings, so it is provider-agnostic - diff two CPU rankings to find a
///   time regression, or two allocation rankings to find an allocation growth -
///   and composes with scoping and filtering (diff two filtered, scoped
///   rankings). The two rankings must be of the same metric and kind (both
///   self-time or both inclusive); mixing them is a caller error the result
///   shape cannot guard against.
///  </para>
/// </remarks>
/// <param name="BeforeScopeWeight">The baseline ranking's scoped total, in the metric's unit.</param>
/// <param name="AfterScopeWeight">The current ranking's scoped total, in the metric's unit.</param>
/// <param name="ScopeDelta">The change in scoped total (<c>AfterScopeWeight - BeforeScopeWeight</c>).</param>
/// <param name="Rows">The per-frame changes, largest absolute change first.</param>
public sealed record RankingDiffResult(
    [property: JsonIgnore] double BeforeScopeWeight,
    [property: JsonIgnore] double AfterScopeWeight,
    [property: JsonIgnore] double ScopeDelta,
    IReadOnlyList<DiffRow> Rows)
{
    private string _kind = TraceKind;
    private IReadOnlyList<RankingDiffCaseResult> _cases = [];

    /// <summary>The result kind for a direct trace-to-trace diff.</summary>
    public const string TraceKind = "trace";

    /// <summary>The result kind for a paired capture-manifest diff.</summary>
    public const string ManifestKind = "manifest";

    /// <summary>Initializes a paired capture-manifest diff.</summary>
    /// <param name="cases">The case-keyed manifest diffs, including an empty completed result.</param>
    public RankingDiffResult(IReadOnlyList<RankingDiffCaseResult> cases)
        : this(0.0, 0.0, 0.0, [])
    {
        ArgumentNullException.ThrowIfNull(cases);
        Cases = cases;
    }

    /// <summary>The result shape: <c>trace</c> or <c>manifest</c>.</summary>
    public string Kind => _kind;

    /// <summary>The per-frame changes for a direct trace diff.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonIgnore]
    public IReadOnlyList<DiffRow> Rows { get; init; } =
        Rows ?? throw new ArgumentNullException(nameof(Rows));

    /// <summary>
    ///  Case-keyed manifest diffs. Empty when this is a direct trace pair or when no
    ///  manifest cases paired.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<RankingDiffCaseResult> Cases
    {
        get => _cases;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _cases = value;
            _kind = ManifestKind;
        }
    }

    /// <summary>Baseline records contributing to the ranking, or <see langword="null"/> when unavailable.</summary>
    [JsonIgnore]
    public int? BeforeContributingRecordCount { get; init; }

    /// <summary>Current records contributing to the ranking, or <see langword="null"/> when unavailable.</summary>
    [JsonIgnore]
    public int? AfterContributingRecordCount { get; init; }

    /// <summary>Unit named by complete per-operation metadata, or <see langword="null"/>.</summary>
    /// <remarks>Direct trace pairs have no operation metadata and leave this <see langword="null"/>.</remarks>
    [JsonIgnore]
    public string? OperationUnit { get; init; }

    /// <summary>Baseline scope weight per operation, or <see langword="null"/>.</summary>
    [JsonIgnore]
    public double? BeforeScopeWeightPerOperation { get; init; }

    /// <summary>Current scope weight per operation, or <see langword="null"/>.</summary>
    [JsonIgnore]
    public double? AfterScopeWeightPerOperation { get; init; }

    /// <summary>Per-operation scope-weight change, or <see langword="null"/>.</summary>
    [JsonIgnore]
    public double? ScopeWeightPerOperationDelta { get; init; }

    /// <summary>Whether one or more frame names were shortened or sanitized for bounded output.</summary>
    [JsonIgnore]
    public bool FrameNamesBounded { get; init; }

    [JsonInclude]
    [JsonPropertyName("beforeScopeWeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal double? SerializedBeforeScopeWeight => Kind == TraceKind ? BeforeScopeWeight : null;

    [JsonInclude]
    [JsonPropertyName("afterScopeWeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal double? SerializedAfterScopeWeight => Kind == TraceKind ? AfterScopeWeight : null;

    [JsonInclude]
    [JsonPropertyName("scopeDelta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal double? SerializedScopeDelta => Kind == TraceKind ? ScopeDelta : null;

    [JsonInclude]
    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyList<DiffRow>? SerializedRows => Kind == TraceKind ? Rows : null;

    [JsonInclude]
    [JsonPropertyName("cases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal IReadOnlyList<RankingDiffCaseResult>? SerializedCases =>
        Kind == ManifestKind ? Cases : null;

    [JsonInclude]
    [JsonPropertyName("beforeContributingRecordCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal int? SerializedBeforeContributingRecordCount =>
        Kind == TraceKind ? BeforeContributingRecordCount : null;

    [JsonInclude]
    [JsonPropertyName("afterContributingRecordCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal int? SerializedAfterContributingRecordCount =>
        Kind == TraceKind ? AfterContributingRecordCount : null;

    [JsonInclude]
    [JsonPropertyName("operationUnit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal string? SerializedOperationUnit => Kind == TraceKind ? OperationUnit : null;

    [JsonInclude]
    [JsonPropertyName("beforeScopeWeightPerOperation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal double? SerializedBeforeScopeWeightPerOperation =>
        Kind == TraceKind ? BeforeScopeWeightPerOperation : null;

    [JsonInclude]
    [JsonPropertyName("afterScopeWeightPerOperation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal double? SerializedAfterScopeWeightPerOperation =>
        Kind == TraceKind ? AfterScopeWeightPerOperation : null;

    [JsonInclude]
    [JsonPropertyName("scopeWeightPerOperationDelta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal double? SerializedScopeWeightPerOperationDelta =>
        Kind == TraceKind ? ScopeWeightPerOperationDelta : null;
}
