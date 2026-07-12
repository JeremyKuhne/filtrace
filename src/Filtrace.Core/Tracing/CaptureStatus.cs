// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Whether capture metadata establishes that an analysis's required providers
///  and keywords were enabled.
/// </summary>
public enum CaptureStatus
{
    /// <summary>The trace does not contain enough metadata to decide.</summary>
    Unknown,

    /// <summary>The required providers and keywords were enabled.</summary>
    Enabled,

    /// <summary>The required providers or keywords were disabled.</summary>
    Disabled
}