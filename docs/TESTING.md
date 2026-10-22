# 开发与测试

## 构建、自检与打包

在 Windows 上使用 PowerShell 7 和 .NET Framework 4.8 的 64 位编译器：

```powershell
pwsh -File .\build.ps1
pwsh -File .\scripts\download-dictionary.ps1
pwsh -File .\scripts\test.ps1
```

构建产物位于 `dist\LightTranslate`，下载的词库源文件保存在 `cache`。
词库脚本使用固定版本的 ECDICT，验证 SHA-256 后生成索引。

默认自检在本地运行，覆盖词库查询、剪贴板快照、终端与语言过滤、原生密码控件识别、
请求取消、对象回收和浮层窗口样式。它会创建自己的测试控件并渲染浮层，
不会显示窗口或向其他桌面应用注入输入。

阅读设置自检额外覆盖旧配置兼容、非法数值回退、字体/背景/时间/比例保存、
表单校验、保存失败不应用、取消与恢复默认值不误保存。配置写入先使用临时文件，
再原子替换原配置。测试只在输出目录创建自己的临时配置并在结束时清理。
自检还覆盖悬停续时、移远宽限期和超时边界，以及英文比例的等号边界、混合文字、
补充平面 Unicode 和标点/数字/空白/表情排除。背景文字对比度检查采样 4,096 个 RGB 颜色。

输出目录中会生成 `reading-settings-preview.png`、`reading-settings-custom-preview.png`
和 `reading-settings-error-preview.png`，使用真实 WPF 控件离屏渲染默认、定制及错误状态。
这些渲染图不代替实际桌面的焦点、键盘、选色器、DPI 和滚动交互验收。

输入监听回归测试会阻塞 UI 调度器，检查独立监听线程仍能处理消息；
随后移除本进程的原生监听，检查恢复机制自动重新安装，并确认捕获的消息交回 UI 调度器。

测试在线服务：

```powershell
pwsh -File .\scripts\test.ps1 -Online
```

在线自检发送三句固定示例文字。结果写入输出目录的 `self-test.json`；
运行日志为 `status.log`，两者均被 Git 忽略。

生成带词库、许可证和默认设置的便携包：

```powershell
pwsh -File .\scripts\package.ps1 -Version 0.1.0-alpha.2
```

GitHub Actions 会执行编译、词库构建和离线自检，并上传构建产物。

## 配置字段

托盘“设置…”分为翻译、浮层和启动三页。配置保存后立即生效，修改应用目录的 `settings.json` 则重启后生效。
自动翻译的托盘快捷切换与已打开面板同步；保存开关失败会恢复托盘勾选并提示错误。
`Diagnostics` 开关每两秒读取一次，可在排查后直接关闭，无需重启。

| 字段 | 默认值 | 作用 |
|---|---|---|
| `Enabled` | `true` | 自动划词翻译开关，也可通过托盘修改 |
| `ClipboardFallback` | `true` | 选区取词开关，关闭后停止所有取词 |
| `TranslationProvider` | `auto` | `auto` 保留本地词库优先、微软失败后腾讯；`microsoft` / `tencent` / `deepl` 仅调用选定服务并跳过词库 |
| `Target` | `zh-Hans` | 微软 / DeepL 的目标语言；本地词库和腾讯输出中文 |
| `MaxSelectionLength` | `2000` | 单次选区字符数上限 |
| `ExcludedApps` | `[]` | 排除的进程名称，可带或不带 `.exe` |
| `Diagnostics` | `false` | 触发诊断日志开关 |
| `BubbleFontFamily` | `Microsoft YaHei UI` | 浮层字体族，使用已安装的字体；缺失或未知字体回退默认 |
| `BubbleFontSize` | `14` | 浮层字号，10–48 DIP（随系统 DPI 缩放） |
| `BubbleBackground` | `""` | 空字符串跟随系统主题，或不透明的 `#RRGGBB`；自动选择对比度 ≥4.5:1 的文字颜色 |
| `AutoHideSeconds` | `0` | 0 沿用原有译文时长（6–22 秒）；1–120 为译文固定秒数，保留悬停续时及提前收起规则。等待和错误提示仍使用原来的时长，避免短设置值中断请求 |
| `MinEnglishPercent` | `0` | 0 关闭新增过滤并保留旧语言规则；1–100 为 A–Z/a–z ÷ 全部 Unicode 字母的最低百分比，等于阈值也触发 |

这些新字段缺失时保留原有默认行为；手动写入越界或非有限字号、非法颜色、未知字体时回退对应默认值。
英文比例排除空白、标点、数字、表情及组合音调符号；中文和其他语言字母计入分母。
补充平面的字母按 Unicode 标量计一次，全角 Latin / 重音字母计入分母但不计英文分子。
大于 0% 时没有 A–Z/a–z 的文本不触发；0% 时无文字字母或纯中文仍被原有规则跳过。

`ClipboardFallback` 沿用早期字段名，同时控制剪贴板取词和独立进程选区读取。

面板纳入 `Enabled`、`ClipboardFallback`、`Target`、`MaxSelectionLength`、`ExcludedApps`
和五项阅读选项。目标语言只校验修改后输入的语言标签语法，不联网获取语言列表；服务是否支持自定义标签由所选服务决定。
修改后的选区上限要求正整数，排除应用支持换行及中英文逗号/分号，按进程名校验、去重。
未改动的原有自定义目标、长度和排除数组逐值保留；隐藏的 `Diagnostics` 克隆保留，不因普通设置或恢复默认值而重置。

### 开机自启

开机自启以当前用户启动文件夹的 `LightTranslate.lnk` 和实际目标路径为准，不额外持久化一个可能失真的 JSON 布尔值。
读取还会保守检查当前用户 `StartupApproved/StartupFolder` 的常见 12 字节记录：已禁用或未知状态不视为启用，且不修改审批注册表。
同名快捷方式指向其他目标或带有参数时不覆盖/删除；读取失败显示无法确认，其他配置仍可保存。
仅用户改变自启选择并保存时写入当前目录的快捷方式，不使用服务、计划任务、提权或安全设置变更。
写入后重新读取验证；配置保存失败时恢复启动项原字节，无法恢复时明确报错并显示重新读取的状态。
回滚发现启动项已被其他程序更改时拒绝覆盖新内容。

统一设置测试使用模拟后端覆盖往返、取消、保存失败、部分失败、未知/禁用状态及托盘同步；
真实 WScript 快捷方式测试使用输出目录中的独立临时文件夹，验证创建/读取/移除、原字节恢复、异目标保护及 I/O 失败，
不使用真实 Startup 目录、不读取或写入真实审批值。测试结束清理自身临时文件。
另生成 `settings-translation-preview.png`、`settings-startup-preview.png` 和 `settings-startup-error-preview.png`。
图中的自启状态是明确标注的隔离/模拟样例，不代表测试机真实状态。

## 取词与数据处理

程序先由独立进程判断文本上下文和读取 UIA 选区；空选区及非文本交互不访问剪贴板。仅确认是文本区域但缺少选区接口时，才使用带内部标记的 `Ctrl+C` 回退，快照仅接受支持的文本格式；原剪贴板无法完整安全备份时保留原内容并提示读取失败。
如果用户或其他应用在取词期间更新了剪贴板，程序会避免覆盖新的内容。
系统剪贴板历史和第三方剪贴板管理器仍可能保留临时复制记录。

自动模式命中本地词库时不发送选区；未命中发送给微软，失败后可能发送给腾讯。
自动或微软模式启动时预热微软连接，不包含选区；腾讯和 DeepL 模式不预热。
明确选择单一服务时跳过词库且不回退；DeepL 需要用户保存密钥，可能计费。
微软和腾讯内置接口无需个人密钥；接口可用性由服务端决定，可能限流或变更。

普通服务和 DeepL 各有最多 256 条译文缓存，仅保存在当前进程内。程序不记录选区原文或用户译文。
诊断日志启用后会记录进程名、进程编号、字符数和触发状态；启动错误日志可能包含本机路径。

## 兼容性记录

首个版本已在开发者的 Codex 桌面环境中手动确认划词显示。早期剪贴板取词也在 Chrome 中验证过。
Windows 本地编译、离线自检、在线翻译测试及首次 GitHub Actions 构建均通过。

取词实现已经移除 UIAutomationClient 句柄；此前测试发现 UIAutomationCore 的原生对象回收会触发访问冲突。
后续兼容性验证重点为 Claude 桌面应用、长时间运行、多显示器及重启后的自启动。

0.1.0-alpha.2 将鼠标和键盘监听移至独立消息线程，回调只捕获输入并排队，
复制选区、跨进程检查和界面操作由 UI 线程执行。监听会在系统恢复、会话切换、
检测到输入与回调不一致时重新安装，并每 30 秒在鼠标按钮释放时刷新。
修复版已在 Codex 中再次手动确认显示译文。
Windows 的低级监听超时机制见 [Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)。

取词依赖应用支持复制选区。原生密码编辑框和已识别的终端进程会被跳过；
网页或自绘密码字段依赖应用本身的复制规则，嵌入其他应用的终端需要单独验证。
从普通权限进程获取管理员应用的选区受 Windows 权限限制。

当前功能聚焦划词阅读：本地词库可离线，句子翻译使用网络。
OCR、朗读、翻译历史、原位替换、AI 润色、自动更新、安装器和代码签名暂未实现。

## 手动验证清单

记录 Windows 版本、轻译版本和目标应用的进程名称。

1. 拖选、双击选词，以及 Shift + 方向键选择文字。
2. 在线选择句子，断网后查询词库中的单词。
3. 打字、滚动外部页面、点击外部和切换应用，检查浮层隐藏及输入焦点。
4. 在浮层上悬停，滚动长译文。
5. 预先复制文本、HTML、图片和文件，检查取词后的剪贴板内容。
6. 翻译等待期间复制其他内容，检查新的剪贴板内容。
7. 连续快速选择不同文字，检查旧响应是否影响当前译文。
8. 检查终端、密码框、排除应用、多显示器、DPI 和屏幕边缘。
9. 持续运行数小时，并在重启 Windows 后检查自启动与划词。
10. 在设置的浮层页选择不同字体、10/48 字号及浅/深/中等亮度背景；确认实际浮层、预览、文字对比度、缩放和长译文滚动。
11. 将时间设为 1/10/120 秒，鼠标停在选区附近但不覆盖浮层检查超时；悬停后移回附近检查重新计时，移远和外部操作仍提前收起。
12. 比例设为 60%，选择 `abc中文` 应翻译，设为 61% 则不触发；选择 `abc中文！？ 123` 的结果相同。100% 时纯英文触发、中英混合不触发；0% 保留旧过滤。
13. 输入非法颜色、字号、秒数或比例，确认就地报错且不保存；取消和关闭设置不应用，重启后保留已保存的值且不改动自动翻译开关等其他设置。
14. 打开面板后切换托盘自动翻译，再保存其他选项，确认开关不会被旧值覆盖；目标语言、选区上限、排除应用和取词开关即时应用，取消不应用。
15. 自启验证只在隔离目录或模拟后端完成；打开、勾选、恢复配置默认值和取消均不得写入，只有保存才能修改；覆盖禁用/未知/异目标以及保存和回滚失败状态。真实用户启动项由用户自行选择，不作为自动测试动作。

## Clipboard capture regression

`./scripts/test-clipboard.ps1` tests capture cancellation, concurrent selections,
snapshot/read/restore races and bounded retries without accessing the clipboard.
`./scripts/test-clipboard.ps1 -Native` creates a private window station and desktop
before opening a clipboard. It aborts if isolation is unavailable; it never falls
back to the interactive clipboard. It tests exact byte preservation for Unicode,
HTML and RTF, empty/unsupported/oversized data, busy access, stale restoration and
delayed rendering. Both commands run in CI; neither needs the dictionary.

Capture now accepts only HGLOBAL text formats (Unicode/ANSI/OEM text, locale,
HTML Format and Rich Text Format), at most 64 formats, 8 MiB per format and
16 MiB total. Unknown or incomplete snapshots bypass Ctrl+C and try the isolated selection helper;
images, file lists and application-private formats are not silently discarded.
Delayed rendering may cause a snapshot to be skipped when its version changes.
A slow native clipboard owner can still delay GetClipboardData. Restore checks
the sequence while holding OpenClipboard, and preallocates every buffer before
EmptyClipboard. Windows provides no atomic multi-format replacement; a native
SetClipboardData failure is reported, and newer user data is never deliberately
replaced to retry a failed restore. Cancellation leaves the current clipboard
alone. A copy from the same source with no observable input/version distinction
cannot be attributed with certainty; this is not a proof of clipboard ownership.

The change bypasses the observed IDataObject/OLE IStream conversion crash path.
The original native crash has not been reproduced, and the faulty format or
provider has not been identified. These tests exercise the replacement behavior,
not the original corrupt COM pointer.

## Tray icon rendering

The offline self-test renders `tray-icon-preview.png` at 16/20/24/32/48/64 px
with three font/background combinations. Glyph ink bounds are centered inside
the solid circle. The selected reading font and automatic contrast foreground
are reused; font size is fitted to the icon, independently of bubble text size.
The test replaces an invisible NotifyIcon 100 times and checks USER handle growth.
This does not replace a live Explorer tray/DPI test. Saved appearance settings
update the running tray icon and dispose its previous Icon; canceled edits do not.

## Translation provider regression

`TranslationProvider` defaults to `auto` for old settings. The panel also offers
`microsoft`, `tencent` and `deepl`; explicit choices bypass the local dictionary and never
fall back to another service. Tencent currently outputs Chinese only. Its target
control is disabled without erasing the last target. Automatic mode
retains the existing Chinese dictionary and Tencent fallback behavior even when
Microsoft's target is another language. DeepL is never part of automatic fallback.

The offline self-test uses an injected HTTP handler to verify routing, automatic
fallback, explicit-provider failure, provider/target cache separation, canceled
in-flight responses, and settings save/cancel/reopen/live application. It makes
no network requests. Applying settings uses the existing capture/request
cancellation path before any new translation. Live service availability is not
covered by these fixture tests.


## Optional DeepL API

DeepL uses only `POST https://api.deepl.com/v2/translate` or
`POST https://api-free.deepl.com/v2/translate`, with `DeepL-Auth-Key` Authorization.
The key suffix `:fx` selects the latter, following the official
[authentication guide](https://developers.deepl.com/docs/getting-started/auth) and
[SDK endpoint selection](https://github.com/DeepLcom/deepl-dotnet/blob/main/DeepL/Translator.cs).
This does not infer a host from plan marketing names. DeepL says its API documentation
applies to all plans. Its [current plans](https://support.deepl.com/hc/en-us/articles/360021200939-DeepL-API-plans)
include Developer (a total, non-resetting allowance), Growth and Enterprise;
legacy Free/Pro are no longer sold. The app does not promise a recurring free quota.
Region-specific Enterprise endpoints are not included in this implementation.
No custom URL, redirect, automatic retry or automatic switch to/from DeepL is allowed.
`zh-Hans` and `zh-Hant` normalize to `ZH-HANS` and `ZH-HANT`.

`deepl-key.bin` contains only current-user DPAPI ciphertext. It is excluded from Git
and release artifacts. Plaintext is not serialized into settings, logs, error messages,
previews or test reports. Opening settings checks only file existence and never fills
the PasswordBox with a stored key. Replacement/clear is staged until Save; config-save
failure attempts to restore the previous encrypted file. Rollback failure is reported.
This is not a crash-atomic transaction across config, credentials and startup state.
DPAPI protects storage from other users, not processes already running as that user;
the key necessarily exists briefly in managed memory and the outgoing auth header.
The controller cancels pending translations on settings application. The DeepL client
also rejects a response if credentials changed during its request, checks credentials
before cache access and clears its separate cache when the credential scope changes.

Run `./scripts/test-providers.ps1` after building, or run the complete offline self-test.
The provider test is also a separate CI step. Only fictitious sentinel credentials,
injected HTTP handlers, a temporary DPAPI file and fake startup registration are used.
Coverage includes zero requests without a key, default/explicit routing, both hosts,
Chinese targets, provider/target/credential cache isolation, authentication failures,
quota exhaustion, 429, invalid targets, request size, malformed responses, timeout,
network failure, cancellation, stale responses, and save/clear/cancel/reopen/rollback.
No live DeepL account, registration, key validation or billed request is exercised.
The generated settings previews show an empty credential field.


## 图片或专用格式剪贴板下的选区读取

仅允许文本快照的守卫会拒绝图片和应用专用格式。这是保护原剪贴板所需的行为。
现在首先通过短时独立进程 `LightTranslate.SelectionHost.exe` 判断上下文并请求应用的
UIA TextPattern 选区；能直接读取的文本无需快照，也不读取或修改剪贴板。
主程序仍不引用 UIAutomationClient，不持有跨进程 UIA/COM 对象，也不恢复原 OLE 快照路径。
辅助进程只接受源窗口、焦点窗口句柄、鼠标起止位置、起点原生窗口和长度上限，验证窗口归属并拒绝密码字段；
选区通过匿名标准输出管道返回，不写文件或日志。辅助进程有 3 秒的自身终止期限，
主程序异步等待上限为 3.8 秒，并丢弃已取消或焦点改变后的响应。
这不是权限沙箱；它隔离 UIA 原生故障，不会终止用户源应用或轻译主进程。
已确认文本区域无法安全读取或超时，以及辅助程序缺失时，浮层提示取词失败并保留剪贴板；尚未确认文本上下文时只保留固定诊断码。

`./scripts/test-selection.ps1` 使用真实 Windows 编辑控件和真实外部辅助进程，
合成选区经过上下文判断、选区读取、英文比例检查，最终命中本地词典。
它同时验证空选区、密码框、错误源窗口、取消和输出长度上限，不调用在线翻译，
不读取用户剪贴板内容，也不改变用户窗口焦点；只记录剪贴板序列号是否保持不变。
`./scripts/test-clipboard.ps1 -Native` 在私有 window station 中额外验证真实编辑控件的
复制命令、原生快照恢复，以及专用格式不会被覆盖；所有数据均为合成数据。
这些测试不等于已验证每个浏览器、Word、管理员权限窗口或远程桌面的实际兼容性。

### Chromium 选区定位

鼠标拖选网页文字不一定把键盘焦点移到该文档。辅助进程优先从鼠标释放位置的
UIA 元素向上查找 TextPattern，随后才尝试原生焦点和 UIA 键盘焦点；每一步验证
源窗口归属，最多查找 32 层，并保留独立进程的总超时限制。它不扫描其他文档、
不读整个页面或 ValuePattern 的值（仅检查 IsReadOnly 元数据），也不修改 Chromium 的可访问性设置。
主程序仅记录固定失败码，例如无 TextPattern、空选区、密码字段、窗口不符、
深度上限、提供方异常和超时；不记录控件名称、选区原文或页面内容。
真实控件测试不覆盖网页布局、iframe 与键盘焦点差异，仍须进行实际拖选验收。

### 窗口操作与文字拖选的边界

鼠标按下、松开时，输入线程只记录窗口句柄、客户区位置、窗口边界和控件类名。
取词前排除非客户区、原生滚动条、跨窗口拖动，以及期间发生的窗口移动或缩放；
客户区内的自绘标题栏再由带 25 毫秒时限的 WM_NCHITTEST 检查排除。
命中测试在 UI 调度线程执行，不在全局鼠标钩子中发送跨进程消息。
不合格的手势不会进入选区读取、剪贴板快照、复制或翻译路径，也不会成为下一次双击的起点。

`./scripts/test-gestures.ps1` 使用自有隐藏原生窗口、编辑框和滚动条，结合合成手势端点，
验证普通文字拖选、双击区域、标题栏、边框、独立滚动条、编辑框内滚动条、自绘标题栏、
客户区发起的移动和缩放，以及移动之后重新选词。测试使用真实窗口几何和命中测试，
不向用户应用注入鼠标事件，不读取剪贴板内容；它不替代实际记事本、浏览器和桌面应用验收。
完全自绘、未暴露原生滚动条且命中测试始终返回客户区的滚动条仍不在此守卫的覆盖范围内。

### 空选区与读取失败

辅助进程的结果区分有文本、成功但选区为空、不支持 TextPattern、密码字段、超时与失败。
成功的空选区直接结束当前取词，不显示失败提示；辅助进程也不再向其他焦点或祖先回退，
避免把另一个控件保留的旧选区作为本次结果。折叠文本范围先检查端点，不读取其相邻文本。
文本区域缺少选区接口、超时和提供方异常仍保留失败提示。未确认文本上下文的无接口、超时或异常仅记固定诊断，不将一般交互当作取词错误。

选区集成测试使用真实 TextBox 和 RichTextBox，分别覆盖文末折叠选区（空拖动后的状态）、
正文中折叠光标、空文档以及空选区前后的正常选词；经过同一上下文判断和错误转换路径，
验证没有复制、剪贴板读取或旧文本返回。另验证真实不支持 TextPattern 的窗口和固定错误协议。
这些控件测试不注入实际记事本鼠标动作；实际记事本的文末空拖以及 Chromium 正常选词仍需人工验收。

### 非文本交互的提前判断

`./scripts/test-selection-context.ps1` 使用自有隐藏 ListView、TreeView 和列表内编辑框，
覆盖空白图标区域、已有项目选中的图标拖动上下文、详细列表和树选择；同时在可备份与
不可备份的模拟剪贴板下验证快照、复制、读取和恢复调用次数均为零，且不返回旧文本。
列表内的重命名编辑框仍能返回所选测试词；不是按 Explorer 进程名或桌面标题硬编码禁用。

辅助进程按控件角色及 Selection/SelectionItem 模式识别项目交互，区分 Edit/Document
文本区域；网页列表仍可向所属 Document 查找文本选区。Shell 的 UIA 层级若跳过原生
根窗口，到达源窗口边界时保留已确认的非文本结果，不查询其他窗口或回退旧焦点。
只有已确认的文本区域缺少文本选区接口时才允许剪贴板回退；未知区域安静结束，
探测失败保留固定诊断码，已确认文本区域的读取失败仍提示，且不会盲目复制。
辅助进程成为取词前置依赖，须与主程序一起分发。

测试不移动用户图标或文件，也不执行真实 Shell 拖放。实际桌面另做只读控件分类验证；
桌面框选、资源管理器列表交互及文件重命名的实际手势仍需人工验收。剪贴板序列号是
只读观察项，其他程序在测试期间可能改变它；不接触剪贴板的断言以调用计数为准。

### 未确认文本上下文的系统浮层交互

窗口不提供 TextPattern 只能说明没有确认可读取的文本选区，不足以认定用户正在选词。
因此通用 Pane/Group 等区域以及尚未确认文本上下文的探测失败会安静结束，仍记录固定
`no-text-pattern`、`provider-error`、`deadline` 等诊断码；不启动剪贴板回退。鼠标所在区域
已成功检查但没有文本证据时，不再转向另一个控件的旧键盘选区。

辅助进程确认 Edit/Document 或取得 TextPattern 后，错误与超时分别使用
`text-provider-error`、`text-deadline`，仍显示读取失败提示。文本框、网页文档和重命名
编辑框无需按宿主进程豁免；没有通知中心、日历或系统进程的黑名单。

上下文测试增加真实 Panel、GroupBox、Button、Label、MonthCalendar、TrackBar、
CheckBox，以及通用面板内 TextBox 的选词，断言无文本区域既不报错也不读写剪贴板，
已确认文本失败仍提示。它们覆盖通知、日历、快速设置常见控件语义，不等于自动操作
或完整复现 Windows 系统浮层；没有清除通知、读取通知正文或更改系统设置。

### 文件属性单元格与真正的重命名编辑框

真实 Explorer 复现表明，文件列表的属性单元格可能暴露为 Edit + ValuePattern，却没有
TextPattern；鼠标释放在这种单元格时，键盘焦点仍在 ListItem。仅凭 Edit 角色允许剪贴板
回退会将文件拖动当成选词失败。现在会检查 Selection/SelectionItem 祖先、焦点和只读
元数据：列表内未活动或只读的属性若没有 TextPattern，按项目交互结束，不发复制命令。
提供 TextPattern 的重命名编辑框及只读可选文本仍可读取；活动的可编辑控件缺少该接口时
仍可走原有安全回退，普通文本提供方异常与超时仍提示。不使用进程名或控件类名黑名单。

鼠标手势还将按下时记录的原生窗口和位置传给辅助进程，在读取释放位置之前检查起点；
起点没有文本证据时，不因终点碰到另一个编辑框或焦点改变而读取其旧选区。该检查不在
鼠标钩子中运行，也不读取起点选中文字；按 Esc 取消的拖动不再排队取词。UIA 起点语义
在释放后的隔离进程中检查，因此源界面在手势期间完全重建的情况仍需按实际应用验收。

上下文测试增加真实隐藏控件的 MSAA Edit 属性角色、单选／多选列表、只读与未活动属性、
列表内真正的编辑框和只读文本选区，并确认列表之外的无接口 Edit 不被全局屏蔽。
另对同一真实 Explorer 的 12 个属性单元格比较旧、新辅助程序：旧版均返回
text-unsupported，修复后均返回 non-text；比较不读取文件名、选区或剪贴板。
该分类比较不替代完整交互验收。人工测试只在专用目录的合成文件上进行，分别检查单选
及多选后横拖释放、框选、Esc 取消拖动、F2 后选取文件名文字并取消重命名；不拖动、
移动或覆盖用户真实文件。当前候选的最终鼠标交互结果需在部署后单独记录。

## Index 翻译专项验证

`scripts/test-index.ps1 -OutputDirectory <候选目录>` 只使用模拟 HTTP，覆盖三档模型参数、固定端点、无 Authorization、User-Agent、temperature=0、关闭思考、简繁中文提示词、响应解析、空结果、截断、模型不一致、HTTP 错误、60 秒总期限、取消、过期结果、限流退避和缓存隔离。配置测试覆盖旧字段迁移、原默认服务、三档保存及回填、取消、重置、失败回滚和无效配置。

仅在明确授权后使用 `-Online`：依次向三档发送同一固定公开短句 `The window closes automatically.`，将请求模型、返回模型、HTTP 状态、尝试次数、耗时、公开句译文和错误写入 `index-live-test.json`。它不读取用户选区、剪贴板或外部文档。文档列出的模型不等同于在线测试成功；以该文件中各档的实际结果为准。

原 `--self-test-offline` 也包含 Index 模拟测试；剪贴板、托盘、手势、外部选区辅助程序和已有翻译源测试继续保留。Index 的等待浮层最多等待至 60 秒请求期限；鼠标移开、切换窗口、开始新操作或保存设置仍会取消旧请求。

## 剪贴板覆盖候选验证

`scripts/test-overwrite.ps1 -OutputDirectory <独立输出目录>` 或 `--self-test-overwrite` 仅使用模拟剪贴板和临时配置，不访问用户真实剪贴板正文。覆盖原因分类、默认关闭、能备份时仍恢复、明确许可后覆盖且永不恢复旧备份、复制失败与空结果、有限次等待、来源／序列号校验、取消／失焦、并发用户复制、重复操作，以及单次许可的上下文、15 秒期限与不可重放检查。

配置用例验证拒绝风险提示、确认后待保存、取消、失败回滚、保存回填、旧配置默认关闭和原 Index 设置保留。实际 WPF 浮层、风险确认框和设置页均可离屏渲染；按钮取消、重复点击和提示收起后不再执行动作。

`--self-test-clipboard-native` 先切换到独立 window station 和 desktop，隔离失败即终止，随后才使用该私有剪贴板内的合成数据验证原生取词和覆盖。新增不支持格式时零复制、明确允许后真实 TextBox.Copy 只保留新文字、复制无变化时不返回旧文字等用例。测试不覆盖用户真实剪贴板，不依赖保存／恢复用户数据实现隔离。

此候选未自动替换或启动本机运行版。真实 Steam 窗口点击、复杂第三方剪贴板管理器、不同 DPI 和持续使用仍需后续授权验收。Windows 延迟渲染等原生调用可能阻塞，轮询次数上限不是对每个原生提供方的绝对执行时限保证。
