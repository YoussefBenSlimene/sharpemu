// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

/// <summary>
/// sceHttpUriParse / sceHttpUriEscape / sceHttpUriBuild — a port of KytyPS5
/// libNet.cpp (LibHttp). Pure string work, no network. Mortal Shell and other
/// UE titles call these while building their (offline) online-service URLs;
/// unresolved they returned garbage and the caller walked a zeroed element.
///
/// SceHttpUriElement layout (0x50 bytes):
/// +0x00 int opaque, +0x08 scheme, +0x10 username, +0x18 password,
/// +0x20 hostname, +0x28 path, +0x30 query, +0x38 fragment (char*),
/// +0x40 u16 port, +0x42 reserved[10].
/// </summary>
public static class HttpUriExports
{
    internal const int HttpErrorInvalidValue = unchecked((int)0x804311FE);
    internal const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    internal const int HttpErrorInvalidUrl = unchecked((int)0x80433060);

    internal const int ElementSize = 0x50;
    private const int MaxUrlLength = 64 * 1024;

    /// <summary>Parsed URI parts; null = absent (distinct from empty).</summary>
    internal sealed class UriParts
    {
        public bool Opaque;
        public ushort Port;
        public string? Scheme, Username, Password, Hostname, Path, Query, Fragment;

        public IEnumerable<string?> InPoolOrder()
        {
            yield return Scheme;
            yield return Username;
            yield return Password;
            yield return Hostname;
            yield return Path;
            yield return Query;
            yield return Fragment;
        }

        /// <summary>Pool bytes needed: every present part + its terminator.</summary>
        public ulong PoolBytes() =>
            (ulong)InPoolOrder().Where(p => p is not null).Sum(p => Encoding.UTF8.GetByteCount(p!) + 1);
    }

    [SysAbiExport(
        Nid = "IWalAn-guFs",
        ExportName = "sceHttpUriParse",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpUriParse(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        var srcAddress = ctx[CpuRegister.Rsi];
        var poolAddress = ctx[CpuRegister.Rdx];
        var requireAddress = ctx[CpuRegister.Rcx];
        var prepare = ctx[CpuRegister.R8];

        if (srcAddress == 0 || !ctx.TryReadNullTerminatedUtf8(srcAddress, MaxUrlLength, out var url))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        if (outAddress == 0 && poolAddress == 0 && requireAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        UriParts parts;
        ulong needed;
        if (url.Length == 0)
        {
            // Kyty ParseEmptyUri: opaque, empty scheme/hostname/path, 3 bytes.
            parts = new UriParts { Opaque = true, Scheme = "", Hostname = "", Path = "" };
            needed = 3;
        }
        else
        {
            var result = TryParse(url, out parts);
            if (result != 0)
            {
                return ctx.SetReturn(result);
            }

            needed = parts.PoolBytes();
        }

        if (requireAddress != 0 && !ctx.TryWriteUInt64(requireAddress, needed))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (outAddress != 0)
        {
            var element = new byte[ElementSize];
            element[0] = parts.Opaque ? (byte)1 : (byte)0;
            element[0x40] = (byte)parts.Port;
            element[0x41] = (byte)(parts.Port >> 8);

            if (poolAddress != 0)
            {
                if (prepare != 0 && prepare < needed)
                {
                    return ctx.SetReturn(HttpErrorOutOfMemory);
                }

                var cursor = poolAddress;
                var slot = 0x08;
                foreach (var part in parts.InPoolOrder())
                {
                    ulong pointer = 0;
                    if (part is not null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(part + "\0");
                        if (!ctx.Memory.TryWrite(cursor, bytes))
                        {
                            return ctx.SetReturn(HttpErrorOutOfMemory);
                        }

                        pointer = cursor;
                        cursor += (ulong)bytes.Length;
                    }

                    BitConverter.TryWriteBytes(element.AsSpan(slot, 8), pointer);
                    slot += 8;
                }
            }

            if (!ctx.Memory.TryWrite(outAddress, element))
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "YuOW3dDAKYc",
        ExportName = "sceHttpUriEscape",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpUriEscape(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        var requireAddress = ctx[CpuRegister.Rsi];
        var prepare = ctx[CpuRegister.Rdx];
        var inAddress = ctx[CpuRegister.Rcx];
        if (inAddress == 0 || !ctx.TryReadNullTerminatedUtf8(inAddress, MaxUrlLength, out var input))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var escaped = Escape(Encoding.UTF8.GetBytes(input));
        var needed = (ulong)escaped.Length + 1;
        if (requireAddress != 0)
        {
            ctx.TryWriteUInt64(requireAddress, needed);
        }

        if (outAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        if (prepare < needed)
        {
            return ctx.SetReturn(HttpErrorOutOfMemory);
        }

        return ctx.Memory.TryWrite(outAddress, Encoding.ASCII.GetBytes(escaped + "\0"))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(
        Nid = "5LZA+KPISVA",
        ExportName = "sceHttpUriBuild",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpUriBuild(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        var requireAddress = ctx[CpuRegister.Rsi];
        var prepare = ctx[CpuRegister.Rdx];
        var elementAddress = ctx[CpuRegister.Rcx];
        if (elementAddress == 0 || (outAddress == 0 && requireAddress == 0))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var element = new byte[ElementSize];
        if (!ctx.Memory.TryRead(elementAddress, element))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        string? ReadPart(int offset)
        {
            var pointer = BitConverter.ToUInt64(element, offset);
            return pointer != 0 && ctx.TryReadNullTerminatedUtf8(pointer, MaxUrlLength, out var text)
                ? text
                : null;
        }

        var parts = new UriParts
        {
            Opaque = BitConverter.ToInt32(element, 0) != 0,
            Scheme = ReadPart(0x08),
            Username = ReadPart(0x10),
            Password = ReadPart(0x18),
            Hostname = ReadPart(0x20),
            Path = ReadPart(0x28),
            Query = ReadPart(0x30),
            Fragment = ReadPart(0x38),
            Port = BitConverter.ToUInt16(element, 0x40),
        };

        var bytes = Encoding.UTF8.GetBytes(Build(parts) + "\0");
        var needed = (ulong)bytes.Length;
        if (requireAddress != 0)
        {
            ctx.TryWriteUInt64(requireAddress, needed);
        }

        if (outAddress != 0)
        {
            if (prepare != 0 && prepare < needed)
            {
                return ctx.SetReturn(HttpErrorOutOfMemory);
            }

            if (!ctx.Memory.TryWrite(outAddress, bytes))
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }
        }

        return ctx.SetReturn(0);
    }

    /// <summary>Kyty HttpUriParse for a non-empty URL. Returns 0 or an error.</summary>
    internal static int TryParse(string url, out UriParts parts)
    {
        parts = new UriParts();
        static bool IsSchemeChar(char c, bool first) =>
            first ? char.IsAsciiLetter(c) : char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';

        if (url.Length == 0 || !IsSchemeChar(url[0], true))
        {
            return HttpErrorInvalidUrl;
        }

        var cursor = 0;
        while (cursor < url.Length && url[cursor] != ':')
        {
            if (!IsSchemeChar(url[cursor], false))
            {
                return HttpErrorInvalidUrl;
            }

            cursor++;
        }

        if (cursor >= url.Length)
        {
            return HttpErrorInvalidUrl;
        }

        parts.Scheme = url[..cursor];
        cursor++;

        if (cursor + 1 < url.Length && url[cursor] == '/' && url[cursor + 1] == '/')
        {
            cursor += 2;
            var authority = cursor;
            var authorityEnd = authority;
            while (authorityEnd < url.Length && url[authorityEnd] is not ('/' or '?' or '#'))
            {
                authorityEnd++;
            }

            var hostBegin = authority;
            for (var p = authority; p < authorityEnd; p++)
            {
                if (url[p] != '@')
                {
                    continue;
                }

                var colon = url.IndexOf(':', authority, p - authority);
                if (colon >= 0)
                {
                    parts.Username = url[authority..colon];
                    parts.Password = url[(colon + 1)..p];
                }
                else
                {
                    parts.Username = url[authority..p];
                }

                hostBegin = p + 1;
            }

            var hostEnd = authorityEnd;
            var portBegin = -1;
            if (hostBegin < authorityEnd && url[hostBegin] == '[')
            {
                var close = url.IndexOf(']', hostBegin, authorityEnd - hostBegin);
                if (close < 0)
                {
                    return HttpErrorInvalidUrl;
                }

                hostEnd = close + 1;
                if (hostEnd < authorityEnd && url[hostEnd] == ':')
                {
                    portBegin = hostEnd + 1;
                }
            }
            else
            {
                for (var p = hostBegin; p < authorityEnd; p++)
                {
                    if (url[p] == ':')
                    {
                        hostEnd = p;
                        portBegin = p + 1;
                        break;
                    }
                }
            }

            if (hostBegin < hostEnd)
            {
                parts.Hostname = url[hostBegin..hostEnd];
            }

            if (portBegin >= 0)
            {
                if (portBegin == authorityEnd)
                {
                    return HttpErrorInvalidUrl;
                }

                uint port = 0;
                for (var p = portBegin; p < authorityEnd; p++)
                {
                    if (!char.IsAsciiDigit(url[p]))
                    {
                        return HttpErrorInvalidUrl;
                    }

                    port = (port * 10) + (uint)(url[p] - '0');
                    if (port > 65535)
                    {
                        return HttpErrorInvalidUrl;
                    }
                }

                parts.Port = (ushort)port;
            }

            cursor = authorityEnd;
        }
        else
        {
            parts.Opaque = true;
        }

        var pathBegin = cursor;
        while (cursor < url.Length && url[cursor] is not ('?' or '#'))
        {
            cursor++;
        }

        if (cursor > pathBegin)
        {
            parts.Path = url[pathBegin..cursor];
        }

        if (cursor < url.Length && url[cursor] == '?')
        {
            var queryBegin = cursor++;
            while (cursor < url.Length && url[cursor] != '#')
            {
                cursor++;
            }

            parts.Query = url[queryBegin..cursor];
        }

        if (cursor < url.Length && url[cursor] == '#')
        {
            parts.Fragment = url[cursor..];
        }

        return 0;
    }

    internal static string Build(UriParts parts)
    {
        var uri = new StringBuilder();
        if (parts.Scheme is not null)
        {
            uri.Append(parts.Scheme).Append(':');
        }

        if (!parts.Opaque && parts.Hostname is not null)
        {
            uri.Append("//");
            if (parts.Username is not null)
            {
                uri.Append(parts.Username);
                if (parts.Password is not null)
                {
                    uri.Append(':').Append(parts.Password);
                }

                uri.Append('@');
            }

            uri.Append(parts.Hostname);
            if (parts.Port != 0)
            {
                uri.Append(':').Append(parts.Port);
            }
        }

        uri.Append(parts.Path).Append(parts.Query).Append(parts.Fragment);
        return uri.ToString();
    }

    internal static string Escape(ReadOnlySpan<byte> input)
    {
        const string hex = "0123456789ABCDEF";
        var escaped = new StringBuilder(input.Length);
        foreach (var b in input)
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~'))
            {
                escaped.Append(c);
            }
            else
            {
                escaped.Append('%').Append(hex[b >> 4]).Append(hex[b & 0xF]);
            }
        }

        return escaped.ToString();
    }
}
