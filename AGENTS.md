# 番茄钟监督 / PomoCC（面向开发者的笔记）

> 给接手这个仓库的 agent / 开发者看的：约定、踩坑记录、验证方法。
> 面向用户的说明在 [README.md](README.md)，面向最终用户的图文手册在 [docs/使用说明.txt](docs/使用说明.txt)。

一个单文件 Windows 桌面小程序：番茄钟 + 监督程序进程监测 + 自动给监督人发告状邮件。

- 当前版本：**0.1**（`App.Version` + `src/AssemblyInfo.cs` + README 三处要一起改）
- 交付物：[dist/番茄钟监督.exe](dist/番茄钟监督.exe)（约 150 KB）+ [dist/番茄钟监督.exe.config](dist/番茄钟监督.exe.config)
- 用户文档：[docs/使用说明.txt](docs/使用说明.txt)（构建时自动复制进 `dist/`）
- 开源协议：Apache-2.0（[LICENSE](LICENSE)）
- 需求来源：用户要求「有界面、双击能用的 Windows 小软件」，行为规则经用户确认并在后续反馈中细化：
  1. 只有「没坚持完」或「某个程序超过它自己的规则时长」才发邮件，正常完成不发；
  2. 开机自启 + 托盘常驻 + 退出需密码；
  3. 邮件内容包含「具体哪个程序、玩了多久」和「计划 vs 实际专注时长」；
  4. 监督目标以**表格**呈现，能从正在运行的程序里点选，不要手敲进程名；
  5. 表格里要有一列可改的**名称**（很多 exe 自报的名字只是 `game` 这种代称），邮件用这个名字；
  6. 任何一行都不能因为高度不够被裁掉；
  7. 界面要现代，不能像十多年前的软件；文本框不要挂滚轮事件。

## 为什么不做成 DSH 插件

用户最初问的是「DSH 插件能不能实现」，但澄清后要的是**独立 Windows 软件**。
两者都可行（DSH 侧有 `subprocess` / `timer` / `commands` / `storageDomain` / `credentials`
服务和 `conversation.composer.dock` 等插槽），但独立 exe 不用开着 DSH、
双击就能用、也不会因为 DSH 升级而失效。因此选择独立 exe。

## 技术选型

| 选择 | 原因 |
|---|---|
| C# WinForms + 系统自带 `csc.exe` | 目标机器只需 .NET Framework 4.x（Win10/11 自带），**零安装、零依赖**；产物约 117 KB |
| 自建视觉层（`Theme.cs` + `Ui.cs`） | 默认 WinForms 控件是十多年前的观感。圆角卡片、扁平圆角按钮、环形倒计时、扁平表格全部自绘，不引第三方 UI 库 |
| 自己实现 SMTP（`SmtpTransport`） | .NET 自带的 `SmtpClient` **不支持 465 端口隐式 SSL**，而国内邮箱大量使用 465 |
| 自己实现 DPI 缩放（`Dpi.cs`） | 框架的自动缩放在实测中被叠加了两次，结果不可预测（见下文） |
| `Card` / 段落全部用 AutoSize 行 | 结构上保证「行高由内容决定」，不可能出现最后一行被裁掉 |
| 不用 `NumericUpDown`，自己写 `NumberBox` | `NumericUpDown` 会抢鼠标滚轮改数值；全程序**没有任何滚轮事件处理器** |
| DPAPI（`ProtectedData`）保护授权码/API Key | 加密绑定当前 Windows 用户，配置拷走也读不出密钥 |
| PBKDF2-SHA1 ×60000 + 随机盐 | 退出/设置密码只存散列，不存明文 |
| `JavaScriptSerializer` | .NET Framework 自带，不引第三方 JSON 库 |

## 目录结构

```
PomodoroSupervisor/
├─ build.ps1                 编译脚本（纯 ASCII；中文 exe 名从 exe-name.txt 显式按 UTF-8 读）
├─ exe-name.txt              产物文件名（中文放在这里，避免脚本编码问题）
├─ src/
│  ├─ App.cs                 入口、单实例互斥、命令行模式、开机自启注册表
│  ├─ Theme.cs               配色、字体、圆角常量（设计像素基准 96 DPI）
│  ├─ Ui.cs                  视觉控件库：FlatButton / Card / InputBox / NumberBox / TimerDial / 表格样式
│  ├─ Dpi.cs                 按真实 DPI 缩放控件树（含跨显示器 DPI 变化）
│  ├─ Settings.cs            设置模型、WatchRule 规则表（含「名称」）、DPAPI、密码散列、校验
│  ├─ Store.cs               数据目录、设置/统计/告状记录持久化、日志、旧配置迁移
│  ├─ Monitor.cs             按规则枚举受监视进程 + 启动时间 + 命中的规则
│  ├─ AppCatalog.cs          枚举本机正在运行的程序（图标/描述/窗口标题）
│  ├─ Supervisor.cs          专注状态机、采样累积、逐条规则判定（核心逻辑，与界面解耦）
│  ├─ Mailer.cs              邮件主题/正文生成、发送分发
│  ├─ SmtpTransport.cs       自带 SMTP 客户端（465 隐式 SSL / 587 STARTTLS）
│  ├─ HttpSender.cs          Resend / SendGrid / Brevo / 自定义 HTTP 四种通道
│  ├─ MainForm.cs            主窗口：环形倒计时 + 卡片 + 托盘 + 密码门禁
│  ├─ SettingsForm.cs        设置窗口：左侧导航 + 三页 + 规则表格
│  ├─ AppPickerForm.cs       「从正在运行的程序里选」选择窗口
│  ├─ Dialogs.cs             无边框现代弹窗（密码/预览）、告状记录窗口
│  ├─ app.manifest           DPI 感知声明（PerMonitorV2）
│  ├─ app.config             刻意留空，仅用于覆盖旧版 exe.config（见下文）
│  └─ SelfTest.cs            自检 / 界面冒烟 / 真实模式 / DPI / 配置兼容 / 发信测试
├─ tools/fake-smtp.mjs       端到端测试用的假 SMTP 服务器（Node）
├─ docs/
│  ├─ 使用说明.txt            面向最终用户的说明（构建时复制到 dist/）
│  └─ images/                README 用的 4 张截图（入库，别被 .gitignore 掉）
├─ tests/                    自检输出（日志/截图），**不入库**，只留 .gitkeep
└─ dist/                     构建产物，**不入库**（发布走 GitHub Releases）
   ├─ 番茄钟监督.exe
   ├─ 番茄钟监督.exe.config
   └─ 使用说明.txt            ← 由 build.ps1 从 docs/ 复制
```

### 入库范围（.gitignore 的取舍）

| 入库 | 理由 |
|---|---|
| `src/`、`build.ps1`、`exe-name.txt`、`docs/`、`tools/fake-smtp.mjs` | 源码、构建脚本、用户文档、测试工具 |
| `README.md`（给用户）、`AGENTS.md`（给开发者）、`LICENSE` | 仓库门面与开发笔记 |
| 不入库 `dist/` | 构建产物，二进制走 Releases |
| 不入库 `tests/*` | 只有运行产物（日志、截图、假 SMTP 抓包）；源码级自检在 `src/SelfTest.cs` |
| 不入库 `config.json` / `stats.json` / `history.jsonl` / `app.log` | **隐私**：含告状记录全文、密码散列、DPAPI 加密的邮箱授权码 |

注意：`*.exe` / `*.dll` 在 `.gitignore` 里是全局兜底，所以任何新加的二进制参考库都要
`git add -f` 并说明原因。`docs/images/*.png` 是刻意入库的，不要加 `*.png` 这种全局图片规则。

## 构建

```powershell
pwsh -File .\build.ps1
```

`build.ps1` 保持纯 ASCII：Windows PowerShell 会把无 BOM 的脚本按 ANSI 解码，
脚本里直接写中文会导致乱码和语法错误。中文产物名改为从 `exe-name.txt` 用
`Get-Content -Encoding UTF8` 读取。

## 监督规则表

表格四列：**名称（可改）| 软件 | 规则时长（分钟）| 操作**。

- `软件` 列 = 程序自报的名字 + 进程名（例如 `game · game.exe`），只读，用于认人。
- `名称` 列 = 用户自己起的名字，**默认等于 `软件` 列内容**，双击即可改。
  邮件和界面都用它；若名字里没带进程名，会自动补成 `奶龙（game.exe）`，方便监督人核对。
- `规则时长` = 这个程序在专注期间允许累计运行多少分钟，逐行可改。
- `操作` = 删除（表格里的链接，或选中后点「删除选中」）。

添加程序有两条路：从正在运行的程序里多选（带图标和真实名字，可搜索，
还能切到后台进程），或者直接选 exe 文件。

## 高 DPI（踩坑记录，改动前请先读）

**症状**：用户在 125% 缩放的 2560x1440 屏幕上看着"很糊"。

**诊断**（`--dpicheck` 实测）：系统 DPI = 120；程序原本没有 DPI 感知声明 →
Windows 把整个窗口当位图拉伸 1.25 倍 → 字模糊。

**修复过程中实测到的四个坑**：

1. 只声明 DPI 感知不够：若同时开启 `EnableWindowsFormsHighDpiAutoResizing`，
   框架会自己再缩放一次，实测 500x400 的窗口变成 786x637。
2. 只用 `AutoScaleMode.Dpi` + `AutoScaleDimensions=(96,96)` 也不行：框架会把
   `AutoScaleDimensions` 改写成当前 DPI（120），缩放系数被算成 1.0，**完全没缩放**。
3. 自己缩放时不能碰 `Form.MinimumSize` / `MaximumSize`（它们是窗口尺寸，框架会再换算）。
4. 自己对控件树做缩放时，**窗体和普通控件必须分开处理**：窗体只缩放 `ClientSize`，
   否则会把窗口尺寸再乘一次系数。这个坑的实际表现是"缩放被叠加两次"，
   只有把调用栈打出来才定位到。

**最终方案**：`Dpi.cs` 按 `GetDpiForSystem()/96` 显式缩放整棵控件树，字号保持 point
（本身随 DPI 正确渲染，不能重复乘系数），并且：

- 只缩放 `Form.ClientSize`，不碰窗口尺寸与屏幕坐标；
- `Dock=None` 缩 Size+Location；`Dock=Top/Bottom` 缩 Height；`Left/Right` 缩 Width；
- `TableLayoutPanel` 的 `Absolute` 行列、`ListView`/`DataGridView` 列宽、`ImageList` 尺寸一并缩放；
- 用 `Dictionary<Form,float>` 记录已缩放系数，忽略窗口创建时框架补发的重复 DPI 事件；
- `app.config` 刻意留空（不启用 WinForms 的高 DPI 开关），避免与自己的缩放叠加。

## 自绘控件的铁律（踩过大坑）

**任何自绘控件都必须自己擦背景。** 表现是：按钮上出现「两层文字」。

成因：`FlatStyle.Flat` 会把按钮标记为 `Opaque`，框架于是不再替它填背景；
为了不闪又开了 `OptimizedDoubleBuffer + AllPaintingInWmPaint`；
而「幽灵按钮」在不悬停时填充色是 `Transparent`，等于除文字之外什么都不画。
结果每一帧的文字都留在双缓冲位图里叠起来 —— 用户看到的就是按钮上摞了两层字。

现在 `FlatButton` / `Card` / `TimerDial` 的 `OnPaint` 第一件事都是
`FillRectangle(ClientRectangle, Ui.EffectiveBackColor(this))`，
其中 `EffectiveBackColor` 向上找到第一个不透明的父容器取色。

这个 bug 靠「能创建窗口、不溢出」是抓不到的，所以专门加了 `--rendertest`：
把控件和整窗真实渲染到位图后**数像素**。

| 检查 | 抓什么 |
|---|---|
| `render-*-background-cleared` | 按钮四角必须是父容器底色（背景被擦干净） |
| `render-*-fill` / `render-*-text` | 填充色正确、文字确实画出来了 |
| `render-is-stable` | 连续两次渲染逐像素一致（有残留就会不一致） |
| `render-*-no-unpainted-area` | 整窗渲染后没有纯黑未绘制区域 |
| `render-*-has-accent` | 主按钮的主色像素数量达标 |
| `*-text-not-clipped` | 标签文字按可用尺寸换行后不会超出高度（被切掉） |

## 布局为什么不会再裁切

所有卡片内容都放在「1 列 + `RowStyle(AutoSize)`」的 `TableLayoutPanel` 里，
卡片本身用 `Dock=Fill`，行高由内容算出来 —— 结构上不存在"高度写小了"的可能。

`--dpicheck` 会对**四个窗口**（主窗口 / 设置 / 添加程序 / 告状记录）递归检查
每个控件是否超出父容器，**容差只有 1px**（纯四舍五入误差）。
历史上正是这个检查抓出了：导航按钮比容器宽 10px、卡片高度只有内容的三分之一、
添加程序窗口表头行溢出 71px。

## 测试

```powershell
# 0a) 截图：把每个窗口渲染成 PNG（交付前必须逐张看过）
.\dist\番茄钟监督.exe --shot .\tests\shots

# 0b) 渲染自检：把按钮和整窗画进位图数像素，抓"文字重影/背景没擦/未绘制区域/文字被截断"
.\dist\番茄钟监督.exe --rendertest .\tests\rendertest.log

# 1) DPI 与布局：感知级别、系统 DPI、窗体缩放、字体渲染、四个窗口是否溢出、
#    三个设置页签逐个检查、文字是否被截断
.\dist\番茄钟监督.exe --dpicheck .\tests\dpicheck.log

# 2) 逻辑自检：配置往返、旧配置迁移、DPAPI、密码、规则校验、逐条规则阈值、
#    进程枚举与启动时间、程序目录枚举、状态机、邮件正文、名称回退规则
.\dist\番茄钟监督.exe --selftest .\tests\selftest.log

# 3) 界面冒烟：真实创建四个窗口并跑计时器，断言表格是 4 列且列名正确
.\dist\番茄钟监督.exe --smoketest .\tests\smoketest.log

# 4) 真实模式：注册表自启往返、托盘图标、单实例互斥、密码与规则持久化、密钥不明文落盘
.\dist\番茄钟监督.exe --realsmoke .\tests\realsmoke.log

# 5) 旧配置兼容：用临时目录加载指定 config.json，确认解析成功且密码散列没被动过
.\dist\番茄钟监督.exe --loadcheck .\tests\loadcheck.log "%APPDATA%\PomodoroSupervisor\config.json"

# 6) SMTP 连通性与加密握手（不登录、不发信）
.\dist\番茄钟监督.exe --smtp-check .\tests\probe-qq587.log smtp.qq.com 587

# 7) 端到端发信：起本地假 SMTP，让程序按真实流程投递并核对报文
node .\tools\fake-smtp.mjs 2560 .\tests\e2e-message.txt .\tests\e2e-session.txt .\tests\e2e-live.log
.\dist\番茄钟监督.exe --mail-test .\tests\mailtest.log 127.0.0.1 2560
```

### 已验证结果（7 项 exit code 全 0）

- `--dpicheck`：PerMonitorV2 生效；主窗口 ClientSize 575x725（= 460x580 × 1.25）；
  四个窗口布局零溢出。
- `--selftest`：18 项全通过，含旧配置迁移、逐条规则阈值边界、
  「名称」默认值等于「软件」列、自定义名称进入邮件正文。
- `--smoketest`：8 项全通过；设置窗口的规则表格为 **4 列 × 3 行**，
  列名「名称 / 软件 / 规则时长（分钟） / 操作」。
- `--realsmoke`：8 项全通过，注册表自启测试后恢复原状。
- `--loadcheck`：直接加载用户真实的 `config.json`，解析成功、密码散列原样保留。
- `--smtp-check`：`smtp.qq.com:587` 等 STARTTLS / 隐式 SSL 握手成功。
- `--mail-test`：完整 SMTP 会话（EHLO → AUTH LOGIN → MAIL FROM → RCPT TO → DATA → `.`），
  服务端收到的报文解码后中文主题与正文完全正确。

### 测试抓到的真实缺陷（都已修）

1. **按钮边缘的黑色边框（用户上报）**：`FlatButton` 原本继承 `Button`，
   **Win32 的 Button 窗口类会自己画一圈三维边框**（上/左深、下/右浅），与自绘叠加；
   且 GDI+ 的 `Pen` 会忽略 alpha，`new Pen(Color.Empty)`（我本想表示"不画边框"）
   实际是**不透明黑笔**，于是每个按钮都被描了一圈黑线。
   现象与用户的描述完全吻合：第一次 hover 前最明显。
   修法：① `FlatButton` 改为继承 `Control` 并自行实现点击/键盘/`IButtonControl` 语义，
   彻底摆脱基类绘制；② 用 `Ui.IsVisibleColor` 挡住 `Color.Empty` / 全透明色。
   验证：修复前按钮最外圈有 31 个暗色中性像素（`#4A4849@0,0`），修复后为 0；
   **屏幕实拍**复核：按钮周边 4px 环带内暗色像素 = 0（此前是 `(122,122,123)` 灰线与 `(174,98,92)` 暗红线）。
2. **邮件预览窗口溢出（用户上报）**：`TextForm` 先清空了 `Body.Controls`（把标题行一起删了），
   又用已被清空的 `Stack.PreferredSize` 去 `FitToContent`，窗口被算成极小 → 正文大量看不到。
   现在 `ModernDialog` 拆出「标题行 + 内容宿主」，撑满型内容加进 `ContentHost`。
3. **设置页大部分内容看不到（用户上报）**：日志卡片每一行都被撑成 100px 高 ——
   `InputBox` 是 `Panel` 子类，`Panel` 默认高度就是 100，而 `AutoSize=false` 时
   WinForms 把"当前尺寸"当作期望尺寸；卡片 100px 高、内容需要 554px，全被裁掉。
   现在复合控件都有明确设计高度，卡片按内容长高，页面开 `AutoScroll` 兜底。
4. **编辑器有洞**：`--dpicheck` 当时全绿 —— 只检查了默认显示的「监督名单」页，
   出问题的「邮件设置」页是隐藏的。现在三个页签都会逐个切换后检查。
5. **弹窗右/下边缘被切（用户上报）**：`ModernDialog` 忘了 `Dpi.ApplyTo`；
   `Card` 描边用整块矩形，右/下边框落在控件边界外被裁。
6. **按钮「两层文字」**：同 1 的根因（基类绘制 + 自绘叠加）。`--rendertest` 用像素证据定位：
   修复前按钮四角是 `#000000`，修复后为 `#FFFFFF`。
7. **点空白处光标不消失（用户上报）**：WinForms 里点标签/面板不会移走焦点。
   现在 `Ui.DismissFocusOnBlankClick` 在点击非输入控件时摘掉焦点，输入框也设了 `HideSelection`。
8. **双击时间改时长（用户需求）**：编辑条做成**浮层**——出现/消失时圆环与卡片尺寸完全不变
   （第一版会缩小圆环并顶出内容，被 `mainform-edit-mode-layout` 抓出后改成浮层）。
9. **窗口能被缩到装不下内容**：`MinimumSize` 之前小于设计尺寸。
10. **计算属性被写进配置**：`SoftwareText` / `DisplayLabel` / `ModeDisplay` 是只读计算属性，
    JavaScriptSerializer 会把它们也序列化。已加 `[ScriptIgnore]`，配置文件只保留真正的设置。
11. **SMTP 漏发 DATA 结束行 `.`**：真实服务器上会一直等到 30 秒超时。
12. **DPI 缩放被叠加两次**：`Dpi.Walk` 重构时残留了重复的缩放块。
13. **每次启动都写注册表 Run 键（用户上报：电脑管家频繁弹「企图开机自启动」）**：
    `AutoStart.Apply` 原来无条件 `SetValue`，安全软件把**每一次 Run 键写入**都当成
    「某程序企图开机自启动」而弹窗 —— 每次开机、每次启动程序都弹。
    现在先读后写、值没变一个字节都不写，并返回"是否真的动了注册表"；
    断言 `autostart-idempotent`：首次写入=true、重复设置=false、取消=true、重复取消=false。
14. **自检改写了用户真实的自启项（我自己闯的祸）**：`--realsmoke` 直接对
    `PomodoroSupervisor` 这个真实值名做写入/删除测试，还用
    `Apply(startWasEnabled)` 恢复 —— 而 `startWasEnabled` 是在切到测试值名之后读的，
    永远是 false，于是把用户的自启项删掉了。现在改用测试专用值名
    `PomodoroSupervisorSelfTest`，并用 `ReadReal()/RestoreReal()` **原样**恢复。
15. **自检在 `%TEMP%` 里堆了 177 个临时数据目录**：现在所有临时目录统一登记，
    `Main` 的 `finally` 里统一删除（要保留用 `POMODORO_KEEP_TEMP=1`）。

### 开发期残留自查（每次交付前跑一遍）

最容易留下的"垃圾"是**注册表自启项**和**临时目录**。清单：

| 落点 | 检查 |
|---|---|
| `HKCU\…\CurrentVersion\Run` / `RunOnce` | 只应有 **一条** `PomodoroSupervisor`，且指向正式 exe |
| `HKLM\…\Run`（含 WOW6432Node） | 应无本项目条目 |
| 启动文件夹（用户 + 公共） | 应无本项目快捷方式 |
| 计划任务 / 服务 | 应无任何指向本项目的条目 |
| `%TEMP%\pomodoro-*` | 自检的临时数据目录，跑完必须为 **0** |
| 工作区里的 exe | 只应有 `dist\番茄钟监督.exe`（dev 版用完即删） |

### 教训：离屏渲染 ≠ 真实渲染

`DrawToBitmap`（WM_PRINT）与真实屏幕绘制**不一致**，这一轮被它坑了两次：

- 实拍比离屏多出 1px 的 `(236,237,240)` 缝线；
- **编辑浮层在离屏渲染里根本不出现**，但屏幕上正常；
- 反而是 `DrawToBitmap` 会带上 `PRF_NONCLIENT`，让按钮多出一圈三维边框，
  把"到底有没有黑边"这件事搅浑。

所以验证分两层：

- 结构性检查用 `--shot` / `--rendertest`（改用 `WM_PRINT + PRF_CLIENT`，不带非客户区）/ `--dpicheck`；
- 交付前必须做一次**屏幕实拍**：`POMODORO_DATA_DIR=<临时目录> POMODORO_NO_REGISTRY=1 番茄钟监督.exe --opensettings|--editminutes`，
  再用 Pillow `ImageGrab` 截取窗口并逐像素采样（扫描时要用"暗且中性灰"的判据，
  而不是单纯阈值 —— 黑边像素的亮度和往往在 360 上下，用 sum<300 会漏掉）。

### 教训

前面几轮我一直用"能创建窗口 / 不溢出"当验证通过的标准，**但这个标准量不到肉眼可见的坏**：
内容被裁在卡片里、隐藏页签没被检查、描边画在边界外 —— 三种情况下断言全绿，界面却是坏的。
现在补了两件真正能"看见"的工具：

- `--shot`：把每个窗口渲染成 PNG，我逐张看过再交付；
- `--rendertest`：把按钮/整窗渲染到位图后数像素（背景是否擦净、有没有未绘制区域、文字有没有被截断）。

## 关键实现细节

- **规则表**：每条 `WatchRule` = 进程名 + 程序自报名 + 用户名称 + 路径 + 规则时长 + 是否启用。
  旧版配置里的「一串进程名」会在加载时自动迁移（时长沿用旧的全局值）。
- **偷玩时长按采样累加**：每 `SampleSeconds`（默认 5 秒）枚举一次命中规则的进程，
  **按每条规则各自的时长**判定，符合表格逐行配置的语义。
- **睡眠不算专注**：每秒 tick 检测实际间隔，超过 15 秒视为睡眠/挂起，该段不计入专注时长。
- **一段专注最多一封邮件**：`FocusSession.Reported` 保证「超时」告状后，即使随后点「放弃」也不重复发信。
- **发送不阻塞界面**：邮件在线程池发送，结果通过事件回到界面（`BeginInvoke`）并用托盘气泡提示；
  失败时邮件全文保留在记录里，可手动补发。
- **密码门禁**：退出、打开设置都需要密码；首运行引导设置（取消则用默认 `123456` 并明确告知）。
- **选择程序列表**：默认只列有窗口的程序，可切换到全部后台进程；
  图标只给有窗口的程序提取，避免为几百个进程读文件拖慢列表。
