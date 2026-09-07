// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Decoder for the guest thread-pointer load <c>mov reg, fs:[0]</c> (PR #791).
/// The host FS base is zero, so an unrewritten load reads linear address 0 and
/// faults. Shared by the ahead-of-time patcher and the fault-time recovery so
/// the two accept exactly the same encodings.
/// </summary>
public static class TlsThreadPointerLoad
{
    public const int MaxLength = 12;

    public static bool TryDecode(ReadOnlySpan<byte> code, out int destinationRegister, out int length)
    {
        destinationRegister = 0;
        length = 0;

        var offset = 0;
        while (offset < code.Length && code[offset] == 0x66)
        {
            offset++;
        }

        if (offset >= code.Length || code[offset] != 0x64)
        {
            return false;
        }

        offset++;
        if (offset >= code.Length)
        {
            return false;
        }

        var rex = (byte)0;
        if (code[offset] >= 0x40 && code[offset] <= 0x4F)
        {
            rex = code[offset];
            offset++;
        }

        if (offset + 7 > code.Length || code[offset] != 0x8B)
        {
            return false;
        }

        var modRm = code[offset + 1];
        var sib = code[offset + 2];
        if ((modRm >> 6) != 0 || (modRm & 7) != 4 || sib != 0x25)
        {
            return false;
        }

        var displacement =
            code[offset + 3] |
            (code[offset + 4] << 8) |
            (code[offset + 5] << 16) |
            (code[offset + 6] << 24);
        if (displacement != 0)
        {
            return false;
        }

        destinationRegister = ((modRm >> 3) & 7) | (((rex & 4) != 0) ? 8 : 0);
        length = offset + 7;
        return true;
    }
}
