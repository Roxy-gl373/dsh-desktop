# DSh Whale · DeepSeek Harness 桌面启动器 + 插件安全系统

把 DeepSeek Harness 的 **Web 版**（`dsh --profile web`，默认 `http://127.0.0.1:3080`）封装成一个
**带图标的桌面应用**：内嵌 Web 窗口（WebView2）、实时日志、托盘、插件安全监控（装插件前自动快照 +
崩溃自动回滚），以及可选的 GitHub Release 自动更新检查。

图标使用你提供的 `dps-view.png`（已转成多尺寸 `.ico`，见 `bin/dps.ico`）。

---

## 特性

- **内嵌 Web 窗口**：不新开浏览器，直接在应用窗口里渲染 DSH Web 界面（基于 WebView2）。
- **实时日志**：把 dsh 服务的 stdout/stderr 逐行即时写入 `logs/dsh-web-<ts>.log`。
- **托盘**：最小化到托盘；右键菜单含显示窗口 / 重启服务 / 打开日志 / 回滚 / 检查更新 / 退出。
- **插件安全监控**：装插件前自动快照；服务在插件加载后崩溃则自动回滚并重启；重启后健康则自验证通过。
- **安全可视化面板**：状态按钮打开面板，展示服务健康、上次好快照、待验证插件与快照列表，并提供
  新建快照 / 回滚 / 验证 / 打开日志。
- **通知气泡**：服务就绪、崩溃回滚、版本更新等事件弹系统通知。
- **自动更新**：启动时检查 `updateUrl`（GitHub Release API），发现新版本弹提示，可从托盘菜单打开。

---

## 目录结构（全部相对定位，可整体搬家）

```
dsh-desktop\
├─ bin\Dwhale.exe              ← 应用（build.ps1 生成，不提交）
├─ bin\dps.ico                 ← 应用图标（不提交，见 .gitignore 说明）
├─ bin\Microsoft.Web.WebView2.*.dll, WebView2Loader.dll   ← WebView2 SDK（build.ps1 放置）
├─ config.json                 ← 本机配置（install-shortcut.ps1 生成，**不提交**）
├─ config.sample.json          ← 配置模板（提交）
├─ dsh-safe-add.cmd            ← 安全装插件入口
├─ LICENSE                     ← MIT（图标版权见 NOTICE 说明）
├─ logs\                       ← 实时日志（运行期生成）
├─ state\                      ← 快照与 manifest（运行期生成）
├─ scripts\
│  ├─ install-shortcut.ps1     ← 生成本机 config.json + 桌面快捷方式（按下脚本所在目录定位）
│  ├─ build.ps1                ← 用 csc 编译 exe（自动寻找 WebView2 SDK）
│  ├─ dsh-safety.ps1           ← 安全模块（Snapshot/Add/Rollback/Verify/Status/Monitor）
│  └─ convert-icon.ps1         ← PNG→ICO 转换
└─ src\Dwhale.cs, MainForm.cs  ← C# 源码
```

所有路径都从脚本/程序**自身所在位置**推导，换目录、换机器只需重跑一次 `install-shortcut.ps1`。

---

## 首次安装（在新机器/新位置）

1. 前置：Node.js、pnpm、.NET Framework 4.x、WebView2 运行时（Windows 10/11 一般自带 Edge WebView2）。
2. 构建（可选，仓库不提交 exe）：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
   ```
   `build.ps1` 会自动寻找 WebView2 SDK（优先 `lib\webview2\`，其次 `bin\`，再尝试 Office 内置位置）。
   若网络可用，最稳的是 `dotnet add package Microsoft.Web.WebView2` 后把三个 DLL 放进 `lib\webview2\`。
3. 生成本机配置 + 桌面快捷方式：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-shortcut.ps1
   ```
   桌面会出现 `DSh Web 鲸鱼娘.lnk`，双击即用。

> 只想生成配置、不动桌面快捷方式：加 `-NoShortcut`；只看解析结果不写文件：加 `-DryRun`。

---

## 用法

- **双击快捷方式**启动；窗口内即内嵌 DSH Web 界面。
- 工具栏：刷新 / 重启服务 / 快照 / 验证 / 安全（含回滚）/ 状态面板 / 打开日志 / 浏览器打开 / 最小化到托盘。
- 关闭窗口默认**最小化到托盘**；从托盘「退出」才真正退出并停止 dsh 服务。

### 安全装插件（先快照）

```powershell
# 方式 A（推荐）
.\dsh-safe-add.cmd "github:Small-tailqwq/dsh-deep-whale#path:/skin-manager"

# 方式 B
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dsh-safety.ps1 `
  -Action Add -Plugin "github:Small-tailqwq/dsh-deep-whale#path:/skin-manager" -Config config.json
```

流程：拍快照 → `dsh plugin --profile web add <插件>` → 标记待验证 → 提示重启。重启后用「重启服务」
或状态面板「验证」；若服务崩溃则监控器自动回滚到装插件前的快照。

### 常用命令

| 用途 | 命令 |
|---|---|
| 状态/健康 | `dsh-safety.ps1 -Action Status` |
| 新建基准快照 | `dsh-safety.ps1 -Action Snapshot -Reason baseline` |
| 验证并提升为 good | `dsh-safety.ps1 -Action Verify` |
| 回滚 | `dsh-safety.ps1 -Action Rollback -Snapshot <id\|lastgood>` |
| 独立监控 | `dsh-safety.ps1 -Action Monitor` |

---

## 配置 `config.json`

`config.json` 由 `install-shortcut.ps1` 生成，字段见 `config.sample.json`。关键项：

- `nodePath` / `dshBinJs`：Node 与 dsh 的 bin.js 绝对路径。
- `dshHome` / `webProfile` / `webHost` / `webPort`：DSH 数据目录与 web profile（默认 127.0.0.1:3080）。
- `logDir` / `stateDir` / `snapshotDir` / `manifestPath` / `safetyScript` / `iconPath`：各资源位置（相对包根）。
- `updateUrl`：GitHub Release API 地址（如 `https://api.github.com/repos/<owner>/<repo>/releases/latest`），
  留空则关闭自动更新检查。
- `appVersion`：当前版本，用于与远端版本比较。

> `config.json` 含本机绝对路径，务必加入 `.gitignore`，不要提交；提交 `config.sample.json`。

---

## 注意事项

- 写的是真实的 `%USERPROFILE%\.dsh`（默认 `DSH_HOME`）。回滚会还原 `~/.dsh/profiles/web\package.json`、
  `pnpm-lock.yaml`、`cordis.patch.yml` 与 `~/.dsh\cordis.patch.yml`。
- 首次装新插件包需重启一次 DSH（新增/删除包才重启；切皮肤/换配置走热重载）。
- WebView2 需要运行时；若 `EnsureCoreWebView2Async` 失败，底部状态栏会提示，此时可用「浏览器打开」。
- 图标是用户提供的图片，版权不属于本项目代码（见 LICENSE 末尾 NOTICE）。
