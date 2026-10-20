using System;
using System.IO;
using System.Text;
using System.Threading;

namespace CodexQuotaTray
{
    /// <summary>
    /// Minimal append-only diagnostic log for tracking refresh stalls.
    /// Prefers %LOCALAPPDATA%\CodexMeter\diagnostic.log, then the executable folder,
    /// then TEMP, so a sandboxed launch (read-only AppData) still leaves a trace.
    /// Never throws: logging must not disturb the tray.
    /// </summary>
    internal static class Diagnostics
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 512 * 1024;
        private static string _activePath;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    var path = ResolvePath();
                    if (path == null) return;

                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var backup = path + ".1";
                        try
                        {
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(path, backup);
                        }
                        catch
                        {
                        }
                    }

                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " [" + Thread.CurrentThread.ManagedThreadId.ToString("00") + "] " +
                        message + Environment.NewLine;
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private static string ResolvePath()
        {
            if (_activePath != null) return _activePath;

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexMeter", "diagnostic.log"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostic.log"),
                Path.Combine(Path.GetTempPath(), "CodexMeter-diagnostic.log")
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var directory = Path.GetDirectoryName(candidate);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);
                    using (new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                    }
                    _activePath = candidate;
                    return _activePath;
                }
                catch
                {
                }
            }

            return null;
        }

        public static string Describe(Exception error)
        {
            if (error == null) return "(null)";
            return error.GetType().Name + ": " + error.Message;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int needed);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static int _integrityRid = -1;

        /// <summary>
        /// Integrity RID of the current process (0x1000 low, 0x2000 medium, 0x3000 high).
        /// A low-integrity process cannot write user files, which makes codex app-server
        /// fail before it can answer, so the tray silently serves stale session data.
        /// </summary>
        public static int IntegrityRid
        {
            get
            {
                if (_integrityRid >= 0) return _integrityRid;
                _integrityRid = 0x2000;
                try
                {
                    IntPtr token;
                    if (OpenProcessToken(GetCurrentProcess(), 0x0008, out token))
                    {
                        try
                        {
                            int needed;
                            GetTokenInformation(token, 25, IntPtr.Zero, 0, out needed);
                            if (needed > 0)
                            {
                                var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(needed);
                                try
                                {
                                    if (GetTokenInformation(token, 25, buffer, needed, out needed))
                                    {
                                        var sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer);
                                        var count = System.Runtime.InteropServices.Marshal.ReadByte(sid, 1);
                                        var offset = 8 + (count - 1) * 4;
                                        _integrityRid = System.Runtime.InteropServices.Marshal.ReadInt32(sid, offset);
                                    }
                                }
                                finally
                                {
                                    System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                                }
                            }
                        }
                        finally
                        {
                            CloseHandle(token);
                        }
                    }
                }
                catch
                {
                }
                return _integrityRid;
            }
        }

        public static bool IsLowIntegrity
        {
            get { return IntegrityRid > 0 && IntegrityRid < 0x2000; }
        }

        public static string DescribeIntegrity()
        {
            var rid = IntegrityRid;
            var name = rid >= 0x4000 ? "system" : rid >= 0x3000 ? "high" : rid >= 0x2000 ? "medium" : "low";
            return name + " (0x" + rid.ToString("X") + ")" + (IsLowIntegrity ? " SANDBOXED" : "");
        }

        /// <summary>One-line snapshot of the variables that can change codex behaviour. Secrets are masked.</summary>
        public static string DescribeEnvironment()
        {
            try
            {
                var builder = new StringBuilder();
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    var name = (entry.Key as string) ?? "";
                    var relevant = name.StartsWith("CODEX", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("OPENAI", StringComparison.OrdinalIgnoreCase)
                        || name.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0
                        || string.Equals(name, "LOCALAPPDATA", StringComparison.OrdinalIgnoreCase);
                    if (!relevant) continue;

                    var value = (entry.Value as string) ?? "";
                    if (name.IndexOf("KEY", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("TOKEN", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("SECRET", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        value = value.Length == 0 ? "(empty)" : "(set,len=" + value.Length + ")";
                    }
                    else if (value.Length > 80)
                    {
                        value = value.Substring(0, 80) + "...";
                    }
                    builder.Append(name).Append('=').Append(value).Append("; ");
                }
                return builder.Length == 0 ? "(none)" : builder.ToString();
            }
            catch
            {
                return "(unavailable)";
            }
        }
    }
}
