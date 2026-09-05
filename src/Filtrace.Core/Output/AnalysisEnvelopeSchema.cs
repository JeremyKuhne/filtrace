// Copyright (c) Jeremy W Kuhne and contributors
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
