// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Benchmarks;

/// <summary>
///  Carries a completed child observation that failed telemetry outcome validation.
/// </summary>
internal sealed class CliProcessTelemetryException : Exception
{
    /// <summary>
    ///  Initializes a new exception for a rejected child observation.
    /// </summary>
    /// <param name="telemetry">The completed child observation.</param>
    /// <param name="innerException">The outcome validation failure.</param>
    public CliProcessTelemetryException(
        CliProcessTelemetry telemetry,
        Exception innerException)
        : base(innerException.Message, innerException)
    {
        Telemetry = telemetry;
    }

    /// <summary>
    ///  Gets the completed child observation that was rejected.
    /// </summary>
    public CliProcessTelemetry Telemetry { get; }
}