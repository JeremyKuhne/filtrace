// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using Filtrace.Output;

namespace Filtrace.Tracing;

/// <summary>
///  The requested CPU sample interval and its clamp to operating-system bounds.
/// </summary>
/// <remarks>
///  <para>
///   Windows accepts any interval and echoes it back, but only honors it inside the
///   profile source's own bounds; outside them the sampling rate silently plateaus.
///   Measured on Windows 11 (10.0.26200), the timer floor is 0.1221 ms: requesting
///   0.25 ms produced 3.99 times the samples of 1 ms and 0.1221 ms produced 8.31 times,
///   both matching the interval, while 0.0625 ms produced only 9.82 times and
///   0.03125 ms fewer still - the same rate, not twice and four times it.
///  </para>
///  <para>
///   These values describe capture configuration, not proof of the physical interval
///   for every recorded sample. Analysis uses trace-recorded interval evidence and
///   reports raw samples when that evidence is incomplete.
///  </para>
/// </remarks>
/// <param name="RequestedMSec">The interval the caller asked for.</param>
/// <param name="EffectiveMSec">The requested interval clamped to the reported bounds.</param>
/// <param name="MinimumMSec">The minimum interval reported by the profile source.</param>
/// <param name="MaximumMSec">The maximum interval reported by the profile source.</param>
public sealed record CpuSampleInterval(
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double RequestedMSec,
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double EffectiveMSec,
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double MinimumMSec,
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double MaximumMSec)
{
    /// <summary>
    ///  Whether the requested interval was clamped to the reported bounds.
    /// </summary>
    public bool Clamped => RequestedMSec != EffectiveMSec;
}
