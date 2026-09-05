// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filtrace.LocalTesting;

/// <summary>
///  Provides source-generated JSON metadata for persisted local-testing state.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(LocalTestingState))]
internal sealed partial class LocalTestingJsonContext : JsonSerializerContext
{
}
