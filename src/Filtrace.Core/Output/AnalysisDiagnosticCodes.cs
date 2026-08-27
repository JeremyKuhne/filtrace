// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Output;

/// <summary>
///  Stable diagnostic code vocabulary for the output contract.
/// </summary>
public static class AnalysisDiagnosticCodes
{
    /// <summary>
    ///  A warning that has not yet been assigned a narrower stable code.
    /// </summary>
    public const string Warning = "warning";

    /// <summary>
    ///  Aggregate frame-name resolution is below the quality threshold.
    /// </summary>
    public const string LowFrameResolution = "low_frame_resolution";

    /// <summary>
    ///  Managed source-line mapping is below the quality threshold.
    /// </summary>
    public const string LowSourceMapping = "low_source_mapping";

    /// <summary>
    ///  A local PDB did not match the module identity recorded in the trace.
    /// </summary>
    public const string PdbIdentityMismatch = "pdb_identity_mismatch";

    /// <summary>
    ///  Capture-provider enablement could not be established.
    /// </summary>
    public const string CaptureStatusUnknown = "capture_status_unknown";

    /// <summary>
    ///  A required capture provider was known to be disabled.
    /// </summary>
    public const string CaptureStatusDisabled = "capture_status_disabled";

    /// <summary>
    ///  A required analysis is unavailable for the trace format.
    /// </summary>
    public const string RequiredAnalysisUnsupported = "required_analysis_unsupported";

    /// <summary>
    ///  A required analysis was enabled but recorded no events.
    /// </summary>
    public const string RequiredAnalysisEmpty = "required_analysis_empty";

    /// <summary>
    ///  A root filter keeps only stacks containing the selected frame.
    /// </summary>
    public const string RootScopeAncestry = "root_scope_ancestry";

    /// <summary>
    ///  Too few contributing records support a directional conclusion.
    /// </summary>
    public const string ThinScope = "thin_scope";

    /// <summary>
    ///  A frame or process selector matched more than one definition.
    /// </summary>
    public const string AmbiguousSelector = "ambiguous_selector";

    /// <summary>
    ///  Rows, payload, or another bounded result dimension was shortened.
    /// </summary>
    public const string TruncatedOutput = "truncated_output";

    /// <summary>
    ///  A caller-supplied limit was clamped to the supported range.
    /// </summary>
    public const string ClampedInput = "clamped_input";

    /// <summary>
    ///  A requested scope axis did not apply to the selected format.
    /// </summary>
    public const string IgnoredScope = "ignored_scope";

    /// <summary>
    ///  A requested process, activity, or time scope was applied.
    /// </summary>
    public const string ScopeApplied = "scope_applied";

    /// <summary>
    ///  One manifest case or another isolated sub-operation failed.
    /// </summary>
    public const string CaseFailure = "case_failure";
}
