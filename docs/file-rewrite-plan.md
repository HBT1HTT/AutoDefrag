# AutoDefrag 1.3 实现方案：大簇卷自算分析 + 文件级重写整理

> 版本标记：`1.3.0-test1`
> 适用范围：簇大小 > 4KB 的 NTFS 卷（当前重点 64KB；128KB 走同一套代码，但需另做 VHD 验证）
> 小簇卷（≤ 4KB）行为不变，继续调用系统 `defrag.exe`。
> 本文是实现前定稿的方案说明，代码落地后以代码与实际日志为准。

---

## 1. 目标与边界

### 1.1 要解决的问题

系统 `defrag.exe` 在 64KB 簇卷上的实测表现：

- `defrag A: /A` 能返回 `exit=0`，但“总碎片空间 = 0%”，且自己声明“大于 64MB 的文件碎片不会计入”；
- `defrag A: /D /H` 约 1 秒结束、`exit=0`，没有产生可验证的整理效果。

因此大簇卷上**不能再用 `defrag.exe` 的退出码或碎片率作为依据**，也不能把“成功”当作“整理过”。

### 1.2 本轮做到什么

| 能力 | 小簇卷（≤4KB） | 大簇卷（>4KB） |
|---|---|---|
| 碎片率来源 | 保持 `defrag /A` | **自算**：卷位图 + 文件 Runlist |
| 空闲布局 | 不展示 | 自算：空闲区间数、最大连续空闲、可整理上限 |
| 实际整理 | `defrag /D`、`/X`、`/O`（保持现状） | **文件级重写**（复制 → 校验 → 还原元数据 → 原子替换 → 释放旧空间） |
| DFS_MOVE_FILE | 不涉及 | 不使用 |
| 结果口径 | 保持 v1.2.0 判定 | “比整理前干净，但不承诺 0 碎片”；跳过的文件必须写清原因 |

### 1.3 明确不做

- 不在系统盘（C:）做任何文件重写整理；
- 不并行搬大文件，不做全盘无上限搬迁；
- 不碰 NTFS 元数据（`$MFT`、`$Bitmap`、`$LogFile` 等），不绕开文件系统直接写簇；
- 不承诺把碎片整理到 0；大簇卷最小单位就是 1 簇（64KB），必然会有零头；
- 不覆盖 `publish/`，不推 GitHub。

---

## 2. 文件清单

新增（均在 `src/AutoDefrag/Rewrite/` 下）：

| 文件 | 职责 |
|---|---|
| `NtfsNative.cs` | 卷句柄、`FSCTL_GET_NTFS_VOLUME_DATA`、`FSCTL_GET_VOLUME_BITMAP`、`FSCTL_GET_RETRIEVAL_POINTERS`；文件句柄/硬链接数/ADS/安全描述符（SDDL）P/Invoke |
| `NtfsModels.cs` | `VolumeGeometry`、`Extent`、`FileLayout`、`FreeRun`、`VolumeLayout`、`CandidateFile`、`RewriteOptions`、`RewriteFileResult`、`RewriteSummary` |
| `VolumeAnalyzer.cs` | 位图 → 空闲区间统计；文件枚举 → Runlist → 碎片指标；卷级/文件级汇总 |
| `RewritePlanner.cs` | 候选过滤、预检、打分排序、批次上限、dry-run 计划生成 |
| `RewriteExecutor.cs` | 单文件事务：复制 → 哈希校验 → 元数据还原 → 原子替换 → 复核 → 释放旧空间；失败回滚 |
| `RewriteReport.cs` | 生成 `Data/rewrite_report.txt`（每个文件前后 extent、跳过原因、汇总），日志同步写 `Logs/engine.log` |
| `LargeClusterRunner.cs` | 大簇卷入口：分析 → 计划 → 执行（或 dry-run）→ 复算 → 返回 `DriveRunResult` |
| `RewriteCommand.cs` | 顶层 `--dry-run` / `--rewrite` 命令行入口，支持 `--drives`、`--path`、`--quiet` |

修改：

| 文件 | 改动 |
|---|---|
| `DefragEngine.cs` | `RunClean` 按簇大小分流：≤4KB 走原逻辑；>4KB 交给 `LargeClusterRunner`。`RunStatus` 在大簇卷上用自算碎片率替换 `defrag /A` |
| `App.xaml.cs` | 识别顶层 `--dry-run`，不进 UI、不建托盘，直接执行预览并退出 |
| `MainViewModel.cs` | 新增 `EngineMode`（持久化）、整理方式文案、大簇卷提示、`StopClean()` 中止 |
| `SettingsWindow.xaml(.cs)` | 新增“整理方式”两档单选：本程序整理 / Windows 自带整理 |
| `AppConfig.cs` | 新增 `EngineMode`（`Self` 默认 / `System`），非法值回落 `Self` |
| `WorkerService.cs` / `WorkerHost.cs` | 传递 `--engine Self|System`，worker 按同一策略执行 |
| `App.xaml.cs` | 托盘新增“停止整理”“预览整理（不动磁盘）”；顶层 `--dry-run` / `--rewrite` 直接执行后退出 |
| `MainWindow.xaml(.cs)` | 底部新增“停止整理”按钮（仅整理中可见） |
| `AppPaths.cs` | 新增 `RewriteReportFile`、`RewritePreviewFile`、`StopFlagFile` |
| `AutoDefrag.csproj` | 版本号 `1.3.0-test1` |
| `README.md` | 增加大簇卷能力、整理方式、dry-run 与限制说明 |

---

## 3. 核心数据结构

```csharp
sealed record VolumeGeometry(
    int BytesPerSector, int SectorsPerCluster, int ClusterBytes,
    long TotalClusters, long FreeClusters, long MftStartLcn);

/// StartLcn = -1 表示稀疏/空洞区段
sealed record Extent(long StartVcn, long StartLcn, long LengthClusters);

sealed record FileLayout(
    string Path, long SizeBytes, int ExtentCount,
    long TotalClusters, long LargestExtentClusters, long FragmentedClusters,
    bool HasSparse);

sealed record FreeRun(long StartLcn, long LengthClusters);

sealed class VolumeLayout
{
    public long TotalClusters, FreeClusters;
    public int FreeRunCount;
    public long MaxFreeRunClusters;          // 最大连续空闲
    public double FreeFragmentationPercent;  // (1 - 最大连续空闲/总空闲) * 100
    public long RewriteUpperBoundClusters;   // 可整理上限（见 5.2）
}

sealed class AnalysisResult
{
    public VolumeGeometry Geometry;
    public VolumeLayout Volume;
    public List<FileLayout> Files;           // 已成功读到 Runlist 的文件
    public int FilesScanned, FilesProbed, FilesUnreadable;
    public double FileFragmentationPercent;  // 已分析文件里“非最大区段簇数”占比
    public bool ScanTruncated;               // 是否因为时间/数量上限提前停止
}

sealed class CandidateFile
{
    public FileLayout Layout;
    public long Score;
    public string ScoreReason;
}

sealed class RewriteOptions
{
    public bool DryRun;
    public int MaxFiles = 40;                // 单次最多处理文件数
    public long MaxBytes = 8L * 1024 * 1024 * 1024; // 单次最多复制字节数
    public int MinFileClusters = 8;          // 小于 8 簇（64KB 簇=512KB）不参与
    public int MaxScanFiles = 200_000;       // 枚举上限
    public int MaxExtentProbes = 20_000;     // Runlist 查询上限
    public int ScanSeconds = 60;             // 扫描时间预算
    public double MinFragmentationPercent = 5.0;
    public long SafetyReserveClusters;       // 安全余量，见 5.2
    public string StopFlagFile;              // 出现即停止下一文件
}
```

---

## 4. 只读分析（A）

### 4.1 卷几何与位图

```
handle = CreateFile("\\.\A:", GENERIC_READ, FILE_SHARE_READ|WRITE, OPEN_EXISTING)
FSCTL_GET_NTFS_VOLUME_DATA → BytesPerCluster / TotalClusters / FreeClusters / MftStartLcn
FSCTL_GET_VOLUME_BITMAP(StartingLcn=0, 缓冲区 = ceil(TotalClusters/8) + 余量)
```

位图语义：**bit=1 表示已分配，bit=0 表示空闲**；最后一个字节超出 `TotalClusters` 的位忽略。

**权限事实（本机实测修正）**：`FSCTL_GET_VOLUME_BITMAP` **需要管理员权限**。普通用户打开 `\\.\A:` 后调用该 IOCTL 返回 `Win32Error = 1 (ERROR_INVALID_FUNCTION)`。因此：

- 普通用户执行的 `--dry-run`：报告写“未能读取卷位图（该接口需要管理员权限），本次只用文件区段数据”，空闲区间/最大连续空闲记为 `--`，**不猜测**；
- 管理员 worker 执行的真实整理：位图可用，报告含完整空闲布局；
- 卷几何（簇大小、总簇、空闲簇）走 `FSCTL_GET_NTFS_VOLUME_DATA`，普通用户可读，失败时回退 `GetDiskFreeSpaceW`。

统计：

```
freeRuns[] = 连续 0 位组成的 [起始 LCN, 长度]
FreeRunCount = freeRuns.Count
MaxFreeRunClusters = max(length)
FreeFragmentationPercent = (1 - MaxFreeRunClusters / FreeClusters) * 100
```

### 4.2 文件 Runlist

对每个候选路径：

```
handle = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ|WRITE|DELETE)
FSCTL_GET_RETRIEVAL_POINTERS(StartingVcn=0)
缓冲区不足（ERROR_MORE_DATA）→ 翻倍重试，上限 16MB
解析：Extent[i].StartVcn = 前一项 NextVcn（首项为 StartingVcn）
      Extent[i].LengthClusters = NextVcn - StartVcn
      Lcn == -1 表示稀疏
```

碎片指标：

```
FragmentedClusters = TotalClusters - LargestExtentClusters
FileFragmentationPercent = FragmentedClusters / TotalClusters * 100
```

卷级文件碎片率：对已分析文件求和 `ΣFragmentedClusters / ΣTotalClusters * 100`。
**口径写清楚**：如果扫描因时间/数量上限提前结束，报告中标注“抽样”，不冒充全卷。

### 4.3 大簇场景的处理

- 最小移动单位是 1 簇：64KB 卷上移动 1 个簇就是 64KB，碎片数量天然比 4KB 卷少 16 倍；
- 不套用 4KB 卷的“碎片率阈值”，候选门槛按“簇数”定义（默认文件 ≥ 8 簇，且非最大区段 ≥ 1 簇）；
- 空闲区至少能容纳 1 个簇才有意义；`MaxFreeRunClusters` 是“能不能整理”的关键指标；
- 系统 `defrag` 声明“大于 64MB 的文件不计入”，我们的分析覆盖所有大小（受扫描上限约束）。

### 4.4 与小簇卷 `defrag /A` 的一致性对比方法

小簇卷继续用 `defrag /A`。为了说明两套口径的差异，在 `RunStatus` 中对小簇卷额外做一次自算，并把两者同时写日志：

```
自算: 文件碎片率 X%（基于 Runlist，按簇加权）
系统: defrag /A 总碎片空间 Y%
说明: 两者统计对象与分母不同（系统按“碎片文件空间/卷容量”，自算按“非最大区段簇数/已分析文件簇数”），
      不要求数值相等，只要求量级与趋势一致；差异超过 10 个百分点时写 WARN。
```

`--dry-run` 报告也会同时打印两种口径，便于逐卷核对。

---

## 5. 候选选择与排序（B 的前置）

### 5.1 候选过滤（先排除，再打分）

硬性跳过（写日志，计入“跳过”）：

| 类别 | 判定 |
|---|---|
| 系统关键文件 | 文件名 `pagefile.sys`、`hiberfil.sys`、`swapfile.sys`、`DumpStack.log*`、根目录 `$*`；属性含 `System` |
| 系统目录 | `\Windows\`、`\Program Files\`、`\Program Files (x86)\`、`\ProgramData\`、`\System Volume Information\`、`\$Recycle.Bin\`、`\Recovery\`、`\Boot\`、`\EFI\` |
| 稀疏/压缩/加密/重解析点 | 属性含 `SparseFile` / `Compressed` / `Encrypted` / `ReparsePoint` |
| 硬链接 | `GetFileInformationByHandle` 的 `nNumberOfLinks > 1` |
| 被占用 | `CreateFile(..., FILE_SHARE_NONE)` 失败且错误为 `ERROR_SHARING_VIOLATION` |
| 无权限/读不到 Runlist | 打开或 IOCTL 失败 |
| 太小 | 文件簇数 < `MinFileClusters`（默认 8 簇） |
| 本来连续 | `ExtentCount <= 1` |
| 碎片太少 | `FragmentedClusters < 1` 或碎片率 < `MinFragmentationPercent` |
| 收益太小 | 可回收空间 `FragmentedClusters × 簇大小` < `MinReclaimBytes`（默认 4MB）——避免为几簇碎片整文件重写 |
| 空间不足 | 可用空闲 < 文件大小 + 安全余量（执行阶段再核对一次） |
| 路径过长/临时文件 | 路径 > 240 字符、`*.adf-tmp-*`、`*.adf-bak-*` |

### 5.2 排序依据

目标：**优先处理“碎片浪费最多、体积适中、移动成本可控”的文件**，而不是盲目挑最大文件。

打分公式（单位统一为“簇”）：

```
waste      = FragmentedClusters                    // 重写后理论上可回收的碎片簇
extentGain = min(ExtentCount - 1, 20) * 2          // 减少的区段数，最多记 40 分
copyCost   = TotalClusters * 0.02                  // 复制成本惩罚（大文件降权）
sizeBias   = TotalClusters >= 2048 ? 20 : 0        // 大文件（64KB×2048 = 128MB 以上）额外加权
score      = waste + extentGain + sizeBias - copyCost
```

排序：`score` 降序；同分时优先“碎片率更高”，再按文件大小降序。
理由：

1. `waste` 是真正能回收的空间，直接对应“整理效果”；
2. `extentGain` 让“区段很多”的文件排前面，减少后续随机读；
3. `copyCost` 避免把 50GB 单文件排在最前，导致一轮只处理一个文件；
4. `sizeBias` 体现“优先把大文件碎片移到空闲区”的意图，但被 `copyCost` 约束，不会失控；
5. 不做并行，单文件失败不影响整批。

> 关于“移到卷尾空闲区”：`CopyFile`/普通写入无法指定目标 LCN，NTFS 会选择当前可用空间。
> 我们能做的是“选择碎片最多的大文件先处理”，并在报告中记录实际落点；不虚构“已移到卷尾”。

### 5.3 批次上限

默认单次最多 `40` 个文件、`8GB` 复制量，避免自动任务在用户不知情时长时间占用磁盘。
达到上限后剩余候选只记录在报告里，下次继续。

---

## 6. 单文件事务流程

```
1. 预检（执行前再查一次）
   - 属性：System / Sparse / Compressed / Encrypted / ReparsePoint → 跳过
   - 硬链接数 > 1 → 跳过
   - 独占打开测试（FILE_SHARE_NONE）失败 → 跳过（被占用）
   - 可用空间 ≥ 文件大小 + 安全余量 → 否则该文件中止
2. 快照元数据
   - 属性、创建/访问/修改时间（UTC）
   - 安全描述符 SDDL（Owner + Group + DACL）
   - 备用数据流（ADS）名称列表
   - 原文件大小、簇数、Runlist（before）
3. 复制到同目录临时文件 `<name>.adf-tmp-<guid>`
   - 同卷保证新数据分配在本卷
   - **先 `SetLength(原文件长度)` 一次性预分配全部空间**：让 NTFS 用单次分配请求挑足够大的连续空闲区。
     实测若按 1MB 分块边写边分配，NTFS 会给每块单独挑洞，512MB 文件从 4 段变成 31 段（更碎）；
     预分配后同一文件 4 段 → 1 段。
   - 顺序写入，复制完调用 FlushFileBuffers
   - 复制 ADS（逐个命名流）
4. 校验内容
   - ≤ 2GiB：SHA-256 全量
   - > 2GiB：大小 + 首/中/尾各 4MiB 的 SHA-256 抽样（报告中注明“抽样”）
   - 不一致 → 删除临时文件，保留原文件，记失败
5. 还原元数据到临时文件
   - 时间戳、属性
   - 安全描述符：SetNamedSecurityInfo(Owner|Group|Dacl)；Owner/Group 与当前一致时只设 DACL（普通用户改 Owner 会被拒 rc=5）
   - 回读 SDDL 与快照比对；比对前去掉 DACL 段的 `AI/AR/AP` 控制标志——从父目录继承的 DACL 会显示 `D:AI(...)`，
     ACE 列表本身相同，不能当作差异；ACE 内容仍逐个比对，不一致 → 中止（保留原文件）
6. 原子替换
   - File.Replace(temp, original, backup="<name>.adf-bak-<guid>")
   - 替换失败 → 原文件仍在，删除临时文件，记失败
7. 替换后复核
   - 文件大小一致、可读、Runlist（after）已更新
   - 复核失败 → 用备份反向 Replace 恢复原文件，记失败
8. 释放旧空间
   - 删除备份文件（旧数据簇随删除释放）
   - 若删除失败：保留备份并写 WARN，不影响新文件
9. 记录
   - before/after 的 extent 列表、簇数、区段数、耗时、哈希模式
```

### 失败与回滚

| 失败点 | 处理 |
|---|---|
| 预检不通过 | 跳过，不产生任何写入 |
| 复制中失败/空间不足 | 删除临时文件，原文件不动 |
| 哈希不一致 | 删除临时文件，原文件不动，记“校验失败” |
| ACL/时间戳还原失败 | 删除临时文件，原文件不动，记“元数据还原失败” |
| Replace 失败 | 删除临时文件，原文件不动 |
| Replace 后复核失败 | 用备份恢复原文件；恢复也失败则保留备份并红色告警 |
| 备份删除失败 | 保留备份（可在报告里看到路径），新文件已生效 |

所有路径都遵守：**验证通过前不删除原文件；任何一步失败都不留半成品。**

### 可中止

执行器在**每个文件开始前**检查 `Data/rewrite_stop.flag`，存在则停止后续文件并写日志；当前文件不会被强行中断。
托盘/命令行都可创建该文件实现“体面停止”。

### CPU 守护

系统方式的守护是“挂起/恢复 `defrag.exe`”：真实整理进程如此，只读的 `/A` 预检与 `/A` 复核也如此
（1.4.3 起；读碎片率时同样按阈值挂起该 defrag 进程，停止时先恢复再终止）。文件级重写由本进程自己读写，
没有子进程可挂起，因此在**分析/枚举、复制、校验、预检、替换后复核、重写后复算**各阶段周期性采样系统 CPU（每秒一次）：
持续高于阈值 10 秒 → 当前线程原地阻塞、暂停读写；回落到阈值以下并稳定 10 秒 → 继续。
暂停/继续次数写入 `worker_state.json` 与 `last_result` 的对应字段，语义与原有守护一致，只是实现形式不同。
注意：阈值必须高于平时的后台占用；若低于常态占用，任务会长时间暂停。

---

## 7. 结果口径与报告

### 7.1 状态

- `成功`：计划内文件全部成功，且至少处理了 1 个；
- `部分成功`：有成功也有失败/跳过；
- `无可整理文件`：分析完成但没有满足条件的文件（不报失败）；
- `失败`：分析阶段就无法读取卷/位图；
- `已停止`：检测到停止标记。

### 7.2 报告内容（`Data/rewrite_report.txt`）

```
卷 A:  簇=65536  总簇=...  空闲簇=...
空闲区间=685  最大连续空闲=2474713 簇(154669 MiB)  可整理上限=...
分析：枚举=... 查询Runlist=... 读取失败=... 是否抽样=...
起点碎片率=...%（文件级，按簇加权）
候选 12 个，计划处理 10 个（上限 40 文件 / 8GB）
[1/10] A:\path\file.bin  大小=...  碎片=...%  区段=...
   before: LCN 12345 x 20, 13000 x 5
   after : LCN 999999 x 25
   哈希=全量  结果=成功  用时=...
[2/10] ... 结果=跳过  原因=文件正在使用（共享冲突）
...
跳过汇总：被占用=3  系统=12  稀疏/压缩/加密/重解析=2  硬链接=1  无权限=4  空间不足=1
总体：成功=8 失败=1 跳过=1  重写=...GB  回收碎片簇=...
```

日志 `Logs/engine.log` 同步记录以上关键行，**不记录任何文件内容**。

### 7.3 dry-run

- `DryRun=true` 时只做分析、预检、排序，输出计划与“如果执行会怎么排序”，**不创建临时文件、不替换、不改任何文件**；
- dry-run 报告文件名固定为 `Data/rewrite_preview.txt`，避免和真实执行结果混在一起。

---

## 8. 与现有代码的集成

### 8.1 `DefragEngine.RunClean`

```
clusterBytes = Native.GetClusterSize(drive)
if (DiskService.IsLargeCluster(clusterBytes))
     if (engineMode == "System")  → 记 Warn，结果="系统方式无效（该磁盘）"，不报成功
     else                        → results.Add(LargeClusterRunner.Run(drive, media, clusterBytes, state, dryRun:false))
else
     原逻辑：Probe(/A) → BuildPlan → RunOneDrive → Probe(/A)
```

整理方式来自设置（`EngineMode`），默认 `Self`：
`System` = 全部走 Windows 自带 `defrag.exe`（1.2.0 行为）；`Self` = 小簇卷仍用 `defrag.exe`，大簇卷改用文件级重写。
大簇卷 + `System` 时不再调用 `defrag`（它对该盘无效），直接给出明确结论，避免“1 秒报成功”。

### 8.2 `DefragEngine.RunStatus`

```
if (largeCluster)
     frag = VolumeAnalyzer.Analyze(drive, quick:true).FileFragmentationPercent
else
     frag = Probe(drive).FragPercent   // 保持现状
```

### 8.3 顶层命令行

```
AutoDefrag.exe --dry-run                      # 预览所有 >4KB 固定磁盘，不动盘
AutoDefrag.exe --dry-run --drives "A:"        # 指定卷
AutoDefrag.exe --dry-run --path "A:\MyDir"    # 只分析指定目录（小规模验证用），盘符自动推导
AutoDefrag.exe --rewrite --path "A:\MyDir"    # 真实整理指定目录（管理员运行时位图可用）
AutoDefrag.exe --rewrite --drives "A:"        # 真实整理整卷（受 40 文件 / 8GB 单批上限约束）
加 --quiet 不弹结果对话框。
```

dry-run 不弹 UAC、不建托盘、不开主窗口，完成后写 `Data/rewrite_preview.txt`；
真实整理写 `Data/rewrite_report.txt`。两者都同步把关键行写 `Logs/engine.log`，不记录文件内容。

---

## 9. A: 实测结果（2026-11-08，64KB 簇，普通用户权限）

### 9.1 受控碎片化测试（`A:\AutoDefragTest\big.dat`，512MB）

造碎片：顺序写 8×64MB，穿插 8×64MB 占位文件后再删除，使 `big.dat` 落到 4 个不连续区段。
实测 before（`fsutil file queryextents` 与自算一致）：

```
区段=4  [20861074+1026, 25855899+2058, 25227490+4089, 21689643+1019]
```

`--rewrite --path A:\AutoDefragTest --quiet` 实测结果：

```
after : 区段=1  [18287319+8192]
可回收碎片 = 4103 簇（约 256.4 MB）
文件级碎片率 50.09% → 0%
校验=全量 SHA256  用时=8.65s
```

元数据核对（全部保留）：

- 大小 512MB 不变；
- 修改时间 2020-01-01 保留；
- SDDL 完全一致（含显式 `Everyone:(R)` ACE）；
- 备用数据流 `meta`（内容 `hello-ads`）保留。

### 9.2 整卷 dry-run（A:）

```
枚举=53799  查询Runlist=4333  读取失败=0  已分析文件=4333  文件级碎片率=25.61%
候选=95  本次选中=7  合计=8 GB  模式=dry-run
```

选中顺序的前 6 个都是 1.1–1.8GB 的高碎片游戏资源文件（碎片率 66%–90%），第 7 个是回收 7.1MB 的本项目构建产物。
普通用户无管理员权限时，报告会写“未能读取卷位图（该接口需要管理员权限）”，空闲区间相关字段为 `--`。

### 9.3 用户可自行复现

1. `dist\AutoDefrag-test.exe --dry-run --drives "A:"`：只读预览，确认候选排序与碎片率。
2. 在自己准备的测试目录（如 `A:\MyTestDir`）先造碎片，再 `--rewrite --path`。
3. 用管理员 `fsutil file queryextents <文件>` 对照报告里的 before/after。
4. 检查权限、ADS、时间戳是否保持。
5. 全程不应出现 C: 的任何写入。

---

## 10. 已知限制

- 无法保证复制后的文件一定连续：NTFS 决定分配位置；若新区段数没有减少，按失败/跳过处理并保留原文件；
- 无法整理 `$MFT`、`$Bitmap`、`$LogFile` 等元数据；
- 正在使用、系统关键、稀疏/压缩/加密/重解析点、硬链接文件一律跳过；
- 大文件复制成本高，SSD 上还有额外写入；
- 128KB 簇未在本机实测，代码不假设簇大小，但仍需 VHD 验证；
- 不承诺 0 碎片，只承诺“可验证地减少区段/回收碎片簇”。
