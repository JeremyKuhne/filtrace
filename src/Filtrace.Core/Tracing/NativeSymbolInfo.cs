// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Outcome of applying locally available native symbols to the sampled modules that
///  left the most frames unresolved.
/// </summary>
/// <remarks>
///  <para>
///   Native frames carry no CLR rundown, so a native module's frames stay unresolved
///   until its PDB is loaded. This reports what the caller-supplied symbol directory
///   was able to cover, so an unresolved native profile can be traced to a concrete
///   cause - no symbol file, or one whose identity does not match the traced binary.
///  </para>
/// </remarks>
/// <param name="ResolvedModules">Bounded highest-impact modules whose symbols were found locally and applied.</param>
/// <param name="MissingSymbolModules">Bounded highest-impact modules for which no local symbol file was found.</param>
public sealed record NativeSymbolInfo(
    IReadOnlyList<string> ResolvedModules,
    IReadOnlyList<string> MissingSymbolModules)
{
    /// <summary>
    ///  Bounded highest-impact modules for which the supplied directory contains a file
    ///  with the expected PDB name, but whose signature or age does not match the
    ///  trace-recorded identity.
    /// </summary>
    public IReadOnlyList<string> IdentityMismatchModules { get; init; } = [];

    /// <summary>
    ///  Bounded highest-impact modules whose symbol lookup was attempted and failed - an
    ///  unreadable or locked symbol file, for instance. Reported separately because such a
    ///  module is neither missing symbols nor carrying the wrong ones.
    /// </summary>
    public IReadOnlyList<string> LookupFailedModules { get; init; } = [];

    /// <summary>
    ///  Sampled frame occurrences that carried no method before the local pass ran.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   This counts every module with unresolved frames, including those below the share
    ///   worth spending a lookup on, so it can exceed the frames accounted for by the
    ///   reported module lists.
    ///  </para>
    /// </remarks>
    public int UnresolvedFrameCount { get; init; }
}
