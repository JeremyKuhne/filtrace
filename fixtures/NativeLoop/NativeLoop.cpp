// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

// A small native workload for verifying local native symbol resolution. Being a plain
// C++ binary is the point: it carries no CLR rundown, so its frames stay unresolved
// until something loads its PDB, and that PDB exists only where the binary was built.
// It builds in seconds with cl.exe, which keeps the check cheap enough to run in CI.

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace
{
    constexpr size_t BufferLength = 4096;
    constexpr size_t TableCount = 1024;

    // Each leaf is kept out of line and given a distinct name so a CPU sample landing in
    // it identifies which one, rather than collapsing into an inlined blob.
    __declspec(noinline) uint32_t ComputeChecksum(const uint8_t* data, size_t length)
    {
        uint32_t checksum = 2166136261u;
        for (size_t index = 0; index < length; index++)
        {
            checksum ^= data[index];
            checksum *= 16777619u;
        }

        return checksum;
    }

    __declspec(noinline) void TransformBuffer(uint8_t* data, size_t length, uint32_t seed)
    {
        for (size_t index = 0; index < length; index++)
        {
            data[index] = static_cast<uint8_t>((data[index] * 31u) + (seed >> (index & 7)));
        }
    }

    __declspec(noinline) uint32_t SearchTable(const uint32_t* table, size_t count, uint32_t needle)
    {
        uint32_t hits = 0;
        for (size_t index = 0; index < count; index++)
        {
            if ((table[index] ^ needle) % 97u == 0)
            {
                hits++;
            }
        }

        return hits;
    }

    // An inclusive ancestor of all three leaves, so a caller/callee view has something
    // above the leaves to attribute time to.
    __declspec(noinline) uint32_t NativeEntryPoint(int iterations)
    {
        uint8_t buffer[BufferLength];
        uint32_t table[TableCount];
        for (size_t index = 0; index < BufferLength; index++)
        {
            buffer[index] = static_cast<uint8_t>(index);
        }

        for (size_t index = 0; index < TableCount; index++)
        {
            table[index] = static_cast<uint32_t>(index * 7919u);
        }

        uint32_t accumulator = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            accumulator += ComputeChecksum(buffer, BufferLength);
            TransformBuffer(buffer, BufferLength, accumulator);
            accumulator += SearchTable(table, TableCount, accumulator);
        }

        return accumulator;
    }
}

int main(int argc, char** argv)
{
    int iterations = 2000;
    for (int index = 1; index < argc - 1; index++)
    {
        if (std::strcmp(argv[index], "--iterations") == 0)
        {
            iterations = std::atoi(argv[index + 1]);
        }
    }

    if (iterations <= 0)
    {
        std::fprintf(stderr, "iterations must be positive\n");
        return 1;
    }

    // Printing the result is what keeps the optimizer from discarding the whole workload.
    std::printf("nativeloop iterations %d checksum %u\n", iterations, NativeEntryPoint(iterations));
    return 0;
}
