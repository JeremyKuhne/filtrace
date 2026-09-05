// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  The inputs for an ETW capture (see <see cref="EtwCollector.Collect"/>).
/// </summary>
public sealed class EtwCollectRequest
{
    /// <summary>
    ///  The executable to launch and trace. The capture spans the process's whole
    ///  lifetime, so launching the built app directly (never <c>dotnet run</c>) keeps the
    ///  trace on the app rather than a build or launcher host.
    /// </summary>
    public required string LaunchExecutable { get; init; }

    /// <summary>
    ///  The arguments passed to <see cref="LaunchExecutable"/>, as a single command-line
    ///  string. Defaults to none.
    /// </summary>
    public string LaunchArguments { get; init; } = "";

    /// <summary>
    ///  The provider set the capture enables. Defaults to <see cref="CollectProfile.Cpu"/>.
    /// </summary>
    public CollectProfile Profile { get; init; } = CollectProfile.Cpu;

    /// <summary>
    ///  The CPU sample interval in milliseconds. Defaults to 1 ms (the ETW default).
    /// </summary>
    public double CpuSampleMSec { get; init; } = 1.0;

    /// <summary>
    ///  An optional cap on capture length in seconds. When set and the process is still
    ///  running at the cap, the capture stops and the process tree is terminated;
    ///  <see langword="null"/> (the default) captures until the process exits on its own.
    /// </summary>
    public int? DurationSeconds { get; init; }

    /// <summary>
    ///  An optional cap on the capture's on-disk size in megabytes. When set, the session
    ///  records into a fixed-size circular buffer that keeps the last N megabytes, bounding
    ///  an otherwise open-ended capture; <see langword="null"/> (the default) writes an
    ///  unbounded sequential file. Note that once a capture fills the ring the oldest events
    ///  are overwritten, which can drop the early JIT method-name events and lower symbol
    ///  resolution, so prefer a cap large enough to hold the run (or
    ///  <see cref="DurationSeconds"/>) when managed frames matter.
    /// </summary>
    public int? MaxSizeMB { get; init; }

    /// <summary>
    ///  How many times to launch <see cref="LaunchExecutable"/> inside the one session.
    ///  Defaults to 1.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Starting and flushing a session costs far more than a short command takes to run,
    ///   so a 30-100 ms scenario captured once is mostly session overhead and yields too
    ///   few samples to rank. Running it repeatedly inside one session pays that cost once
    ///   and produces one trace to convert instead of many thin ones. The runs are
    ///   sequential, and each is recorded in <see cref="EtwCollectResult.Invocations"/>.
    ///  </para>
    /// </remarks>
    public int Iterations { get; init; } = 1;

    /// <summary>
    ///  The <c>.etl</c> file the capture is written to.
    /// </summary>
    public required string OutputPath { get; init; }
}
