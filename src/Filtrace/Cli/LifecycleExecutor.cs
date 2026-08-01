// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using Filtrace.Output;
using Filtrace.Tracing.Providers;

namespace Filtrace.Cli;

/// <summary>
///  Runs a lifecycle request against the analysis core: read the kernel process and
///  image events into per-invocation wall-clock phases, cap the per-invocation detail,
///  wrap the result in the output contract, and render it as text or JSON.
/// </summary>
/// <remarks>
///  <para>
///   The aggregate phase medians always reflect every measured invocation; only the
///   per-invocation detail list is capped, so a long capture matrix stays inside the
///   output budget without changing the summary it is read for.
///  </para>
/// </remarks>
internal static class LifecycleExecutor
{
    /// <summary>
    ///  Executes the lifecycle request.
    /// </summary>
    /// <param name="request">The validated lifecycle inputs.</param>
    /// <param name="output">The writer the result is rendered to.</param>
    /// <param name="error">The writer load errors are reported to.</param>
    /// <returns>A process exit code (see <see cref="ExitCodes"/>).</returns>
    public static int Run(LifecycleRequest request, TextWriter output, TextWriter error)
    {
        // Defensive: the verb enforces top >= 1, but Run is also called directly, so
        // guard the boundary rather than emit a confusing "top 0" report.
        if (request.Top < 1)
        {
            error.WriteLine("top must be 1 or greater.");
            return ExitCodes.UsageError;
        }

        List<string> warnings = [];
        if (!TraceExecution.TryReadEtlReport(
            request.Path,
            "process lifecycle",
            () => new LifecycleProvider().Read(request.Path, request.Scope, request.Images, warnings),
            error,
            out LifecycleResult? full))
        {
            return ExitCodes.InputError;
        }

        if (full.InvocationCount == 0)
        {
            warnings.Add($"No process matching '{full.Scope}' was found in the trace.");
        }
        else if (full.MeasuredCount == 0)
        {
            warnings.Add(
                "No invocation had both its start and its stop recorded, so no phase medians are "
                + "reported; every lifetime shown is a lower bound clipped to the capture window.");
        }
        else if (full.MeasuredCount < full.InvocationCount)
        {
            warnings.Add(
                $"{full.InvocationCount - full.MeasuredCount} of {full.InvocationCount} invocations were "
                + "clipped to the capture window and are excluded from the phase medians.");
        }

        IReadOnlyList<LifecycleInvocation> shown = full.Invocations;
        if (shown.Count > request.Top)
        {
            shown = [.. shown.Take(request.Top)];
            warnings.Add(
                $"Showing the first {request.Top} of {full.Invocations.Count} invocations in start order; "
                + "the medians cover all of them.");
        }

        LifecycleResult report = full with { Invocations = shown };
        AnalysisResult<LifecycleResult> envelope = new(report, warnings, SteeringHints.ForLifecycle(full));

        if (request.Format == OutputFormat.Json)
        {
            output.WriteLine(OutputJson.Serialize(envelope));
        }
        else
        {
            LifecycleTextRenderer.Render(envelope, request.Path, output);
        }

        return ExitCodes.Success;
    }
}
