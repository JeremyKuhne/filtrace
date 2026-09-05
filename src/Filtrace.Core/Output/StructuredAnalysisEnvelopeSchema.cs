// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Output;

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
