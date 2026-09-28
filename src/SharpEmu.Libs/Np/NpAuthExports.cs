// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

// Offline libSceNpAuth — KytyPS5 libNet.cpp LibNpAuth parity (issue: Quake
// II aborts on unresolved sceNpAuthCreateRequest before NP swizzle; the
// previous Q2 fix covered libSceNpManager only). Requests complete as
// SIGNED_OUT and the game falls back to offline mode, as on Kyty.
public static class NpAuthExports
{
    private const int RequestIdOffset = 0x10000000;

    private const int NpAuthErrorInvalidArgument = unchecked((int)0x80550301);
    private const int NpAuthErrorAborted = unchecked((int)0x80550304);
    private const int NpAuthErrorRequestMax = unchecked((int)0x80550305);
    private const int NpAuthErrorRequestNotFound = unchecked((int)0x80550306);
    private const int NpAuthErrorInvalidId = unchecked((int)0x80550307);
    private const int NpErrorSignedOut = unchecked((int)0x80550006); // shared NP family

    private enum NpAuthRequestState
    {
        None = 0,
        Ready,
        Aborted,
        Complete,
    }

    private sealed class NpAuthRequest
    {
        public bool Async;
        public NpAuthRequestState State = NpAuthRequestState.None;
        public int Result;
    }

    private static readonly object _authGate = new();
    private static readonly List<NpAuthRequest?> _authRequests = new();
    private static int _authActiveRequests;

    private static NpAuthRequest? FindRequest(int reqId)
    {
        var index = reqId - RequestIdOffset - 1;
        return index < 0 ||
            index >= _authRequests.Count ||
            _authRequests[index] is not { State: not NpAuthRequestState.None } request
                ? null
                : request;
    }

    private static int CreateRequest(bool async, string caller)
    {
        lock (_authGate)
        {
            if (_authActiveRequests >= 16)
            {
                return NpAuthErrorRequestMax;
            }

            for (var i = 0; i < _authRequests.Count; i++)
            {
                if (_authRequests[i] is null || _authRequests[i]!.State == NpAuthRequestState.None)
                {
                    _authRequests[i] = new NpAuthRequest { Async = async, State = NpAuthRequestState.Ready };
                    _authActiveRequests++;
                    var id = i + RequestIdOffset + 1;
                    TraceNpAuth($"{caller} -> req={id}");
                    return id;
                }
            }

            _authRequests.Add(new NpAuthRequest { Async = async, State = NpAuthRequestState.Ready });
            _authActiveRequests++;
            var newId = _authRequests.Count - 1 + RequestIdOffset + 1;
            TraceNpAuth($"{caller} -> req={newId}");
            return newId;
        }
    }

    // Completes the request as SIGNED_OUT (offline terminal state, same answer
    // Kyty gives); sync callers get the error as the call's return value.
    private static int CompleteSignedOut(NpAuthRequest request)
    {
        if (request.State == NpAuthRequestState.Complete)
        {
            request.Result = NpAuthErrorInvalidArgument;
            return NpAuthErrorInvalidArgument;
        }
        if (request.State == NpAuthRequestState.Aborted)
        {
            request.Result = NpAuthErrorAborted;
            return NpAuthErrorAborted;
        }

        request.State = NpAuthRequestState.Complete;
        request.Result = NpErrorSignedOut;
        return request.Async ? 0 : NpErrorSignedOut;
    }

    [SysAbiExport(
        Nid = "6bwFkosYRQg",
        ExportName = "sceNpAuthCreateRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateRequest(CpuContext ctx) =>
        ctx.SetReturn(CreateRequest(async: false, "auth-create"));

    [SysAbiExport(
        Nid = "N+mr7GjTvr8",
        ExportName = "sceNpAuthCreateAsyncRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthCreateAsyncRequest(CpuContext ctx) =>
        ctx.SetReturn(CreateRequest(async: true, "auth-create-async"));

    [SysAbiExport(
        Nid = "H8wG9Bk-nPc",
        ExportName = "sceNpAuthDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthDeleteRequest(CpuContext ctx)
    {
        var reqId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_authGate)
        {
            if (FindRequest(reqId) is not { } request)
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }

            request.State = NpAuthRequestState.None;
            request.Async = false;
            request.Result = 0;
            _authActiveRequests--;
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(
        Nid = "cE7wIsqXdZ8",
        ExportName = "sceNpAuthAbortRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthAbortRequest(CpuContext ctx)
    {
        var reqId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_authGate)
        {
            if (FindRequest(reqId) is not { } request)
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }

            if (request.State != NpAuthRequestState.Complete)
            {
                request.State = NpAuthRequestState.Aborted;
            }

            return ctx.SetReturn(0);
        }
    }

    private static int WaitOrPollAsync(CpuContext ctx, int reqId, ulong resultAddress)
    {
        if (resultAddress == 0)
        {
            return NpAuthErrorInvalidArgument;
        }

        lock (_authGate)
        {
            if (FindRequest(reqId) is not { } request)
            {
                return NpAuthErrorRequestNotFound;
            }

            // Kyty semantics: async pending reads are INVALID_ID; the caller
            // first must Complete via GetAuthorizationCode/GetIdToken.
            if (!request.Async || request.State == NpAuthRequestState.Ready)
            {
                return NpAuthErrorInvalidId;
            }

            if (!ctx.TryWriteInt32(resultAddress, request.Result))
            {
                return unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return 0;
        }
    }

    [SysAbiExport(
        Nid = "SK-S7daqJSE",
        ExportName = "sceNpAuthWaitAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthWaitAsync(CpuContext ctx) =>
        ctx.SetReturn(WaitOrPollAsync(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "gjSyfzSsDcE",
        ExportName = "sceNpAuthPollAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthPollAsync(CpuContext ctx) =>
        ctx.SetReturn(WaitOrPollAsync(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "KI4dHLlTNl0",
        ExportName = "sceNpAuthGetAuthorizationCodeV3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetAuthorizationCodeV3(CpuContext ctx)
    {
        var reqId = unchecked((int)ctx[CpuRegister.Rdi]);
        var authCode = ctx[CpuRegister.Rdx];
        var issuerId = ctx[CpuRegister.Rcx];

        if (authCode != 0)
        {
            WriteZero(ctx, authCode, 136);
        }
        if (issuerId != 0)
        {
            _ = ctx.TryWriteInt32(issuerId, 0);
        }

        lock (_authGate)
        {
            if (FindRequest(reqId) is not { } request)
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }
            if (request.State == NpAuthRequestState.Aborted)
            {
                request.Result = NpAuthErrorAborted;
                return ctx.SetReturn(NpAuthErrorAborted);
            }

            return ctx.SetReturn(CompleteSignedOut(request));
        }
    }

    [SysAbiExport(
        Nid = "RdsFVsgSpZY",
        ExportName = "sceNpAuthGetIdTokenV3",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpAuth")]
    public static int NpAuthGetIdTokenV3(CpuContext ctx)
    {
        var reqId = unchecked((int)ctx[CpuRegister.Rdi]);
        var idToken = ctx[CpuRegister.Rdx];
        if (idToken != 0)
        {
            WriteZero(ctx, idToken, 4104);
        }

        lock (_authGate)
        {
            if (FindRequest(reqId) is not { } request)
            {
                return ctx.SetReturn(NpAuthErrorRequestNotFound);
            }
            if (request.State == NpAuthRequestState.Aborted)
            {
                request.Result = NpAuthErrorAborted;
                return ctx.SetReturn(NpAuthErrorAborted);
            }

            return ctx.SetReturn(CompleteSignedOut(request));
        }
    }

    private static void WriteZero(CpuContext ctx, ulong address, int size)
    {
        var buffer = new byte[size];
        _ = ctx.Memory.TryWrite(address, buffer);
    }

    private static void TraceNpAuth(string message) =>
        SharpEmu.Libs.Diagnostics.GameDebug.RateLimited("npauth", message);
}
