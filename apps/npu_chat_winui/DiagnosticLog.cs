using System.Diagnostics;
using System.Text;

namespace NpuChat;

internal static class DiagnosticLog
{
    private static readonly object Sync = new();
    private const long LogLimit = 8 * 1024 * 1024;
    private static StreamWriter? writer;
    internal static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NpuChat", "Logs");
    internal static string? FilePath { get; private set; }

    internal static void Start()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string? launcherLog = Environment.GetEnvironmentVariable("NPU_CHAT_LOG");
                FilePath = launcherLog is not null &&
                    string.Equals(Path.GetDirectoryName(launcherLog), Folder, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(launcherLog).StartsWith("npu-chat-", StringComparison.Ordinal)
                    ? launcherLog
                    : Path.Combine(Folder, $"npu-chat-{DateTime.UtcNow:yyyyMMdd-HHmmss.fff}-{Environment.ProcessId}.log");
                writer = Open(FilePath);
                foreach (var old in new DirectoryInfo(Folder).EnumerateFiles("npu-chat-*.log")
                             .Where(file => !file.Name.EndsWith(".previous.log", StringComparison.Ordinal))
                             .OrderByDescending(file => file.LastWriteTimeUtc).Skip(20))
                {
                    if (old.FullName == FilePath) continue;
                    TryDelete(old.FullName);
                    TryDelete(RolloverPath(old.FullName));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(error.Message);
            }
        }
    }

    internal static void Write(string source, string message)
    {
        lock (Sync)
        {
            if (writer is null) return;
            try
            {
                if (writer.BaseStream.Length >= LogLimit && FilePath is string path)
                {
                    writer.Dispose();
                    writer = null;
                    File.Move(path, RolloverPath(path), overwrite: true);
                    writer = Open(path);
                }
                foreach (string line in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    writer!.WriteLine($"{DateTimeOffset.Now:O} [{source}] {line[..Math.Min(line.Length, 8192)]}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(error.Message);
            }
        }
    }

    private static StreamWriter Open(string path) => new(new FileStream(path, FileMode.Append,
        FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };

    private static string RolloverPath(string path) => Path.ChangeExtension(path, ".previous.log");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
