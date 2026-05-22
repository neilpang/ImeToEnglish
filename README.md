# ImeToEnglish

一个常驻系统托盘的 Windows 小工具,自动把输入法切回英文,并在敲键盘时隐藏鼠标。

[![Build & Release](https://github.com/neilpang/ImeToEnglish/actions/workflows/release.yml/badge.svg)](https://github.com/neilpang/ImeToEnglish/actions/workflows/release.yml)

## 功能

- **切窗口时自动切英文**
  焦点切到任何窗口后稍作延迟再检测当前输入法状态,然后:
  - 已经是英文 → 不动
  - 中文输入法 (微软拼音 / 搜狗 / 五笔等) → 仅切到英文模式 (中/英 切换),保留中文输入法,**不**强切到英文键盘布局
  - 其他非英文 (日 / 韩等) → 切到英文 (en-US) 键盘布局
  - TSF 类输入法 (现代微软拼音、搜狗) 不响应 `WM_IME_CONTROL` 时,自动模拟一次 Shift 按键作为兜底切换

- **Chrome / Edge 等浏览器地址栏获焦立刻切英文**
  通过 UI Automation 监听焦点变化。在浏览器地址栏 (omnibox) 获焦时立即切换,但**不会**误触发于网页内的 `<input>` / `<textarea>` (通过判断父链上是否有 Document 元素过滤)。
  支持: chrome / msedge / brave / vivaldi / opera / thorium

- **敲键盘时隐藏鼠标**
  全局低级键盘 / 鼠标钩子。
  - 任何按键按下 → 立即把系统光标替换为透明位图 (覆盖箭头 / I 形 / 等待 / 调整大小 / 手型 等 14 种标准光标)
  - 停止敲击超过 2 秒 → 自动恢复
  - 鼠标移动 (排除程序合成的注入事件) → 立即恢复
  - 边敲边动鼠标 (500ms 内) 不会触发隐藏,避免闪烁

- **开机自启动**
  通过 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册表项实现,无需管理员权限。

- **托盘菜单**
  - 切焦点时切英文 (开/关)
  - 敲键盘时隐藏鼠标 (开/关)
  - 开机自启动 (开/关)
  - Exit

## 下载安装

### 1. 安装 .NET 9 桌面运行时 (~50MB,只装一次)

打开 https://dotnet.microsoft.com/download/dotnet/9.0,在 **".NET Desktop Runtime 9.0.x"** 一栏下载 **Windows x64** 版本并安装。

### 2. 下载本工具

到本仓库的 [Releases](../../releases) 页面下载 `ImeToEnglish-vX.Y.Z-win-x64.exe`,放到一个固定位置 (例如 `C:\Tools\ImeToEnglish\`)。

### 3. 运行

双击 `ImeToEnglish.exe`。任务栏托盘里会出现一个**蓝底白字 "EN"** 图标。

> Win11 默认会折叠新增的托盘图标。点任务栏右下角的 `^` 找到 EN 图标,把它拖到任务栏上让它常驻。

右键托盘图标可以开启/关闭各项功能、设置开机自启动。

## 注意事项

- **挪动 exe 位置后**,需要在托盘菜单里把 *开机自启动* 取消再重新勾上,这样注册表里的路径会更新到新位置。
- **TSF 输入法的中英切换键**默认是 `Shift`。如果你改过 (例如改成 `Ctrl+Space`),"切焦点时切英文" 在中文 IME 模式下可能不生效 — 提 issue 告诉我你的快捷键。
- **隐藏鼠标用的是 `SetSystemCursor`**,会改写整个系统的光标资源,直到本程序调用 `SystemParametersInfo(SPI_SETCURSORS)` 把它们恢复。程序退出 / 崩溃时都会自动恢复 (`AppDomain.ProcessExit` / `UnhandledException` 兜底)。万一遇到鼠标一直不见,**重启资源管理器** (Ctrl+Shift+Esc → 找到 "Windows 资源管理器" → 右键重启) 即可。
- 全局键鼠钩子可能被某些杀毒软件标记。**Microsoft Defender 一般不会**,但企业终端管理软件会拦。

## 从源码构建

需要: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (或更新)

```bash
git clone https://github.com/neilpang/ImeToEnglish.git
cd ImeToEnglish
dotnet publish -c Release -r win-x64 -o publish
```

输出目录 `publish/` 里就一个 `ImeToEnglish.exe` (~230 KB,framework-dependent 单文件,PDB 嵌入),双击即可运行。

## 工作原理简述

| 功能 | 关键 API |
|---|---|
| 切窗口时切英文 | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + `GetKeyboardLayout` + `WM_IME_CONTROL` / `WM_INPUTLANGCHANGEREQUEST` / `SendInput(VK_SHIFT)` |
| 浏览器地址栏检测 | `System.Windows.Automation.AddAutomationFocusChangedEventHandler` + `TreeWalker` |
| 隐藏鼠标 | `SetWindowsHookEx(WH_KEYBOARD_LL / WH_MOUSE_LL)` + `SetSystemCursor(CreateCursor(...))` |
| 恢复鼠标 | `SystemParametersInfo(SPI_SETCURSORS)` |
| 开机自启动 | 写注册表 `HKCU\...\Run\ImeToEnglish` |

整体只有一个 `Program.cs`,代码不到 600 行。

## License

MIT
