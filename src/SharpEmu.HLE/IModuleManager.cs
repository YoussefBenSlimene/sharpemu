// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public interface IModuleManager
{
    /// <summary>Registers pre-built exports (the compile-time generated registry).</summary>
    int RegisterExports(IReadOnlyList<ExportedFunction> exports);

    void Freeze();

    bool TryGetFunction(string nid, out Delegate function);

    bool TryGetExport(string nid, out ExportedFunction export);

    bool TryGetExportByName(string exportName, out ExportedFunction export);

    /// <summary>
    /// Blocks until the HLE JIT warm sweep that <see cref="Freeze"/> kicked off
    /// has completed. Guest execution must not start before it finishes (a
    /// first JIT or .cctor on a guest thread's hijacked stack fail-fasts the
    /// CLR), but window creation and ELF loading must not wait for it.
    /// </summary>
    void WaitForWarmup();

    bool TryDispatch(string nid, CpuContext context, out OrbisGen2Result result);

    OrbisGen2Result Dispatch(string nid, CpuContext context);
}
