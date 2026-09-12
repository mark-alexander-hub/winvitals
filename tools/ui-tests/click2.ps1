param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [Parameter(Mandatory = $true)][string] $OutPrefix,
    [string[]] $ExtraArgs = @('--gui','--no-elevate','--run','sleep'),
    [string] $Button = 'Fix this',
    [int] $QuietSeconds = 8
)

# Clicks a button by posting real WM_LBUTTONDOWN/UP messages to the window, then
# touches nothing for a while. UI Automation is used only once, to find the button's
# rectangle. Windows are then enumerated with plain Win32 so no automation client is
# interacting with the modal loop while we watch it.

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class W2 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
"@
[void][W2]::SetProcessDPIAware()

$p = Start-Process -FilePath $Exe -ArgumentList $ExtraArgs -PassThru
$null = $p.Handle

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)

$btn = $null; $win = $null
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

# The button may be below the fold. Scroll the results pane to the bottom with one
# ScrollPattern call, then re-read the button's rectangle.
$scrollCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
$scroller = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $scrollCond)
if ($scroller) {
    $sp = $scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $view = $scroller.Current.BoundingRectangle
    $nc2 = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Button)
    $placed = $false
    foreach ($pct in 0,10,20,30,40,50,60,70,80,90,100) {
        $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, $pct)
        Start-Sleep -Milliseconds 250
        $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nc2)
        $r = $btn.Current.BoundingRectangle
        $inside = ($r.Y -ge $view.Y + 4) -and (($r.Y + $r.Height) -le ($view.Y + $view.Height - 4))
        if ($inside) { "button in view at scroll ${pct}%"; $placed = $true; break }
    }
    if (-not $placed) { "COULD NOT BRING BUTTON INTO VIEW"; try { $p.Kill() } catch {}; exit 5 }
} else { "no scrollable pane found" }

$rect = $btn.Current.BoundingRectangle          # screen coords, physical px
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
$wr = New-Object W2+RECT; [void][W2]::GetWindowRect($hwnd, [ref]$wr)
"window rect ($($wr.Left),$($wr.Top))-($($wr.Right),$($wr.Bottom)); button rect ($([int]$rect.X),$([int]$rect.Y)) $([int]$rect.Width)x$([int]$rect.Height)"
$origin = New-Object W2+POINT; [void][W2]::ClientToScreen($hwnd, [ref]$origin)
$cx = [int]($rect.X + $rect.Width / 2 - $origin.X)
$cy = [int]($rect.Y + $rect.Height / 2 - $origin.Y)
"button at client ($cx,$cy); posting click"

$lp = [IntPtr](($cy -shl 16) -bor ($cx -band 0xFFFF))
[void][W2]::PostMessage($hwnd, 0x0201, [IntPtr]1, $lp)   # WM_LBUTTONDOWN, MK_LBUTTON
Start-Sleep -Milliseconds 60
[void][W2]::PostMessage($hwnd, 0x0202, [IntPtr]0, $lp)   # WM_LBUTTONUP

# Now hands off. No automation calls for the quiet period.
Start-Sleep -Seconds $QuietSeconds
$p.Refresh()
"after ${QuietSeconds}s quiet: alive=$(-not $p.HasExited)"

$n = 0
foreach ($h in [W2]::WindowsOf([uint32]$p.Id)) {
    $t = [W2]::Title($h)
    $r = New-Object W2+RECT; [void][W2]::GetWindowRect($h, [ref]$r)
    $wd = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    "  window '$t' ${wd}x${ht}"
    if ($wd -gt 0 -and $ht -gt 0) {
        $bmp = New-Object System.Drawing.Bitmap $wd, $ht
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc(); [void][W2]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
        $file = "$OutPrefix-$n.png"; $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        "    saved $file"; $n++
    }
}
try { $p.Kill() } catch {}
