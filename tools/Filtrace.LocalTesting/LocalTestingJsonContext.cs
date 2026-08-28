// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filtrace.LocalTesting;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(LocalTestingState))]
internal sealed partial class LocalTestingJsonContext : JsonSerializerContext
{
}

internal sealed class LocalTestingStatusJsonConverter()
    : JsonStringEnumConverter<LocalTestingStatus>(
        JsonNamingPolicy.CamelCase,
        allowIntegerValues: false)
{
}