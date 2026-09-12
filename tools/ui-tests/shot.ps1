param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [Parameter(Mandatory = $true)][string] $Out,
    [string] $ExtraArgs = '--gui --no-elevate',
    [int] $WaitMs = 4000
)

# Captures the target window with PrintWindow, which asks the window to render itself
# into our bitmap. CopyFromScreen would photograph the screen region instead, picking
# up whatever happens to be on top of it.

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

[void][Win]::SetProcessDPIAware()

$p = Start-Process -FilePath $Exe -ArgumentList $ExtraArgs -PassThru
$null = $p.Handle
Start-Sleep -Milliseconds $WaitMs
$p.Refresh()

if ($p.HasExited) { "PROCESS EXITED EARLY with code $($p.ExitCode)"; exit 1 }

$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { "NO MAIN WINDOW"; try { $p.Kill() } catch {}; exit 2 }

$r = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref] $r)
$w = $r.Right - $r.Left
$hgt = $r.Bottom - $r.Top
"window $w x $hgt"

$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# flag 2 = PW_RENDERFULLCONTENT, required for hardware-composited WPF content
$okShot = [Win]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"PrintWindow returned $okShot, saved $Out"

try { $p.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 600; if (-not $p.HasExited) { $p.Kill() } } catch {}
