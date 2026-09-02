# DSh Whale v1.0.1

DeepSeek Harness 桌面启动器（自包含发布版）。解压后运行 `install.cmd` 即可在本机生成配置与桌面快捷方式。

## 内容
- `bin\Dwhale.exe` 主程序（内嵌 WebView2，含图标）
- WebView2 SDK（Core / WinForms / Loader）
- `scripts\install-shortcut.ps1`（生成 config.json + 桌面快捷方式）、`dsh-safety.ps1`（插件安全模块）
- `install.cmd`（一键安装）、`README.md`、`CHANGELOG.md`、`docs\` 运行截图、`config.sample.json`

## 安装（新机器）
1. 装好 **Node.js**、**pnpm**、**DeepSeek Harness（dsh）本体**、**WebView2 运行时**（Win10/11 自带）、**.NET Framework 4.x**。
2. 解压本 zip 到任意目录。
3. 双击 `install.cmd`（自动检测 node/dsh 路径，生成 `config.json` + 桌面「DSh Web」快捷方式）。
4. 双击桌面快捷方式即用（单实例，一个托盘图标）。

## 功能
- 内嵌 Web 窗口（WebView2）+ 高 DPI 清晰 + 按窗口等比缩放
- 托盘 + 单实例 + 多开（新建窗口）
- 实时日志、插件安全监控（装插件前自动快照、崩溃自动回滚）、安全面板、通知 + 自动更新
- 安全装插件：`.\dsh-safe-add.cmd "<插件spec>"`

> 提示：也可把本仓库地址交给任意 AI，让对方按 README 的安装流程自动帮你完成环境检测与插件安装。
