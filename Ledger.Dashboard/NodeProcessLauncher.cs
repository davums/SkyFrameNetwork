using System.Diagnostics;

namespace Ledger.Dashboard;

internal static class NodeProcessLauncher
{
    public static string InstalledNodePath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Ledger.Node", "Ledger.Node.exe"));
    public static bool IsInstalled => File.Exists(InstalledNodePath);
    public static string InstalledDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Skyframe", "data");

    public static bool TryStart(int port, string dataDirectory, out string error)
    {
        error = "";
        if (IsInstalled) return TryStartInstalledService(out error);
        var project = FindNodeProject();
        if (project is null)
        {
            error = "Could not find the installed Ledger.Node service or Ledger.Node.csproj. Reinstall Skyframe Network or start Ledger.Node manually.";
            return false;
        }
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "dotnet", WorkingDirectory = Path.GetDirectoryName(project)!,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            info.ArgumentList.Add("run"); info.ArgumentList.Add("--project"); info.ArgumentList.Add(project); info.ArgumentList.Add("--");
            info.ArgumentList.Add("--port"); info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(Path.GetFullPath(dataDirectory));
            if (Process.Start(info) is null) { error = "Could not start the Ledger.Node process."; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static bool TryStartInstalledService(out string error)
    {
        error = "";
        try
        {
            var info = new ProcessStartInfo { FileName = "sc.exe", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("start"); info.ArgumentList.Add("SkyframeNode");
            using var process = Process.Start(info);
            if (process is null) { error = "Could not request the Skyframe Node service start."; return false; }
            if (!process.WaitForExit(15000)) { error = "Timed out while starting the Skyframe Node service."; return false; }
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (process.ExitCode == 0 || output.Contains("already been started", StringComparison.OrdinalIgnoreCase)) return true;
            error = string.IsNullOrWhiteSpace(output) ? $"The Skyframe Node service did not start (sc.exe exit {process.ExitCode})." : output.Trim();
            return false;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static string? FindNodeProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Ledger.Node", "Ledger.Node.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}