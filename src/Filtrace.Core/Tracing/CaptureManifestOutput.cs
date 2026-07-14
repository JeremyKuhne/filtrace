// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

internal static class CaptureManifestOutput
{
    public const int MaxWarningsPerCase = 4;
    public const int MaxWarningLength = 240;
    public const int MaxFrameLength = 160;

    public static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaxWarningsPerCase)
        {
            warnings.Add(Bound(warning, MaxWarningLength));
        }
    }

    public static string BoundFrame(string frame) => Bound(frame, MaxFrameLength);

    private static string Bound(string value, int maxLength)
    {
        int length = Math.Min(value.Length, maxLength);
        if (length < value.Length
            && length > 0
            && char.IsHighSurrogate(value[length - 1])
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        int firstControl = -1;
        for (int index = 0; index < length; index++)
        {
            if (char.IsControl(value[index]))
            {
                firstControl = index;
                break;
            }
        }

        if (firstControl < 0)
        {
            return length == value.Length ? value : value[..length];
        }

        char[] sanitized = value[..length].ToCharArray();
        for (int index = firstControl; index < sanitized.Length; index++)
        {
            if (char.IsControl(sanitized[index]))
            {
                sanitized[index] = ' ';
            }
        }

        return new string(sanitized);
    }
}