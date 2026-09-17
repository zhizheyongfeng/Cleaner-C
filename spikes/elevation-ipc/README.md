# 提权与 IPC 验证脚本（spike）· 一次性验证代码

> ⚠️ **这是探索性验证脚本，不是产品代码。** 目的是回答"按需提权方案是否可行"，产出的数字被引用为 [`docs/ARCHITECTURE.md`](../../docs/ARCHITECTURE.md) 附录 A.10 与 §11 的依据。

## 它验证了什么

| 结论 | 实测（本机，2026-09-17） |
|---|---|
| **无免提权读取路径** | 非管理员 `GENERIC_READ` / `GENERIC_WRITE` / `FILE_READ_DATA` 全部 `err=5`；`fsutil` 的 `ntfsinfo` / `usn readjournal` / `usn enumData` 同样拒绝访问 |
| 非提权仅能做 | `FILE_READ_ATTRIBUTES` 打开句柄、`usn queryjournal` 元数据、`file queryfileid` 单文件 FRN —— **都拿不到文件大小** |
| **UAC 往返耗时** | **1193 ms**（含用户点击"是"） |
| 提权后能力 | 可打开 `\\.\C:` 并读取 |
| **提权后工作目录** | ⚠️ 变为 `C:\Windows\system32` → 实现必须用绝对路径 |
| 用户点"否" | `Win32Error=1223 (ERROR_CANCELLED)` |
| **跨完整性级别 IPC** | ✅ 未提权宿主创建命名管道服务端，**提权客户端可连接并传输数据** |
| **同文件自提权（双模式）** | ✅ 已验证：同一脚本靠参数切换角色并自我提权 |

## 运行方式

需要**一个非管理员会话**启动（脚本会自行请求提权，届时会弹 UAC）：

```powershell
# ① 卷访问权限 + 按需提权往返
powershell -NoProfile -ExecutionPolicy Bypass -File .\elevation-probe.ps1

# ② 命名管道跨完整性级别通信（会再弹一次 UAC）
powershell -NoProfile -ExecutionPolicy Bypass -File .\pipe-probe.ps1
```

`vol.cs` 是 `CreateFileW` / `ReadFile` / `CloseHandle` 的 P/Invoke 声明，由上面两个脚本通过 `Add-Type -Path` 加载。

## 踩过的坑（已写入架构文档）

1. **设备路径转义**：首版脚本用 shell heredoc 生成，`\\` 被折成 `\`，得到非法的 `\.\C:`，`CreateFileW` 返回 `err=123 (ERROR_INVALID_NAME)` —— 一度被**误读为权限问题**。现改用字符码构造路径（见 `$devPath`）。
2. **命名管道异步模式**：服务端未传 `[System.IO.Pipes.PipeOptions]::Asynchronous` 时，`BeginWaitForConnection` 抛 `InvalidOperationException`（"管道尚未以异步模式打开"）。
3. **提权进程工作目录变化**：提权后 CWD 变为 `C:\Windows\system32`，脚本内一律使用 `$PSScriptRoot` 构造绝对路径。

## 目录说明

| 文件 | 说明 |
|---|---|
| `elevation-probe.ps1` | 卷访问权限矩阵 + 按需提权往返 + 子进程回传 |
| `pipe-probe.ps1` | 命名管道跨完整性级别通信 + 同文件双模式自提权 |
| `vol.cs` | P/Invoke 声明（被上述脚本加载） |

> **注意**：脚本运行会在当前目录生成 `elev2-inner.txt` / `pipe-client-report.txt` 等临时报告。这些是运行产物，不应提交（若需要请加入忽略规则）。
