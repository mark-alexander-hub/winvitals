using System.IO;
using System.Text;

namespace WinVitals.App;

/// <summary>
/// A plain text log under %LOCALAPPDATA%\WinVitals\logs.
///
/// This exists because "the app just closed" is not a bug report anyone can act on.
/// Every unhandled exception, every repair, and every navigation lands here, so a
/// user who hits a problem has a file to attach instead of a memory to describe.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVitals", "logs");

    public static string Path_ => _path ??= Resolve();

    private static string Resolve()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            return Path.Combine(Directory, $"winvitals-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "winvitals.log");
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message}\n{ex}";
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(Path_, line, Encoding.UTF8); }
            catch { /* a log that cannot be written must never take the app down */ }
        }
    }
}
