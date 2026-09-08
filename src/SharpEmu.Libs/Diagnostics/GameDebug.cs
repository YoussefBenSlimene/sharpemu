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

    public static bool Enabled =>
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_GAME_DBG"),
            "1",
            StringComparison.Ordinal);

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

    private static readonly HashSet<string> _onceKeys = new();
}
