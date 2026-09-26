// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.Libs.Diagnostics;

/// <summary>
/// Always-on fault-hunting trace for the black-screen / freeze investigations
/// (Quake II empty Com_Error, Hellboy PreloadManager spin, Mortal Shell black
/// screen). Writes to stderr with a GAME-DBG prefix at a low, capped rate so
/// normal play is not flooded, and can be silenced with
/// SHARPEMU_DISABLE_GAME_DBG=1.
/// </summary>
public static class GameDebug
{
    private static int _rateBucket;
    private static readonly object Gate = new();

    // Read once: this is consulted on per-draw hot paths, and an environment
    // lookup (plus string compare) per draw/compute/present added up.
    private static readonly bool _enabled =
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_GAME_DBG"),
            "1",
            StringComparison.Ordinal);

    public static bool Enabled => _enabled;

    /// <summary>
    /// True when the next <see cref="RateLimited"/> call would actually print.
    /// Callers on per-draw paths check this before formatting the message, so
    /// the (usually suppressed) string is never built. Advances the bucket.
    /// </summary>
    public static bool ShouldEmitRateLimited(int maxPerRun = 64)
    {
        if (!_enabled)
        {
            return false;
        }

        var counter = Interlocked.Increment(ref _rateBucket);
        return counter <= maxPerRun || counter % 64 == 0;
    }

    /// <summary>Prints a message already admitted by <see cref="ShouldEmitRateLimited"/>.</summary>
    public static void Emit(string tag, string message)
    {
        Console.Error.WriteLine($"[GAME-DBG:{tag}] {message}");
        Console.Error.Flush();
    }

    /// <summary>
    /// Prints once per key (e.g. an address or call site) — for one-shot
    /// findings that must not flood the log.
    /// </summary>
    public static void Once(string key, string message)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            if (!_onceKeys.Add(key))
            {
                return;
            }
        }

        Console.Error.WriteLine($"[GAME-DBG] {message}");
        Console.Error.Flush();
    }

    /// <summary>
    /// Rate-limited print: at most <paramref name="maxPerRun"/> lines with this
    /// tag per emulator session, then every 64th call.
    /// </summary>
    public static void RateLimited(string tag, string message, int maxPerRun = 64)
    {
        if (!Enabled)
        {
            return;
        }

        var counter = Interlocked.Increment(ref _rateBucket);
        if (counter > maxPerRun && counter % 64 != 0)
        {
            return;
        }

        Console.Error.WriteLine($"[GAME-DBG:{tag}] {message}");
        Console.Error.Flush();
    }

    /// <summary>Dumps a hex+ascii window of guest memory for offline analysis.</summary>
    public static string HexPreview(ReadOnlySpan<byte> data, int maxBytes = 32)
    {
        var len = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(len * 3);
        for (var i = 0; i < len; i++)
        {
            sb.Append(data[i].ToString("X2"));
            sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads the raw texel data at a texture address and reports whether it's
    /// all zeros (uninitialized) or has content. Called for 1×1 placeholder
    /// textures to distinguish "never uploaded" from "uploaded black".
    /// </summary>
    public static void CheckTextureContent(SharpEmu.HLE.ICpuMemory memory, ulong texelAddress, uint format, uint width, uint height)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var bytesPerPixel = format switch { 10 => 4, 14 => 16, _ => 4 };
            var totalBytes = (int)(width * height * bytesPerPixel);
            if (totalBytes <= 0 || totalBytes > 65536)
            {
                totalBytes = 64;
            }

            var data = new byte[totalBytes];
            if (!memory.TryRead(texelAddress, data))
            {
                Once($"texel-{texelAddress:X16}", $"texel read FAILED at 0x{texelAddress:X16} ({totalBytes} bytes)");
                return;
            }

            var nonzero = 0;
            foreach (var b in data)
            {
                if (b != 0)
                {
                    nonzero++;
                }
            }

            Once(
                $"texel-{texelAddress:X16}",
                $"texel content at 0x{texelAddress:X16}: {nonzero}/{totalBytes} nonzero bytes " +
                $"fmt={format} {width}x{height} hex={HexPreview(data, 16)}");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private static readonly HashSet<string> _onceKeys = new();
}
