using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // The probe must exit before single-instance checks and before any quota,
            // settings or tray initialization.
            if (args != null && args.Length == 1 && args[0] == "--anchor-probe")
            {
                TaskbarWidgetForm.WriteAnchorProbeResult();
                return;
            }
            try
            {
                Diagnostics.Write("main: start args=" + (args == null || args.Length == 0 ? "(none)" : string.Join(" ", args)));
                Diagnostics.Write("main: env " + Diagnostics.DescribeEnvironment());
                Diagnostics.Write("main: integrity " + Diagnostics.DescribeIntegrity() +
                    " user=" + Environment.UserDomainName + "\\" + Environment.UserName);
                WaitForPreviousVersion(args);
                bool createdNew;
                using (var mutex = new Mutex(true, "CodexQuotaTray.SingleInstance", out createdNew))
                {
                    Diagnostics.Write("main: single-instance createdNew=" + createdNew);
                    if (!createdNew)
                    {
                        MessageBox.Show("Codex 余额已经在运行。", "Codex 余额", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                    {
                        Diagnostics.Write("main: unhandled UI exception " + Diagnostics.Describe(e.Exception));
                    };
                    AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                    {
                        Diagnostics.Write("main: unhandled domain exception " + Diagnostics.Describe(e.ExceptionObject as Exception));
                    };
                    Application.Run(new TrayApplicationContext());
                    Diagnostics.Write("main: message loop exited");
                    GC.KeepAlive(mutex);
                }
            }
            catch (Exception error)
            {
                Diagnostics.Write("main: startup failed " + Diagnostics.Describe(error));
                throw;
            }
        }

        private static void WaitForPreviousVersion(string[] args)
        {
            if (args == null || args.Length != 2 || !string.Equals(args[0], "--wait-for-pid", StringComparison.OrdinalIgnoreCase)) return;
            int processId;
            if (!int.TryParse(args[1], out processId) || processId <= 0) return;
            try
            {
                using (var process = Process.GetProcessById(processId)) process.WaitForExit(30000);
            }
            catch
            {
            }
        }
    }
}
