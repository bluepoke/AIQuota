using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace AIQuota;

/// <summary>
/// Downloads a newer release zip, then hands off to a small detached PowerShell helper
/// that waits for this process to exit, replaces the running exe, and relaunches it.
///
/// The swap can't happen in-process: Windows keeps the running exe's file open for as
/// long as this process is alive, so nothing can overwrite it until after we exit. The
/// helper script is the standard workaround for a self-updating single-exe app with no
/// separate installer/updater binary.
/// </summary>
public static class SelfUpdater
{
    // The self-contained zip bundles the whole .NET runtime (tens of MB); the
    // framework-dependent one is just the app itself (well under a MB). A safe
    // threshold to tell which one is currently installed from its own file size.
    private const long SelfContainedSizeThresholdBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Downloads and extracts the update, then launches a helper script that will replace
    /// the running executable and restart it once this process exits. The caller is
    /// expected to exit the application right after this returns.
    /// </summary>
    public static async Task PrepareAsync(NewVersionInfo update, CancellationToken cancellationToken)
    {
        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable's path.");

        var downloadUrl = ChooseAssetUrl(update, currentExePath);

        var stagingDir = Path.Combine(Path.GetTempPath(), "AIQuota-update-" + Guid.NewGuid());
        Directory.CreateDirectory(stagingDir);
        var zipPath = Path.Combine(stagingDir, "update.zip");
        var extractDir = Path.Combine(stagingDir, "extracted");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"AIQuota/{AppInfo.Version}");
            using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var target = File.Create(zipPath);
            await response.Content.CopyToAsync(target, cancellationToken);
        }

        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var newExePath = Directory.EnumerateFiles(extractDir, "AIQuota.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("The downloaded package doesn't contain AIQuota.exe.");

        var logPath = Path.Combine(stagingDir, "update.log");
        var scriptPath = Path.Combine(stagingDir, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildApplyScript(Environment.ProcessId, newExePath, currentExePath, stagingDir, logPath));

        Process helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -NoExit -File \"{scriptPath}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = stagingDir,
            }) ?? throw new InvalidOperationException("powershell.exe did not start.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Could not start the update helper (powershell.exe). Your system's security software may be blocking it.", ex);
        }

        // powershell.exe starting doesn't mean the script ran - security software can kill it
        // right after launch. Give it a moment and check, so a silent block surfaces as a
        // visible error instead of the app just disappearing with nothing having happened.
        await Task.Delay(1500, cancellationToken);
        if (helper.HasExited)
        {
            var log = File.Exists(logPath) ? File.ReadAllText(logPath).Trim() : null;
            var detail = string.IsNullOrEmpty(log) ? "" : $"\n\n{log}";
            throw new InvalidOperationException(
                $"The update helper closed immediately (exit code {helper.ExitCode}). " +
                $"Your system's security software may be blocking it.{detail}");
        }

        TryBringHelperWindowToFront();
    }

    /// <summary>
    /// A background tray app has no window of its own to hand focus off from, so Windows
    /// often leaves the new console sitting behind everything else instead of on top.
    /// PowerShell's own console has no visible window to bring forward on modern Windows:
    /// by default a new console is hosted as a tab inside Windows Terminal, and the classic
    /// "GetConsoleWindow" trick returns a null handle for a process attached to that kind of
    /// pseudo-console. So this looks for the actual top-level window from the outside -
    /// Windows Terminal if present (its default terminal since Windows 11), else classic
    /// conhost.exe - and forces that to the foreground instead. Best-effort: the update
    /// itself doesn't depend on this succeeding.
    /// </summary>
    private static void TryBringHelperWindowToFront()
    {
        try
        {
            var host = Process.GetProcessesByName("WindowsTerminal").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                ?? Process.GetProcessesByName("conhost").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);

            if (host is not null)
            {
                NativeMethods.ShowWindow(host.MainWindowHandle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(host.MainWindowHandle);
            }
        }
        catch (InvalidOperationException)
        {
            // A matched process may have exited between the lookup and the window calls.
        }
    }

    private static string ChooseAssetUrl(NewVersionInfo update, string currentExePath)
    {
        var isSelfContained = TryGetFileSize(currentExePath) is not { } size || size > SelfContainedSizeThresholdBytes;
        var preferred = isSelfContained ? update.SelfContainedAssetUrl : update.FrameworkDependentAssetUrl;
        return preferred
            ?? update.SelfContainedAssetUrl
            ?? update.FrameworkDependentAssetUrl
            ?? throw new InvalidOperationException("This release has no downloadable zip asset.");
    }

    private static long? TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Passing the running process's own PID lets the script wait for the exact
    /// process to exit (rather than a fixed delay) before it's safe to overwrite the exe.</summary>
    private static string BuildApplyScript(int processId, string sourceExe, string targetExe, string stagingDir, string logPath)
    {
        sourceExe = EscapeForSingleQuotedPowerShell(sourceExe);
        targetExe = EscapeForSingleQuotedPowerShell(targetExe);
        stagingDir = EscapeForSingleQuotedPowerShell(stagingDir);
        var escapedLogPath = EscapeForSingleQuotedPowerShell(logPath);

        return $$"""
            try { Start-Transcript -Path '{{escapedLogPath}}' -Append | Out-Null } catch {}
            $Host.UI.RawUI.WindowTitle = 'AIQuota update'

            # A background tray app has no foreground window to hand off to, so Windows
            # often opens this new console behind everything else instead of on top.
            # Force it forward so it's actually visible.
            try {
                Add-Type -Name Win32ForceForeground -Namespace AIQuota -MemberDefinition '
                    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
                    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
                    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
                '
                $consoleWindow = [AIQuota.Win32ForceForeground]::GetConsoleWindow()
                [AIQuota.Win32ForceForeground]::ShowWindow($consoleWindow, 9) | Out-Null # SW_RESTORE
                [AIQuota.Win32ForceForeground]::SetForegroundWindow($consoleWindow) | Out-Null
            } catch {}

            Write-Host 'AIQuota update'
            Write-Host '-------------'
            Write-Host 'Waiting for AIQuota to close...'
            Wait-Process -Id {{processId}} -Timeout 30 -ErrorAction SilentlyContinue

            Write-Host 'Installing the new version...'
            $copied = $false
            for ($i = 0; $i -lt 20; $i++) {
                try {
                    Copy-Item -LiteralPath '{{sourceExe}}' -Destination '{{targetExe}}' -Force -ErrorAction Stop
                    $copied = $true
                    break
                } catch {
                    Start-Sleep -Milliseconds 500
                }
            }

            if ($copied) {
                Write-Host 'Update installed. Starting AIQuota...'
                Start-Process -FilePath '{{targetExe}}' -ArgumentList '{{Program.PostUpdateRelaunchArgument}}'
                Write-Host 'Done - you can close this window.'
            } else {
                Write-Host 'Update failed: could not replace AIQuota.exe (it may still be in use).' -ForegroundColor Red
                Write-Host 'You can retry the update from the AIQuota tray menu, or update manually from:'
                Write-Host '{{AppInfo.RepositoryUrl}}/releases/latest'
            }

            Set-Location $env:TEMP
            Remove-Item -LiteralPath '{{stagingDir}}' -Recurse -Force -ErrorAction SilentlyContinue
            """;
    }

    /// <summary>Doubles embedded single quotes, the escaping PowerShell expects inside a
    /// '...' literal (e.g. a Windows username with an apostrophe in the file path).</summary>
    private static string EscapeForSingleQuotedPowerShell(string value) => value.Replace("'", "''");

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
