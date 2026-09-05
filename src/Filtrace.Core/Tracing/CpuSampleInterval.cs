// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using Filtrace.Output;

namespace Filtrace.Tracing;

/// <summary>
///  The CPU sample interval a capture asked for and the one it will actually get, with
///  the bounds the operating system reported.
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
///   So the interval a caller gets cannot be read back; it has to be derived from the
///   bounds. That is what this carries, and why <see cref="Clamped"/> is worth
///   reporting: a capture that silently sampled eight times slower than requested
///   produces a ranking whose weights are wrong by that factor.
///  </para>
/// </remarks>
/// <param name="RequestedMSec">The interval the caller asked for.</param>
/// <param name="EffectiveMSec">The interval the operating system will honor.</param>
/// <param name="MinimumMSec">The smallest interval the profile source honors.</param>
/// <param name="MaximumMSec">The largest interval the profile source honors.</param>
public sealed record CpuSampleInterval(
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double RequestedMSec,
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double EffectiveMSec,
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double MinimumMSec,
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double MaximumMSec)
{
    /// <summary>
    ///  Whether the operating system will sample at a different rate than requested.
    /// </summary>
    public bool Clamped => RequestedMSec != EffectiveMSec;
}
