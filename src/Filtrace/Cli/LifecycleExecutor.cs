// Copyright (c) Jeremy W Kuhne and contributors
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
        // Defensive: the verb enforces top >= 0, but Run is also called directly, so
        // guard the boundary rather than emit a confusing negative-row report.
        if (request.Top < 0)
        {
            error.WriteLine("top must be 0 or greater.");
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

        warnings.AddRange(LifecycleProvider.DescribeCoverage(full));

        LifecycleResult report = LifecycleProvider.LimitDetail(full, request.Top, out string? warning);
        if (warning is not null)
        {
            warnings.Add(warning);
        }

        AnalysisResult<LifecycleResult> envelope = new(
            report,
            warnings,
            SteeringHints.ForLifecycle(full),
            new AnalysisContext("lifecycle"));

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
