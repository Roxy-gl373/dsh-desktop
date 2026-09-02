# 变更记录（CHANGELOG）

本文件记录 `DSh Whale` 启动器自首次发布以来的修改与优化。格式参照 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/)，版本遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

---

## [1.0.1] - 2026-09-02

自初版发布后的第一次集中优化。初版还是“系统托盘 + 开浏览器”形态，本版改为完整的桌面窗口。

### 新增
- **内嵌 Web 窗口**：基于 WebView2 直接在窗口内渲染 DSH Web 界面，不再新开浏览器。
- **多开（新建窗口）**：进程内可再开多个 DSH Web 窗口，仍只占一个托盘图标。
- **单实例**：重复打开只保留一个实例 / 一个托盘图标，并唤醒已运行窗口。
- **安全可视化面板**：展示服务健康、上次好快照、待验证插件与快照列表，可新建快照 / 回滚 / 验证 / 打开日志。
- **通知气泡 + 自动更新**：服务就绪、崩溃回滚、版本更新等事件弹通知；启动时检查 GitHub Release。
- **等比缩放**：默认按窗口宽度等比缩放，可手动 缩小 / 100% / 放大，支持 `Ctrl+滚轮`、`Ctrl ±`。
- **发布打包**：新增 `publish.ps1` 输出自包含 zip（exe + WebView2 SDK + 图标 + 安装脚本），新机器解压后 `install.cmd` 即用。

### 改进
- **高 DPI 清晰度**：声明 Per-Monitor-V2 感知，文字/图片不再因系统缩放发虚。
- **WebView2 缓存归位**：浏览器缓存移至 `state\webview2`，不再污染 `bin`。
- **可移植**：全局按脚本/程序自身位置推导路径；提供 `config.sample.json` 模板，机器相关配置不入仓库。
- **构建脚本**：新增 `build.ps1` 一条命令编译 exe（csc + 自动定位 WebView2 SDK）。

### 修复
- 更新 WebView2 缩放 API 为控件级 `WebView2.ZoomFactor`（适配较旧 SDK）。
- 脚本兼容 PowerShell 5.1（BOM 编码、`Join-Path` 等）。

---

## [1.0.0] - 2026-09-01

初版发布。系统托盘形态：带自定义图标的 **exe 启动器**，自动拉起 `dsh --profile web` 并打开浏览器；实时记录服务日志；含插件安全监控（装插件前自动快照、崩溃自动回滚、自验证）。

- 桌面快捷方式（自定义图标）。
- 实时日志 `logs\dsh-web-<时间戳>.log` 与安全日志。
- 安全模块 `dsh-safety.ps1`：Snapshot / Add / Rollback / Verify / Status / Monitor。
