// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

public static class KernelExports
{
    private static readonly object _cxaGate = new();
    private static readonly List<CxaDestructorEntry> _cxaDestructors = new();
    private static readonly object _coredumpGate = new();
    private static ulong _coredumpHandler;
    private static ulong _coredumpHandlerContext;

    private readonly record struct CxaDestructorEntry(
        ulong Function,
        ulong Argument,
        ulong ModuleHandle);

    [SysAbiExport(
        Nid = "WB66evu8bsU",
        ExportName = "sceKernelGetCompiledSdkVersion",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetCompiledSdkVersion(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8zLSfEfW5AU",
        ExportName = "sceCoredumpRegisterCoredumpHandler",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceCoredump")]
    public static int CoredumpRegisterHandler(CpuContext ctx)
    {
        lock (_coredumpGate)
        {
            _coredumpHandler = ctx[CpuRegister.Rdi];
            _coredumpHandlerContext = ctx[CpuRegister.Rsi];
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "uMei1W9uyNo",
        ExportName = "exit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Exit(CpuContext ctx)
    {
        var status = unchecked((int)ctx[CpuRegister.Rdi]);
        Console.Error.WriteLine($"[LOADER][INFO] exit(status={status})");
        GuestThreadExecution.RequestCurrentEntryExit("exit", status);
        ctx[CpuRegister.Rax] = unchecked((ulong)status);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "XKRegsFpEpk",
        ExportName = "catchReturnFromMain",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CatchReturnFromMain(CpuContext ctx)
    {
        var status = unchecked((int)ctx[CpuRegister.Rdi]);
        Console.Error.WriteLine($"[LOADER][INFO] catchReturnFromMain(status={status})");
        GuestThreadExecution.RequestCurrentEntryExit("catchReturnFromMain", status);
        ctx[CpuRegister.Rax] = unchecked((ulong)status);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "bzQExy189ZI",
        ExportName = "_init_env",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int InitEnv(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8G2LB+A3rzg",
        ExportName = "atexit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Atexit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "tsvEmnenz48",
        ExportName = "__cxa_atexit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaAtexit(CpuContext ctx)
    {
        var destructorFunction = ctx[CpuRegister.Rdi];
        var destructorArgument = ctx[CpuRegister.Rsi];
        var moduleHandle = ctx[CpuRegister.Rdx];
        if (destructorFunction == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        lock (_cxaGate)
        {
            _cxaDestructors.Add(new CxaDestructorEntry(
                destructorFunction,
                destructorArgument,
                moduleHandle));
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "H2e8t5ScQGc",
        ExportName = "__cxa_finalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaFinalize(CpuContext ctx)
    {
        var moduleHandle = ctx[CpuRegister.Rdi];

        lock (_cxaGate)
        {
            if (moduleHandle == 0)
            {
                _cxaDestructors.Clear();
            }
            else
            {
                for (var i = _cxaDestructors.Count - 1; i >= 0; i--)
                {
                    if (_cxaDestructors[i].ModuleHandle == moduleHandle)
                    {
                        _cxaDestructors.RemoveAt(i);
                    }
                }
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kbw4UHHSYy0",
        ExportName = "__pthread_cxa_finalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCxaFinalize(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "6Z83sYWFlA8",
        ExportName = "_exit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int UnderscoreExit(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ac86z8q7T8A",
        ExportName = "sceKernelExitSblock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelExitSblock(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "6UgtwV+0zb4",
        ExportName = "scePthreadCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCreate(CpuContext ctx)
        => PthreadCreateCore(ctx, ctx[CpuRegister.R8]);

    private static int PthreadCreateCore(CpuContext ctx, ulong nameAddress)
    {
        var threadIdAddress = ctx[CpuRegister.Rdi];
        var attrAddress = ctx[CpuRegister.Rsi];
        var entryAddress = ctx[CpuRegister.Rdx];
        var argument = ctx[CpuRegister.Rcx];
        var name = nameAddress == 0 ? string.Empty : ReadCString(ctx, nameAddress, 256);
        // Install the guest-object allocator before creating the handle so the
        // very first pthread_t handed to guest code is already a guest-memory
        // object (scePthreadCreate runs before the first scePthreadSelf).
        KernelPthreadCompatExports.EnsureGuestThreadObjectAllocatorForCreate(ctx);
        var threadHandle = KernelPthreadState.CreateThreadHandle(name);
        KernelPthreadExtendedCompatExports.GetThreadStartScheduling(
            ctx,
            attrAddress,
            out var priority,
            out var affinityMask);
        KernelPthreadExtendedCompatExports.RegisterThreadStart(
            threadHandle,
            name,
            priority,
            affinityMask);
        if (threadIdAddress != 0 && !ctx.TryWriteUInt64(threadIdAddress, threadHandle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (ShouldTracePthread())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] pthread_create: out=0x{threadIdAddress:X16} attr=0x{attrAddress:X16} " +
                $"entry=0x{entryAddress:X16} arg=0x{argument:X16} name_ptr=0x{nameAddress:X16} " +
                $"name='{name}' priority={priority} affinity=0x{affinityMask:X} -> thread=0x{threadHandle:X16}");
        }

        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is not null && entryAddress != 0)
        {
            var request = new GuestThreadStartRequest(
                threadHandle,
                entryAddress,
                argument,
                attrAddress,
                name,
                priority,
                affinityMask);
            if (!scheduler.TryStartThread(ctx, request, out var error))
            {
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] pthread_create: failed to schedule guest thread '{name}' entry=0x{entryAddress:X16}: {error}");
                ctx[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "OxhIB8LB-PQ",
        ExportName = "pthread_create",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCreate(CpuContext ctx)
    {
        return PthreadCreateCore(ctx, nameAddress: 0);
    }

    [SysAbiExport(
        Nid = "Jmi+9w9u0E4",
        ExportName = "pthread_create_name_np",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCreateNameNp(CpuContext ctx)
    {
        return PthreadCreateCore(ctx, ctx[CpuRegister.R8]);
    }

    [SysAbiExport(
        Nid = "3kg7rT0NQIs",
        ExportName = "scePthreadExit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadExit(CpuContext ctx)
    {
        var value = ctx[CpuRegister.Rdi];
        // Run cleanup on the still-executable thread before unwinding it.
        KernelPthreadExtendedCompatExports.RunThreadLocalDestructors(ctx);
        KernelMemoryCompatExports.RunThreadDtors(ctx);
        GuestThreadExecution.RequestCurrentEntryExit("scePthreadExit", value);
        ctx[CpuRegister.Rax] = value;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "FJrT5LuUBAU",
        ExportName = "pthread_exit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePosix")]
    public static int PosixPthreadExit(CpuContext ctx)
    {
        var value = ctx[CpuRegister.Rdi];
        KernelPthreadExtendedCompatExports.RunThreadLocalDestructors(ctx);
        KernelMemoryCompatExports.RunThreadDtors(ctx);
        GuestThreadExecution.RequestCurrentEntryExit("pthread_exit", value);
        ctx[CpuRegister.Rax] = value;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "onNY9Byn-W8",
        ExportName = "scePthreadJoin",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadJoin(CpuContext ctx)
    {
        var threadId = ctx[CpuRegister.Rdi];
        var returnValueAddress = ctx[CpuRegister.Rsi];

        if (ShouldTracePthread())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] pthread_join: thread=0x{threadId:X16} retval_out=0x{returnValueAddress:X16}");
        }

        var returnValue = 0UL;
        if (GuestThreadExecution.Scheduler is { } scheduler &&
            !scheduler.TryJoinThread(ctx, threadId, out returnValue, out var error))
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] pthread_join: thread=0x{threadId:X16}: {error}");
            var result = string.Equals(
                error,
                "thread cannot join itself",
                StringComparison.Ordinal)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
            return (int)result;
        }

        if (returnValueAddress != 0 &&
            !ctx.TryWriteUInt64(returnValueAddress, returnValue))
        {
            ctx[CpuRegister.Rax] =
                unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "h9CcP3J0oVM",
        ExportName = "pthread_join",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadJoin(CpuContext ctx)
    {
        return PthreadJoin(ctx);
    }

    [SysAbiExport(
        Nid = "wuCroIGjt2g",
        ExportName = "open",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Open(CpuContext ctx) => KernelMemoryCompatExports.PosixOpen(ctx);

    [SysAbiExport(
        Nid = "1G3lF1Gg1k8",
        ExportName = "sceKernelOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelOpen(CpuContext ctx)
    {
        // Real hardware returns -1 with errno on failure (BSD syscall
        // convention). Leaking the raw 0x8002xxxx sentinel makes callers that
        // check `fd == -1` store it as a "valid" handle and later close/read
        // through it (Quake II: close(0x80020002) spam -> fatal abort).
        var result = KernelMemoryCompatExports.KernelOpenUnderscore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_OK
            ? 0
            : KernelMemoryCompatExports.KernelSyscallFailure(ctx, result);
    }

    [SysAbiExport(
        Nid = "mqQMh1zPPT8",
        ExportName = "fstat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fstat(CpuContext ctx) => KernelMemoryCompatExports.PosixFstat(ctx);

    [SysAbiExport(
        Nid = "hcuQgD53UxM",
        ExportName = "printf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Printf(CpuContext ctx)
    {
        ulong fmtPtr = ctx[CpuRegister.Rdi];
        string fmt = ReadCString(ctx, fmtPtr, 4096);
        string outStr = KernelMemoryCompatExports.FormatStringFromVarArgs(ctx, fmt, firstGpArgIndex: 1);
        if (outStr.EndsWith('\n') || outStr.EndsWith('\r'))
        {
            Console.Write($"[DEBUG][PRINF] {outStr}");
        }
        else
        {
            Console.WriteLine($"[DEBUG][PRINF] {outStr}");
        }

        ctx[CpuRegister.Rax] = (ulong)System.Text.Encoding.UTF8.GetByteCount(outStr);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EMutwaQ34Jo",
        ExportName = "perror",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Perror(CpuContext ctx)
    {
        ulong sPtr = ctx[CpuRegister.Rdi];

        string msg;
        if (sPtr == 0)
        {
            msg = "perror(NULL)";
        }
        else
        {
            msg = ReadCString(ctx, sPtr, 2048);
            msg = $"perror(\"{msg}\")";
        }

        Console.WriteLine(msg);

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int _abortTraceCount;

    // One-shot: dump the abort call site — return address, registers and the
    // strings the game's Com_Error machinery has staged — so the empty-message
    // fatal (Quake II "Installation") can be traced to its source.
    private static void TraceAbortCallSite(CpuContext ctx)
    {
        if (Interlocked.Increment(ref _abortTraceCount) != 1)
        {
            return;
        }

        try
        {
            var rsp = ctx[CpuRegister.Rsp];
            var pointer = new byte[8];
            ulong ReadU64(ulong address) =>
                ctx.Memory.TryRead(address, pointer)
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(pointer)
                    : 0;

            string ReadStr(ulong address)
            {
                if (address == 0)
                {
                    return "<null>";
                }

                Span<byte> buf = stackalloc byte[96];
                if (!ctx.Memory.TryRead(address, buf))
                {
                    return $"<unreadable 0x{address:X16}>";
                }

                var len = buf.IndexOf((byte)0);
                if (len < 0)
                {
                    len = buf.Length;
                }

                return System.Text.Encoding.UTF8.GetString(buf[..len]);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] abort call-site: ret=0x{ReadU64(rsp):X16} " +
                $"rdi=0x{ctx[CpuRegister.Rdi]:X16} rsi=0x{ctx[CpuRegister.Rsi]:X16} " +
                $"rdx=0x{ctx[CpuRegister.Rdx]:X16} rcx=0x{ctx[CpuRegister.Rcx]:X16} " +
                $"r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16} " +
                $"rbx=0x{ctx[CpuRegister.Rbx]:X16} r12=0x{ctx[CpuRegister.R12]:X16} " +
                $"r13=0x{ctx[CpuRegister.R13]:X16} r14=0x{ctx[CpuRegister.R14]:X16} " +
                $"r15=0x{ctx[CpuRegister.R15]:X16} rbp=0x{ctx[CpuRegister.Rbp]:X16}");
            for (var slot = 0; slot < 6; slot++)
            {
                var value = ReadU64(rsp + (ulong)(8 * slot));
                Console.Error.WriteLine(
                    $"[LOADER][INFO]   abort stack[{slot}] @0x{rsp + (ulong)(8 * slot):X16} = 0x{value:X16} \"{ReadStr(value)}\"");
            }

            // The known Com_Error staging globals from earlier runs.
            foreach (var global in new ulong[] { 0x80230E0F0, 0x802AB1650, 0x8019B0DD0 })
            {
                var staged = ReadU64(global);
                Console.Error.WriteLine(
                    $"[LOADER][INFO]   err-global 0x{global:X16} -> 0x{staged:X16} \"{ReadStr(staged)}\"");
            }

            // Guest code at the abort return address: lets us disassemble which
            // cvar/string the caller passed as the (empty) error message.
            var ret = ReadU64(rsp);
            if (ret >= 0x10000)
            {
                Span<byte> code = stackalloc byte[64];
                if (ctx.Memory.TryRead(ret - 16, code))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][INFO]   abort-ret code window @0x{ret - 16:X16}: " +
                        $"{SharpEmu.Libs.Diagnostics.GameDebug.HexPreview(code, 64)}");
                }

                // The frame above: caller return + its stack args.
                var rbp = ctx[CpuRegister.Rbp];
                for (var frame = 0; frame < 3; frame++)
                {
                    var nextRbp = ReadU64(rbp);
                    var callerRet = ReadU64(rbp + 8);
                    if (nextRbp == 0 || callerRet < 0x10000)
                    {
                        break;
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][INFO]   abort frame#{frame}: rbp=0x{rbp:X16} ret=0x{callerRet:X16}");
                    rbp = nextRbp;
                }
            }

            Console.Error.Flush();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] abort trace failed: {exception.GetType().Name}: {exception.Message}");
            Console.Error.Flush();
        }
    }

    private static string ReadCString(CpuContext ctx, ulong address, int maxLen)
    {
        Span<byte> buf = stackalloc byte[maxLen];
        Span<byte> one = stackalloc byte[1];
        var len = 0;
        while (len < buf.Length)
        {
            if (!ctx.Memory.TryRead(address + (ulong)len, one))
                return len == 0 ? $"<unreadable 0x{address:X16}>" : System.Text.Encoding.UTF8.GetString(buf[..len]);

            if (one[0] == 0)
                break;

            buf[len++] = one[0];
        }

        try { return System.Text.Encoding.UTF8.GetString(buf[..len]); }
        catch { return System.Text.Encoding.ASCII.GetString(buf[..len]); }
    }

    private static bool ShouldTracePthread()
    {
        return string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREADS"), "1", StringComparison.Ordinal);
    }

    [SysAbiExport(
        Nid = "L1SBTkC+Cvw",
        ExportName = "abort",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Abort(CpuContext ctx)
    {
        // Route through the same graceful guest-entry-exit path as exit(): letting the call
        // fall through to the host's native abort() does not unwind the guest thread cleanly.
        Console.Error.WriteLine("[LOADER][INFO] abort() called by guest - terminating");
        TraceAbortCallSite(ctx);
        GuestThreadExecution.RequestCurrentEntryExit("abort", -1);
        ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
    Nid = "tU5e3f9gSiU",
    ExportName = "sceKernelIsTrinityMode",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libKernel")]
    public static int KernelIsTrinityMode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
    Nid = "DLORcroUqbc",
    ExportName = "sceKernelGetOpenPsId",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libKernel")]
    public static int KernelGetOpenPsId(CpuContext ctx)
    {
        ulong bufferPtr = ctx[CpuRegister.Rdi];

        if (bufferPtr == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> openPsId = stackalloc byte[16];

        if (!ctx.Memory.TryWrite(bufferPtr, openPsId))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
