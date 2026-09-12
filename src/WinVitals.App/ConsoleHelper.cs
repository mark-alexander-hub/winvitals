using System.IO;
using System.Runtime.InteropServices;

namespace WinVitals.App;

/// <summary>
/// Lets a windowed application write to the console that launched it.
///
/// WinVitals ships as one executable that is a GUI when double-clicked and a CLI when
/// given arguments. A WinExe has no console of its own, so it borrows its parent's.
/// </summary>
internal static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    public static void Attach()
    {
        // Attach to the calling shell if there is one; otherwise make our own window so
        // output is not thrown away when launched from Explorer with arguments.
        if (!AttachConsole(AttachParentProcess)) AllocConsole();

        // The streams were bound to nothing at startup and have to be rebuilt now that
        // a real console handle exists.
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }

    public static void Detach()
    {
        try
        {
            Console.Out.Flush();
            FreeConsole();
        }
        catch
        {
            // Nothing useful to do if the console has already gone.
        }
    }
}
