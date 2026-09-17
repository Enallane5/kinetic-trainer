# Launches the app on a given page with the simulated sensor, captures just the app window to a PNG, closes it.
# Usage: .\tools\capture-page.ps1 -Exe <path> -Page sensor -Out C:\tmp\sensor.png [-WaitSeconds 8]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Page = 'ride',
    [Parameter(Mandatory)] [string] $Out,
    [int] $WaitSeconds = 8,
    [string] $ExtraArgs = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
$proc = Start-Process -FilePath $Exe -ArgumentList "--simulate --page $Page $ExtraArgs" -PassThru
try {
    $deadline = (Get-Date).AddSeconds(30)
    do { Start-Sleep -Milliseconds 500; $proc.Refresh() } while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($proc.MainWindowHandle -eq 0) { throw "window never appeared" }
    Start-Sleep -Seconds $WaitSeconds
    [Win32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 400
    $r = New-Object Win32+RECT
    # DWMWA_EXTENDED_FRAME_BOUNDS = 9 gives the visible frame without the invisible resize border.
    if ([Win32]::DwmGetWindowAttribute($proc.MainWindowHandle, 9, [ref]$r, 16) -ne 0) { [Win32]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null }
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Out ($w x $h)"
} finally {
    if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }
    if (-not $proc.HasExited) { $proc.Kill() }
}
