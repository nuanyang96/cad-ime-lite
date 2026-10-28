# CadImeLite — CAD 输入法自动切换助手

在 AutoCAD 里自动切换中文/英文输入状态：命令行出英文，双击多行文字出中文，离开 CAD 复位中文。

当前版本：**v6.5**（2026-10-28）

## 为什么有这个项目

在 CAD 里画图时，命令行要敲英文命令，编辑文字要敲中文，手动按 Shift 来回切很烦。这个工具根据焦点窗口和 CAD 内部状态自动切换，只在状态变化时动一次，不覆盖用户手按的 Shift。

## 关键技术点（都是实测结论，不是推测）

1. **不能用 `IMC_SETCONVERSIONMODE` 走天下**：微软拼音认这条通道，微信输入法、搜狗拼音不认——接口返回成功，输入法实际不理，而且读回来恒为 0，之前的版本就被"读回 0"骗了。
2. **TSF 全局 compartment 读不到**：外部进程拿不到第三方输入法的中英状态（实测全局表为空、29 个 compartment GUID 全部 `S_FALSE`），除非注入目标进程，风险不接受。
3. **真正可用的第三条路**：`IMC_SETOPENSTATUS`（输入法开/关）。微信输入法、搜狗拼音都吃这一条；微软拼音仍走转换模式。
4. **按输入法身份分流**：`auto` 看 acad.exe 加载了哪个 IME 模块（`wetype` / `sogou` / `py` 等）决定走 open 还是 conv，不靠"设了再读回"试错。
5. **多行文字判据靠 LISP**：AutoCAD 的在位编辑器没有窗口特征，只能由 AutoLISP 命令反应器写注册表 `Line3=CN/IDLE` 供工具读取。

## 版本与验收

| 版本 | 字节 | 状态 |
|---|---|---|
| v5 | 31,232 | 主机微软拼音可用；第三方输入法无效 |
| v6.0–v6.2 | 37,888–40,960 | 迭代过程版本 |
| v6.3 | 41,472 | 微信输入法 3/3、搜狗拼音 3/3、主机 3/3 |
| v6.4 | 42,496 | 单实例保护（`Global\CadImeLite.SingleInstance`） |
| **v6.5** | **44,032** | 修托盘「中/英」图标（运行时绘制），当前交付版 |

验收覆盖两个环境：
- 主机：Windows + AutoCAD 2024 + 微软拼音
- 虚拟机：Windows 10 + AutoCAD 2024 + 微信输入法 3.0.1.12 / 搜狗拼音 16.9.0.5535

详细验收证据见 `交付/验收记录-v6.md` 与 `交付/验收补充-主机与搜狗-20261028.md`。

## 安装

1. 把 `交付/CadImeLite.exe` 和 `交付/rules.ini` 复制到 `%LOCALAPPDATA%\CadImeLite\`
2. 加自启：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 新建 `CadImeLite`，值指向该 exe
3. 启动 exe，托盘出现「中/英」图标
4. LSP 侧需要 `CadImeSwitch.lsp` + `autoload.lsp` 放进 `C:\CadImeSwitch\`，由 CAD 启动组或 `acaddoc.lsp` 加载（这是多行文字判据的来源，缺了它多行文字会被判成英文）

也可以直接双击 `交付/安装-CadImeLite.bat`。

## 回退

`交付/rollback-to-v5.cmd` 可退回 v5（依赖同目录备份 `CadImeLite.exe.bak-v5-20261028`）。

## 配置

`rules.ini`：

```ini
scope=acad          # 只在这些进程里生效，分号分隔
channel=auto        # auto / imm32conv / imm32open / shift / tsf
tsf_guid=           # 留空；第三方输入法不开放时无可用值
```

其余规则见文件内注释（窗口类名表、前缀规则、中文判据优先级等）。

## 目录结构

```
src/v2/Lite.cs                主程序源码（约 78 KB）
test/v2/                      自测产物
交付/CadImeLite.exe           当前交付版 v6.5
交付/rules.ini                配置
交付/变更说明-v*.md           每版改了什么、为什么
交付/验收记录-v*.md           验收证据（日志原文、截图）
交付/验收截图-v6/             托盘图标截图（其余含 CAD 界面的截图仅本机留档，不入仓库）
交付/安装、更新、回退脚本
错误复盘与改进.md             12 条踩坑记录
```

## 已知限制

1. `open` 通道本质是"关输入法"而不是"改模式"：微信/搜狗下用户手按 Shift 不会有反应（微软拼音走 conv 通道不受影响）。
2. `open` 通道偶发设不上（实测有两次 `未生效…降级`），工具会重试/降级，最终结果正确但不绝对稳定。
3. 依赖 LSP 提供多行文字判据，LSP 没加载时多行文字会被判成英文。
4. 第三方输入法名单靠模块名匹配，名单外的输入法会走"未识别"回落，日志里能看出来。
5. 本机禁止 NETLOAD 外部 .NET 程序集，所以只能用 AutoLISP 这条通道。
## 仓库同步说明

本机 git 协议连 github.com 会超时（api.github.com 正常），所以推送走 GitHub API：
	ools/push-to-github.ps1。用法是把 DSH_SECRET_GITHUB_TOKEN_PAT 放进环境变量后直接运行，
它会把本地 HEAD 的全部文件按 git 对象内容推成一次提交，远端与本地逐字节一致。