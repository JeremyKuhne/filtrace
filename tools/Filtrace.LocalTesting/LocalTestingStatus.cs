// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;

namespace Filtrace.LocalTesting;

/// <summary>
///  Identifies the durable phase of a recoverable local-testing workflow.
/// </summary>
[JsonConverter(typeof(LocalTestingStatusJsonConverter))]
internal enum LocalTestingStatus
{
    /// <summary>
    ///  Represents a missing or unrecognized persisted status.
    /// </summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>
    ///  Indicates that baseline capture completed but installation is not yet active.
    /// </summary>
    [JsonStringEnumMemberName("installing")]
    Installing,

    /// <summary>
    ///  Indicates that the local CLI, MCP entry, and skill are installed.
    /// </summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>
    ///  Indicates that baseline restoration is in progress.
    /// </summary>
    [JsonStringEnumMemberName("restoring")]
    Restoring,

    /// <summary>
    ///  Indicates that restoration completed and only managed-directory cleanup remains.
    /// </summary>
    [JsonStringEnumMemberName("cleanup")]
    Cleanup
}
