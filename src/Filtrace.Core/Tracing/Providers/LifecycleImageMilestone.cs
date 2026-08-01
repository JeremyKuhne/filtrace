// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Providers;

/// <summary>
///  When a named module was loaded, relative to the start of the invocation that
///  loaded it, summarized across invocations.
/// </summary>
/// <remarks>
///  <para>
///   A loader milestone turns "the command spent 40 ms before doing any work" into
///   "it spent 30 ms of that reaching hostfxr". Only the first load per invocation is
///   timed, so a module loaded into both the root and a child reports the earlier one.
///  </para>
/// </remarks>
/// <param name="Module">The module file name the milestone matched.</param>
/// <param name="Count">How many invocations loaded the module.</param>
/// <param name="MedianOffsetMs">The median load offset from the root start, in milliseconds.</param>
/// <param name="MinimumOffsetMs">The smallest load offset, in milliseconds.</param>
/// <param name="MaximumOffsetMs">The largest load offset, in milliseconds.</param>
public sealed record LifecycleImageMilestone(
    string Module,
    int Count,
    double MedianOffsetMs,
    double MinimumOffsetMs,
    double MaximumOffsetMs);
