using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Login helper: launch the quota UI once when a Codex desktop session starts.
// Closing the quota UI manually will not reopen it during the same session.
internal static class CodexQuotaFollower
{
    private static bool IsCodexDesktopRunning()
    {
        bool found = false;
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule.FileName;
                    if (path.IndexOf("\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0)
                        found = true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return found;
    }

    private static bool IsQuotaRunning()
    {
        Mutex existing;
        if (!Mutex.TryOpenExisting("CodexQuotaTray.SingleInstance", out existing)) return false;
        existing.Dispose();
        return true;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var target = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexQuotaTray-v2.6.3.exe");
        if (args.Length == 2 && args[0] == "--check")
        {
            File.WriteAllText(args[1], "CodexDesktopRunning=" + IsCodexDesktopRunning()
                + Environment.NewLine + "QuotaRunning=" + IsQuotaRunning()
                + Environment.NewLine + "TargetExists=" + File.Exists(target));
            return;
        }
        bool created;
        using (var singleInstance = new Mutex(true, "CodexQuotaTray.Follower", out created))
        {
            if (!created) return;
            bool wasRunning = false;
            while (true)
            {
                var running = IsCodexDesktopRunning();
                if (running && !wasRunning && !IsQuotaRunning() && File.Exists(target))
                {
                    try
                    {
                        using (var launched = Process.Start(new ProcessStartInfo(target)
                        {
                            WorkingDirectory = Path.GetDirectoryName(target),
                            UseShellExecute = false,
                            CreateNoWindow = true
                        })) { }
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
                wasRunning = running;
                Thread.Sleep(3000);
            }
        }
    }
}
