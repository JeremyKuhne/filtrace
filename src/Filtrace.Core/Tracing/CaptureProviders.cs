// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Filtrace.Tracing;

/// <summary>
///  The concrete providers a <see cref="CollectProfile"/> enables, resolved away from
///  the session so the mapping can be read and tested without opening a kernel trace.
/// </summary>
/// <param name="KernelKeywords">The kernel provider keywords to enable.</param>
/// <param name="StackKeywords">The subset of <paramref name="KernelKeywords"/> whose events carry call stacks.</param>
/// <param name="ClrKeywords">The CLR provider keywords, or <c>0</c> to leave the CLR provider off.</param>
/// <param name="ClrLevel">The CLR provider level.</param>
internal sealed record CaptureProviders(
    KernelTraceEventParser.Keywords KernelKeywords,
    KernelTraceEventParser.Keywords StackKeywords,
    ClrTraceEventParser.Keywords ClrKeywords,
    TraceEventLevel ClrLevel)
{
    // The kernel events every profile needs: the sampled profiler itself, plus the
    // process, thread, and image-load events that let the reader attribute a sample to a
    // process and a module. Deliberately NOT KernelTraceEventParser.Keywords.Default,
    // which also carries DiskIO, DiskFileIO, DiskIOInit, NetworkTCPIP, MemoryHardFaults,
    // and ProcessCounters - machine-wide traffic no filtrace analysis of a `collect`
    // capture reads, and whose DiskFileIO name rundown alone enumerates every open file
    // on the box.
    private const KernelTraceEventParser.Keywords CpuKernelKeywords =
        KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.Thread
        | KernelTraceEventParser.Keywords.ImageLoad
        | KernelTraceEventParser.Keywords.Profile;

    // Just enough of the CLR to keep managed frames readable: Jit and NGen name the
    // methods, Loader names their modules, and JittedMethodILToNativeMap carries the
    // IL offsets that turn a native address into a source line. Everything else in
    // ClrTraceEventParser.Keywords.Default - GC, Type, Contention, Exception, Threading,
    // Stack, ThreadTransfer, and the heap keywords - is the volume a startup capture
    // cannot afford.
    private const ClrTraceEventParser.Keywords NamingClrKeywords =
        ClrTraceEventParser.Keywords.Jit
        | ClrTraceEventParser.Keywords.NGen
        | ClrTraceEventParser.Keywords.Loader
        | ClrTraceEventParser.Keywords.JittedMethodILToNativeMap;

    /// <summary>
    ///  The providers <paramref name="profile"/> enables.
    /// </summary>
    /// <param name="profile">The capture profile.</param>
    /// <returns>The resolved providers.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="profile"/> is not a defined profile.</exception>
    public static CaptureProviders For(CollectProfile profile) => profile switch
    {
        // Stacks are attached only to the sampled events the rankings read; stacking
        // every kernel event would bloat the trace without helping any analysis.
        CollectProfile.Cpu => new(
            CpuKernelKeywords,
            KernelTraceEventParser.Keywords.Profile,
            ClrTraceEventParser.Keywords.Default,
            TraceEventLevel.Verbose),

        CollectProfile.ThreadTime => new(
            CpuKernelKeywords
                | KernelTraceEventParser.Keywords.ContextSwitch
                | KernelTraceEventParser.Keywords.Dispatcher,
            KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ContextSwitch,
            ClrTraceEventParser.Keywords.Default,
            TraceEventLevel.Verbose),

        // The level stays Verbose: the method-name payload rides on MethodLoadVerbose,
        // which is a Verbose-level event, so lowering the level would cost the managed
        // names this profile narrows the keywords to keep.
        CollectProfile.Startup => new(
            CpuKernelKeywords,
            KernelTraceEventParser.Keywords.Profile,
            NamingClrKeywords,
            TraceEventLevel.Verbose),

        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown capture profile.")
    };

    /// <summary>
    ///  Whether the CLR provider is enabled at all.
    /// </summary>
    public bool EnablesClr => ClrKeywords != 0;
}
