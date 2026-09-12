param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [Parameter(Mandatory = $true)][string] $OutPrefix,
    [string[]] $ExtraArgs = @('--gui','--no-elevate','--run','sleep'),
    [string] $Button = 'Fix this',
    [int] $SettleMs = 3000
)

# UIA to press the button (the only reliable way to reach a WPF Click handler from
# outside), then plain Win32 EnumWindows to find and capture every visible window
# the process owns -- UIA does not list owned dialogs at desktop level.

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class W3 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
"@
[void][W3]::SetProcessDPIAware()

$p = Start-Process -FilePath $Exe -ArgumentList $ExtraArgs -PassThru
$null = $p.Handle

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$btn = $null
foreach ($i in 1..25) {
    Start-Sleep -Milliseconds 800
    $p.Refresh(); if ($p.HasExited) { "EXITED before button, code $($p.ExitCode)"; exit 1 }
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($win) {
        $nc = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Button)
        $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc)
        if ($btn) { break }
    }
}
if (-not $btn) { "NO BUTTON '$Button'"; try { $p.Kill() } catch {}; exit 2 }

"invoking '$Button'"
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds $SettleMs

$p.Refresh(); "alive=$(-not $p.HasExited)"
$n = 0
foreach ($h in [W3]::WindowsOf([uint32]$p.Id)) {
    $t = [W3]::Title($h)
    $r = New-Object W3+RECT; [void][W3]::GetWindowRect($h, [ref]$r)
    $wd = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    "  window '$t' ${wd}x${ht}"
    if ($wd -le 0 -or $ht -le 0) { continue }
    $bmp = New-Object System.Drawing.Bitmap $wd, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc(); [void][W3]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
    $safe = ($t -replace '[^A-Za-z0-9]', '_'); if (-not $safe) { $safe = 'untitled' }
    $file = "$OutPrefix-$n-$safe.png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    "    saved $file"; $n++
}
try { $p.Kill() } catch {}
