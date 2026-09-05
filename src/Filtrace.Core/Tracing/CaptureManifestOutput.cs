// Copyright (c) Jeremy W Kuhne and contributors
// SPDX-License-Identifier: MIT
// See LICENSE file in the project root for full license information

namespace Filtrace.Tracing;

/// <summary>
///  Applies deterministic size and character-safety bounds to manifest analysis output.
/// </summary>
internal static class CaptureManifestOutput
{
    /// <summary>
    ///  The maximum number of warnings retained for one manifest case.
    /// </summary>
    public const int MaxWarningsPerCase = 4;

    /// <summary>
    ///  The maximum number of UTF-16 characters retained in one case warning.
    /// </summary>
    public const int MaxWarningLength = 240;

    /// <summary>
    ///  The maximum number of UTF-16 characters retained in a frame name.
    /// </summary>
    public const int MaxFrameLength = 160;

    /// <summary>
    ///  Appends a sanitized, bounded warning when the per-case warning budget has room.
    /// </summary>
    /// <param name="warnings">The warnings already retained for the case.</param>
    /// <param name="warning">The warning text to sanitize and append.</param>
    public static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaxWarningsPerCase)
        {
            warnings.Add(Bound(warning, MaxWarningLength));
        }
    }

    /// <summary>
    ///  Replaces control characters and shortens a frame name without splitting a surrogate pair.
    /// </summary>
    /// <param name="frame">The frame name to make safe for bounded output.</param>
    /// <returns>The original frame when already safe and within budget; otherwise a bounded copy.</returns>
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
