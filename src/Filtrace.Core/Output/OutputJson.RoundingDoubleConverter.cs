// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filtrace.Output;

public static partial class OutputJson
{
    /// <summary>
    ///  Writes doubles rounded to a fixed number of decimal places so serialized
    ///  rankings are deterministic and free of floating-point noise.
    /// </summary>
    private sealed class RoundingDoubleConverter : JsonConverter<double>
    {
        private readonly int _digits;

        public RoundingDoubleConverter(int digits) => _digits = digits;

        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(Math.Round(value, _digits, MidpointRounding.AwayFromZero));
    }
}
