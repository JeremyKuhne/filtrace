// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Filtrace.Tracing;

/// <summary>
///  A half-bounded or fully bounded time window into a trace, measured in
///  milliseconds relative to the start of the capture, used to scope a read to the
///  slice around an interesting event.
/// </summary>
/// <remarks>
///  <para>
///   Every sampled event carries a timestamp relative to the start of the trace, so
///   a time window is the one scope axis that applies to every metric: the CPU
///   sampler, the allocation ticks, the exception throws, the contention and wait
///   pairs, the activities, and the thread-time intervals all anchor at a time. A
///   read scoped to a window keeps only the samples whose anchor time
///   (<see cref="Contains"/>) falls inside it, which is how an analysis is narrowed
///   to the few seconds around a latency spike or one slow request without
///   physically rewriting the trace.
///  </para>
///  <para>
///   Either bound may be left open: a window with only a start runs from that time
///   to the end of the trace, and one with only an end runs from the start of the
///   trace to that time. A window with neither bound set is unbounded and keeps every
///   sample.
///  </para>
/// </remarks>
public readonly struct TimeWindow
{
    /// <summary>
    ///  Initializes a new <see cref="TimeWindow"/> spanning
    ///  <paramref name="startMSec"/> to <paramref name="endMSec"/>, either of which may
    ///  be <see langword="null"/> for an open bound.
    /// </summary>
    /// <param name="startMSec">
    ///  The inclusive lower bound in milliseconds relative to the start of the trace,
    ///  or <see langword="null"/> to run from the start of the trace.
    /// </param>
    /// <param name="endMSec">
    ///  The inclusive upper bound in milliseconds relative to the start of the trace,
    ///  or <see langword="null"/> to run to the end of the trace.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///  A bound is negative or not a number.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///  <paramref name="startMSec"/> is greater than <paramref name="endMSec"/>.
    /// </exception>
    public TimeWindow(double? startMSec, double? endMSec)
    {
        if (startMSec is double start && (double.IsNaN(start) || start < 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(startMSec), startMSec, "The window start must be a non-negative number of milliseconds.");
        }

        if (endMSec is double end && (double.IsNaN(end) || end < 0.0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(endMSec), endMSec, "The window end must be a non-negative number of milliseconds.");
        }

        if (startMSec is double lower && endMSec is double upper && lower > upper)
        {
            throw new ArgumentException(
                $"The window start ({FormatMSec(lower)} ms) must not be greater than the end ({FormatMSec(upper)} ms).",
                nameof(startMSec));
        }

        StartMSec = startMSec;
        EndMSec = endMSec;
    }

    /// <summary>
    ///  The inclusive lower bound in milliseconds relative to the start of the trace,
    ///  or <see langword="null"/> when the window runs from the start of the trace.
    /// </summary>
    public double? StartMSec { get; }

    /// <summary>
    ///  The inclusive upper bound in milliseconds relative to the start of the trace,
    ///  or <see langword="null"/> when the window runs to the end of the trace.
    /// </summary>
    public double? EndMSec { get; }

    /// <summary>
    ///  Whether the window constrains anything - <see langword="true"/> when at least
    ///  one bound is set, <see langword="false"/> for an unbounded window that keeps
    ///  every sample.
    /// </summary>
    public bool IsBounded => StartMSec is not null || EndMSec is not null;

    /// <summary>
    ///  Returns whether an event at <paramref name="relativeMSec"/> milliseconds into
    ///  the trace falls within the window (both bounds inclusive).
    /// </summary>
    /// <param name="relativeMSec">The event's timestamp in milliseconds relative to the start of the trace.</param>
    /// <returns><see langword="true"/> when the time is inside the window.</returns>
    public bool Contains(double relativeMSec) =>
        (StartMSec is not double start || relativeMSec >= start)
        && (EndMSec is not double end || relativeMSec <= end);

    /// <summary>
    ///  Renders the window as <c>[start, end] ms</c>, with an open bound shown as the
    ///  word <c>start</c> or <c>end</c>.
    /// </summary>
    /// <returns>The window label.</returns>
    public override string ToString()
    {
        string lower = StartMSec is double start ? FormatMSec(start) : "start";
        string upper = EndMSec is double end ? FormatMSec(end) : "end";
        return $"[{lower}, {upper}] ms";
    }

    // Formats a millisecond value invariantly and without a trailing ".0" so the
    // labels stay ASCII-stable across locales and read as whole milliseconds.
    private static string FormatMSec(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    ///  Parses a <c>start,end</c> window string - milliseconds relative to the trace
    ///  start, a single comma, either bound optional - into its two bounds, so the CLI
    ///  and the MCP tool share one time-window grammar.
    /// </summary>
    /// <param name="text">
    ///  The window text, or <see langword="null"/>/empty for no window. The grammar is
    ///  <c>start,end</c> with a single comma; either bound may be omitted for an open
    ///  side (<c>1000,5000</c>, <c>1000,</c>, or <c>,5000</c>).
    /// </param>
    /// <param name="startMSec">The parsed lower bound, or <see langword="null"/> for an open start or no window.</param>
    /// <param name="endMSec">The parsed upper bound, or <see langword="null"/> for an open end or no window.</param>
    /// <param name="errorMessage">The reason the value is malformed, or <see langword="null"/> on success.</param>
    /// <returns>
    ///  <see langword="true"/> when <paramref name="text"/> is empty or a valid window;
    ///  otherwise <see langword="false"/> with <paramref name="errorMessage"/> set.
    /// </returns>
    public static bool TryParse(
        string? text,
        out double? startMSec,
        out double? endMSec,
        [NotNullWhen(false)] out string? errorMessage)
    {
        startMSec = null;
        endMSec = null;
        errorMessage = null;

        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        int comma = text.IndexOf(',');
        if (comma < 0)
        {
            errorMessage =
                "The time window must be 'start,end' in milliseconds relative to the trace start; either "
                + "bound may be omitted (e.g. '1000,5000', '1000,', or ',5000').";
            return false;
        }

        if (text.IndexOf(',', comma + 1) >= 0)
        {
            errorMessage =
                "The time window must contain a single ',' separating the start and end (e.g. '1000,5000').";
            return false;
        }

        string startText = text[..comma].Trim();
        string endText = text[(comma + 1)..].Trim();

        if (startText.Length == 0 && endText.Length == 0)
        {
            errorMessage = "The time window needs at least one bound (e.g. '1000,', ',5000', or '1000,5000').";
            return false;
        }

        if (startText.Length > 0)
        {
            if (!TryParseBound(startText, "start", out double start, out errorMessage))
            {
                return false;
            }

            startMSec = start;
        }

        if (endText.Length > 0)
        {
            if (!TryParseBound(endText, "end", out double end, out errorMessage))
            {
                return false;
            }

            endMSec = end;
        }

        if (startMSec is double lower && endMSec is double upper && lower > upper)
        {
            errorMessage =
                $"The time window start ({startText} ms) must not be greater than the end ({endText} ms).";
            return false;
        }

        return true;
    }

    // Parses one window bound as a non-negative, finite number of milliseconds, naming
    // the offending side in the error so a typo is easy to spot.
    private static bool TryParseBound(
        string text,
        string which,
        out double value,
        [NotNullWhen(false)] out string? errorMessage)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value)
            && !double.IsInfinity(value)
            && value >= 0.0)
        {
            errorMessage = null;
            return true;
        }

        value = 0.0;
        errorMessage = $"The time window {which} '{text}' is not a valid non-negative number of milliseconds.";
        return false;
    }
}
