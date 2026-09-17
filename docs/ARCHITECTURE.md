# 架构设计 · C 盘 AI 分析清理工具（DiskLens 暂定名）

> 文档定位：**技术架构**。产品需求见 [PRD.md](PRD.md)，排期见 [ROADMAP.md](ROADMAP.md)。
> 本文件中的所有性能与占用数字**均来自本机真实探测**，见文末[附录 A](#附录-a本机实测基线)。

---

## 1. 技术选型（已定）

**本地服务 + 浏览器 UI**：Node 后端（扫描内核 + 规则引擎 + AI 网关）+ Vite/React 前端，先把算法与交互跑通。**注意：这只是开发栈，不等于分发形态** —— 分发形态在 **M2 末尾定稿（见 §10）**，M4 只做实现。用户是否需要安装 Node 完全取决于此，不能拖到 M4 才想。

| 决策 | 选择 | 理由 |
|---|---|---|
| 运行时 | Node.js 22（本机 v22.22.3 已装） | 零额外工具链即可开工；本机**未装 Rust/Python/Go**，选它们会阻塞 M1 |
| 用户界面 | Vite + React + TypeScript，浏览器访问 `127.0.0.1:<port>` | Treemap 生态成熟（d3-hierarchy）；调试成本最低 |
| 服务形态 | 本地 HTTP 服务，仅监听回环地址 | 天然支持「边扫边看」（SSE 流式推送），也为 M4 换壳留好接口边界 |
| 重活隔离 | `worker_threads` | 扫描不能阻塞 HTTP 响应与进度推送 |
| 索引存储 | SQLite（`node:sqlite` 或 better-sqlite3） | 百万级节点需要本地索引才能支撑下钻与检索 |
| 提权 | 默认非管理员运行 | 本机实测**当前非管理员**，且非管理员已能覆盖绝大多数清理目标（见附录 A） |

> **为什么不用 Electron 起步**：Electron 会把"渲染进程 + 打包 + 安装器"一次性引入，而本期要验证的是"扫描准确性 + AI 解释质量"这两件与壳无关的事。架构上把扫描内核设计为独立的库/服务，M4 换壳不影响内核。

## 2. 分层架构

```
┌────────────────────────────────────────────────────────────────┐
│  前端 (Vite + React + TS)                                      │
│  · Treemap (d3-hierarchy)  · 悬浮卡  · AI 侧栏  · 设置页       │
│  · 扫描进度条 / 取消                                            │
└───────────────▲────────────────────────────┬───────────────────┘
                │ SSE 进度 / JSON 结果        │ 用户操作
┌───────────────┴────────────────────────────▼───────────────────┐
│  API 层 (Fastify/Express, 仅 127.0.0.1)                        │
│  POST /api/scan  GET /api/tree  GET /api/node/:id              │
│  POST /api/explain  GET|PUT /api/settings                      │
└───────┬──────────────────────┬──────────────────────┬──────────┘
        │                      │                      │
┌───────▼────────┐  ┌──────────▼─────────┐  ┌────────▼──────────┐
│  扫描引擎       │  │  规则引擎           │  │  AI 网关（可选）   │
│ (worker_threads)│  │  rules/*.json      │  │  Provider 抽象     │
│ · 有界并发遍历  │──▶│  匹配 → 分类/风险   │──▶│  · 结构化 JSON 输出│
│ · 硬链接去重    │  │  · 本地结论文案     │  │  · schema 校验     │
│ · 权限失败收集  │  │  · 规则指纹 (缓存键)│  │  · 缓存 / 降级     │
└───────┬─────────┘  └──────────┬─────────┘  └────────┬──────────┘
        │                       │                      │
┌───────▼───────────────────────▼──────────────────────▼─────────┐
│  SQLite 索引：nodes / rules_applied / explanations / settings  │
└────────────────────────────────────────────────────────────────┘
```

**关键边界**：规则引擎是**纯函数**（输入节点特征 → 输出分类/风险/文案），不依赖网络。AI 网关是**可选增强层**，可整层关闭而不影响其余部分。这条边界保证"无 Key 时功能完整"不是口号。

## 3. 扫描引擎

### 3.1 三条路线与实测取舍

| 路线 | 实测表现（本机） | 结论 |
|---|---|---|
| **(a) Node 递归遍历** | 同步递归 `C:\Windows\System32`（17,715 文件 / 9.42 GB）耗时 **8.1 s → 2,187 文件/秒** | **不可接受为全盘方案**，必须优化 |
| **(b) `robocopy /L /E` 枚举** | 同一目录 **4.0–7.4 s** | 作为**可选快速通道 / 交叉校验**，不作为主引擎（解析输出脆弱、无硬链接信息） |
| **(c) NTFS MFT 直读**（`FSCTL_ENUM_USN_DATA`） | 理论秒级（未实测，本机无现成实现） | **最终形态，列入 M4**；需管理员权限，Node 无标准库支持，可用 .NET 10 sidecar（本机已装 SDK 10.0.302）或 Rust native 模块 |

**M1 采取路线 (a) + 强制优化**，动因：全盘按 2,187 文件/秒推算，百万级文件需要 **5–8 分钟**，与 PRD 的"100 GB ≤ 60 s"目标差距一个数量级。

### 3.2 路线 (a) 的优化手段（M1 必做）

1. **有界并发**：目录遍历与元数据读取使用并发上限（初值 64–128）+ 背压，充分利用 SSD 队列深度。
2. **实测教训（来自本次探测）**：无上限并发 `stat`（对每个文件 `await stat` 不做限流）会**退化甚至停滞**，实测中出现扫描迟迟不返回、输出为空的现象。**并发上限与背压不是优化项，是正确性前提。**
3. **优先使用目录项自带信息**：`readdir(withFileTypes)` 可省去对每个条目的类型判断；能批量拿元数据就绝不逐文件 `stat`。
4. **跳过已知无意义目标**：junction / symlink 默认不跟随（防循环）、系统保留名、已标记的损坏目录。
5. **中断友好**：扫描任务支持协作式取消，取消后已扫描部分仍可用于展示。

### 3.3 正确性陷阱（硬性要求，不是"最好处理"）

> 这一节是本工具与现有工具拉开差距的地方。**统计错了，后面的 AI 解释再漂亮也是误导。**

#### (1) 硬链接重复计算 —— 必须去重

**实测**：`C:\Windows\WinSxS` = **9.99 GB**。该目录大量使用硬链接：同一个物理文件被链接到多个文件名下。任何"按目录求和"的实现都会把这个文件重复计入多次，**得出严重偏大的结果** —— 这正是 CCleaner / WizTree 类工具被诟病的典型误报来源。

**要求**：

- 统计物理占用时，以 **文件 ID（volume serial + file index）为键去重**，同一物理文件只计一次。
- **两种口径都算，且都要能展示**：
  - `logicalSize`（逻辑大小，各硬链接相加）—— 回答"这个目录看起来多大"；
  - `physicalSize`（去重后物理占用，即"删掉它到底能释放多少"）—— 回答用户真正关心的问题。
- 界面上必须标明当前显示的是哪种口径；**AI 解释中给出的"预计可回收"必须以 `physicalSize` 为准**。

#### (2) 权限降级 —— 必须显式披露

**实测**：当前**非管理员**账户下，读取 `C:\Windows\System32\drivers\DriverData` 抛 `scandir` 权限错误（`EPERM`）；`C:\Program Files\WindowsApps` 统计结果为 **0.00 GB**（并非真的为空，而是读不到）。

**要求**：

- 扫描过程收集所有**未能读取的目录清单 + 错误码 + 数量**。
- UI 顶部常驻显示"**有 N 个目录未能读取（权限不足），当前结果为下界**"，并提供"以管理员身份重扫"的入口说明。
- 导出报告中必须包含该清单。
- **绝不静默跳过**：静默跳过会让用户以为"C 盘只有 120 GB 数据"，从而做出错误判断。

#### (3) 其他特殊对象

- 无大小的文件、稀疏文件（按分配大小 `allocationSize` 而非逻辑大小评估物理占用）。
- 系统独占文件（`pagefile.sys`、`swapfile.sys`、`hiberfil.sys`）需单独标注为"系统管理，不建议手工处理"。（本机实测这三者**均不存在**，休眠未启用、虚拟内存可能在别的盘。）
- 回收站（`$Recycle.Bin`）单独成项并给出"清空回收站"的明确入口。
- 云同步占位文件（OneDrive 等"仅在线"文件）需与实际占用区分，避免虚报。

### 3.4 验证方法（M1 验收）

- 与 **WizTree（MFT 口径）** 和 **PowerShell `Get-ChildItem -Recurse | Measure-Object Length -Sum`** 三方对照同一批目录。
- 差异 **< 2%** 视为通过；差异必须能解释（硬链接口径 / 权限缺失），并写入测试结果。
- 用附录 A 的实测值作为回归基线。

## 4. 规则引擎（先于 AI 的核心）

**设计立场：能不能删，由本地规则表决定；AI 只负责把结论讲成人话。** 这样既省钱（绝大多数交互 0 token），又从根本上抑制幻觉。

### 4.1 规则表结构（`rules/*.json`）

```jsonc
{
  "id": "windows-winsxs",
  "matchers": {
    "pathPattern": "C:\\Windows\\WinSxS",   // 支持通配与变量：%LOCALAPPDATA%、%USERPROFILE%
    "matchType": "prefix",                    // prefix | glob | regex | exact
    "requireAdmin": false
  },
  "category": "system-component-store",
  "risk": "system",                           // safe | caution | risky | system
  "verdict": "do-not-delete",                 // keep | safe-to-clean | clean-with-care | do-not-delete | use-builtin-tool
  "what": "Windows 更新后保留的旧组件备份，用于随时回滚系统更新与修复损坏组件。",
  "why": "系统更新、修复与卸载功能依赖它。手动删除会导致后续更新失败、系统组件损坏且难以修复。",
  "advice": "不要手动删除。需要回收空间时使用系统自带的 DISM 组件清理命令。",
  "keepIf": ["近 30 天内安装过系统更新", "系统曾出现需要修复的问题"],
  "revert": "partial",                        // none | full | partial | via-builtin-tool
  "builtinTool": "DISM /Online /Cleanup-Image /StartComponentCleanup",
  "sizeSource": "physical",                   // 该规则的解释应基于哪种口径
  "tags": ["系统", "硬链接", "需管理员"]
}
```

### 4.2 匹配与决策流程

```
节点(路径/类型/大小) ──▶ 按优先级匹配规则（最长前缀 + 具体度排序）
        │
        ├─ 命中 ──▶ 输出 { category, risk, verdict, what, why, advice, revert }
        │             └─▶ 悬浮卡直接渲染（0 延迟 / 0 token）
        │
        └─ 未命中 ──▶ 通用启发式（按扩展名/路径特征/是否在用户文档域）
                      └─▶ 输出"未知项"结论：不判定为安全，风险默认 caution
```

**风险等级定义（不可被 AI 覆盖）**：

| 等级 | 含义 | UI 表现 |
|---|---|---|
| `safe` | 清理无副作用（如浏览器缓存、临时文件） | 绿色，可直接建议清理 |
| `caution` | 清理有代价但可接受（如需要重新下载/重新登录） | 黄色，说明代价后再建议 |
| `risky` | 可能影响功能或数据（如应用数据目录） | 橙色，默认不勾选 |
| `system` | 系统关键，禁止手动删除 | 红色，**禁止一键清理**，只给系统自带方案 |

**未知项默认 `caution`**：永远不把"我不认识"当作"它安全"。

### 4.3 规则库首批覆盖范围（按附录 A 实测的占用大户优先）

`WinSxS`、`Windows\Installer`、`SoftwareDistribution\Download`、`Windows\Temp`、`Windows\Logs`、`Prefetch`、`LiveKernelReports`、回收站、`ProgramData\Package Cache`、`AppData\Local\Temp`、`AppData\Local\Packages`（含 UWP 应用数据）、`INetCache`、Chrome/Edge 缓存、各类开发工具缓存（npm/pip/uv/IDE）、下载目录、用户文档域（**只读展示，明确不建议清理**）。

## 5. AI 网关

### 5.1 Provider 抽象

```ts
interface ExplainProvider {
  id: string;                       // openai | deepseek | anthropic | gemini | ollama | custom
  chat(req: ExplainRequest, signal: AbortSignal): Promise<ExplainResult>;
  probe(): Promise<{ ok: boolean; models?: string[] }>;   // 设置页连通性自检
}
```

默认以 **OpenAI 兼容协议**为基准实现（`baseURL + apiKey + model` 三件套即可适配绝大多数服务与自建网关）；其余 provider 作为适配器实现。

### 5.2 请求体设计（隐私红线）

**只发送结构化摘要，绝不发送文件内容、绝不发送完整路径中的用户隐私片段。**

```jsonc
{
  "nodeKind": "directory",
  "pathTemplate": "C:\\Windows\\<component-store>",   // 必要时对用户名等片段做脱敏
  "category": "system-component-store",
  "sizePhysicalGB": 9.99,
  "sizeLogicalGB": 14.3,
  "risk": "system",
  "matchedRuleId": "windows-winsxs",
  "fileCount": 84213,
  "userContext": { "windowsVersion": "10.0.19045" }
}
```

明确禁止项：文件名清单、文件内容、截图、用户目录下的具体文档名、任何可识别个人身份的信息。

### 5.3 输出契约与校验

模型必须返回结构化结果，本地做 schema 校验（失败则重试一次，再失败则降级）：

```jsonc
{
  "inPlainWords": "……",            // 这是什么（给完全不懂的人看）
  "relatedToUser": "……",           // 和用户自身内容/使用习惯的关联
  "impactIfDeleted": "……",         // 删了会发生什么（含具体症状）
  "canRevert": "partial",           // none | full | partial | via-builtin-tool
  "recommendation": "do-not-delete",// 允许的建议集合
  "expectedReclaimGB": 3.0,         // 必须 <= 本地计算的 physicalSize
  "confidence": "high"
}
```

**硬性护栏**：

1. `recommendation` **不得比本地规则的 `verdict` 更激进**（例如系统级项不允许模型改口为"可以删"）。越权则丢弃模型结果并回落本地文案。
2. `expectedReclaimGB` 不得超过本地算出的 `physicalSize`，超出即视为幻觉，钳制并标记。
3. 模型输出中的路径不得被当作可执行指令（不渲染为可点击的删除动作）。

### 5.4 缓存与降级

- **缓存键** = `matchedRuleId + category + 风险等级 + 取整后的大小档位 + provider/model`。同一类问题全盘只问一次。
- **降级链**：AI 可用 → AI 结果；AI 超时/失败/未配置 → 本地规则文案；两者都有 → 界面并排展示，标注来源。
- **计量**：记录每次调用 token 与耗时，设置页展示累计用量，让"花不花钱"对用户可见。
- **超时**：默认 20 s 上限；流式返回时首个 token 优先渲染。

### 5.5 密钥存储

- 存放于用户本地配置文件（`%APPDATA%\<app>\settings.json` 或系统凭据管理器），权限收紧到当前用户。
- **绝不写入日志**；日志中对 key 统一替换为 `sk-****`。
- **绝不进入导出报告**。
- 设置页支持"一键清除密钥"。

### 5.6 模型自动发现与测速（降低配置门槛）

"模型 ID"是小白用户填不出来的东西，这一步必须由工具承担。

**模型自动发现**

1. 用户只填两栏：`baseURL` + `apiKey`（provider 可自动识别）。
2. 服务端拉取模型列表：OpenAI 兼容协议为 `GET {baseURL}/models`；Ollama 为 `GET /api/tags`；Anthropic / Gemini 走各自适配器的列表方法。
3. 归一化为统一的 `ModelInfo[]` 交给前端下拉选择：

   ```ts
   interface ModelInfo {
     id: string;             // 真正传给 API 的模型 ID
     label: string;          // 展示名（可含上下文长度）
     providerId: string;
     contextWindow?: number;
     capabilities?: { vision?: boolean; jsonMode?: boolean };
   }
   ```

4. **必须保留手填兜底**：列表接口不存在、被网关禁用或返回非标准格式时，自动回退为手填输入框并说明原因。**不得因发现失败而阻断配置流程** —— 与"无 Key 也能用"是同一条原则。
5. 失败原因要区分并给出可操作提示：`401` Key 无效；`404` 地址不对（最常见的是漏写 `/v1`）；超时 → 地址不可达；非 JSON → 不是 OpenAI 兼容服务。
6. Key 只用于本次请求：不写日志、不回传前端；`GET /api/settings` 只返回"是否已配置"+ 掩码。

**模型测速**

目的不是跑分，而是让用户在候选模型之间**用数据做取舍**（"便宜但快" vs "贵但准"）。

- **测什么**：TTFT（首 token 时间）、总延迟、生成速度（`completion_tokens ÷ 生成耗时` = TPS）、以及**一次真实解释任务的结构化输出通过率**（能否通过 schema 校验）。
- **怎么测**：对用户勾选的候选模型并发发起（并发上限 3–5，避免触发限流），使用**固定的微型探针 prompt**（一小段磁盘条目的解释任务），受控 `max_tokens`（如 128），单模型超时 30 s、失败重试 1 次。
- **怎么展示**：三项原始数值 + 一个建议标签（"最快" / "最省" / "最准但不快"），**不合成单一分数排名** —— 输出质量无法用一个数概括，单一排名会误导选择。
- **成本透明**：测速会真实消耗 token，界面必须写明"本次测速预计消耗约 N tokens"，用户确认后再执行。
- **缓存**：同一 `baseURL + model` 的结果带时间戳缓存，7 天后可重测，避免每次打开设置页都花钱。

**为什么放在服务端而不是前端直连**：① 规避浏览器 CORS 限制（很多模型服务不允许浏览器直连）；② Key 不下发到前端，泄露面更小；③ 测速与用量可统一计量。

## 6. API 草案

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/api/scan` | 启动扫描；请求体含根路径、选项；**SSE 流式**返回进度（文件数、已扫体积、当前路径、未授权目录数）与最终结果 id |
| `GET` | `/api/tree?root=&depth=` | 取可视化用的聚合树（Treemap 数据源，按需分级下钻） |
| `GET` | `/api/node/:id` | 单节点详情：两种口径大小、分类、风险、命中规则、本地解释文案 |
| `POST` | `/api/explain` | 请求 AI 深入解释；命中缓存则直接返回；未配置 Key 返回明确的降级响应 |
| `POST` | `/api/explain/stream` | （可选）流式解释，改善体感 |
| `GET`/`PUT` | `/api/settings` | 读写 provider/baseURL/model 等；**`GET` 永不回传明文 key**（只回传是否已配置 + 掩码） |
| `POST` | `/api/settings/probe` | 连通性自检 |
| `POST` | `/api/settings/models` | 用给定 baseURL + key 拉取可用模型列表（自动发现）；失败时返回**可区分的错误原因**（401 / 404 / 超时 / 非 JSON），前端据此回退手填 |
| `POST` | `/api/settings/benchmark` | 对候选模型测速（TTFT / 总延迟 / TPS / 结构化输出通过率）；**执行前需前端确认 token 消耗**；可用 SSE 逐个回报进度 |
| `GET` | `/api/scan/:id/unreadable` | 未授权目录清单（对应 3.3-(2)） |

**约定**：所有接口仅绑定 `127.0.0.1`；写入类接口（M3 的清理）需带会话令牌，防本机其他程序误调。

## 7. 数据模型（SQLite 草案）

```sql
nodes(id, parent_id, name, path, kind, logical_size, physical_size,
      file_count, dir_count, file_id, importance, risk, category);

rules_applied(node_id, rule_id, verdict, risk, matched_at);

explanations(cache_key PRIMARY KEY, rule_id, category, risk, size_bucket,
             provider, model, payload_json, created_at, tokens_in, tokens_out);

scan_runs(id, root, started_at, finished_at, cancelled,
          file_count, logical_bytes, physical_bytes, unreadable_count);

unreadable_paths(scan_run_id, path, error_code, message);

settings(key, value_enc, updated_at);
```

索引要点：`nodes(parent_id)`、`nodes(path)`、`nodes(file_id)`（硬链接去重与物理占用汇总）、`explanations(cache_key)`。百万级节点的写入建议**批量事务提交**，避免逐行 fsync。

## 8. 安全模型

| 层 | 措施 |
|---|---|
| 扫描阶段 | 全程只读；不跟随 junction/symlink；不写任何被扫描目录 |
| 展示阶段 | 风控等级由本地规则决定；`system` 级永不可被标记为可清理 |
| AI 阶段 | 只发统计摘要；输出做 schema 校验 + 越权钳制；不把模型输出当可执行指令 |
| 清理阶段（M3） | 删前精确清单 → 二次确认 → **仅删除到回收站** → 结果留痕报告；高危路径额外确认，不可绕过；白名单保护 |
| 本地服务 | 仅回环监听；写操作需令牌；CORS 收紧 |
| 隐私 | Key 不记日志、不进报告；提供"完全离线模式"（禁用全部外发，仅用本地规则） |

**清理能力（M3 起）只提供"删除到回收站"**：不提供永久删除，也不提供任何"一键清理全部"的入口。

## 9. 建议的代码组织（供后续搭脚手架参考，本轮未创建）

```
packages/
  core-scan/       # 扫描引擎：有界并发遍历、硬链接去重、物理/逻辑双口径
  core-rules/      # 规则引擎 + rules/*.json 规则库
  core-ai/         # Provider 抽象、缓存、校验、降级、计量
  core-clean/      # （M3）回收站删除、预检、报告导出
server/            # HTTP + SSE 层，串起上述能力
web/               # Vite + React + TS 前端（Treemap / 悬浮卡 / 侧栏 / 设置）
```

分层原则：`core-*` 不依赖 `server`，`server` 不依赖 `web`；`core-rules` 与 `core-scan` 不允许引入网络依赖。这条约束直接对应"无 Key 可用"与"AI 不能决定安全"两条产品原则。

---

## 10. 分发形态：终端用户需要装 Node 吗？

**不需要。但前提是我们必须主动决定分发方式 —— 这里要分清两件被混为一谈的事：**

| | 是什么 | 影响终端用户吗 |
|---|---|---|
| **开发栈**（Node + Vite） | 我们写代码、调试、跑测试用的环境 | ❌ **不影响**。用户不装 Node 也能用 |
| **分发形态** | 最终交到用户手上的那个东西 | ✅ 完全取决于我们怎么打包 |

**当前方案的真实风险**：如果直接把源码交给用户、让用户自己 `npm install && npm start`，那这个工具**就只有开发者能用** —— 这与 PRD 的"目标用户是不懂技术的普通用户"直接冲突。因此把分发形态的**决策门从 M4 前移到 M2 末尾**（见 [ROADMAP.md](ROADMAP.md)），M4 只负责实现，避免到收尾阶段才发现要返工。

### 10.1 可选方案

| 方案 | 用户需要装什么 | 体积（参考） | 评价 |
|---|---|---|---|
| **免安装单文件 exe**（Node SEA） | **零安装** | **实测 87.1 MB** | ✅ **本机已实测通过**。保留全部 Node 代码，改动最小，是"先跑通、后优化"的稳妥路径 |
| **Tauri v2** | **零安装**（依赖系统 WebView2） | 约 5–15 MB | ✅ 本机已核实 WebView2 存在。体积与体验最优，但需 Rust 工具链（本机未装），且核心要改为 Rust |
| **自包含 .NET 发布** | **零安装** | 约 60–80 MB | 若 M4 用 .NET 写 MFT 加速内核，可让扫描内核与服务统一到一个 .NET 宿主，**减少"两个运行时"的复杂度** |
| **Electron** | **零安装** | 约 80–150 MB | 兼容性最省心，体积与内存代价最大 |
| 源码分发 + 要求装 Node | **需装 Node** | 0 | ❌ 只适合作开发者路径，**不可作为面向小白用户的主路径** |

### 10.2 影响该决策的技术联动（已核实，见附录 A.7）

- **`node:sqlite` 是 Node 22 内置模块**（本机验证可用）。原生模块（如 better-sqlite3）恰恰是单文件打包最容易卡住的地方，所以 **M1 就应以 `node:sqlite` 为默认实现**，把原生依赖降级为可选优化 —— 这是一个"现在选对、以后省事"的决定。
- 前端构建为静态资源，由本地服务直接托管；用户看到的是一个窗口或本地网页，**不需要知道 Node 的存在**。
- M4 还需一并解决：安装包与签名、自动更新、卸载入口、首次启动的防火墙/杀软提示、端口占用与单实例。

### 10.3 决策要求

**M2 末尾必须给出结论**，判据只有一条：**一个不懂技术的用户拿到产物后，不做任何环境配置就能看到 C 盘分析结果。** 未达成则该里程碑不算完成。

## 附录 A：本机实测基线

> 采集时间：2026-09-17。所有数字均为在当前开发机上真实执行命令得到的结果（PowerShell 5.1 / Node v22.22.3）。
> 用途：① 作为架构决策依据；② 作为 M1 的回归验证基线。
> 隐私说明：本文档涉及用户目录的路径已做匿名化处理（统一写作 `C:\Users\<用户名>\`），数值本身未做任何修改。

### A.1 磁盘与环境

| 项目 | 实测值 |
|---|---|
| C 盘容量 | 总 **150.7 GB**；已用 **144.8 GB**；剩余 **5.9 GB** |
| 操作系统 | Windows 10 22H2（内部版本 19045） |
| Node.js / npm | v22.22.3 / 10.9.8 |
| .NET SDK | 10.0.302（可用于 M4 的 MFT sidecar） |
| PowerShell | 5.1 |
| 未安装 | Python、Rust、Go |
| **当前权限** | **非管理员**（`IsInRole(Administrator) = False`） |
| AI 环境 | **无任何预置 API Key**；**未安装 Ollama**、本地 11434 端口无服务 |

**结论**：BYOK 与"无 Key 降级"是**必需功能**而非可选增强；非管理员是默认运行假设。

### A.2 大块占用（真实清理目标）

| 路径 | 实测大小 | 备注 |
|---|---|---|
| `C:\Windows\WinSxS` | **9.99 GB** | 含大量硬链接，直接求和会重复计算 |
| `C:\Users\<用户名>\AppData\Local\Packages` | **6.22 GB** | UWP 应用数据 |
| 回收站（`$Recycle.Bin`） | **2.28 GB**（2,140 项） | 可安全清空 |
| `C:\Windows\Installer` | **1.60 GB** | 卸载/修复依赖，**不可手删** |
| `C:\ProgramData\Package Cache` | **0.45 GB** | VS/运行时安装缓存 |
| Chrome 缓存（`...\Default\Cache`） | **0.28 GB** | `safe` |
| Edge 缓存（`...\Default\Cache`） | **0.25 GB** | `safe` |
| `C:\Users\<用户名>\AppData\Local\Temp` | **0.16 GB** | `safe`（使用中的文件跳过） |
| `C:\Users\<用户名>\AppData\Local\NVIDIA` | **0.04 GB** | 可清理着色器缓存 |
| `C:\Windows\Logs` | **0.03 GB** | `safe` |
| `C:\Windows\Panther` | **0.01 GB** | 安装日志 |
| `C:\Users\<用户名>\Downloads` | **0.01 GB** | 用户数据，**不建议工具代删** |
| `C:\Windows\LiveKernelReports` | ≈0.00 GB | 异常时才会增长 |
| `C:\Windows\Prefetch` | ≈0.00 GB | 删除收益极低且影响启动优化 |
| `C:\Windows\Temp` | **0.00 GB** | 已接近空 |
| `C:\Windows\SoftwareDistribution\Download` | **0.00 GB** | 未积压更新包（未打补丁时会显著增长） |

### A.3 不存在的常见"传说中大文件"

| 路径 | 实测 |
|---|---|
| `C:\hiberfil.sys` | **不存在**（休眠未启用） |
| `C:\pagefile.sys` | **不存在**（虚拟内存可能位于其他盘） |
| `C:\swapfile.sys` | **不存在** |
| `C:\Windows.old` | **不存在**（无升级残留） |

**结论**：规则库与文案不能假设"这些文件一定存在"。缺项时 UI 应显示"未发现"，而非 0 GB 占位 —— 二者对用户的含义完全不同。

### A.4 权限盲区（必须显式披露）

| 现象 | 实测 |
|---|---|
| `C:\Windows\System32\drivers\DriverData` | `scandir` 抛 **EPERM**（权限不足） |
| `C:\Program Files\WindowsApps` | 求和结果为 **0.00 GB**（实为读不到，非真为空） |
| `C:\Windows\System32`（递归） | 出现 18 处读取错误 |

**结论**：非管理员扫描的结果天然是**下界**。UI 必须常驻提示未授权目录数量，并提供提权重扫入口。

### A.5 性能基线

| 方案 | 实测 | 推算全盘（百万级文件） |
|---|---|---|
| Node 同步递归遍历 | `System32`：17,715 文件 / 9.42 GB → **8.1 s**（**2,187 文件/秒**） | **5–8 分钟** —— 不满足 PRD 的 60 s 目标 |
| Node 异步无上限并发 | **退化/停滞**（实测中出现迟迟不返回、输出为空） | 不可用，**验证了"有界并发 + 背压"是硬要求** |
| `robocopy /L /E` 枚举 | `System32` → **4.0–7.4 s** | 快于路线 (a)，但输出解析脆弱、无硬链接信息 → 仅作辅助 |
| NTFS MFT 直读 | 未实测（本机无实现） | 预计秒级 → M4 |

### A.6 基线快照（供 M1 回归对比）

```
C 盘剩余            5.9 GB
WinSxS              9.99 GB   (物理口径需去重)
AppData\Local\Packages 6.22 GB
回收站              2.28 GB / 2,140 项
Windows\Installer   1.60 GB
ProgramData\Package Cache 0.45 GB
Chrome/Edge 缓存    0.28 / 0.25 GB
未授权目录          ≥3 处（含 DriverData、WindowsApps）
扫描速度基线        2,187 文件/秒（同步递归，System32）
```

### A.7 分发相关实测（M2 分发决策依据）

| 项目 | 实测值 |
|---|---|
| WebView2 Evergreen 运行时 | **已安装**，版本 **153.0.4234.32**（随 Win10 22H2 的 Edge 存在）→ Tauri 路线可用 |
| Node SEA 单文件打包 | **实测通过**：产物 **87.1 MB**，在剥离 Node 的 PATH 环境下成功运行 |
| 单文件 exe 的外部依赖 | 仅 `ntdll.dll` / `KERNEL32.DLL` / `KERNELBASE.dll` 等系统库（无 Node 依赖） |
| 当前 `node.exe` 体积 | 86,969,160 字节（86.9 MB） |
| `node:sqlite` 内置模块 | **可用**（Node v22.22.3，带 experimental 警告）→ 可避免原生模块 |
| .NET 运行时 | 已装（含 10.0.10）→ 自包含发布路线可用 |

---

## 附录 B：待决事项（不阻塞 M1，进入对应阶段前拍板）

| 事项 | 影响 | 决策时机 |
|---|---|---|
| 仓库名与开源协议 | 仓库对外形象 | M1 开始前 |
| 分发形态（单文件 exe / Tauri / Electron / 自包含 .NET） | **用户是否需要装运行时** | **M2 末尾决策**（见 §10），M4 实现 |
| MFT 加速实现：.NET sidecar vs Rust native | 打包复杂度与性能 | M4 |
| 是否内置离线小模型 | 安装包体积 | 当前判断：**不做**，先用规则库降级 |
| SQLite 驱动选择：`node:sqlite` vs better-sqlite3 | 原生依赖与打包 | **M1 起默认 `node:sqlite`**（内置、无原生模块，避免阻塞单文件打包）；better-sqlite3 降级为可选优化 |


