using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

// WebWindow.cs — a secondary DSH Web window (multi-window support within the single process).
// Shows another embedded WebView2 for the same web UI; closing it only closes that window.

namespace DshWhale
{
    public sealed class WebWindow : Form
    {
        readonly Launcher l;
        WebView2 webView;
        double zoom = 1.0;
        bool autoFit = true;
        double fitBaseWidth = 0;
        System.Windows.Forms.Timer resizeDebounce;
        ToolStripStatusLabel status;

        public event Action NewWindowRequested;

        public WebWindow(Launcher launcher)
        {
            l = launcher;
            Text = "DSh Web（新窗口）";
            Width = 1100; Height = 720; MinimumSize = new Size(640, 440);
            StartPosition = FormStartPosition.CenterParent;
            if (!string.IsNullOrEmpty(l.Cfg.iconPath) && System.IO.File.Exists(l.Cfg.iconPath))
            {
                try { Icon = new Icon(l.Cfg.iconPath); } catch { }
            }

            // simple toolbar
            var ts = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
            ts.Items.Add(MakeButton("新建窗口", (s, e) => { var h = NewWindowRequested; if (h != null) h(); }));
            ts.Items.Add(MakeButton("刷新", (s, e) => Navigate()));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(MakeButton("缩小", (s, e) => SetZoom(zoom - 0.1)));
            ts.Items.Add(MakeButton("100%", (s, e) => SetZoom(1.0)));
            ts.Items.Add(MakeButton("放大", (s, e) => SetZoom(zoom + 0.1)));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(MakeButton("浏览器打开", (s, e) => l.OpenExternalBrowser()));
            ts.Items.Add(MakeButton("关闭", (s, e) => Close()));
            Controls.Add(ts);

            webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(webView);
            webView.BringToFront();

            status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var ss = new StatusStrip();
            ss.Items.Add(status);
            Controls.Add(ss);

            resizeDebounce = new System.Windows.Forms.Timer { Interval = 160 };
            resizeDebounce.Tick += (s, e) => { resizeDebounce.Stop(); ApplyFitZoom(); };
            Resize += (s, e) => { if (autoFit) { resizeDebounce.Stop(); resizeDebounce.Start(); } };

            Shown += async (s, e) => await InitAsync();
        }

        async Task InitAsync()
        {
            try
            {
                string wvDir = System.IO.Path.Combine(l.Cfg.stateDir, "webview2");
                try { System.IO.Directory.CreateDirectory(wvDir); } catch { }
                var env = await CoreWebView2Environment.CreateAsync(null, wvDir);
                await webView.EnsureCoreWebView2Async(env);
                try
                {
                    webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                    webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
                    webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                }
                catch { }
                webView.Source = new Uri(l.WebUrl);
                status.Text = "● " + l.WebUrl;
            }
            catch (Exception ex) { status.Text = "WebView2 初始化失败：" + ex.Message; }
        }

        void Navigate()
        {
            try { if (webView.CoreWebView2 != null) webView.Source = new Uri(l.WebUrl); }
            catch (Exception ex) { status.Text = "导航失败：" + ex.Message; }
        }

        void SetZoom(double z)
        {
            autoFit = false;
            zoom = Math.Max(0.25, Math.Min(4.0, z));
            try { if (webView.CoreWebView2 != null) webView.ZoomFactor = zoom; } catch { }
            status.Text = "缩放 " + Math.Round(zoom * 100) + "%";
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
                status.Text = "等比缩放 " + Math.Round(zoom * 100) + "%";
            }
            catch { }
        }

        ToolStripButton MakeButton(string text, EventHandler onClick)
        {
            var b = new ToolStripButton(text);
            b.Click += onClick;
            return b;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { webView.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
