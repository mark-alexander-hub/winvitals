# UI test drivers

Scripts that drive the WinVitals interface from outside, used to verify every page
renders and the confirm dialog opens. They target a **Debug** build (asInvoker) so no
UAC prompt is needed.

    $exe = 'src\WinVitals.App\bin\Debug\net8.0-windows\win-x64\WinVitals.exe'
    .\tools\ui-tests\shot.ps1    -Exe $exe -Out home.png                 # capture the window
    .\tools\ui-tests\pages.ps1   -Exe $exe -OutPrefix page               # select every nav entry, capture each
    .\tools\ui-tests\dialog3.ps1 -Exe $exe -OutPrefix dlg                # press "Fix this", capture every window
    .\tools\ui-tests\click2.ps1  -Exe $exe -OutPrefix posted             # posted mouse click (does NOT reach WPF; kept as a record)

Things learned the hard way, so they are not re-learned:

- Press WPF buttons with UI Automation `InvokePattern`. Posted `WM_LBUTTONDOWN`
  messages never reach WPF's hit-testing.
- UI Automation does not list an owned modal dialog at desktop level. Enumerate the
  process's windows with Win32 `EnumWindows` (dialog3.ps1) to find it.
- Capture with `PrintWindow` (flag 2). `CopyFromScreen` photographs whatever is on top
  of the screen, which once was the user's browser.
- The app writes a flow log to `%LOCALAPPDATA%\WinVitals\logs`; read it after a run.
