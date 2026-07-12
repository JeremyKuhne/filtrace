// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Defines which matching frame a stack query selects when one stack contains
///  multiple frames matching the same substring.
/// </summary>
public enum FrameMatchSelection
{
    /// <summary>Select the first match in the outermost-first stack.</summary>
    Outermost,

    /// <summary>Select the last match in the outermost-first stack.</summary>
    Deepest
}