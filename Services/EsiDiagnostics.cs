using System.IO;

namespace EveCommandCenter.Services;

public static class EsiDiagnostics
{
    private static readonly object Gate = new();
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "Logs");
    private static string Flag => Path.Combine(DirectoryPath, "esi-debug.enabled");
    public static bool Enabled
    {
        get => File.Exists(Flag);
        set { Directory.CreateDirectory(DirectoryPath); if (value) File.WriteAllText(Flag, "enabled"); else if (File.Exists(Flag)) File.Delete(Flag); }
    }
    public static string Status { get; private set; } = "Waiting for ESI work.";
    public static void Write(string message)
    {
        message = message.Replace("\r", " ").Replace("\n", " ");
        if (message.Length > 2048) message = message[..2048];
        Status = message;
        if (!Enabled) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "esi-debug.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                {
                    if (File.Exists(path + ".2")) File.Delete(path + ".2");
                    if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
                    File.Move(path, path + ".1");
                }
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}\n");
            }
            catch { /* Diagnostics must never interrupt ESI or the UI. */ }
        }
    }
}
