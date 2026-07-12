// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Describes what one normalized <see cref="SampleStack"/> record represents,
///  independently of the metric weight carried by that record.
/// </summary>
public enum StackRecordSemantics
{
    /// <summary>The source does not establish a meaningful record count.</summary>
    Unavailable,

    /// <summary>A periodic CPU sampler observation.</summary>
    PeriodicCpuSamples,

    /// <summary>A record from a speedscope sampled profile with unknown sampling cadence.</summary>
    SampledProfileRecords,

    /// <summary>A duration interval reconstructed from a speedscope evented profile.</summary>
    EventedIntervals,

    /// <summary>A mix of sampled-profile records and evented intervals.</summary>
    MixedProfileRecords
}