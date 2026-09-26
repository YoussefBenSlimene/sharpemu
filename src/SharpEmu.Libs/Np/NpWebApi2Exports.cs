// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private static int _initialized;
    private static int _nextLibraryContextHandle;
    private static int _nextPushEventHandle;
    private static int _nextUserContextHandle = 1000;
    private static readonly object _contextGate = new();
    private static readonly HashSet<int> _libraryContexts = [];

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var libraryContextId = CreateLibraryContextId();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "MsaFhR+lPE4",
        ExportName = "sceNpWebApi2PushEventCreateFilter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var filterHandle = Interlocked.Increment(ref _nextPushEventHandle);
        TraceNpWebApi2("push-event-create-filter", libraryContextId, (ulong)filterHandle);
        return ctx.SetReturn(filterHandle);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2InitializeAlt(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var handle = CreatePushEventHandle();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init-alt", libraryContextId, 0);
        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);

        TraceNpWebApi2(
            "create-user-context",
            libraryContextId,
            unchecked((uint)userId));

        if (Volatile.Read(ref _initialized) == 0 ||
            !IsValidLibraryContextId(libraryContextId) ||
            userId == -1)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var userContextId = Interlocked.Increment(ref _nextUserContextHandle);
        return ctx.SetReturn(userContextId);
    }

    [SysAbiExport(
        Nid = "QafxeZM3WK4",
        ExportName = "sceNpWebApi2PushEventDeletePushContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeletePushContext(CpuContext ctx)
    {
        // Offline stub (KytyPS5 parity): the push-event context bookkeeping is
        // a no-op and the delete reports success. Quake II calls this during
        // NP social teardown with user_context_id=-1; leaving it unresolved
        // returned an error that fed the game's fatal path.
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var pushContextId = ctx[CpuRegister.Rsi];
        TraceNpWebApi2("push-event-delete-push-context", userContextId, pushContextId);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        RemoveLibraryContextId(libraryContextId);
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    // ---- Offline request surface (KytyPS5 libNet.cpp LibNpWebApi2 parity) ----
    //
    // UE online subsystems (Smurfs, Mortal Shell: OnlineAsyncTaskThreadPS5)
    // poll these from their async task thread. Unresolved, every call logged a
    // blocking stderr warning and returned an ORBIS "not found" sentinel the
    // engine retries forever. Kyty answers them the way a signed-out console
    // does: requests are created, SendRequest returns NOT_SIGNED_IN, bodies are
    // empty, and CheckTimeout is a no-op maintenance tick.

    private const int NpWebApi2ErrorRequestNotFound = unchecked((int)0x80553406);
    private const int NpWebApi2ErrorNotSignedIn = unchecked((int)0x80553407);
    private const string JsonContentType = "application/json; charset=utf-8";
    private static long _nextRequestId = 1000;
    private static int _nextCallbackId;
    private static readonly HashSet<long> _requests = [];

    [SysAbiExport(
        Nid = "3Tt9zL3tkoc",
        ExportName = "sceNpWebApi2CheckTimeout",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CheckTimeout(CpuContext ctx)
    {
        // Internal maintenance tick; requests complete synchronously here, so
        // there is no pending timeout state to advance (Kyty: empty body).
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "9X9+cneTGUU",
        ExportName = "sceNpWebApi2DeleteUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2DeleteUserContext(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "3EI-OSJ65Xc",
        ExportName = "sceNpWebApi2CreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateRequest(CpuContext ctx)
    {
        // (userCtxId, apiGroup, path, method, contentParameter, int64* requestId)
        var requestIdAddress = ctx[CpuRegister.R9];
        if (requestIdAddress != 0)
        {
            var requestId = Interlocked.Increment(ref _nextRequestId);
            if (!ctx.TryWriteUInt64(requestIdAddress, unchecked((ulong)requestId)))
            {
                return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
            }

            lock (_contextGate)
            {
                _requests.Add(requestId);
            }

            TraceNpWebApi2("create-request", unchecked((int)ctx[CpuRegister.Rdi]), unchecked((ulong)requestId));
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "lQOCF84lvzw",
        ExportName = "sceNpWebApi2SendRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2SendRequest(CpuContext ctx)
    {
        // (int64 requestId, data, size, ResponseInformationOption* info)
        var requestId = unchecked((long)ctx[CpuRegister.Rdi]);
        if (!HasRequest(requestId))
        {
            return ctx.SetReturn(NpWebApi2ErrorRequestNotFound);
        }

        var info = ctx[CpuRegister.Rcx];
        if (info != 0)
        {
            // struct { int32 http_status; char* error_object; size_t error_object_size; size_t response_data_size; }
            _ = ctx.TryWriteUInt32(info, 0);
            _ = ctx.TryWriteUInt64(info + 24, 0);
            if (ctx.TryReadUInt64(info + 8, out var errorObject) && errorObject != 0 &&
                ctx.TryReadUInt64(info + 16, out var errorObjectSize) && errorObjectSize > 0)
            {
                _ = ctx.Memory.TryWrite(errorObject, new byte[1]);
            }
        }

        TraceNpWebApi2("send-request", 0, unchecked((ulong)requestId));
        return ctx.SetReturn(NpWebApi2ErrorNotSignedIn);
    }

    [SysAbiExport(
        Nid = "OOY9+ObfKec",
        ExportName = "sceNpWebApi2ReadData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2ReadData(CpuContext ctx)
    {
        var requestId = unchecked((long)ctx[CpuRegister.Rdi]);
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        // Offline: the response body is always empty (0 bytes read).
        return ctx.SetReturn(HasRequest(requestId) ? 0 : NpWebApi2ErrorRequestNotFound);
    }

    [SysAbiExport(
        Nid = "zpiPsH7dbFQ",
        ExportName = "sceNpWebApi2AbortRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2AbortRequest(CpuContext ctx) =>
        ctx.SetReturn(HasRequest(unchecked((long)ctx[CpuRegister.Rdi])) ? 0 : NpWebApi2ErrorRequestNotFound);

    [SysAbiExport(
        Nid = "vvzWO-DvG1s",
        ExportName = "sceNpWebApi2DeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2DeleteRequest(CpuContext ctx)
    {
        lock (_contextGate)
        {
            _requests.Remove(unchecked((long)ctx[CpuRegister.Rdi]));
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "egOOvrnF6mI",
        ExportName = "sceNpWebApi2AddHttpRequestHeader",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2AddHttpRequestHeader(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "HwP3aM+c85c",
        ExportName = "sceNpWebApi2GetHttpResponseHeaderValueLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2GetHttpResponseHeaderValueLength(CpuContext ctx)
    {
        var requestId = unchecked((long)ctx[CpuRegister.Rdi]);
        var fieldName = ctx[CpuRegister.Rsi];
        var lengthAddress = ctx[CpuRegister.Rdx];
        if (fieldName == 0 || lengthAddress == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        if (!HasRequest(requestId))
        {
            return ctx.SetReturn(NpWebApi2ErrorRequestNotFound);
        }

        var length = IsContentTypeField(ctx, fieldName) ? (ulong)JsonContentType.Length + 1 : 0UL;
        _ = ctx.TryWriteUInt64(lengthAddress, length);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "hksbskNToEA",
        ExportName = "sceNpWebApi2GetHttpResponseHeaderValue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2GetHttpResponseHeaderValue(CpuContext ctx)
    {
        var requestId = unchecked((long)ctx[CpuRegister.Rdi]);
        var fieldName = ctx[CpuRegister.Rsi];
        var value = ctx[CpuRegister.Rdx];
        var valueSize = ctx[CpuRegister.Rcx];
        if (fieldName == 0 || value == 0 || valueSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        if (!HasRequest(requestId))
        {
            return ctx.SetReturn(NpWebApi2ErrorRequestNotFound);
        }

        var header = IsContentTypeField(ctx, fieldName) ? JsonContentType : string.Empty;
        var bytes = System.Text.Encoding.ASCII.GetBytes(header);
        var copy = (int)Math.Min((ulong)bytes.Length, valueSize - 1);
        var buffer = new byte[copy + 1];
        Array.Copy(bytes, buffer, copy);
        _ = ctx.Memory.TryWrite(value, buffer);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "fIATVMo4Y1w",
        ExportName = "sceNpWebApi2PushEventDeleteHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeleteHandle(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "fY3QqeNkF8k",
        ExportName = "sceNpWebApi2PushEventRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventRegisterCallback(CpuContext ctx) =>
        ctx.SetReturn(Interlocked.Increment(ref _nextCallbackId));

    private static bool HasRequest(long requestId)
    {
        lock (_contextGate)
        {
            return _requests.Contains(requestId);
        }
    }

    private static bool IsContentTypeField(CpuContext ctx, ulong fieldName) =>
        ctx.TryReadNullTerminatedUtf8(fieldName, 64, out var name) &&
        string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase);

    private static int CreateLibraryContextId()
    {
        var handle = Interlocked.Increment(ref _nextLibraryContextHandle);
        lock (_contextGate)
        {
            _libraryContexts.Add(handle);
        }

        return handle;
    }

    private static int CreatePushEventHandle()
    {
        return Interlocked.Increment(ref _nextPushEventHandle);
    }

    private static bool IsValidLibraryContextId(int libraryContextId)
    {
        if (libraryContextId <= 0 || libraryContextId >= 0x8000)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _libraryContexts.Contains(libraryContextId);
        }
    }

    private static void RemoveLibraryContextId(int libraryContextId)
    {
        lock (_contextGate)
        {
            _libraryContexts.Remove(libraryContextId);
            if (_libraryContexts.Count == 0)
            {
                Interlocked.Exchange(ref _initialized, 0);
            }
        }
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
