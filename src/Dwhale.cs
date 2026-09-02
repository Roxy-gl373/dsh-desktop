using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

// Dwhale.cs — DeepSeek Harness (DSH) Web launcher/supervisor core.
// Owns the dsh `web` child process, the real-time log, the health poll, the supervisor
// thread (crash-driven rollback + self-verify), and the rollback/verify invocations.
// The GUI (MainForm.cs) consumes this via public state + events.

namespace DshWhale
{
    public sealed class Config
    {
        public string appName { get; set; }
        public string nodePath { get; set; }
        public string dshBinJs { get; set; }
        public string dshCmd { get; set; }
        public string dshHome { get; set; }
        public string webProfile { get; set; }
        public string webHost { get; set; }
        public int webPort { get; set; }
        public int healthTimeoutMs { get; set; }
        public int pollIntervalMs { get; set; }
        public string logDir { get; set; }
        public string stateDir { get; set; }
        public string snapshotDir { get; set; }
        public string manifestPath { get; set; }
        public string safetyScript { get; set; }
        public string iconPath { get; set; }
        public string configPath { get; set; }
        public string updateUrl { get; set; }
        public string appVersion { get; set; }
    }

    public sealed class PendingInstall
    {
        public string plugin { get; set; }
        public string snapshotId { get; set; }
        public string installedAt { get; set; }
    }

    public sealed class Manifest
    {
        public PendingInstall pendingInstall { get; set; }
        public string lastGoodSnapshotId { get; set; }
    }

    public static class Json
    {
        static readonly JavaScriptSerializer ser = new JavaScriptSerializer();
        public static T Read<T>(string path) { using (var fs = File.OpenText(path)) return ser.Deserialize<T>(fs.ReadToEnd()); }
        public static T Parse<T>(string s) { return ser.Deserialize<T>(s); }
    }

    public sealed class SafetyState
    {
        public bool healthy;
        public string lastGoodSnapshotId;
        public PendingInstall pendingInstall;
    }

    public sealed class Launcher
    {
        public Config Cfg { get; private set; }
        public event Action<string> OnLog;
        public event Action<string, string, ToolTipIcon> OnNotify;   // title, message, icon
        public event Action<bool> OnServerUpChanged;
        public event Action OnRolledBack;
        public event Action OnStateChanged;

        Process dshProc;
        int restartCount;
        StreamWriter logWriter;
        readonly object logLock = new object();
        readonly object manifestLock = new object();
        volatile bool stopping;
        volatile bool userStop;
        volatile bool serverUp;
        bool verifiedThisRun;
        int healthySeconds;
        DateTime lastStart;

        public Launcher(Config c) { Cfg = c; }

        public string WebUrl { get { return "http://" + Cfg.webHost + ":" + Cfg.webPort; } }
        public bool IsUp { get { return serverUp; } }
        public int RestartCount { get { return restartCount; } }
        public string LogFile { get; private set; }

        public void Start()
        {
            Directory.CreateDirectory(Cfg.logDir);
            LogFile = Path.Combine(Cfg.logDir, "dsh-web-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            logWriter = new StreamWriter(new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = false };
            LogLine("==== DSh Whale launcher start ====");
            LogLine("URL=" + WebUrl + "  profile=" + Cfg.webProfile + "  dsh=" + Cfg.dshBinJs);
            LogLine("version=" + (string.IsNullOrEmpty(Cfg.appVersion) ? "0" : Cfg.appVersion));

            if (IsHealthy())
            {
                LogLine("Server already reachable at " + WebUrl + " -> reusing it (no new server started).");
                SetServerUp(true);
            }
            else
            {
                SetServerUp(false);
                StartServer();
            }
            Thread t = new Thread(SuperviseLoop) { IsBackground = true };
            t.Start();
        }

        void SetServerUp(bool v)
        {
            serverUp = v;
            var h = OnServerUpChanged; if (h != null) { try { h(v); } catch { } }
            var s = OnStateChanged; if (s != null) { try { s(); } catch { } }
        }

        public void StartServer()
        {
            lock (logLock)
            {
                if (stopping) return;
                if (!File.Exists(Cfg.nodePath) || !File.Exists(Cfg.dshBinJs))
                {
                    LogLine("ERROR: node/dsh not found. node=" + Cfg.nodePath + " dsh=" + Cfg.dshBinJs);
                    Notify("DSh Whale", "未找到 node 或 dsh，请先运行安装脚本。", ToolTipIcon.Error);
                    return;
                }
                try { if (dshProc != null && !dshProc.HasExited) dshProc.Kill(); } catch { }
                var psi = new ProcessStartInfo();
                psi.FileName = Cfg.nodePath;
                psi.Arguments = "\"" + Cfg.dshBinJs + "\" --profile " + Cfg.webProfile
                    + " --host " + Cfg.webHost + " --port " + Cfg.webPort + " --no-open";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                if (!string.IsNullOrEmpty(Cfg.dshHome)) psi.EnvironmentVariables["DSH_HOME"] = Cfg.dshHome;
                psi.EnvironmentVariables["NODE_NO_WARNINGS"] = "1";

                var p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                try { p.Start(); } catch (Exception ex) { LogLine("ERROR starting dsh: " + ex.Message); Notify("DSh Whale", "启动 dsh 失败：" + ex.Message, ToolTipIcon.Error); return; }

                dshProc = p;
                lastStart = DateTime.Now;
                restartCount++;
                verifiedThisRun = false;
                healthySeconds = 0;
                LogLine("Started dsh (pid=" + p.Id + ") restarts=" + restartCount);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) LogLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) LogLine("[err] " + e.Data); };
                p.Exited += (s, e) => OnServerExited();
            }
        }

        void OnServerExited()
        {
            if (stopping) return;
            int code = 0; try { code = dshProc.ExitCode; } catch { }
            LogLine("SERVER EXITED. code=" + code);
            SetServerUp(false);
            if (!userStop && HasPendingInstall())
            {
                LogLine("Crash while plugin install pending -> ROLLBACK.");
                Notify("DSh Whale", "检测到插件加载后崩溃，正在自动回滚…", ToolTipIcon.Warning);
                RunRollback();
                StartServer();
            }
            else if (!userStop)
            {
                Thread.Sleep(2000);
                if (!stopping) StartServer();
            }
            var s = OnStateChanged; if (s != null) { try { s(); } catch { } }
        }

        void SuperviseLoop()
        {
            int failCount = 0;
            while (!stopping)
            {
                Thread.Sleep(Cfg.pollIntervalMs <= 0 ? 3000 : Cfg.pollIntervalMs);
                if (stopping) break;
                bool healthy;
                try { healthy = IsHealthy(); } catch { healthy = false; }
                if (healthy)
                {
                    failCount = 0;
                    if (!serverUp) { SetServerUp(true); Notify("DSh Whale", "服务已就绪：" + WebUrl, ToolTipIcon.Info); }
                    if (HasPendingInstall())
                    {
                        DateTime? ia = GetPendingInstalledAt();
                        if (ia.HasValue && lastStart >= ia.Value)
                        {
                            healthySeconds += Math.Max(1, Cfg.pollIntervalMs / 1000);
                            if (healthySeconds >= 30 && !verifiedThisRun)
                            {
                                LogLine("Server healthy after pending install -> Verify.");
                                RunVerify();
                                verifiedThisRun = true;
                                healthySeconds = 0;
                            }
                        }
                    }
                    else { healthySeconds = 0; }
                    continue;
                }
                failCount++;
                bool procAlive = false;
                try { procAlive = dshProc != null && !dshProc.HasExited; } catch { procAlive = false; }
                SetServerUp(false);
                if (failCount >= 3 && !procAlive && !userStop)
                {
                    LogLine("Health down x" + failCount + " and dsh not running.");
                    if (HasPendingInstall())
                    {
                        LogLine("Auto-rollback triggered (health lost after install).");
                        Notify("DSh Whale", "服务在插件加载后失去健康，自动回滚。", ToolTipIcon.Warning);
                        RunRollback();
                        StartServer();
                        failCount = 0;
                    }
                    else if (restartCount < 5)
                    {
                        StartServer();
                        failCount = 0;
                    }
                    else
                    {
                        Notify("DSh Whale", "服务多次重启仍失败，请查看日志。", ToolTipIcon.Warning);
                    }
                }
            }
        }

        public bool IsHealthy()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(WebUrl.TrimEnd('/') + "/");
                req.Method = "GET"; req.Timeout = 2500; req.KeepAlive = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    int c = (int)resp.StatusCode; return c >= 200 && c < 500;
                }
            }
            catch { return false; }
        }

        public void OpenExternalBrowser()
        {
            try { Process.Start(new ProcessStartInfo(WebUrl) { UseShellExecute = true }); LogLine("Opened external browser -> " + WebUrl); }
            catch (Exception ex) { LogLine("OpenBrowser error: " + ex.Message); }
        }

        public void RestartServer(string reason) { LogLine("Restart requested (" + reason + ")."); StartServer(); }

        public SafetyState GetSafety()
        {
            var st = new SafetyState { healthy = IsHealthy() };
            try
            {
                if (File.Exists(Cfg.manifestPath))
                {
                    lock (manifestLock)
                    {
                        var m = Json.Read<Manifest>(Cfg.manifestPath);
                        if (m != null) { st.lastGoodSnapshotId = m.lastGoodSnapshotId; st.pendingInstall = m.pendingInstall; }
                    }
                }
            }
            catch { }
            return st;
        }

        bool HasPendingInstall()
        {
            SafetyState s = GetSafety();
            return s.pendingInstall != null;
        }

        DateTime? GetPendingInstalledAt()
        {
            try
            {
                if (!File.Exists(Cfg.manifestPath)) return null;
                lock (manifestLock)
                {
                    var m = Json.Read<Manifest>(Cfg.manifestPath);
                    if (m == null || m.pendingInstall == null) return null;
                    DateTimeOffset dto;
                    if (DateTimeOffset.TryParse(m.pendingInstall.installedAt, out dto)) return dto.LocalDateTime;
                }
            }
            catch { }
            return null;
        }

        void RunSafetyAction(string action, string extraArg, string argName, string logTag)
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + Cfg.safetyScript + "\" -Action " + action;
                if (!string.IsNullOrEmpty(extraArg)) psi.Arguments += " -" + argName + " \"" + extraArg + "\"";
                psi.Arguments += " -Config \"" + Cfg.configPath + "\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                LogLine("Running " + logTag + ": " + psi.Arguments);
                var p = Process.Start(psi);
                p.OutputDataReceived += (s, e) => { if (e.Data != null) LogLine("[" + logTag + "] " + e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) LogLine("[" + logTag + "-err] " + e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(180000)) p.Kill();
                LogLine(logTag + " finished, exit=" + p.ExitCode);
                var sc = OnStateChanged; if (sc != null) { try { sc(); } catch { } }
            }
            catch (Exception ex) { LogLine(logTag + " error: " + ex.Message); }
        }

        public void RunRollback()
        {
            SafetyState s = GetSafety();
            string snap = (s.pendingInstall != null) ? s.pendingInstall.snapshotId : (string.IsNullOrEmpty(s.lastGoodSnapshotId) ? "lastgood" : s.lastGoodSnapshotId);
            RunSafetyAction("Rollback", snap, "Snapshot", "rollback");
            var r = OnRolledBack; if (r != null) { try { r(); } catch { } }
        }

        public void RunVerify() { RunSafetyAction("Verify", null, null, "verify"); }
        public void RunSnapshot(string reason) { RunSafetyAction("Snapshot", reason, "Reason", "snapshot"); }

        public string RunStatus()
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + Cfg.safetyScript + "\" -Action Status -Config \"" + Cfg.configPath + "\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(120000);
                return string.IsNullOrEmpty(err) ? outp : outp + "\n[err]\n" + err;
            }
            catch (Exception ex) { return "Status error: " + ex.Message; }
        }

        void Notify(string title, string msg, ToolTipIcon icon)
        {
            var h = OnNotify; if (h != null) { try { h(title, msg, icon); } catch { } }
        }

        public void Shutdown()
        {
            stopping = true;
            userStop = true;
            try { if (dshProc != null && !dshProc.HasExited) dshProc.Kill(); } catch { }
            LogLine("Stopped by user. Goodbye.");
            try { if (logWriter != null) logWriter.Flush(); } catch { }
        }

        void LogLine(string line)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            lock (logLock)
            {
                if (logWriter != null) { try { logWriter.WriteLine(ts + "  " + line); logWriter.Flush(); } catch { } }
            }
            var h = OnLog; if (h != null) { try { h(ts + "  " + line); } catch { } }
        }
    }

    public sealed class SingleInstance : IDisposable
    {
        readonly Mutex mutex;
        public EventWaitHandle ShowEvent { get; private set; }
        public bool IsPrimary { get; private set; }

        public SingleInstance(string name)
        {
            // Session-local named mutex: only one DSh Whale per user/logon session.
            mutex = new Mutex(false, "Local\\" + name);
            try { IsPrimary = mutex.WaitOne(0, false); }
            catch { IsPrimary = true; } // an abandoned/odd mutex: err on the side of running
            if (IsPrimary)
            {
                // Primary instance: keep the mutex for its lifetime and expose a "show me" event.
                try { ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + name + ".Show"); }
                catch { ShowEvent = null; }
            }
            else
            {
                // A second instance: ask the running one to bring its window forward, then exit.
                try { var e = EventWaitHandle.OpenExisting("Local\\" + name + ".Show"); e.Set(); } catch { }
            }
        }

        public void Dispose()
        {
            try { if (ShowEvent != null) ShowEvent.Dispose(); } catch { }
            try { if (IsPrimary) mutex.ReleaseMutex(); } catch { }
            try { mutex.Dispose(); } catch { }
        }
    }

    public static class Program
    {
        // Make the process per-monitor-V2 DPI aware before any window is created, so the
        // embedded WebView2 renders at native pixel density instead of being bitmap-scaled (blurry).
        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static void EnableDpiAwareness()
        {
            try
            {
                // PER_MONITOR_AWARE_V2 = -4. If the OS doesn't support it, fall back to system-aware.
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            EnableDpiAwareness();

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = Path.Combine(dir, "config.json");
            if (!File.Exists(cfgPath))
            {
                string pkg = Path.GetFullPath(Path.Combine(dir.TrimEnd('\\'), ".."));
                string alt = Path.Combine(pkg, "config.json");
                if (File.Exists(alt)) cfgPath = alt;
            }
            if (!File.Exists(cfgPath))
            {
                MessageBox.Show("未找到 config.json（启动器配置文件）。请先运行安装脚本。", "DSh Whale", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Config cfg = null;
            try { cfg = Json.Read<Config>(cfgPath); }
            catch (Exception ex) { MessageBox.Show("config.json 解析失败：" + ex.Message, "DSh Whale"); return; }
            if (string.IsNullOrEmpty(cfg.nodePath) || string.IsNullOrEmpty(cfg.dshBinJs))
            {
                MessageBox.Show("config.json 缺少 nodePath/dshBinJs。请先运行安装脚本。", "DSh Whale");
                return;
            }
            if (string.IsNullOrEmpty(cfg.webHost)) cfg.webHost = "127.0.0.1";
            if (cfg.webPort <= 0) cfg.webPort = 3080;
            if (string.IsNullOrEmpty(cfg.webProfile)) cfg.webProfile = "web";

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Single-instance guard: repeated launches bring the running window to the front
            // instead of spawning another window + tray icon.
            using (var single = new SingleInstance("DShWhale.Launcher"))
            {
                if (!single.IsPrimary) { return; }

                var launcher = new Launcher(cfg);
                launcher.Start();

                var form = new MainForm(launcher);
                form.BindShowSignal(single.ShowEvent);
                Application.Run(form);
            }
        }
    }
}
