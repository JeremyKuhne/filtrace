// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  What a capture manifest recorded, which decides how its cases are read.
/// </summary>
public enum CaptureKind
{
    /// <summary>
    ///  Benchmark cases, one trace per benchmark. The only kind a manifest written before
    ///  this discriminator existed can be, so it is the default when none is recorded.
    /// </summary>
    Benchmark,

    /// <summary>
    ///  Command scenarios, one trace per scenario, each holding repeated launches of that
    ///  command.
    /// </summary>
    Command
}
