// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json.Serialization;
using Filtrace.Output;

namespace Filtrace.Tracing;

/// <summary>
///  One contiguous run of included ETW CPU samples with the same trace-recorded
///  timer interval.
/// </summary>
/// <param name="IntervalMSec">The interval in milliseconds.</param>
/// <param name="SampleCount">The number of included samples weighted by this interval.</param>
public readonly record struct CpuSampleIntervalSegment(
    [property: JsonConverter(typeof(SubMillisecondDoubleConverter))] double IntervalMSec,
    int SampleCount);