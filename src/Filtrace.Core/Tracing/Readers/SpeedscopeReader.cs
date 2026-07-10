// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Text.Json;

namespace Filtrace.Tracing.Readers;

/// <summary>
///  Reads standard evented or sampled time-based speedscope profiles
///  (<c>.speedscope.json</c>) into CPU samples weighted in milliseconds.
/// </summary>
/// <remarks>
///  <para>
///   Evented profiles record frame open (<c>O</c>) and close (<c>C</c>) events;
///   sampled profiles record stacks and weights directly. Both normalize their
///   declared time unit to milliseconds, the CPU metric the aggregator consumes.
///   Each profile maps to one thread, and frame names are already symbol-resolved.
///  </para>
/// </remarks>
internal sealed class SpeedscopeReader : ITraceReader
{
    /// <inheritdoc/>
    public TraceFormat Format => TraceFormat.Speedscope;

    /// <inheritdoc/>
    public bool CanRead(string path) =>
        path.EndsWith(".speedscope.json", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public TraceReadResult Read(
        string path,
        string? symbolsDirectory = null,
        ScopeRequest? scope = null,
        SymbolOptions? symbolOptions = null)
    {
        // A speedscope profile carries no process information, so process scoping does
        // not apply; likewise it carries no native frames, so symbolOptions does not
        // apply. Both parameters are accepted for interface uniformity.
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        string[] frameNames = ReadFrameNames(root);
        List<SampleStack> samples = [];

        if (root.TryGetProperty("profiles", out JsonElement profiles)
            && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement profile in profiles.EnumerateArray())
            {
                // Every speedscope profile requires a type. Unknown explicit types are
                // extensions we can ignore, but a missing type is malformed input rather
                // than an extension profile and should retain the clean input-error path.
                string? type = profile.GetProperty("type").GetString();
                if (string.IsNullOrEmpty(type))
                {
                    throw new FormatException("Speedscope profile 'type' must be a non-empty string.");
                }

                if (string.Equals(type, "evented", StringComparison.Ordinal))
                {
                    double millisecondsPerUnit = ResolveMillisecondsPerUnit(profile);
                    ReadEventedProfile(profile, frameNames, samples, millisecondsPerUnit);
                }
                else if (string.Equals(type, "sampled", StringComparison.Ordinal))
                {
                    double millisecondsPerUnit = ResolveMillisecondsPerUnit(profile);
                    ReadSampledProfile(profile, frameNames, samples, millisecondsPerUnit);
                }
            }
        }

        List<string> warnings = [];
        if (samples.Count == 0)
        {
            warnings.Add("No timed samples were found in the speedscope file.");
        }

        // Speedscope is currently an aggregate CPU input; this reader does not expose
        // profile positions as the trace-relative anchors rank --time consumes. Ignore a
        // requested window, but say so rather than silently returning the whole profile.
        if (scope?.Window is TimeWindow window && window.IsBounded)
        {
            warnings.Add(
                "Time-window scoping is not applied to a speedscope trace; the requested window was ignored. Use a .nettrace or .etl capture to scope by trace-relative time.");
        }

        return new TraceReadResult(samples, 1.0, warnings);
    }

    private static string[] ReadFrameNames(JsonElement root)
    {
        if (!root.TryGetProperty("shared", out JsonElement shared)
            || !shared.TryGetProperty("frames", out JsonElement frames)
            || frames.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        string[] names = new string[frames.GetArrayLength()];
        int i = 0;
        foreach (JsonElement frame in frames.EnumerateArray())
        {
            names[i++] = frame.TryGetProperty("name", out JsonElement name)
                ? name.GetString() ?? ""
                : "";
        }

        return names;
    }

    private static void ReadEventedProfile(
        JsonElement profile,
        string[] frameNames,
        List<SampleStack> samples,
        double millisecondsPerUnit)
    {
        if (!profile.TryGetProperty("events", out JsonElement events)
            || events.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        string thread = profile.TryGetProperty("name", out JsonElement profileName)
            ? profileName.GetString() ?? ""
            : "";

        List<int> stack = [];
        double? lastAt = null;

        foreach (JsonElement e in events.EnumerateArray())
        {
            double at = e.GetProperty("at").GetDouble();

            if (lastAt is double previous && stack.Count > 0)
            {
                double delta = (at - previous) * millisecondsPerUnit;
                if (delta > 0)
                {
                    string[] frames = new string[stack.Count];
                    for (int i = 0; i < stack.Count; i++)
                    {
                        int index = stack[i];
                        frames[i] = (uint)index < (uint)frameNames.Length ? frameNames[index] : "?";
                    }

                    samples.Add(new SampleStack(frames, delta, thread));
                }
            }

            string type = e.GetProperty("type").GetString() ?? "";
            int frameIndex = e.GetProperty("frame").GetInt32();

            if (type == "O")
            {
                stack.Add(frameIndex);
            }
            else if (type == "C")
            {
                for (int k = stack.Count - 1; k >= 0; k--)
                {
                    if (stack[k] == frameIndex)
                    {
                        stack.RemoveAt(k);
                        break;
                    }
                }
            }

            lastAt = at;
        }
    }

    private static void ReadSampledProfile(
        JsonElement profile,
        string[] frameNames,
        List<SampleStack> samples,
        double millisecondsPerUnit)
    {
        if (!profile.TryGetProperty("samples", out JsonElement profileSamples)
            || profileSamples.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        bool hasWeights = profile.TryGetProperty("weights", out JsonElement weights)
            && weights.ValueKind == JsonValueKind.Array;
        string thread = profile.TryGetProperty("name", out JsonElement profileName)
            ? profileName.GetString() ?? ""
            : "";

        int sampleIndex = 0;
        foreach (JsonElement profileSample in profileSamples.EnumerateArray())
        {
            if (profileSample.ValueKind != JsonValueKind.Array)
            {
                sampleIndex++;
                continue;
            }

            double weight = hasWeights && sampleIndex < weights.GetArrayLength()
                ? weights[sampleIndex].GetDouble() * millisecondsPerUnit
                : millisecondsPerUnit;
            sampleIndex++;
            if (weight <= 0.0)
            {
                continue;
            }

            string[] frames = new string[profileSample.GetArrayLength()];
            int framePosition = 0;
            foreach (JsonElement frame in profileSample.EnumerateArray())
            {
                int frameIndex = frame.GetInt32();
                frames[framePosition++] = (uint)frameIndex < (uint)frameNames.Length
                    ? frameNames[frameIndex]
                    : "?";
            }

            if (frames.Length > 0)
            {
                samples.Add(new SampleStack(frames, weight, thread));
            }
        }
    }

    private static double ResolveMillisecondsPerUnit(JsonElement profile)
    {
        string unit = profile.TryGetProperty("unit", out JsonElement profileUnit)
            ? profileUnit.GetString() ?? ""
            : "";

        return unit switch
        {
            "nanoseconds" => 0.000001,
            "microseconds" => 0.001,
            "milliseconds" => 1.0,
            "seconds" => 1000.0,
            _ => throw new NotSupportedException(
                $"Speedscope CPU input requires a time unit (nanoseconds, microseconds, milliseconds, or seconds); found '{unit}'.")
        };
    }
}
