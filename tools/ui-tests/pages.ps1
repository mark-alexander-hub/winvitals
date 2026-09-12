param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [Parameter(Mandatory = $true)][string] $OutPrefix
)

# Selects each navigation entry in turn and captures the window after it settles.
# Pages that measure things (Home stats, Clean up's disk scan) get a longer wait.

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W4 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[void][W4]::SetProcessDPIAware()

$p = Start-Process -FilePath $Exe -ArgumentList '--gui','--no-elevate' -PassThru
$null = $p.Handle
Start-Sleep -Milliseconds 4500
$p.Refresh(); if ($p.HasExited) { "EXITED at start, code $($p.ExitCode)"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "NO WINDOW"; try { $p.Kill() } catch {}; exit 2 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle

function Capture($file) {
    $r = New-Object W4+RECT; [void][W4]::GetWindowRect($hwnd, [ref]$r)
    $wd = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $wd, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc(); [void][W4]::PrintWindow($hwnd, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

$pages = @(
    @{ Name = 'Home';         Wait = 3000 },
    @{ Name = 'Diagnose';     Wait = 1200 },
    @{ Name = 'Clean up';     Wait = 7000 },
    @{ Name = 'Speed up';     Wait = 1500 },
    @{ Name = 'Repair';       Wait = 1200 },
    @{ Name = 'Undo changes'; Wait = 1200 }
)

$n = 0
foreach ($page in $pages) {
    $nc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $page.Name)
    $tc = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $both = New-Object System.Windows.Automation.AndCondition($nc, $tc)
    $item = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $both)
    if (-not $item) { "  nav '$($page.Name)' NOT FOUND"; continue }

    try {
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } catch { "  select failed for '$($page.Name)': $($_.Exception.Message)"; continue }

    Start-Sleep -Milliseconds $page.Wait
    $p.Refresh(); if ($p.HasExited) { "EXITED on '$($page.Name)', code $($p.ExitCode)"; exit 3 }

    $safe = ($page.Name -replace '[^A-Za-z0-9]', '')
    $file = "$OutPrefix-$n-$safe.png"
    Capture $file
    "  captured '$($page.Name)' -> $file"
    $n++
}

try { $p.Kill() } catch {}
"done"
