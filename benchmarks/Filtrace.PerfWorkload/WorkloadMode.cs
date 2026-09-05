// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.PerfWorkload;

/// <summary>
///  Selects the work pattern emitted by the Track D trace workload.
/// </summary>
internal enum WorkloadMode
{
    /// <summary>
    ///  Runs recursive CPU work without emitting application activities.
    /// </summary>
    Cpu,

    /// <summary>
    ///  Emits nested order, query, and render activities around CPU work.
    /// </summary>
    Activity
}
