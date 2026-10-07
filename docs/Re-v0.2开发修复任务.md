# PomoCC Re-v0.2 开发修复任务

> **状态：已完成**（第 1~5 组全部实现并通过测试；完整回归见 `tests/rev02-*.log`，--selftest 61 项、--smoketest 22 项、其余模式全部 exit 0）。

## 目标

本轮针对 v0.2 复审中仍存在的问题进行收尾修复。目标是让监督逻辑、配置更新、数据迁移、手动发信和网络协议行为真正达到可验收状态。

本轮只处理以下问题：

- 当前 session 的配置快照不完整，监督规则仍可能被中途替换；
- 设置、统计和后台监督线程之间存在并发读写风险；
- 数据迁移和配置保存不是可靠的原子操作；
- 主窗口的测试发信仍可能阻塞 UI；
- 长时间暂停后的采样边界需要明确并补测；
- SMTP 状态码判断不严格；
- HTTP 发信未明确禁止自动重定向。

除非任务明确要求，不要顺手重构无关 UI、修改产品文案或扩展功能。

## 当前复审结论

以下改动已经基本有效，本轮不要回退：

- 监督核心已经使用后台循环和单调时钟，UI Timer 只负责刷新界面；
- 睡眠/恢复事件已经接入；
- 进程实例使用 `PID + StartTime` 识别；
- 同一个 exe 的多个实例按 exe 汇总规则时间，每个实例单独记录；
- `Running`、`Completed`、`Abandoned`、`Violated` 状态已经建立；
- CRLF 邮箱校验已经加入；
- 自定义 HTTP 地址已经限制为 HTTPS，或本机 HTTP；
- API Key 使用请求头，不要改回查询参数；
- 历史 JSONL 已有进程内写锁；
- 设置窗口、SMTP 连接测试和历史记录重发已经使用异步发信外壳。

## 执行原则

1. 先补充失败测试，再修改实现。
2. 每组完成后单独运行相关测试，再运行完整回归测试。
3. 后台线程不得直接访问 WinForms 控件。
4. UI 不得直接修改后台线程正在使用的 `Session`、`Stats`、`Settings` 或规则集合。
5. 不要用吞异常的方式表示关键步骤成功。关键写入失败必须能被调用方识别。
6. 不要把“文件存在”当成“文件内容已经完整”。
7. 保持 .NET Framework 4.x 和当前单文件构建方式兼容。

---

## 第 0 组：建立修复基线

### 目标

确认当前源码、构建和测试结果，避免把环境问题误认为代码问题。

### 任务

- 使用 `build.ps1` 从当前源码重新构建。
- 运行 `--selftest`、`--smoketest`、`--dpicheck`、`--rendertest`。
- 测试全部使用临时数据目录，不得修改真实 `%APPDATA%\PomoCC`。
- 如果 DPAPI 在测试环境中不可用，要明确记录为环境阻塞，不要为了测试修改生产加密逻辑。

### 验收标准

- 构建退出码为 0。
- 测试命令和退出码被记录。
- 能区分“测试失败”和“测试环境无法加载 DPAPI 用户配置”。

---

## 第 1 组：固定 session 配置并统一线程同步（最高优先级）

### 涉及文件

- `src/Supervisor.cs`
- `src/MainForm.cs`
- `src/SettingsForm.cs`
- `src/SelfTest.cs`

### 当前问题

`StartFocus()` 虽然复制了 `Settings`，但 `DoSampleLocked()` 仍然使用全局 `watchRules`。设置窗口保存后会调用 `RefreshWatchList()`，导致当前 session 可能突然切换到新规则。

此外，`MainForm.ApplySettings()` 直接替换 `sup.Settings` 和 `sup.Stats`，没有通过 `Supervisor` 的统一锁处理。后台 Tick、统计保存和 UI 设置应用可能互相覆盖数据。

### 实现要求

1. 在 `FocusSession` 中增加本段专用的活动规则快照。

   - 在 `StartFocus()` 中从 session 的设置快照生成活动规则；
   - 当前 session 的进程扫描、规则限制、程序名称和显示数量都使用这份规则；
   - session 开始后，设置窗口修改规则不得影响当前 session；
   - 下一段 session 才使用新规则。

2. 增加统一的设置应用入口，例如 `Supervisor.ApplySettings(Settings settings)`。

   - 设置替换、规则刷新、必要的统计处理必须在 `gate` 内完成；
   - UI 不得再直接写 `sup.Settings` 或 `sup.Stats`；
   - 专注期间不能用刚从磁盘重新加载的 `Stats` 覆盖后台正在累计的统计；
   - 如果必须刷新当天统计，应合并计数，而不是直接丢弃当前内存中的计数。

3. 明确设置修改的产品行为：专注中允许修改，但下一段才生效。

   - 专注中仍然可以打开设置窗口并编辑规则；
   - 点击保存后，当前 session 继续使用开始时的配置；
   - 保存成功后提示：`本次专注继续使用原设置，新设置将在下一段专注开始时生效。`；
   - 用户取消保存时不显示这条提示；
   - 下一段 session 必须使用刚保存的新设置；
   - 不要把设置窗口锁死，也不要让新规则立即切换当前 session。

4. 对外提供只读快照或复制对象。

   - UI 读取状态继续使用 `Supervisor.Read()`；
   - UI 不得直接遍历后台正在修改的 `Session.Watched`、`Session.Events` 或 `Stats`；
   - `WatchRules` 返回副本，不返回内部集合。

5. 保持事件回调在锁外执行。

### 必须新增的测试

- `session-rules-use-snapshot`：开始 session 后修改全局规则，当前 session 仍只扫描原规则。
- `settings-apply-during-session`：专注中保存新设置，不改变当前 session 的规则和计划时间。
- `settings-apply-shows-next-session-notice`：专注中保存设置后显示“下一段生效”提示。
- `settings-apply-next-session-uses-new-rules`：结束当前 session 后，下一段使用新规则。
- `stats-not-overwritten-during-session`：专注中应用设置，已有完成数、放弃数和告状数不能被磁盘旧值覆盖。
- `settings-apply-race`：后台 Tick 与设置应用并发执行，不抛异常、不丢计数。

### 验收标准

- session 开始后，监督规则、规则时长和 `OnlyCountAfterStart` 固定不变。
- 设置窗口在专注中仍可编辑；保存后明确提示“本次不变，下一段生效”。
- 当前 session 结束后，下一段 session 使用新设置。
- 后台 Tick 和 UI 设置操作不产生未同步集合访问或统计回退。
- 原有计时、实例识别和状态机测试继续通过。

---

## 第 2 组：迁移、配置和统计的原子写入

### 涉及文件

- `src/Store.cs`
- `src/SelfTest.cs`

### 当前问题

迁移现在直接复制到正式目标文件。如果复制被中断，目标文件可能只包含部分内容。下一次启动只要发现目标文件存在，就会跳过复制，最后仍可能写迁移标记并删除旧目录。

`WriteMigrateMarker()` 还会吞掉写入错误，调用方无法确认迁移标记是否真的写入。

`SaveSettings()` 和 `SaveStats()` 也直接覆盖正式文件，程序崩溃或断电时可能留下截断 JSON。

### 实现要求

1. 迁移文件必须先写临时文件。

   - 临时文件放在新数据目录内；
   - 写完后关闭文件并验证内容；
   - 配置文件必须成功反序列化；
   - 历史文件至少要能逐行解析，损坏行必须被发现并记录；
   - 验证成功后再原子替换正式文件。

2. 迁移不能因为目标文件存在就默认完成。

   - 正式文件存在但校验失败时，必须重新复制；
   - 临时文件或损坏目标文件不能阻止重试；
   - 不要覆盖用户已经存在且通过验证的新文件。

3. 迁移标记必须有明确成功结果。

   - `WriteMigrateMarker()` 返回 `bool` 或失败时抛出异常；
   - 只有文件写入并重新读取验证成功后，才允许删除旧目录；
   - 标记写入失败时必须保留旧目录；
   - 旧目录删除失败可以保留，但要写日志并允许下次清理。

4. `SaveSettings()` 和 `SaveStats()` 使用统一的原子写入辅助方法。

   - 先写临时文件；
   - 成功关闭并验证 JSON 后，再替换正式文件；
   - 替换失败时保留原正式文件和临时文件，方便下次恢复；
   - 不得先删除正式文件再写新文件。

### 必须新增的测试

- `migration-partial-destination-retries`：目标文件是残缺文件时，下一次迁移能够重新复制完整内容。
- `migration-marker-write-failure-keeps-old-dir`：迁移标记写入失败时，旧目录不被删除。
- `migration-validates-all-copied-files`：配置、统计、历史和日志的迁移结果可验证。
- `settings-save-atomic`：模拟正式文件替换失败，旧配置仍然可读取。
- `stats-save-atomic`：模拟保存中断，统计文件不会变成不可解析 JSON。

### 验收标准

- 任意一个文件复制中断后，旧目录仍然存在。
- 修复故障后再次启动能够补齐文件。
- 只有全部文件完成并验证、迁移标记确认写入后，才删除旧目录。
- 配置和统计保存失败不会破坏上一次有效内容。

---

## 第 3 组：补齐所有手动发信的异步化

### 涉及文件

- `src/MainForm.cs`
- `src/AsyncMail.cs`
- `src/SelfTest.cs`

### 当前问题

设置窗口和历史记录窗口已经异步化，但主窗口的“测试发信”仍直接调用 `Mailer.Send()`。SMTP 或 HTTP 超时时，主窗口仍会卡住。

### 实现要求

- 主窗口测试发信也必须使用 `AsyncMail.Run()`；
- 请求期间禁用测试按钮或对应菜单操作；
- 显示“发送中…”状态；
- 成功和失败都恢复 UI 状态；
- 窗口关闭后忽略后台结果；
- 后台线程不得直接调用 `MessageBox`、`Cursor` 或其他 WinForms 控件。

### 必须新增的测试

- `mainform-test-mail-is-async`：点击主窗口测试发信后立即返回，UI 仍可操作。
- `mainform-test-mail-button-state`：发送期间不能重复触发，结束后状态恢复。
- `mainform-test-mail-close-safe`：窗口关闭后后台结果不会访问已释放控件。

### 验收标准

- 主窗口、设置窗口、历史窗口的手动发信行为一致。
- 网络不可达或超时时，UI 不假死。
- 发送成功和失败都能给出结果。

---

## 第 4 组：长时间暂停后的计时和采样边界

### 涉及文件

- `src/Supervisor.cs`
- `src/SelfTest.cs`

### 当前问题

长时间间隔超过 `SleepGapSeconds` 时，专注时间不会增加，但之后仍可能执行一次采样，并按一个采样周期给监督程序累计时间。

电源事件正常到达时通常可以避免这个问题，但必须为电源事件丢失或线程恢复延迟定义明确行为。

### 实现要求

- 普通 UI 卡顿：真实经过时间应计入专注；
- 系统睡眠/休眠：睡眠时间不得计入专注；
- 无法判断的超长间隔：采用保守策略；
- 建议超长间隔发生后重置计时和采样基准，并跳过本次采样，避免把未知时间算给监督程序；
- 日志中记录超长间隔和处理结果。

### 必须新增的测试

- `blocked-ui-time-counted`：UI 阻塞期间专注时间仍推进。
- `sleep-gap-with-running-process`：睡眠间隔期间即使进程仍在模拟运行，也不累计睡眠时间。
- `long-gap-skips-unknown-sample`：无法判断超长间隔时，不额外产生一个完整采样周期的程序使用时间。
- `power-resume-resets-baseline`：Suspend/Resume 后不重复累计间隔。

### 验收标准

- 不会因为 UI 卡顿而少计普通专注时间。
- 不会因为睡眠或未知长间隔而提前触发程序违规。
- 既有计时和电源事件测试继续通过。

---

## 第 5 组：发信协议边界加固

### 涉及文件

- `src/SmtpTransport.cs`
- `src/HttpSender.cs`
- `src/Settings.cs`
- `src/SelfTest.cs`

### 5.1 SMTP 返回码

当前 SMTP 响应只比较返回码第一位。应严格比较完整三位状态码。

要求：

- 期待 `250` 时只能接受 `250`；
- 期待 `220` 时只能接受 `220`；
- 期待 `354` 时只能接受 `354`；
- 多行响应的每一行状态码格式必须正确；
- 错误码要保留服务器返回文本，方便用户排查。

测试：

- `smtp-rejects-wrong-three-digit-code`：`220` 不能冒充 `250`；
- `smtp-multiline-response`：合法多行响应仍能通过；
- 假 SMTP 端到端流程继续通过。

### 5.2 HTTP 自动重定向

`HttpWebRequest` 默认允许自动重定向。发信请求携带认证头时，不应无条件跟随重定向。

要求：

- 设置 `AllowAutoRedirect = false`；
- 收到 3xx 时直接报告重定向错误，或手动校验目标地址后再发起新请求；
- 不允许认证头被发送到未经校验的新地址；
- 固定服务和自定义 HTTPS 地址仍保持现有策略。

测试：

- `http-redirect-is-not-followed`：返回 3xx 时不会自动发送第二次请求；
- `http-auth-header-stays-on-original-request`：认证头不会泄漏到未验证地址；
- 原有 HTTP URL 安全策略继续通过。

### 5.3 连接资源

- `BeginConnect()` 使用的 `AsyncWaitHandle` 应在连接结束后释放；
- 连接超时、服务器拒绝和协议错误都要转换为可读错误；
- 不要让连接测试回到系统默认的无限等待。

---

## 第 6 组：完整回归与发布验收

### 必须运行

```powershell
pwsh -File .\build.ps1
.\dist\PomoCC-番茄钟监督.exe --selftest .\tests\rev02-selftest.log
.\dist\PomoCC-番茄钟监督.exe --smoketest .\tests\rev02-smoketest.log
.\dist\PomoCC-番茄钟监督.exe --dpicheck .\tests\rev02-dpicheck.log
.\dist\PomoCC-番茄钟监督.exe --rendertest .\tests\rev02-rendertest.log
```

### 发布前检查

- 构建退出码为 0；
- 所有新增测试退出码为 0；
- 没有真实用户数据被改写；
- 没有遗留临时目录；
- 没有遗留测试注册表项；
- UI 冒烟测试和 DPI 检查继续通过；
- `git diff` 只包含本轮任务相关修改；
- `AGENTS.md`、`CHANGELOG.md` 和任务文档中的状态与实际代码一致。

## 不纳入本轮

- 默认密码 `123456`：按当前产品决策保留；
- 旧版 `PomodoroSupervisor` 注册表值清理：当前不是问题；
- 历史文件整体压缩、归档和流式读取：当前不是阻塞问题；
- 与上述问题无关的视觉重构、命名调整和功能扩展。

## 完成定义

只有同时满足以下条件，才能称为 Re-v0.2 完成：

1. 第 1～5 组的新增测试全部通过。
2. 原有 v0.2 回归测试全部通过。
3. 构建、UI 冒烟、DPI 和渲染检查全部通过。
4. 当前 session 不受中途设置修改影响。
5. 迁移、配置保存和统计保存失败时都不会破坏已有数据。
6. 所有手动发信入口都不会阻塞 UI。
7. SMTP 和 HTTP 发信边界测试全部通过。
