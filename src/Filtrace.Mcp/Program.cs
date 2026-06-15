// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Filtrace.Mcp;
using Filtrace.Output;
using Filtrace.Server;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP JSON-RPC stream; every diagnostic must go to stderr or it
// corrupts the protocol. Drop the host's default providers first so nothing the host
// pre-registered can reach stdout, then add a single console provider pinned to stderr
// for every level.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(static options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// One cache of parsed traces is shared across every tool call for the server's lifetime.
builder.Services.AddSingleton<TraceStore>();

builder.Services
    .AddMcpServer(static options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "filtrace",
            Version = GetServerVersion()
        };

        // The workflow summary the client surfaces to the model at initialize time.
        options.ServerInstructions = TraceServerInstructions.Text;
    })
    .WithStdioServerTransport()
    // Register the tools with the shared serializer options so each typed result - its
    // structured content, its text mirror, and the generated output schema - is written
    // with the same source-generated (AOT-safe), deterministically rounded contract the
    // CLI uses.
    .WithTools<TraceTools>(OutputJson.SerializerOptions);

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

// Reports the server version a client sees in serverInfo at initialize time. MinVer
// stamps the informational version (e.g. "0.1.0+<sha>") but leaves AssemblyVersion at
// 0.0.0.0, so read the informational version and drop the "+<build metadata>" suffix
// for a clean semantic version. Falls back to the file version, then to "0.0.0".
static string GetServerVersion()
{
    Assembly assembly = typeof(TraceTools).Assembly;

    string? informational = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;

    if (!string.IsNullOrEmpty(informational))
    {
        // Strip the "+<commit>" build-metadata suffix MinVer appends.
        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }

    return assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0";
}

