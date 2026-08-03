// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Output;

/// <summary>
///  The legacy advertised shape of a tool result through schema version 9.
/// </summary>
/// <remarks>
///  <para>
///   Retained for source and binary compatibility. MCP tools advertise
///   <see cref="StructuredAnalysisEnvelopeSchema"/> from schema version 10 onward.
///  </para>
/// </remarks>
/// <param name="SchemaVersion">The envelope version.</param>
/// <param name="Warnings">Quality warning messages.</param>
/// <param name="Hints">Suggested next-step messages.</param>
/// <param name="Context">
///  The effective operation and scope, left unexpanded so its nested process-id arrays
///  are not repeated in every tool's permanent schema.
/// </param>
/// <param name="Result">The typed payload, whose shape varies by tool.</param>
public sealed record AnalysisEnvelopeSchema(
    int SchemaVersion,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Hints,
    object Context,
    object Result);

/// <summary>
///  The advertised structured shape of a tool result, with nested vocabularies and
///  the payload left unexpanded.
/// </summary>
/// <remarks>
///  Tools return the fully typed <see cref="AnalysisResult{T}"/> and still send complete
///  structured content. This only changes what the tool list advertises, so a client
///  learns the envelope without every result shape being expanded into the model's
///  context on every conversation.
/// </remarks>
/// <param name="SchemaVersion">The envelope version.</param>
/// <param name="Warnings">Quality diagnostics left unexpanded.</param>
/// <param name="Hints">Structured next steps left unexpanded.</param>
/// <param name="Context">The effective operation and scope, left unexpanded.</param>
/// <param name="Result">The typed payload, whose shape varies by tool.</param>
public sealed record StructuredAnalysisEnvelopeSchema(
    int SchemaVersion,
    IReadOnlyList<object> Warnings,
    IReadOnlyList<object> Hints,
    object Context,
    object Result);
