# CadImeLite 使用说明（v4）

AutoCAD 里自动切换中英文的小工具：**多行文字编辑器 = 中文，命令行 / 面板 = 英文，离开 CAD 自动恢复中文**。只动微软拼音的中/英模式，全程不切键盘布局，其他应用（微信、记事本、浏览器）一次都不碰。

## 一、安装 / 更新 / 停用

| 动作 | 做法 |
|---|---|
| 一键安装（单文件） | 双击 `CadImeLite-一键安装.cmd`（自带 exe 与 rules.ini，无需其他文件），装完自动启动，窗口 4 秒后自关 |
| 首次安装（整目录） | 双击 `安装-CadImeLite.bat`，看到 `[1/2]` `[2/2]` 且无 `FAILED` 即成功；装完托盘常驻 |
| 升级 | 把新的 `CadImeLite.exe`、`rules.ini` 放进本目录，双击 `更新-CadImeLite.bat` |
| 停用（旧工具） | 双击 `停用-CadImeSwitch.bat`：结束进程、删自启、把 `acaddoc.lsp` 改名 `.disabled` |
| 彻底移除本工具 | 托盘右键退出 → 删 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 里的 `CadImeLite` → 删 `%LOCALAPPDATA%\CadImeLite` |

安装位置：`%LOCALAPPDATA%\CadImeLite\`（`CadImeLite.exe`、`rules.ini`、`ime-lite.log`）
自启项：`HKCU\...\Run\CadImeLite`

## 二、依赖（不满足就不保证能用）

1. Windows 10 / 11（自带 .NET Framework 4.x）
2. 输入法必须是**微软拼音**（其他输入法未验证）
3. AutoCAD 进程名必须是 `acad.exe`

## 三、判定规则（`rules.ini`）

优先级从高到低：

1. **绘图区**：焦点控件的父链里含 `MDIClient` → 中文（多行文字在位编辑器挂在这里）
2. **前缀规则**：类名前缀 `HwndWrapper[DefaultDomain` → 英文（CAD 2024 的命令行是 WPF 窗口，类名里带 GUID，每次启动都变，所以只能按前缀匹配）
3. **类名表**：`Edit=0`（行编辑框英文）、`RichEdit20A=1` 等逐条对应

`scope=acad`：前台窗口不是 CAD 时，一次都不触发。

## 四、看日志判断是否正常

日志：`%LOCALAPPDATA%\CadImeLite\ime-lite.log`

| 日志行 | 含义 |
|---|---|
| `规则 11 条, 前缀规则 1 条, scope=acad` | 加载了 v4 配置（没有「前缀规则 1 条」就是旧版没换成功） |
| `判据=绘图区(MDIClient) 目标=中` | 多行文字里切中文 |
| `判据=前缀(HwndWrapper[DefaultDomain) 目标=英` | 命令行里切英文 |
| `离开acad 复位=中文` | 切到别的应用前把中文还回去 |
| `conv 0x0401->0x0000 OK` | 从中文切到英文并回读确认 |

## 五、已知限制

1. CAD 里**所有 WPF 面板**（特性等）会被判成英文，要输中文按一下 Shift；工具只在焦点变化时动一次，不会覆盖手动切换。
2. 绘图区判据对 MDI 区里嵌的 Chromium 窗口（`Chrome_WidgetWin_1`）偏宽，会把那个面板设成中文，无副作用。
3. 焦点在 CAD 内外来回闪时，`离开acad 复位` 可能重复几次（值一样，属重复劳动），日志会变长。
4. 只在「Windows + 微软拼音 + AutoCAD 2024」上验证过；2018 等其他版本需自行验收。

## 六、换机器验收三步

1. 命令行敲 `abc` → 应是**英文字母**（不出拼音候选）
2. 双击多行文字 → 直接**中文**
3. 切到微信/记事本 → 直接中文，不用按 Shift

任一条不符，把 `%LOCALAPPDATA%\CadImeLite\ime-lite.log` 里对应那次的 `cls=` 和 `父链=` 发出来，按实际类名改 `rules.ini`。

## 七、踩过的坑（避免重犯）

- 旧版用「切英文布局 00000409」表达英文，本机没这个布局，程序现场造了一个 → 窗口没有输入法，打不出中文。
- 合成 Shift 无效（`SendInput` 返回 0），别用。
- 跨进程 `ImmGetContext` 返回 0，拿不到上下文就放弃并记日志。
- 批处理一律 CRLF + 纯 ASCII，否则 cmd 把中文当命令执行。

完整复盘见上一级目录的 `错误复盘与改进.md`。
