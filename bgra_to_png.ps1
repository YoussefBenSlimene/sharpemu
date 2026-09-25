# Convert the raw frame dumps produced by the VideoOut diagnostics into PNGs
# and print a content fingerprint for each, so "the screen is black" can be
# proven from pixels instead of inferred from logs.
#
# Handles both dump shapes (width/height are the 3rd dash-separated field of
# both names):
#   present-0001-1920x1080-B8G8R8A8Unorm.bgra   (run_ms_framedump.ps1)
#   0007-0x0000008FC2000000-3840x2160-R8G8B8A8Unorm.rgba (run_ms_guestimg.ps1)
#
# RGBA dumps are byte-swapped to BGRA before saving so the PNG colours are
# right; the *-0x*- named files are guest render targets, whose format field
# tells which order the emulator wrote.
param(
    [string]$DumpDir = (Join-Path $PSScriptRoot "ms_frames"),
    [string]$OutDir = $null
)

$ErrorActionPreference = "Stop"
if (-not $OutDir) { $OutDir = Join-Path $DumpDir "png" }

if (-not (Test-Path $DumpDir)) { Write-Host "ERROR: no dump dir $DumpDir"; exit 1 }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# Per-byte PowerShell loops take minutes on 8 MB frames; do the work in C#.
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class FrameDump
{
    public static string Analyze(string path, string outPath, int width, int height, bool swapRb)
    {
        var bytes = File.ReadAllBytes(path);
        var expected = width * height * 4;
        if (bytes.Length < expected)
            return string.Format("skip (short read {0}/{1}): {2}", bytes.Length, expected, Path.GetFileName(path));

        long nonBlack = 0, nonzeroBytes = 0;
        for (var i = 0; i < expected; i += 4)
        {
            var b = bytes[i]; var g = bytes[i + 1]; var r = bytes[i + 2]; var a = bytes[i + 3];
            if (b != 0 || g != 0 || r != 0) nonBlack++;
            if (b != 0) nonzeroBytes++;
            if (g != 0) nonzeroBytes++;
            if (r != 0) nonzeroBytes++;
            if (a != 0) nonzeroBytes++;
        }

        long unique = 0;
        var seen = new System.Collections.Generic.HashSet<uint>();
        for (var i = 0; i + 4 <= expected; i += 4 * 251)
        {
            var pixel = (uint)(bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24));
            if (seen.Add(pixel)) unique++;
        }

        if (swapRb)
        {
            for (var i = 0; i < expected; i += 4) { var t = bytes[i]; bytes[i] = bytes[i + 2]; bytes[i + 2] = t; }
        }

        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        {
            var rect = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(bytes, 0, data.Scan0, expected); }
            finally { bitmap.UnlockBits(data); }
            bitmap.Save(outPath, ImageFormat.Png);
        }

        var total = (double)width * height;
        return string.Format(
            "{0,-52} {1}x{2} nonblack={3}/{4} ({5:P2}) nonzero_bytes={6} sample_unique={7} -> {8}",
            Path.GetFileName(path), width, height, nonBlack, (long)total,
            nonBlack / total, nonzeroBytes, unique, Path.GetFileName(outPath));
    }
}
'@

$files = @(Get-ChildItem $DumpDir -File | Where-Object { $_.Extension -in '.bgra', '.rgba' } | Sort-Object Name)
if ($files.Count -eq 0) { Write-Host "ERROR: no .bgra/.rgba files in $DumpDir"; exit 1 }

foreach ($file in $files) {
    $parts = $file.BaseName.Split('-')
    if ($parts.Count -lt 3) { Write-Host "skip (unparsed): $($file.Name)"; continue }

    $dims = $parts[2].Split('x')
    $width = [int]$dims[0]
    $height = [int]$dims[1]
    $formatName = $parts[$parts.Count - 1]
    # R8G8B8A8 dumps need their red/blue channels swapped for GDI+.
    $swapRb = $formatName.StartsWith('R8G8B8A8', [StringComparison]::OrdinalIgnoreCase)

    $pngPath = Join-Path $OutDir ($file.BaseName + ".png")
    Write-Host ([FrameDump]::Analyze($file.FullName, $pngPath, $width, $height, $swapRb))
}

Write-Host "PNGs in $OutDir"

