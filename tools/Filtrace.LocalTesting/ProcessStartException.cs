// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.LocalTesting;

/// <summary>
///  Identifies a failure raised directly while starting a process.
/// </summary>
/// <param name="message">The error message.</param>
/// <param name="innerException">The failure raised by the process API.</param>
internal sealed class ProcessStartException(string message, Exception innerException)
    : Exception(message, innerException)
{
}