// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Session;

namespace Filtrace.Tracing;

/// <summary>
///  Reads the CPU sampling bounds the operating system reports for the interval timer,
///  so a requested interval can be resolved to the one a capture will actually get.
/// </summary>
/// <remarks>
///  <para>
///   Windows exposes each profile source's honored range through
///   <see cref="TraceEventProfileSources"/> in 100-nanosecond units. Reading them beats
///   hard-coding a floor: the value is a platform property, it is queryable without
///   elevation, and a machine that reports a different one is then handled correctly
///   rather than confidently mis-documented.
///  </para>
/// </remarks>
public static class CpuSampleBounds
{
    /// <summary>
    ///  The timer profile source, which is what CPU sampling uses.
    /// </summary>
    private const string TimerSourceName = "Timer";

    /// <summary>
    ///  100-nanosecond ticks per millisecond, the unit the OS reports in.
    /// </summary>
    private const double TicksPerMillisecond = 10_000.0;

    /// <summary>
    ///  The smallest interval a caller may ask for. This is an outer sanity bound, not the
    ///  honored floor: the honored floor is a machine property read at capture time (see
    ///  <see cref="TryReadTimerBounds"/>), so a compile-time attribute cannot express it.
    ///  Anything below this is a unit mistake rather than a sampling request.
    /// </summary>
    public const double MinimumAcceptedMSec = 0.01;

    /// <summary>
    ///  The largest interval a caller may ask for. TraceEvent itself refuses anything past
    ///  one second, so asking beyond it can only fail.
    /// </summary>
    public const double MaximumAcceptedMSec = 1000.0;

    /// <summary>
    ///  Resolves <paramref name="requestedMSec"/> against the operating system's reported
    ///  bounds for the CPU interval timer.
    /// </summary>
    /// <param name="requestedMSec">The interval the caller asked for, in milliseconds.</param>
    /// <returns>
    ///  The requested and effective intervals with the bounds they were resolved against.
    ///  When the bounds cannot be read, the request is returned unclamped with both
    ///  bounds set to it, so a caller never sees a fabricated clamp.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///  <paramref name="requestedMSec"/> is not a positive, finite number.
    /// </exception>
    public static CpuSampleInterval Resolve(double requestedMSec)
    {
        if (!double.IsFinite(requestedMSec) || requestedMSec <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedMSec), requestedMSec,
                "The CPU sample interval must be a positive, finite number of milliseconds.");
        }

        if (!TryReadTimerBounds(out double minimumMSec, out double maximumMSec))
        {
            return new CpuSampleInterval(requestedMSec, requestedMSec, requestedMSec, requestedMSec);
        }

        double effectiveMSec = Math.Clamp(requestedMSec, minimumMSec, maximumMSec);
        return new CpuSampleInterval(requestedMSec, effectiveMSec, minimumMSec, maximumMSec);
    }

    /// <summary>
    ///  Reads the interval timer's honored range from the operating system.
    /// </summary>
    /// <param name="minimumMSec">The smallest honored interval, in milliseconds.</param>
    /// <param name="maximumMSec">The largest honored interval, in milliseconds.</param>
    /// <returns><see langword="true"/> when the bounds were read.</returns>
    public static bool TryReadTimerBounds(out double minimumMSec, out double maximumMSec)
    {
        minimumMSec = 0;
        maximumMSec = 0;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return TryReadWindowsTimerBounds(out minimumMSec, out maximumMSec);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadWindowsTimerBounds(out double minimumMSec, out double maximumMSec)
    {
        minimumMSec = 0;
        maximumMSec = 0;

        try
        {
            if (!TraceEventProfileSources.GetInfo().TryGetValue(TimerSourceName, out ProfileSourceInfo? timer)
                || timer is null
                || timer.MinInterval <= 0
                || timer.MaxInterval < timer.MinInterval)
            {
                return false;
            }

            minimumMSec = timer.MinInterval / TicksPerMillisecond;
            maximumMSec = timer.MaxInterval / TicksPerMillisecond;
            return true;
        }
        catch (Exception ex) when (IsProfileSourceUnavailable(ex))
        {
            // An older or restricted platform that cannot report profile sources leaves the
            // request unclamped rather than failing a capture that would otherwise work.
            return false;
        }
    }

    private static bool IsProfileSourceUnavailable(Exception exception)
    {
        return exception is InvalidOperationException
            || exception is NotSupportedException
            || exception is ApplicationException
            || exception is System.ComponentModel.Win32Exception
            || exception is System.Runtime.InteropServices.COMException;
    }
}
