// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filtrace.Output;

/// <summary>
///  Writes a double at sub-millisecond precision, for the few values whose meaning the
///  wire format's two-decimal rounding would destroy.
/// </summary>
/// <remarks>
///  <para>
///   Two decimals is right for sampled weights and percentages, where more digits imply
///   a precision the sampling does not have. It is wrong for a sample interval: Windows
///   reports the timer's honored floor as 1221 hundred-nanosecond ticks - 0.1221 ms -
///   which rounds to 0.12, an 18% error on the value every weight in the trace is scaled
///   by, and one that no longer distinguishes the floor from a 0.125 ms request.
///  </para>
///  <para>
///   Four decimals is exactly the granularity the platform reports in, since one
///   hundred-nanosecond tick is 0.0001 ms. A property-level converter takes precedence
///   over the options-level rounding, so this applies only where it is asked for.
///  </para>
/// </remarks>
internal sealed class SubMillisecondDoubleConverter : JsonConverter<double>
{
    /// <summary>One hundred-nanosecond tick expressed in milliseconds is 0.0001.</summary>
    private const int TickDigits = 4;

    /// <inheritdoc/>
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDouble();

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(Math.Round(value, TickDigits, MidpointRounding.AwayFromZero));
}
