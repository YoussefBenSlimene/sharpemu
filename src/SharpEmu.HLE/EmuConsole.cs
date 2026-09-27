// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.HLE;

/// <summary>
/// KytyPS5-style user console.
///
/// KytyPS5 prints a short, readable log to the terminal:
/// <c>Initialized: Config</c> … <c>Initialized: Graphics</c>, <c>Title ID: …</c>,
/// <c>version = N</c> (sceAgcInit), <c>AJM codec=…</c>, one
/// <c>Shaders: VS … | PS … | CS …</c> line per newly compiled shader, and
/// <c>Unresolved import stub called: …</c>. Everything else goes to its log file.
///
/// SharpEmu historically wrote thousands of <c>[LOADER]…</c> developer lines to
/// stderr. In <b>Kyty console mode</b> (the default for normal game launches)
/// <see cref="Install"/> wraps stdout/stderr so that:
/// <list type="bullet">
/// <item>lines written through <see cref="Line"/> always reach the terminal;</item>
/// <item>every other line is hidden from the terminal unless it is an error the
///   user should see. The --log-file mirror wraps this filter, so the log file
///   still receives the complete developer stream.</item>
/// </list>
/// Set <c>SHARPEMU_CONSOLE=verbose</c>, pass <c>--verbose-console</c> or
/// <c>--log-level=debug|trace</c> to get the old full developer stream. Any
/// SHARPEMU_LOG_* / SHARPEMU_TRACE_* switch also selects it, so the benchmark
/// and diagnostic scripts keep parsing the same lines.
/// </summary>
public static class EmuConsole
{
    private static readonly object Gate = new();
    private static bool _installed;
    private static bool _verbose = IsVerboseRequested();

    // Shader counters, Kyty order: VS PS CS GS LS HS TES.
    private static long _vs;
    private static long _ps;
    private static long _cs;
    private static long _gs;
    private static long _ls;
    private static long _hs;
    private static long _tes;

    /// <summary>True when the full developer log goes to the terminal.</summary>
    public static bool Verbose => _verbose;

    private static bool IsVerboseRequested()
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_CONSOLE");
        return string.Equals(mode, "verbose", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "1", StringComparison.Ordinal);
    }

    public static void ForceVerbose()
    {
        _verbose = true;
    }

    /// <summary>
    /// Wraps Console.Out / Console.Error so the terminal only receives
    /// Kyty-style lines and errors. Install this BEFORE any log-file mirror
    /// (the mirror then wraps the filter and still records every line).
    /// No-op in verbose mode or when already installed.
    /// </summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed || _verbose)
            {
                return;
            }

            _installed = true;
            Console.SetOut(new FilteringWriter(Console.Out));
            Console.SetError(new FilteringWriter(Console.Error));
        }
    }

    /// <summary>
    /// Decides verbose mode from the command line / environment before
    /// <see cref="Install"/>: developer log levels, an explicit flag, or any
    /// SHARPEMU_LOG_* / SHARPEMU_TRACE_* diagnostic switch keep the full stream
    /// (diagnostic scripts parse it).
    /// </summary>
    public static void ConfigureFromArguments(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--verbose-console", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--log-level=trace", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--log-level=debug", StringComparison.OrdinalIgnoreCase))
            {
                _verbose = true;
                return;
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_CONSOLE"), "kyty", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Output redirected to a FILE (bench_run.ps1, run_*.ps1 `> log 2>&1`,
        // CI) = a log is being captured for parsing: keep the full stream.
        // Interactive terminals and the GUI's pipes get the Kyty console.
        if (IsStdErrRedirectedToFile())
        {
            _verbose = true;
            return;
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key &&
                (key.StartsWith("SHARPEMU_LOG_", StringComparison.OrdinalIgnoreCase) ||
                 key.StartsWith("SHARPEMU_TRACE_", StringComparison.OrdinalIgnoreCase)) &&
                entry.Value is string value && value.Length != 0 && value != "0")
            {
                _verbose = true;
                return;
            }
        }
    }

    private static bool IsStdErrRedirectedToFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // /proc/self/fd/2 -> a regular file when redirected with 2>file.
                var target = new FileInfo("/proc/self/fd/2").LinkTarget;
                return target is not null &&
                       target.StartsWith('/') &&
                       !target.StartsWith("/dev/", StringComparison.Ordinal) &&
                       File.Exists(target);
            }
            catch
            {
                return false;
            }
        }

        try
        {
            var handle = GetStdHandle(-12); // STD_ERROR_HANDLE
            return handle != 0 && handle != -1 && GetFileType(handle) == FileTypeDisk;
        }
        catch
        {
            return false;
        }
    }

    private const uint FileTypeDisk = 0x0001;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int stdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetFileType(nint handle);

    [ThreadStatic]
    private static bool _forceNextLine;

    /// <summary>Writes a Kyty-style line that always reaches the terminal (and the log file).</summary>
    public static void Line(string text)
    {
        _forceNextLine = true;
        try
        {
            Console.Error.WriteLine(text);
        }
        finally
        {
            _forceNextLine = false;
        }
    }

    public static void Initialized(string subsystem) => Line($"Initialized: {subsystem}");

    public enum ShaderStage
    {
        Vertex,
        Pixel,
        Compute,
        Geometry,
        Local,
        Hull,
        TessEval,
    }

    /// <summary>Counts a newly compiled shader and prints Kyty's totals line.</summary>
    public static void ShaderCompiled(ShaderStage stage)
    {
        switch (stage)
        {
            case ShaderStage.Vertex: Interlocked.Increment(ref _vs); break;
            case ShaderStage.Pixel: Interlocked.Increment(ref _ps); break;
            case ShaderStage.Compute: Interlocked.Increment(ref _cs); break;
            case ShaderStage.Geometry: Interlocked.Increment(ref _gs); break;
            case ShaderStage.Local: Interlocked.Increment(ref _ls); break;
            case ShaderStage.Hull: Interlocked.Increment(ref _hs); break;
            case ShaderStage.TessEval: Interlocked.Increment(ref _tes); break;
        }

        Line(
            $"Shaders: VS {Volatile.Read(ref _vs)} | PS {Volatile.Read(ref _ps)} | " +
            $"CS {Volatile.Read(ref _cs)} | GS {Volatile.Read(ref _gs)} | " +
            $"LS {Volatile.Read(ref _ls)} | HS {Volatile.Read(ref _hs)} | " +
            $"TES {Volatile.Read(ref _tes)}");
    }

    public static (long Vs, long Ps, long Cs) ShaderTotals() =>
        (Volatile.Read(ref _vs), Volatile.Read(ref _ps), Volatile.Read(ref _cs));

    /// <summary>
    /// Terminal filter: hides developer chatter, keeps warnings/errors and
    /// anything the user needs to act on. Pure function for tests.
    /// </summary>
    public static bool ShouldShowOnTerminal(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        // Guest printf output and fatal messages are user-relevant (Kyty shows
        // guest stdout too).
        if (line.StartsWith("[DEBUG][PRINF]", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Contains("[ERROR]", StringComparison.Ordinal) ||
            line.Contains("[FATAL]", StringComparison.Ordinal) ||
            line.Contains("[CRIT", StringComparison.Ordinal) ||
            line.Contains("Unhandled exception", StringComparison.Ordinal) ||
            line.Contains("abort() called by guest", StringComparison.Ordinal) ||
            line.Contains("device lost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private sealed class FilteringWriter : TextWriter
    {
        private readonly TextWriter _terminal;
        private readonly StringBuilder _pending = new();
        private bool _pendingForced;

        public FilteringWriter(TextWriter terminal)
        {
            _terminal = terminal;
        }

        public override Encoding Encoding => _terminal.Encoding;

        public override void Write(char value)
        {
            lock (_pending)
            {
                _pendingForced |= _forceNextLine;
                if (value == '\n')
                {
                    FlushLineLocked();
                }
                else if (value != '\r')
                {
                    _pending.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            lock (_pending)
            {
                _pendingForced |= _forceNextLine;
                var start = 0;
                while (true)
                {
                    var newline = value.IndexOf('\n', start);
                    if (newline < 0)
                    {
                        _pending.Append(value, start, value.Length - start);
                        return;
                    }

                    var end = newline > start && value[newline - 1] == '\r' ? newline - 1 : newline;
                    _pending.Append(value, start, end - start);
                    FlushLineLocked();
                    start = newline + 1;
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_pending)
            {
                _pendingForced |= _forceNextLine;
                Write(value);
                FlushLineLocked();
            }
        }

        public override void WriteLine()
        {
            lock (_pending)
            {
                _pendingForced |= _forceNextLine;
                FlushLineLocked();
            }
        }

        public override void Flush()
        {
            lock (_pending)
            {
                _terminal.Flush();
            }
        }

        private void FlushLineLocked()
        {
            var line = _pending.ToString();
            var forced = _pendingForced;
            _pending.Clear();
            _pendingForced = false;
            if (forced || ShouldShowOnTerminal(line))
            {
                _terminal.WriteLine(line);
                _terminal.Flush();
            }
        }
    }
}
