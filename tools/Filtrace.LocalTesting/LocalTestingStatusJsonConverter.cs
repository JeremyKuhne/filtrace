// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filtrace.LocalTesting;

/// <summary>
///  Converts local-testing status values to their camel-case names and rejects numeric enum values.
/// </summary>
internal sealed class LocalTestingStatusJsonConverter()
    : JsonStringEnumConverter<LocalTestingStatus>(
        JsonNamingPolicy.CamelCase,
        allowIntegerValues: false)
{
}
