// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Tracks trace-recorded ETW timer intervals and assigns the interval active at
///  each CPU sample.
/// </summary>
internal sealed class CpuSampleWeighting
{
    /// <summary>
    ///  Maximum number of interval segments retained as provenance metadata.
    /// </summary>
    internal const int MaximumRetainedIntervalSegments = 32;

    private const int TimerSampleSource = 0;
    private const int SetIntervalOpcode = 72;
    private const int CollectionStartOpcode = 73;
    private const double HundredNanosecondsPerMillisecond = 10_000.0;

    private readonly List<CpuSampleIntervalSegment> _intervals = [];
    private double? _currentIntervalMSec;
    private double? _previousAppliedIntervalMSec;
    private bool _retainCurrentIntervalSegment;
    private int _omittedIntervalSegmentCount;
    private int _omittedIntervalSampleCount;
    private int _unknownIntervalSampleCount;

    /// <summary>
    ///  The distinct valid timer intervals observed in event order.
    /// </summary>
    public IReadOnlyList<CpuSampleIntervalSegment> Intervals => _intervals;

    /// <summary>
    ///  Whether every observed sample had a trace-recorded interval.
    /// </summary>
    public bool HasCompleteIntervalEvidence => _unknownIntervalSampleCount == 0 && _intervals.Count > 0;

    /// <summary>
    ///  Number of samples observed before a valid timer interval was established.
    /// </summary>
    public int UnknownIntervalSampleCount => _unknownIntervalSampleCount;

    /// <summary>
    ///  Number of interval segments omitted after the retained metadata limit.
    /// </summary>
    public int OmittedIntervalSegmentCount => _omittedIntervalSegmentCount;

    /// <summary>
    ///  Number of samples belonging to omitted interval segments.
    /// </summary>
    public int OmittedIntervalSampleCount => _omittedIntervalSampleCount;

    /// <summary>
    ///  Observes one ETW sampled-profile interval record.
    /// </summary>
    /// <param name="opcode">The PerfInfo event opcode.</param>
    /// <param name="sampleSource">The profile source identifier; zero is the timer.</param>
    /// <param name="newInterval100Nanoseconds">The recorded interval in 100-nanosecond ticks.</param>
    public void ObserveInterval(int opcode, int sampleSource, int newInterval100Nanoseconds)
    {
        if (opcode is not (SetIntervalOpcode or CollectionStartOpcode)
            || sampleSource != TimerSampleSource)
        {
            return;
        }

        if (newInterval100Nanoseconds <= 0)
        {
            _currentIntervalMSec = null;
            return;
        }

        double intervalMSec = newInterval100Nanoseconds / HundredNanosecondsPerMillisecond;
        _currentIntervalMSec = intervalMSec;
    }

    /// <summary>
    ///  Returns the interval active for one sample, or one raw sample when the
    ///  interval has not been established.
    /// </summary>
    /// <returns>The sample weight in milliseconds when known, otherwise one raw sample.</returns>
    public double GetSampleWeight()
    {
        if (_currentIntervalMSec is double intervalMSec)
        {
            if (_previousAppliedIntervalMSec == intervalMSec)
            {
                if (_retainCurrentIntervalSegment)
                {
                    CpuSampleIntervalSegment current = _intervals[^1];
                    _intervals[^1] = current with { SampleCount = current.SampleCount + 1 };
                }
                else
                {
                    _omittedIntervalSampleCount++;
                }
            }
            else
            {
                _previousAppliedIntervalMSec = intervalMSec;
                _retainCurrentIntervalSegment = _intervals.Count < MaximumRetainedIntervalSegments;
                if (_retainCurrentIntervalSegment)
                {
                    _intervals.Add(new CpuSampleIntervalSegment(intervalMSec, 1));
                }
                else
                {
                    _omittedIntervalSegmentCount++;
                    _omittedIntervalSampleCount++;
                }
            }

            return intervalMSec;
        }

        _previousAppliedIntervalMSec = null;
        _unknownIntervalSampleCount++;
        return 1.0;
    }
}