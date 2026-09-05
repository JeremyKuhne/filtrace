// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

// The profile-to-provider mapping is pure: it opens no session and needs no elevation,
// so it is unit-testable everywhere, unlike the capture it configures.
[TestClass]
public sealed class CaptureProvidersTests
{
    private const KernelTraceEventParser.Keywords MachineWideNoise =
        KernelTraceEventParser.Keywords.DiskIO
            | KernelTraceEventParser.Keywords.DiskFileIO
            | KernelTraceEventParser.Keywords.DiskIOInit
            | KernelTraceEventParser.Keywords.NetworkTCPIP
            | KernelTraceEventParser.Keywords.MemoryHardFaults
            | KernelTraceEventParser.Keywords.ProcessCounters;

    [TestMethod]
    [DataRow(CollectProfile.Cpu)]
    [DataRow(CollectProfile.ThreadTime)]
    [DataRow(CollectProfile.Startup)]
    public void For_AnyProfile_EnablesNoMachineWideDiskOrNetworkKeywords(CollectProfile profile)
    {
        CaptureProviders providers = CaptureProviders.For(profile);

        // KernelTraceEventParser.Keywords.Default carries all of these, and the DiskFileIO
        // name rundown alone enumerates every open file on the box. No filtrace analysis of
        // a collect capture reads them, so no profile may pay for them.
        (providers.KernelKeywords & MachineWideNoise).Should().Be(
            KernelTraceEventParser.Keywords.None,
            "a machine-wide capture pays for every keyword across the whole box");
    }

    [TestMethod]
    [DataRow(CollectProfile.Cpu)]
    [DataRow(CollectProfile.ThreadTime)]
    [DataRow(CollectProfile.Startup)]
    public void For_AnyProfile_EnablesTheSamplerAndProcessAttribution(CollectProfile profile)
    {
        CaptureProviders providers = CaptureProviders.For(profile);

        // Without these a sample cannot be attributed to a process or a module, which every
        // ranking depends on.
        providers.KernelKeywords.Should().HaveFlag(KernelTraceEventParser.Keywords.Profile);
        providers.KernelKeywords.Should().HaveFlag(KernelTraceEventParser.Keywords.Process);
        providers.KernelKeywords.Should().HaveFlag(KernelTraceEventParser.Keywords.Thread);
        providers.KernelKeywords.Should().HaveFlag(KernelTraceEventParser.Keywords.ImageLoad);
    }

    [TestMethod]
    [DataRow(CollectProfile.Cpu)]
    [DataRow(CollectProfile.ThreadTime)]
    [DataRow(CollectProfile.Startup)]
    public void For_AnyProfile_StacksOnlyKeywordsItEnabled(CollectProfile profile)
    {
        CaptureProviders providers = CaptureProviders.For(profile);

        (providers.StackKeywords & ~providers.KernelKeywords).Should().Be(
            KernelTraceEventParser.Keywords.None,
            "stacks cannot be attached to events the session did not enable");
    }

    [TestMethod]
    public void For_ThreadTime_AddsTheBlockedIntervalKeywords()
    {
        CaptureProviders threadTime = CaptureProviders.For(CollectProfile.ThreadTime);
        CaptureProviders cpu = CaptureProviders.For(CollectProfile.Cpu);

        // Context switches are what turn CPU sampling into wall-clock attribution, and the
        // stacks have to follow or the blocked intervals have no call stack.
        threadTime.KernelKeywords.Should().HaveFlag(KernelTraceEventParser.Keywords.ContextSwitch);
        threadTime.KernelKeywords.Should().HaveFlag(KernelTraceEventParser.Keywords.Dispatcher);
        threadTime.StackKeywords.Should().HaveFlag(KernelTraceEventParser.Keywords.ContextSwitch);
        cpu.KernelKeywords.Should().NotHaveFlag(KernelTraceEventParser.Keywords.ContextSwitch);
    }

    [TestMethod]
    [DataRow(CollectProfile.Cpu)]
    [DataRow(CollectProfile.ThreadTime)]
    [DataRow(CollectProfile.Startup)]
    public void For_AnyProfile_EnablesNoUnreadClrKeywords(CollectProfile profile)
    {
        CaptureProviders providers = CaptureProviders.For(profile);

        // ClrTraceEventParser.Keywords.Default carries all of these, and none of them
        // feed an analysis TraceCapabilities.AnalysesFor offers on an .etl. They are not
        // free: GCHeapSurvivalAndMovement makes the runtime walk moved and surviving
        // object ranges on every collection, and Stack adds a stack walk to CLR events.
        foreach (ClrTraceEventParser.Keywords unread in new[]
        {
            ClrTraceEventParser.Keywords.GCHeapSurvivalAndMovement,
            ClrTraceEventParser.Keywords.GCHeapAndTypeNames,
            ClrTraceEventParser.Keywords.GCHeapDump,
            ClrTraceEventParser.Keywords.Type,
            ClrTraceEventParser.Keywords.Stack,
            ClrTraceEventParser.Keywords.Contention,
            ClrTraceEventParser.Keywords.Threading,
        })
        {
            providers.ClrKeywords.Should().NotHaveFlag(unread);
        }
    }

    [TestMethod]
    [DataRow(CollectProfile.Cpu)]
    [DataRow(CollectProfile.ThreadTime)]
    [DataRow(CollectProfile.Startup)]
    public void For_AnyProfile_EnablesTheManagedNamingKeywords(CollectProfile profile)
    {
        CaptureProviders providers = CaptureProviders.For(profile);

        // Without these every managed frame in the capture is an unnamed address.
        providers.ClrKeywords.Should().HaveFlag(ClrTraceEventParser.Keywords.Jit);
        providers.ClrKeywords.Should().HaveFlag(ClrTraceEventParser.Keywords.NGen);
        providers.ClrKeywords.Should().HaveFlag(ClrTraceEventParser.Keywords.Loader);
        providers.ClrKeywords.Should().HaveFlag(ClrTraceEventParser.Keywords.JittedMethodILToNativeMap);
    }

    [TestMethod]
    [DataRow(CollectProfile.Cpu)]
    [DataRow(CollectProfile.ThreadTime)]
    public void For_AnalysisProfile_EnablesTheTimelineLaneKeywords(CollectProfile profile)
    {
        CaptureProviders providers = CaptureProviders.For(profile);

        // The timeline's gc and alloc lanes read GC events; its exception lane reads
        // Exception events. Startup deliberately drops both.
        providers.ClrKeywords.Should().HaveFlag(ClrTraceEventParser.Keywords.GC);
        providers.ClrKeywords.Should().HaveFlag(ClrTraceEventParser.Keywords.Exception);
    }

    [TestMethod]
    public void For_Startup_DiffersFromCpuOnlyInItsClrKeywords()
    {
        CaptureProviders startup = CaptureProviders.For(CollectProfile.Startup);
        CaptureProviders cpu = CaptureProviders.For(CollectProfile.Cpu);

        // Startup is the same CPU data at lower runtime perturbation, not less CPU data.
        startup.KernelKeywords.Should().Be(cpu.KernelKeywords);
        startup.StackKeywords.Should().Be(cpu.StackKeywords);
        startup.ClrKeywords.Should().NotBe(cpu.ClrKeywords);
    }

    [TestMethod]
    public void For_Startup_KeepsTheManagedNamingKeywordsAndDropsTheRest()
    {
        CaptureProviders providers = CaptureProviders.For(CollectProfile.Startup);

        // Jit/NGen name the methods, Loader names their modules, and the IL-to-native map
        // is what turns a native address into a source line. Nothing else.
        providers.ClrKeywords.Should().Be(
            ClrTraceEventParser.Keywords.Jit
                | ClrTraceEventParser.Keywords.NGen
                | ClrTraceEventParser.Keywords.Loader
                | ClrTraceEventParser.Keywords.JittedMethodILToNativeMap);
    }

    [TestMethod]
    [DataRow(CollectProfile.Cpu)]
    [DataRow(CollectProfile.ThreadTime)]
    [DataRow(CollectProfile.Startup)]
    public void For_AnyProfile_EnablesClrAtVerbose(CollectProfile profile)
    {
        CaptureProviders providers = CaptureProviders.For(profile);

        // The method-name payload rides on MethodLoadVerbose, a Verbose-level event, so no
        // profile may lower the level - narrowing the keywords is how volume comes down.
        providers.EnablesClr.Should().BeTrue();
        providers.ClrLevel.Should().Be(TraceEventLevel.Verbose);
    }

    [TestMethod]
    public void For_UndefinedProfile_ThrowsArgumentOutOfRange()
    {
        Action act = () => CaptureProviders.For((CollectProfile)999);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("profile");
    }
}
