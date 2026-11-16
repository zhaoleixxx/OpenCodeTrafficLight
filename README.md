# OpenCode 红绿灯

一个常驻在桌面上的交通灯悬浮窗，用「绿 / 黄 / 红」实时显示 [OpenCode](https://opencode.ai) 的工作状态——不用切回终端，余光一瞥就知道它是在干活、在等你确认，还是已经完成。

支持 **macOS**（Swift + AppKit）与 **Windows 10/11**（C# + WinForms），两端同一套灯语与状态判定。

- macOS：原生 Swift，无第三方依赖，约 800 行
- Windows：单文件 exe（96 KB），用系统自带 .NET Framework 4.8，无需安装运行时
- 只读 OpenCode 本地服务 API，不修改任何数据

> Windows 版安装、构建与差异说明见 [Windows/README.md](Windows/README.md)。

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

闪烁只有三种，且各有明确含义：

- 黄呼吸：等你，不紧急
- 红快闪：故障，最醒目
- 绿柔闪：完成未读，温和提醒

「完成未查看」的判定依据是 OpenCode 的 `time.viewed < time.idle` —— 你打开会话看过之后，绿灯就停止闪烁。

## 安装

### 方式一：下载预编译包

到 [Releases](https://github.com/zhaoleixxx/OpenCodeTrafficLight/releases/latest) 下载 `OpenCodeTrafficLight-2.0.0-macos.zip`，解压后双击 `Install.command`。

这是通用二进制（Apple Silicon + Intel），无需 Xcode。包内已附带 `watcher.sh`。

首次打开若提示「无法打开，因为无法验证开发者」（未做 Apple 公证），右键点击 `Install.command` → 选择「打开」→ 再点「打开」。

### 方式二：从源码构建

需要 macOS 13+ 与 Xcode Command Line Tools（`xcode-select --install`）。

```bash
git clone https://github.com/zhaoleixxx/OpenCodeTrafficLight.git
cd OpenCodeTrafficLight
./build.sh
```

然后双击 `Install.command`，同样的 Gatekeeper 提示处理方式。

安装脚本会：

1. 把 `OpenCodeTrafficLight.app` 复制到 `~/Applications`
2. 把 `watcher.sh` 装到 `~/Library/Application Support/OpenCodeTrafficLight/`
3. 注册 LaunchAgent `local.opencode.trafficlight`（登录自启，`KeepAlive`）

之后在终端里运行 `opencode`，悬浮窗会自动出现；退出 OpenCode 后自动消失。

## 使用

- 按住悬浮窗任意位置拖动，位置自动记住
- 右键菜单：立即刷新 / 打开 OpenCode / 始终置顶 / 复位位置 / 退出
- 选择「退出」后，本次 OpenCode 运行期间不再自动出现（15 分钟后恢复自动弹出）；重新打开 OpenCode 也会恢复
- 鼠标悬停可查看当前会话标题与详情

## 自检 / 调灯

```bash
# 每 3 秒轮播六种灯效
./build/OpenCodeTrafficLight --selftest

# 固定显示某一种
./build/OpenCodeTrafficLight --selftest --state=redfast
# 可选值: yellow | pulse | red | redfast | greensoft | green
```

## 工作原理

```
opencode (终端)  ──启动──▶  watcher.sh  ──拉起──▶  OpenCodeTrafficLight.app
     │                    (LaunchAgent, 每 2s)          │
     │                                                  │ 每 1.2s 轮询
     └────────── service.json（含端口与密码）◀──────────┘
                                │
                                ▼
                  http://127.0.0.1:<port>/api/...
```

`watcher.sh` 每 2 秒检查是否存在**挂在真实 TTY 上的交互式 `opencode` CLI**：

- 有则启动悬浮窗；全部关闭则结束悬浮窗并清理退出标记
- 忽略 `opencode serve --service`、无 TTY 的 `opencode run` 以及桌面版进程

悬浮窗从 `~/.local/state/opencode/service.json` 读取本地服务端口与密码（Basic Auth），随后轮询接口：

| 接口 | 用途 |
| --- | --- |
| `GET /api/session/active` | 正在运行的会话 |
| `GET /api/session?limit=25&order=desc` | 最近会话（含 `outcome` / `time.viewed`） |
| `GET /api/session/{id}/permission` | 待处理权限请求 |
| `GET /api/session/{id}/form` | 待确认表单（再查详情确认 pending） |

所有请求只走 `127.0.0.1`，不上传任何数据。

## 目录结构

```
.
├── Sources/main.swift          # macOS：全部 App 源码（状态机 + 悬浮窗 + HTTP 客户端）
├── Info.plist                  # macOS：Bundle 配置（LSUIElement = true，无 Dock 图标）
├── build.sh                    # macOS：编译 arm64 / x86_64 并 lipo 合并、ad-hoc 签名
├── Install.command             # macOS：安装到 ~/Applications + 注册 LaunchAgent
├── Uninstall.command           # macOS：卸载
├── watcher.sh                  # macOS：守护脚本（每 2s 探测 OpenCode，控制悬浮窗生命周期）
└── Windows/
    ├── Program.cs              # 入口、单实例、--selftest 参数
    ├── Monitor.cs              # 状态判定（与 macOS 版逐条对应）
    ├── Api.cs                  # 本地 REST 客户端（Basic Auth，带超时）
    ├── ServiceConfig.cs        # service.json 多路径探测
    ├── OpenCodeProcess.cs      # 进程守护（替代 watcher.sh）
    ├── TrafficLightWindow.cs   # 分层窗口 + GDI+ 绘制 + 灯效动画 + 拖拽/菜单
    ├── App.cs                  # 组合根：托盘、轮询、自检、位置记忆
    ├── Json.cs                 # 极简 JSON 解析/序列化（无第三方依赖）
    ├── Settings.cs             # %APPDATA% 配置读写
    ├── build.sh                # 交叉编译脚本（macOS/Linux 也可）
    ├── install.ps1             # 安装 + 登录自启
    └── uninstall.ps1           # 卸载
```

## 卸载

双击 `Uninstall.command`，会移除 App、Support 目录与 LaunchAgent。

## 日志

```bash
tail -f ~/Library/Logs/OpenCodeTrafficLight.watcher.log
```

## License

[MIT](LICENSE)
