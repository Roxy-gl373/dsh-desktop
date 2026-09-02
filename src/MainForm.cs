using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Web.Script.Serialization;

namespace DshWhale
{
    public sealed class MainForm : Form
    {
        readonly Launcher l;
        WebView2 webView;
        ToolStripStatusLabel statusServer;
        ToolStripStatusLabel statusState;
        ToolStripStatusLabel statusVersion;
        NotifyIcon tray;
        ToolStripMenuItem rollbackItem;
        ToolStripMenuItem openNewVersionItem;
        volatile string latestTag;
        volatile string latestUrl;
        bool autoFit = true;
        double fitBaseWidth = 0;
        double zoom = 1.0;
        System.Windows.Forms.Timer resizeDebounce;
        ToolStripButton fitButton;
        readonly List<WebWindow> openWindows = new List<WebWindow>();

        public MainForm(Launcher launcher)
        {
            l = launcher;
            Text = l.Cfg.appName ?? "DSh Whale";
            string iconPath = l.Cfg.iconPath;
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                try { Icon = new Icon(iconPath); } catch { }
            }
            Width = 1280; Height = 820; MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;

            BuildToolbar();
            BuildWebView();
            BuildStatusStrip();
            SetupTray();

            resizeDebounce = new System.Windows.Forms.Timer { Interval = 160 };
            resizeDebounce.Tick += (s, e) => { resizeDebounce.Stop(); ApplyFitZoom(); };
            Resize += (s, e) => { if (autoFit) { resizeDebounce.Stop(); resizeDebounce.Start(); } };

            l.OnNotify += OnNotifyRaised;
            l.OnServerUpChanged += OnServerUpRaised;
            l.OnStateChanged += OnStateRaised;
            l.OnRolledBack += OnRolledBackRaised;

            Shown += (s, e) => { ThreadPool.QueueUserWorkItem(_ => CheckUpdateAsync()); };
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing && !exiting) { e.Cancel = true; HideToTray(); } };
        }

        volatile bool exiting;

        // ---------- UI construction ----------
        void BuildToolbar()
        {
            var ts = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
            ts.Items.Add(MakeButton("刷新", (s, e) => Navigate(true)));
            ts.Items.Add(MakeButton("新建窗口", (s, e) => NewWindow()));
            ts.Items.Add(MakeButton("重启服务", (s, e) => { l.RestartServer("manual toolbar"); }));
            ts.Items.Add(MakeButton("快照", (s, e) => { l.RunSnapshot("manual"); NotifyTooltip("已创建快照"); }));
            ts.Items.Add(MakeButton("验证", (s, e) => { l.RunVerify(); NotifyTooltip("验证完成"); }));
            rollbackItem = new ToolStripMenuItem("回滚到上一个好的快照");
            rollbackItem.Click += (s, e) => { l.RunRollback(); NotifyTooltip("回滚完成"); };
            ts.Items.Add(new ToolStripDropDownButton("安全") { DropDownItems = { rollbackItem } });
            ts.Items.Add(MakeButton("状态面板", (s, e) => ShowSafetyDialog()));
            ts.Items.Add(new ToolStripSeparator());
            fitButton = new ToolStripButton("等比缩放") { CheckOnClick = true, Checked = true };
            fitButton.CheckedChanged += (s, e) => {
                autoFit = fitButton.Checked;
                if (autoFit) { fitBaseWidth = 0; resizeDebounce.Start(); }
                else { SetZoom(1.0); }
            };
            ts.Items.Add(fitButton);
            ts.Items.Add(MakeButton("缩小", (s, e) => SetZoom(zoom - 0.1)));
            ts.Items.Add(MakeButton("100%", (s, e) => SetZoom(1.0)));
            ts.Items.Add(MakeButton("放大", (s, e) => SetZoom(zoom + 0.1)));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(MakeButton("打开日志", (s, e) => OpenFolder(l.Cfg.logDir)));
            ts.Items.Add(MakeButton("浏览器打开", (s, e) => l.OpenExternalBrowser()));
            ts.Items.Add(MakeButton("最小化到托盘", (s, e) => HideToTray()));
            Controls.Add(ts);
        }

        // ---------- zoom (proportional / manual) ----------
        void SetZoom(double z)
        {
            autoFit = false;
            if (fitButton != null) { fitButton.Checked = false; }
            zoom = Math.Max(0.25, Math.Min(4.0, z));
            try { if (webView.CoreWebView2 != null) webView.ZoomFactor = zoom; } catch { }
            statusServer.Text = "缩放 " + Math.Round(zoom * 100) + "%";
        }

        void ApplyFitZoom()
        {
            try
            {
                if (webView.CoreWebView2 == null) return;
                int w = webView.ClientSize.Width;
                if (w <= 0) return;
                if (fitBaseWidth <= 0) { fitBaseWidth = w; zoom = 1.0; }
                else { zoom = Math.Max(0.25, Math.Min(4.0, (double)w / fitBaseWidth)); }
                webView.ZoomFactor = zoom;
                statusServer.Text = "等比缩放 " + Math.Round(zoom * 100) + "%";
            }
            catch { }
        }

        void BuildWebView()
        {
            webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(webView);
            webView.BringToFront();
        }

        void BuildStatusStrip()
        {
            var ss = new StatusStrip();
            statusServer = new ToolStripStatusLabel();
            statusServer.Spring = true; statusServer.TextAlign = ContentAlignment.MiddleLeft;
            statusState = new ToolStripStatusLabel();
            statusVersion = new ToolStripStatusLabel();
            ss.Items.Add(statusServer);
            ss.Items.Add(statusState);
            ss.Items.Add(statusVersion);
            Controls.Add(ss);
            UpdateStatusBar();
        }

        void SetupTray()
        {
            tray = new NotifyIcon();
            try
            {
                if (!string.IsNullOrEmpty(l.Cfg.iconPath) && File.Exists(l.Cfg.iconPath)) tray.Icon = new Icon(l.Cfg.iconPath);
                else tray.Icon = Icon;
            }
            catch { }
            tray.Text = l.Cfg.appName ?? "DSh Whale";
            tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (s, e) => ShowFromTray());
            menu.Items.Add("打开 Web 界面", null, (s, e) => { ShowFromTray(); Navigate(true); });
            menu.Items.Add("新建窗口", null, (s, e) => { ShowFromTray(); NewWindow(); });
            menu.Items.Add("重启服务", null, (s, e) => l.RestartServer("tray"));
            menu.Items.Add("打开日志文件夹", null, (s, e) => OpenFolder(l.Cfg.logDir));
            rollbackItem = new ToolStripMenuItem("回滚到上一个好的快照");
            rollbackItem.Click += (s, e) => { l.RunRollback(); NotifyTooltip("回滚完成"); };
            menu.Items.Add(rollbackItem);
            openNewVersionItem = new ToolStripMenuItem("检查更新") { Enabled = false };
            openNewVersionItem.Click += (s, e) => OpenRelease();
            menu.Items.Add(openNewVersionItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitApp());
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowFromTray();
        }

        ToolStripButton MakeButton(string text, EventHandler onClick)
        {
            var b = new ToolStripButton(text);
            b.Click += onClick;
            return b;
        }

        // ---------- window/tray ----------
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            InitWebViewAsync();
        }

        async void InitWebViewAsync()
        {
            try
            {
                statusServer.Text = "正在初始化内嵌浏览器…";
                string wvDir = Path.Combine(l.Cfg.stateDir, "webview2");
                try { Directory.CreateDirectory(wvDir); } catch { }
                var env = await CoreWebView2Environment.CreateAsync(null, wvDir);
                await webView.EnsureCoreWebView2Async(env);
                try
                {
                    // Enable native zoom shortcuts (Ctrl+scroll, Ctrl+/-, Ctrl+0) and fit.
                    webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                    webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
                    webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                }
                catch { }
                webView.Source = new Uri(l.WebUrl);
            }
            catch (Exception ex)
            {
                statusServer.Text = "WebView2 初始化失败：" + ex.Message;
            }
        }

        void Navigate(bool reload)
        {
            try
            {
                if (webView.CoreWebView2 == null) { statusServer.Text = "等待 WebView2 初始化…"; return; }
                webView.Source = new Uri(l.WebUrl);
            }
            catch (Exception ex) { statusServer.Text = "导航失败：" + ex.Message; }
        }

        public void HideToTray() { Hide(); }
        void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        // Listens for the single-instance "show me" signal so a second launch brings this
        // window forward instead of creating another instance (and another tray icon).
        public void BindShowSignal(EventWaitHandle ev)
        {
            if (ev == null) return;
            var t = new Thread(() =>
            {
                while (!exiting)
                {
                    try { ev.WaitOne(); } catch { return; }
                    Marshal(() => ShowFromTray());
                }
            }) { IsBackground = true, Name = "dsh-whale-show" };
            t.Start();
        }

        void ExitApp()
        {
            exiting = true;
            try { tray.Visible = false; tray.Dispose(); } catch { }
            try { foreach (var w in openWindows.ToArray()) { w.Close(); } } catch { }
            l.Shutdown();
            Close();
            Application.Exit();
        }

        void NewWindow()
        {
            var w = new WebWindow(l);
            w.NewWindowRequested += () => NewWindow();
            w.FormClosed += (s, e) => openWindows.Remove(w);
            openWindows.Add(w);
            w.Show();
        }

        void OpenFolder(string dir)
        {
            try { if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        void NotifyTooltip(string msg) { statusServer.Text = msg; }

        // ---------- event marshalling (background -> UI) ----------
        void Marshal(Action a) { if (InvokeRequired) { try { BeginInvoke(a); } catch { } } else a(); }

        void OnNotifyRaised(string t, string m, ToolTipIcon icon)
        {
            Marshal(() => { try { tray.ShowBalloonTip(3000, t ?? "DSh Whale", m ?? "", icon); } catch { } });
        }

        void OnServerUpRaised(bool up)
        {
            Marshal(() =>
            {
                statusServer.Text = up ? "● 服务已就绪  " + l.WebUrl : "○ 服务未就绪";
                statusServer.ForeColor = up ? Color.Green : Color.Red;
                if (up && webView.CoreWebView2 == null) Navigate(true);
                else if (up && webView.CoreWebView2 != null) { try { webView.CoreWebView2.Reload(); } catch { } }
                UpdateStatusBar();
            });
        }

        void OnStateRaised() { Marshal(() => UpdateStatusBar()); }
        void OnRolledBackRaised() { Marshal(() => { statusServer.Text = "已回滚到上一个好的快照"; UpdateStatusBar(); }); }

        void UpdateStatusBar()
        {
            try
            {
                var s = l.GetSafety();
                statusServer.Text = s.healthy ? "● 服务已就绪  " + l.WebUrl : "○ 服务未就绪";
                statusServer.ForeColor = s.healthy ? Color.Green : Color.Red;
                string stateStr = "上次好快照：" + (string.IsNullOrEmpty(s.lastGoodSnapshotId) ? "无" : s.lastGoodSnapshotId);
                if (s.pendingInstall != null) stateStr += " | 待验证插件：" + s.pendingInstall.plugin;
                statusState.Text = stateStr;
                statusVersion.Text = "v" + (string.IsNullOrEmpty(l.Cfg.appVersion) ? "?" : l.Cfg.appVersion);
            }
            catch { }
        }

        // ---------- safety visualization ----------
        void ShowSafetyDialog()
        {
            var dlg = new SafetyDialog(l);
            dlg.ShowDialog(this);
            UpdateStatusBar();
        }

        // ---------- auto-update ----------
        void CheckUpdateAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(l.Cfg.updateUrl)) return;
                string ver = string.IsNullOrEmpty(l.Cfg.appVersion) ? "0.0.0" : l.Cfg.appVersion;
                using (var wc = new WebClient())
                {
                    wc.Headers["User-Agent"] = "DSh-Whale";
                    string body = wc.DownloadString(l.Cfg.updateUrl);
                    var ser = new JavaScriptSerializer();
                    var o = ser.Deserialize<Dictionary<string, object>>(body);
                    string tag = o != null && o.ContainsKey("tag_name") ? (o["tag_name"] as string) : null;
                    string url = o != null && o.ContainsKey("html_url") ? (o["html_url"] as string) : null;
                    if (!string.IsNullOrEmpty(tag) && IsNewer(tag, ver))
                    {
                        latestTag = tag.TrimStart('v', 'V'); latestUrl = url;
                        Marshal(() =>
                        {
                            openNewVersionItem.Enabled = true;
                            openNewVersionItem.Text = "新版本 " + latestTag + " 可用 — 点击查看";
                            try { tray.ShowBalloonTip(6000, "DSh Whale", "发现新版本 " + latestTag + "，点击托盘菜单查看。", ToolTipIcon.Info); } catch { }
                        });
                    }
                }
            }
            catch { /* offline / no release — silently skip */ }
        }

        static bool IsNewer(string tag, string cur)
        {
            try
            {
                var a = tag.TrimStart('v', 'V').Split('.');
                var b = cur.TrimStart('v', 'V').Split('.');
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    int ai = i < a.Length ? int.Parse(a[i].Trim()) : 0;
                    int bi = i < b.Length ? int.Parse(b[i].Trim()) : 0;
                    if (ai > bi) return true;
                    if (ai < bi) return false;
                }
            }
            catch { return false; }
            return false;
        }

        void OpenRelease()
        {
            if (string.IsNullOrEmpty(latestUrl) && !string.IsNullOrEmpty(l.Cfg.updateUrl))
            {
                try { Process.Start(new ProcessStartInfo(l.Cfg.updateUrl) { UseShellExecute = true }); } catch { }
                return;
            }
            if (!string.IsNullOrEmpty(latestUrl)) { try { Process.Start(new ProcessStartInfo(latestUrl) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { tray.Visible = false; tray.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    public sealed class SafetyDialog : Form
    {
        readonly Launcher l;
        TextBox jsonBox;
        ListView snapList;
        Label summary;

        public SafetyDialog(Launcher launcher)
        {
            l = launcher;
            Text = "安全模块状态";
            Width = 760; Height = 560; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(6) };
            AddBtn(top, "刷新", (s, e) => RefreshAsync());
            AddBtn(top, "新建快照", (s, e) => { l.RunSnapshot("manual"); RefreshAsync(); });
            AddBtn(top, "回滚到最近", (s, e) => { l.RunRollback(); RefreshAsync(); });
            AddBtn(top, "验证", (s, e) => { l.RunVerify(); RefreshAsync(); });
            AddBtn(top, "打开日志", (s, e) => OpenFolder(l.Cfg.logDir));
            Controls.Add(top);

            summary = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(6), TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(summary);

            snapList = new ListView { Dock = DockStyle.Top, Height = 130, View = View.Details, FullRowSelect = true };
            snapList.Columns.Add("快照", 210);
            snapList.Columns.Add("类型", 110);
            snapList.Columns.Add("插件", 200);
            Controls.Add(snapList);

            jsonBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9) };
            Controls.Add(jsonBox);

            Shown += (s, e) => RefreshAsync();
        }

        void AddBtn(FlowLayoutPanel p, string text, EventHandler h)
        {
            var b = new Button { Text = text, Width = 96, Height = 28 };
            b.Click += h;
            p.Controls.Add(b);
        }

        async void RefreshAsync()
        {
            try
            {
                string status = await TaskEx.Run(() => l.RunStatus());
                jsonBox.Text = status;
                ParseHighlights(status);
            }
            catch (Exception ex) { jsonBox.Text = ex.Message; }
        }

        void ParseHighlights(string status)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var o = ser.Deserialize<Dictionary<string, object>>(status);
                bool healthy = o.ContainsKey("serverHealthy") && Convert.ToBoolean(o["serverHealthy"]);
                string lastGood = o.ContainsKey("lastGoodSnapshotId") && o["lastGoodSnapshotId"] != null ? o["lastGoodSnapshotId"].ToString() : "无";
                string pending = "";
                if (o.ContainsKey("pendingInstall") && o["pendingInstall"] != null)
                {
                    var pi = o["pendingInstall"] as Dictionary<string, object>;
                    if (pi != null && pi.ContainsKey("plugin")) pending = pi["plugin"].ToString();
                }
                summary.Text = "服务：" + (healthy ? "正常" : "异常") + "   上次好快照：" + lastGood + (string.IsNullOrEmpty(pending) ? "" : "   待验证插件：" + pending);

                snapList.Items.Clear();
                if (o.ContainsKey("snapshots"))
                {
                    var arr = o["snapshots"] as object[];
                    if (arr != null)
                    {
                        foreach (var it in arr)
                        {
                            var d = it as Dictionary<string, object>;
                            if (d == null) continue;
                            var li = new ListViewItem(d.ContainsKey("Id") ? d["Id"].ToString() : "");
                            li.SubItems.Add(d.ContainsKey("Kind") ? d["Kind"].ToString() : "");
                            li.SubItems.Add(d.ContainsKey("Plugin") ? d["Plugin"].ToString() : "");
                            snapList.Items.Add(li);
                        }
                    }
                }
            }
            catch { /* json may be non-object (error line) — ignore */ }
        }

        void OpenFolder(string dir)
        {
            try { if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }

    // tiny Task.Run shim available on .NET Framework without extra usings
    internal static class TaskEx
    {
        public static System.Threading.Tasks.Task<T> Run<T>(Func<T> f)
        {
            return System.Threading.Tasks.Task.Factory.StartNew(f);
        }
    }
}
