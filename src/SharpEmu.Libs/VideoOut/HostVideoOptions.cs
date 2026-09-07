// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Metal;

public enum HostWindowMode
{
    Windowed,
    Borderless,
    ExclusiveFullscreen,
}

public enum HostScalingMode
{
    Fit,
    Cover,
    Stretch,
    Integer,
}

public enum HostHdrMode
{
    Auto,
    On,
    Off,
}

public sealed record HostVideoOptions
{
    public static HostVideoOptions Default { get; } = new();

    public HostWindowMode WindowMode { get; init; } = HostWindowMode.Windowed;

    public HostScalingMode ScalingMode { get; init; } = HostScalingMode.Fit;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int DisplayIndex { get; init; }

    public int RefreshRate { get; init; }

    public bool VSync { get; init; } = true;

    public HostHdrMode HdrMode { get; init; } = HostHdrMode.Auto;

    public HostVideoOptions Normalize() => this with
    {
        Width = Math.Clamp(Width, 640, 16384),
        Height = Math.Clamp(Height, 360, 16384),
        DisplayIndex = Math.Max(0, DisplayIndex),
        RefreshRate = Math.Clamp(RefreshRate, 0, 1000),
        HdrMode = Enum.IsDefined(HdrMode) ? HdrMode : HostHdrMode.Auto,
    };
}

public static class HostVideoHost
{
    private static HostVideoOptions _currentOptions = HostVideoOptions.Default;

    public static HostVideoOptions CurrentOptions => Volatile.Read(ref _currentOptions);

    public static bool TryConfigureVideo(HostVideoOptions options)
    {
        var normalized = options.Normalize();
        Volatile.Write(ref _currentOptions, normalized);
        return VulkanVideoPresenter.TryConfigureVideo(normalized) &
               MetalVideoPresenter.TryConfigureVideo(normalized);
    }

    /// <summary>
    /// Opens the host window immediately (splash/black) instead of waiting for
    /// the guest's first flip. KytyPS5 launches titles this way: the window is
    /// visible while the game still boots, so startup no longer looks like a
    /// multi-minute hang. Each backend no-ops when unsupported or already
    /// running; whichever backend the title's VideoOut later drives simply
    /// attaches to the window that is already up.
    /// </summary>
    public static void EnsureWindowStarted(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        VulkanVideoPresenter.EnsureStarted((uint)width, (uint)height);
        if (OperatingSystem.IsMacOS())
        {
            MetalVideoPresenter.EnsureStarted((uint)width, (uint)height);
        }
    }
}
