// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Output;

/// <summary>
///  The advertised shape of a tool result: the envelope, with the payload left
///  unexpanded.
/// </summary>
/// <remarks>
///  <para>
///   Tools return the fully typed <see cref="AnalysisResult{T}"/> and still send complete
///   structured content. This only changes what the tool list advertises, so a client
///   learns the envelope without every result shape being expanded into the model's
///   context on every conversation.
///  </para>
/// </remarks>
/// <param name="SchemaVersion">The envelope version.</param>
/// <param name="Warnings">Quality warnings; empty when there are none.</param>
/// <param name="Hints">Suggested next steps; empty when there are none.</param>
/// <param name="Result">The typed payload, whose shape varies by tool.</param>
public sealed record AnalysisEnvelopeSchema(
    int SchemaVersion,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Hints,
    object Result);
