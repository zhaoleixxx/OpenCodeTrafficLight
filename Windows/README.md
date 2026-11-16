# OpenCode 红绿灯 · Windows 版

用交通灯形态显示 [OpenCode](https://opencode.ai) 工作状态的桌面悬浮窗，Windows 10/11 原生实现。

与 macOS 版同一套灯语、同一套状态判定逻辑，用 C# + WinForms 重写；单文件 exe，不依赖 .NET 运行时安装包（使用系统自带的 .NET Framework 4.8）。

## 灯语

| 灯态 | 含义 |
| --- | --- |
| 🟢 绿灯常亮 | 空闲 / 全部完成 |
| 🟢 绿灯柔闪（1s 周期） | 刚完成、你还没查看（持续 90 秒后转常亮） |
| 🟡 黄灯常亮 | 任务进行中 |
| 🟡 黄灯呼吸（1.6s 周期） | 等你确认（表单 / 提问） |
| 🔴 红灯常亮 | 需要你处理（权限请求） |
| 🔴 红灯快闪（2Hz） | 任务失败（最近 10 分钟内） |
| 🔴 红灯常亮 | 服务未连接 / 读不到状态（悬停文案可区分） |

## 安装

1. 下载发布包（或自行构建），解压。
2. 右键 `install.ps1` → **使用 PowerShell 运行**。
   若被策略拦截，在 PowerShell 里执行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

安装脚本会：

- 把 `OpenCodeTrafficLight.exe` 复制到 `%LOCALAPPDATA%\Programs\OpenCodeTrafficLight\`
- 写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 实现登录自启
- 立即启动悬浮窗

首次运行若出现 SmartScreen 提示（未做代码签名），点「更多信息」→「仍要运行」。

## 使用

- 按住悬浮窗拖动，位置自动记住
- 右键菜单：立即刷新 / 打开 OpenCode / 始终置顶 / 复位位置 / 隐藏 / 退出
- 托盘图标：显示红绿灯、立即刷新、退出
- 悬停查看当前会话标题与详情

「隐藏」只隐藏本次运行：OpenCode 全部退出后重新启动，悬浮窗会恢复自动出现（与 macOS 版一致，暂停最多 15 分钟）。

## 自检 / 调灯

```powershell
# 每 3 秒轮播六种灯效
.\OpenCodeTrafficLight.exe --selftest

# 固定显示某一种
.\OpenCodeTrafficLight.exe --selftest --state=redfast
# 可选值: yellow | pulse | red | redfast | greensoft | green
```

## 构建

需要 .NET SDK 6.0 以上（**macOS / Linux 也能交叉编译**，不需要 Windows 机器）：

```bash
cd Windows
./build.sh          # 或 dotnet build -c Release
```

产物：`dist/OpenCodeTrafficLight.exe`（约 96 KB）。

## 与 macOS 版的差异

| 方面 | macOS | Windows |
| --- | --- | --- |
| 语言 / UI | Swift + AppKit | C# + WinForms |
| 透明实现 | `NSWindow` + `CALayer` | 分层窗口 `UpdateLayeredWindow`（per-pixel alpha） |
| 灯效动画 | Core Animation | 30fps 定时重绘 + 余弦缓动/关键字曲线 |
| 进程守护 | `watcher.sh` + LaunchAgent | 内置 2 秒轮询（`Win32_Process` 命令行判断） |
| 配置 | `UserDefaults` | `%APPDATA%\OpenCodeTrafficLight\settings.json` |
| 常驻入口 | 悬浮窗右键菜单 | 托盘图标 + 悬浮窗右键菜单 |

`service.json` 路径按以下顺序探测，取第一个可用：

1. `%XDG_STATE_HOME%\opencode\service.json`
2. `%USERPROFILE%\.local\state\opencode\service.json`
3. `%LOCALAPPDATA%\opencode\service.json`
4. `%LOCALAPPDATA%\opencode\state\service.json`
5. `%APPDATA%\opencode\service.json`
6. 以上目录里的 `service-*.json`（取最新）

只接受 `127.0.0.1` / `localhost` 回环地址。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

移除程序目录、`%APPDATA%\OpenCodeTrafficLight` 与登录自启项。

## 已知限制

- 未做代码签名：首次运行会有 SmartScreen 提示
- 只识别进程名为 `opencode` / `OpenCode` 且能看到命令行的进程；无法读取命令行时按「未运行」处理，避免后台服务让灯常亮
- 与 macOS 版一样只读本地 API，不上传任何数据

## License

[MIT](../LICENSE)
