using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NpuChat;

internal enum NpuPowerPolicy { System, Auto, PowerSaver, Balanced, Performance }

internal sealed class NpuPowerController
{
    private static readonly string Tool = Path.Combine(Environment.SystemDirectory, "AMD", "xrt-smi.exe");
    private readonly SemaphoreSlim changes = new(1);
    private string? original;
    private string? applied;

    internal static bool IsAvailable => File.Exists(Tool);

    internal static string? ManualMode(NpuPowerPolicy policy) => policy switch
    {
        NpuPowerPolicy.PowerSaver => "powersaver",
        NpuPowerPolicy.Balanced => "balanced",
        NpuPowerPolicy.Performance => "performance",
        _ => null
    };

    internal static bool IsPluggedIn => GetSystemPowerStatus(out var status) && status.AcLineStatus == 1;

    internal async Task<bool> ApplyAsync(string? mode)
    {
        await changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (mode is null && applied is null) return true;
            var current = await ReadModeAsync().ConfigureAwait(false);
            if (current != applied)
            {
                // An outside change supersedes the setting Phoenix last applied.
                applied = null;
                original = current;
            }
            string? target = mode ?? original;
            if (target is null || target == current) return true;
            await RunAsync("configure", "--pmode", target).ConfigureAwait(false);
            applied = mode ?? target;
            if (await ReadModeAsync().ConfigureAwait(false) != target)
                throw new InvalidOperationException($"AMD did not confirm {target} mode.");
            if (mode is null) applied = null;
            DiagnosticLog.Write("npu-power", $"NPU mode {current} → {target}.");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
                                     or InvalidOperationException or TimeoutException)
        {
            DiagnosticLog.Write("npu-power", $"Could not change NPU power mode: {error.Message}");
            return false;
        }
        finally { changes.Release(); }
    }

    private static async Task<string> ReadModeAsync()
    {
        string report = await RunAsync("examine", "--report", "platform").ConfigureAwait(false);
        var match = Regex.Match(report, @"Power Mode\s*:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        string mode = Regex.Replace(match.Groups[1].Value, @"\s+", "").ToLowerInvariant();
        return mode is "default" or "powersaver" or "balanced" or "performance" or "turbo"
            ? mode : throw new InvalidOperationException("AMD did not report a recognized NPU power mode.");
    }

    private static async Task<string> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo(Tool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the AMD NPU utility.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            string error = (await errors.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException(error.Length == 0 ? (await output.ConfigureAwait(false)).Trim() : error);
        }
        return await output.ConfigureAwait(false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
