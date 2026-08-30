using System.Text;

namespace ZoomAutoAdmit.WaitingRoomTester;

public static class TesterLogger
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZoomAutoAdmit",
        "Logs",
        "waiting-room-tester.log");

    private static readonly object SyncLock = new();

    static TesterLogger()
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch { }
    }

    public static void Log(string tag, string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = $"[{timestamp}] [{tag}] {message}";

        lock (SyncLock)
        {
            try
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        ConsoleColor prevColor = Console.ForegroundColor;
        Console.ForegroundColor = tag switch
        {
            "ACTION" => ConsoleColor.Green,
            "DESKTOP" => ConsoleColor.Cyan,
            "WEB" => ConsoleColor.Magenta,
            "ERROR" => ConsoleColor.Red,
            "WAITING_ROOM" => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray
        };

        Console.WriteLine(line);
        Console.ForegroundColor = prevColor;
    }

    public static void Desktop(string message) => Log("DESKTOP", message);
    public static void Web(string message) => Log("WEB", message);
    public static void Action(string message) => Log("ACTION", message);
    public static void WaitingRoom(string message) => Log("WAITING_ROOM", message);
    public static void Error(string reason) => Log("ERROR", reason);

    public static string ReadAllLogs()
    {
        lock (SyncLock)
        {
            try
            {
                if (File.Exists(LogFilePath))
                {
                    return File.ReadAllText(LogFilePath, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                return $"Failed to read logs: {ex.Message}";
            }
        }
        return "(No logs yet)";
    }
}
