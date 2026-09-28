// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

/// <summary>
/// Offline libSceHttp2, ported from KytyPS5 <c>libNet.cpp</c> (namespace
/// LibHttp2). Contexts, templates and requests are tracked so ids validate;
/// every send "times out" (HTTP2_ERROR_TIMEOUT) the way an offline console
/// answers, so UE4's online subsystem gives up cleanly instead of spinning on
/// unresolved imports (Smurfs: sceHttp2CreateTemplate / SetMinSslVersion).
/// </summary>
public static class Http2Exports
{
    // Kyty values (libNet.cpp LibHttp2).
    private const int Http2ErrorInvalidId = unchecked((int)0x817B1100);
    private const int Http2ErrorBeforeSend = unchecked((int)0x817B1065);
    private const int Http2ErrorTimeout = unchecked((int)0x817B1068);
    private const int Http2ErrorNullPointer = unchecked((int)0x817B1225);
    private const int Http2ErrorInvalidArgument = unchecked((int)0x80436016);

    private static readonly ConcurrentDictionary<int, Http2Context> _contexts = new();
    private static readonly ConcurrentDictionary<int, int> _templates = new();       // tmpl -> ctx
    private static readonly ConcurrentDictionary<int, Http2Request> _requests = new();
    private static int _nextContextId;
    private static int _nextTemplateId;
    private static int _nextRequestId;

    private sealed record Http2Context(int NetId, int SslId, ulong PoolSize, int MaxRequests);

    private sealed class Http2Request(int templateId)
    {
        public int TemplateId { get; } = templateId;
        public int SendResult { get; set; } = Http2ErrorBeforeSend;
        public int AsyncResult { get; set; } = Http2ErrorBeforeSend;
        public int AsyncEvent { get; set; }
    }

    [SysAbiExport(
        Nid = "3JCe3lCbQ8A",
        ExportName = "sceHttp2Init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2Init(CpuContext ctx)
    {
        var netId = unchecked((int)ctx[CpuRegister.Rdi]);
        var sslId = unchecked((int)ctx[CpuRegister.Rsi]);
        var poolSize = ctx[CpuRegister.Rdx];
        var maxRequests = unchecked((int)ctx[CpuRegister.Rcx]);

        if (poolSize == 0 || maxRequests <= 0)
        {
            return ctx.SetReturn(Http2ErrorInvalidArgument);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        _contexts[id] = new Http2Context(netId, sslId, poolSize, maxRequests);

        TraceHttp2("init", id, unchecked((ulong)netId), unchecked((ulong)sslId), poolSize, unchecked((ulong)maxRequests));
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "YiBUtz-pGkc",
        ExportName = "sceHttp2Term",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2Term(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.TryRemove(id, out _))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        foreach (var (templateId, contextId) in _templates)
        {
            if (contextId == id)
            {
                RemoveTemplate(templateId);
            }
        }

        TraceHttp2("term", id, 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "+wCt7fCijgk",
        ExportName = "sceHttp2CreateTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateTemplate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        var templateId = Interlocked.Increment(ref _nextTemplateId);
        _templates[templateId] = contextId;
        TraceHttp2("create-template", templateId, unchecked((ulong)contextId), 0, 0, 0);
        return ctx.SetReturn(templateId);
    }

    [SysAbiExport(
        Nid = "pDom5-078DA",
        ExportName = "sceHttp2DeleteTemplate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2DeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(RemoveTemplate(templateId) ? 0 : Http2ErrorInvalidId);
    }

    [SysAbiExport(
        Nid = "mmyOCxQMVYQ",
        ExportName = "sceHttp2CreateRequestWithURL",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2CreateRequestWithUrl(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        if (!_templates.ContainsKey(templateId))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        _requests[requestId] = new Http2Request(templateId);
        TraceHttp2("create-request", requestId, unchecked((ulong)templateId), 0, 0, 0);
        return ctx.SetReturn(requestId);
    }

    [SysAbiExport(
        Nid = "c8D9qIjo8EY",
        ExportName = "sceHttp2DeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2DeleteRequest(CpuContext ctx) =>
        ctx.SetReturn(_requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _) ? 0 : Http2ErrorInvalidId);

    [SysAbiExport(
        Nid = "nrPfOE8TQu0",
        ExportName = "sceHttp2AddRequestHeader",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2AddRequestHeader(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        return ctx.SetReturn(_requests.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : Http2ErrorInvalidId);
    }

    [SysAbiExport(
        Nid = "FSAFOzi0FpM",
        ExportName = "sceHttp2SetRequestContentLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SetRequestContentLength(CpuContext ctx) =>
        ctx.SetReturn(_requests.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : Http2ErrorInvalidId);

    // Option setters: valid on a template or a request id (Kyty GetHttp2Options).
    [SysAbiExport(Nid = "jjFahkBPCYs", ExportName = "sceHttp2SetAuthEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetAuthEnabled(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "b9AvoIaOuHI", ExportName = "sceHttp2SetAutoRedirect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetAutoRedirect(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "uRosf8GQbHQ", ExportName = "sceHttp2SetInflateGZIPEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetInflateGzipEnabled(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "B37SruheQ5Y", ExportName = "sceHttp2SslDisableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SslDisableOption(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "EWcwMpbr5F8", ExportName = "sceHttp2SslEnableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SslEnableOption(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "BJgi0CH7al4", ExportName = "sceHttp2SetRedirectCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetRedirectCallback(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "izvHhqgDt44", ExportName = "sceHttp2SetRecvTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetRecvTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "XPtW45xiLHk", ExportName = "sceHttp2SetSendTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetSendTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "-HIO4VT87v8", ExportName = "sceHttp2SetConnectTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetConnectTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "n8hMLe31OPA", ExportName = "sceHttp2SetConnectionWaitTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetConnectionWaitTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "ACjtE27aErY", ExportName = "sceHttp2SetResolveTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetResolveTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "VYMxTcBqSE0", ExportName = "sceHttp2SetTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetTimeOut(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(Nid = "YrWX+DhPHQY", ExportName = "sceHttp2SetSslCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetSslCallback(CpuContext ctx) => SetOption(ctx);

    // Seen unresolved in Smurfs; not in Kyty. Accepted as a template/request
    // option like the other SSL knobs.
    [SysAbiExport(Nid = "09tk+kIA1Ns", ExportName = "sceHttp2SetMinSslVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetMinSslVersion(CpuContext ctx) => SetOption(ctx);

    [SysAbiExport(
        Nid = "rbqZig38AT8",
        ExportName = "sceHttp2SendRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SendRequest(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 && ctx[CpuRegister.Rdx] != 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        if (!_requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        request.SendResult = Http2ErrorTimeout; // offline
        return ctx.SetReturn(request.SendResult);
    }

    [SysAbiExport(
        Nid = "A+NVAFu4eCg",
        ExportName = "sceHttp2SendRequestAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2SendRequestAsync(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 && ctx[CpuRegister.Rdx] != 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        if (!_requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        request.SendResult = Http2ErrorTimeout;
        request.AsyncResult = request.SendResult;
        request.AsyncEvent = 0;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "MOp-AUhdfi8",
        ExportName = "sceHttp2WaitAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2WaitAsync(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        if (!_requests.TryGetValue(requestId, out var request))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        // struct { int event_type; int req_id; int result; u8 pad[4]; void* reserved; }
        _ = ctx.Memory.TryWrite(resultAddress, new byte[24]);
        _ = ctx.TryWriteUInt32(resultAddress, unchecked((uint)request.AsyncEvent));
        _ = ctx.TryWriteUInt32(resultAddress + 4, unchecked((uint)requestId));
        _ = ctx.TryWriteUInt32(resultAddress + 8, unchecked((uint)request.AsyncResult));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "9XYJwCf3lEA",
        ExportName = "sceHttp2GetStatusCode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2GetStatusCode(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        _ = ctx.TryWriteUInt32(statusAddress, 0);
        return ctx.SetReturn(SendResultOf(ctx));
    }

    [SysAbiExport(
        Nid = "o0DBQpFE13o",
        ExportName = "sceHttp2GetResponseContentLength",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2GetResponseContentLength(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rsi];
        var lengthAddress = ctx[CpuRegister.Rdx];
        if (resultAddress == 0 || lengthAddress == 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        _ = ctx.TryWriteUInt64(lengthAddress, 0);
        var send = SendResultOf(ctx);
        _ = ctx.TryWriteUInt32(resultAddress, send != 0 ? uint.MaxValue : 0);
        return ctx.SetReturn(send);
    }

    [SysAbiExport(
        Nid = "-rdXUi2XW90",
        ExportName = "sceHttp2GetAllResponseHeaders",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2GetAllResponseHeaders(CpuContext ctx)
    {
        var headerAddress = ctx[CpuRegister.Rsi];
        var sizeAddress = ctx[CpuRegister.Rdx];
        if (headerAddress == 0 || sizeAddress == 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        _ = ctx.TryWriteUInt64(headerAddress, 0);
        _ = ctx.TryWriteUInt64(sizeAddress, 0);
        return ctx.SetReturn(SendResultOf(ctx));
    }

    [SysAbiExport(
        Nid = "QygCNNmbGss",
        ExportName = "sceHttp2ReadData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2ReadData(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 && ctx[CpuRegister.Rdx] != 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        return ctx.SetReturn(SendResultOf(ctx)); // offline: error, or 0 bytes
    }

    [SysAbiExport(
        Nid = "bGN-6zbo7ms",
        ExportName = "sceHttp2ReadDataAsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp2")]
    public static int Http2ReadDataAsync(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0 && ctx[CpuRegister.Rdx] != 0)
        {
            return ctx.SetReturn(Http2ErrorNullPointer);
        }

        if (!_requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(Http2ErrorInvalidId);
        }

        request.AsyncResult = request.SendResult != 0 ? request.SendResult : 0;
        request.AsyncEvent = 1;
        return ctx.SetReturn(0);
    }

    private static int SendResultOf(CpuContext ctx) =>
        _requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request)
            ? request.SendResult
            : Http2ErrorInvalidId;

    private static int SetOption(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(_requests.ContainsKey(id) || _templates.ContainsKey(id) ? 0 : Http2ErrorInvalidId);
    }

    private static bool RemoveTemplate(int templateId)
    {
        if (!_templates.TryRemove(templateId, out _))
        {
            return false;
        }

        foreach (var (requestId, request) in _requests)
        {
            if (request.TemplateId == templateId)
            {
                _requests.TryRemove(requestId, out _);
            }
        }

        return true;
    }

    private static void TraceHttp2(string operation, int id, ulong arg0, ulong arg1, ulong arg2, ulong arg3)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_HTTP2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] http2.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16} arg3=0x{arg3:X16}");
    }
}
