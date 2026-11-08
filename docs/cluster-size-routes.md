# 大簇卷整理：三条技术路线评估

> 适用范围：AutoDefrag v1.2.0 之后的技术选型。本文只做评估与路线图，**不改动已发布版本行为，不包含实现代码**。
> 目标：在“支持所有簇大小、兼容性尽量高、性能尽量好”的前提下，决定整理能力应该往哪条路走。

---

## 0. 结论先行

| 项 | 结论 |
|---|---|
| **推荐主线** | **路线 A：只读分析 + 建议**（自读卷位图与文件 Runlist，自己算碎片指标；整理仍交给系统，系统不支持时明确说明） |
| **推荐补充** | **路线 B：单文件重写整理**，作为显式勾选的可选功能，不做自动、不做全盘 |
| **不推荐** | **路线 C：libntfs-3g / 自研 NTFS 离线写入**，投入与风险远大于收益；仅保留“镜像只读分析”作为研究性分支 |
| **关键事实** | Windows 的 `FSCTL_MOVE_FILE` 在大簇卷上不可依赖；而 `FSCTL_GET_VOLUME_BITMAP`、`FSCTL_GET_RETRIEVAL_POINTERS` 与簇大小无关，**已在本机 64KB 簇卷上实测可用** |
| **立刻能改善体验的部分** | 不再依赖 `defrag /A` 的“0% 碎片”结果；所有簇大小都能显示真实碎片率、空闲布局与最大连续空闲区 |

一句话：**“分析”可以做到全簇大小覆盖，“整理”做不到全簇大小覆盖；先把分析做对、把能力边界说清楚，再用可选的文件级重写补一部分整理效果。**

---

## 1. 现状与实测证据

### 1.1 本机卷信息（只读查询）

| 卷 | 文件系统 | 簇大小 | 容量 | 可用 |
|---|---|---|---|---|
| A: | NTFS | **65,536 字节（64KB）** | 1.82 TB | 567.1 GB |
| C: | NTFS | 4,096 字节（4KB） | 94.6 GB | 40.9 GB |

A: 正好是“大簇卷”的真实样本，本轮所有读接口验证都在它上面完成。

### 1.2 读接口实测（64KB 簇，只读，无任何写盘）

在 A: 上直接调用（非管理员、普通用户权限）：

| 接口 | 结果 |
|---|---|
| `FSCTL_GET_NTFS_VOLUME_DATA` | 成功：`BytesPerCluster=65536`、`TotalClusters=30,523,647`、`FreeClusters=9,291,628` |
| `FSCTL_GET_VOLUME_BITMAP` | 成功：位图 3,815,456 字节，覆盖全部 30,523,647 簇 |
| `FSCTL_GET_RETRIEVAL_POINTERS` | 成功：`AutoDefrag.exe`（66.8MB）返回 1 个区段、1,069 簇、起始 LCN 11,001,632 |

位图统计（按文档 `1=已分配` 解析）：

- 已分配 **21,232,019** 簇；空闲 **9,291,628** 簇（约 567 GB，与卷报告的空闲簇完全一致）；
- 空闲区间 **685 个**，平均每个区间 **13,564 簇（约 847 MB）**；
- **最大连续空闲 2,474,713 簇 = 154,669 MiB（约 151 GiB）**；
- 最大连续空闲占全部空闲空间的 **26.6%**。

补充：把卷句柄的访问权限改为 `0` 时，两个 IOCTL 都返回 `err=1`（`ERROR_INVALID_FUNCTION`），说明**读位图需要真正的卷句柄读权限**；在数据卷 A: 上普通用户即可获得，系统卷 C: 未测（需管理员）。

**结论：位图与 Runlist 读取不依赖 4KB 簇假设，64KB 簇卷工作正常。** 这是路线 A 能成立的核心依据。

### 1.3 系统 defrag 在 64KB 簇卷上的实际表现（只读分析，真实日志）

v1.2.0 的管理员 worker 在 A: 上执行过 `defrag A: /A`，日志原文要点：

```
exit=0
卷大小 = 1.81 TB，可用空间 = 570.37 GB
总碎片空间 = 0%
可用空间的最大大小 = 154.25 GB
注意: 大于 64MB 的文件碎片不会计入碎片统计信息。
不需要对该卷进行碎片整理。
操作成功完成。
```

随后 `defrag A: /D /H` 的结果是 **1 秒结束、exit=0**。

解读：

- `defrag /A` 在 64KB 簇卷上**能返回成功**，但给出的“总碎片空间 0%”明显不可信，且它自己声明**大于 64MB 的文件不参与统计**；
- `可用空间的最大大小 = 154.25 GB`（与我们位图算出的最大连续空闲 154,669 MiB 属同一量级；两者统计时点与单位口径不同，只作参考，不做逐字节核对）；
- `defrag /D` 1 秒结束，符合“参数被接受但并没有真正移动数据”的表现。

**结论：在当前大簇卷上，系统 defrag 的“成功”不能当作“整理过”的证据。** 这也是 v1.2.0 在 A: 上显示 `--` 碎片率、并把结果标成“可能不支持/无法确认”的原因。

### 1.4 `FSCTL_MOVE_FILE` 的簇大小限制：证据强度分级

| 证据 | 内容 | 强度 |
|---|---|---|
| Microsoft 官方文档（`FSCTL_MOVE_FILE`、`Defragmenting Files`） | 说明了移动粒度、支持的文件/流类型、16KB 对齐建议，但**没有写簇大小上限** | 官方，但“未提及” |
| Microsoft 官方文档（`FSCTL_GET_VOLUME_BITMAP`） | 只说明“需要卷句柄、只有管理员能打开卷句柄”，**没有簇大小限制** | 官方 |
| 第三方技术文档（Digiater, “NT Defragging”） | 明确写：`FSCTL_MOVE_FILE` 不支持大于 4KB 的簇，返回 `STATUS_INVALID_DEVICE_REQUEST` | 第三方，较早 |
| OSR 开发者社区帖子（2006） | 开发者称“文档说错误原因是簇大小大于 4KB”，其 4KB 卷仍报 error 1 | 社区，间接 |
| **本机行为证据** | 64KB 卷上 `defrag /A` 报 0% 碎片、`/D` 1 秒完成；系统整理没有产生实际效果 | 实测，但只能证明“不可依赖”，不能直接证明具体返回码 |

**关于 128KB 簇：没有实测。** 本机没有 128KB 簇卷，且当前会话不是管理员，无法创建测试 VHD。因此本文对 128KB 的结论一律标注为“待验证”，不写成已确认事实。

**本轮结论（置信度：高，但非“本机已实测返回码”）**：`FSCTL_MOVE_FILE` 在大簇卷上不能作为整理能力的基础；即使个别系统版本接受请求，也不能保证实际移动了数据。**AutoDefrag 不应把 `FSCTL_MOVE_FILE` 当作核心方案。**

### 1.5 复制语义实测（用于评估路线 B）

在 A:（64KB 簇）上对一个测试文件做 `Copy-Item`（底层 `CopyFileW`）：

| 项 | 结果 |
|---|---|
| 文件数据 | 完整复制 |
| 备用数据流（ADS，`file:meta`） | **保留**，内容一致 |
| 显式 ACL（`Everyone:Read`） | **丢失**：目标只保留了目录继承来的 ACL |
| 文件 ID / 硬链接身份 | 新文件，新 ID（复制语义决定） |

**结论：B 路线不能直接依赖系统复制来“搬文件”，ACL、硬链接、所有者在替换时都要专门处理，否则会静默改变权限语义。**

---

## 2. 三条路线对比

### 2.1 路线 A：只读分析 + 建议

**做什么**：`FSCTL_GET_VOLUME_BITMAP` 读全卷空闲位图；`FSCTL_GET_RETRIEVAL_POINTERS` 逐文件读 Runlist；自己计算碎片数量、碎片大小分布、空闲区间分布、最大连续空闲区、可整理上限；整理动作仍然调用系统 `defrag`，系统不支持时明确告诉用户原因。

**能覆盖的簇大小**：4KB / 8KB / … / 64KB / 128KB / 更大，**理论上全部**（这两个 IOCTL 以“簇”为单位，不假设 4KB；64KB 已实测，128KB 待验证）。

**整理效果**：**不直接产生整理效果**。它解决的是“看得见、说得清、不误报”。

**性能**：很好。位图是 3.8MB/30M 簇，读取与统计在秒级；逐文件 Runlist 取决于文件数量，可按需抽样/限制。

**实现工作量**：小。核心是 IOCTL 封装、Runlist 解析、统计与 UI 展示；不需要碰任何写路径。

**风险**：极低（只读）。唯一风险是“分析结果被误读成整理结果”，需要文案与状态严格区分。

**可维护性**：高。接口稳定，逻辑是纯计算。

### 2.2 路线 B：文件级重写整理（copy → verify → 替换）

**做什么**：把文件内容读出来，写到同卷的新位置，让 NTFS 重新分配连续区间；校验通过后替换原文件。

**能覆盖的簇大小**：**全部**（与簇大小无关），但受“文件能否被独占打开”限制。

**整理效果**：**局部、单文件**。大簇卷上确实可能让某个大文件重新连续；但：

- 不能整理 `$MFT`、`$Bitmap`、`$LogFile` 等元数据；
- 不能整理正在使用/被锁定的文件（页面文件、注册表、系统日志、运行中的程序）；
- 不能改变全卷空闲布局，可能把“大空闲区”消耗掉，反而让后续整理更难；
- 小文件（< 1 簇）在大簇卷上本来就是一个簇，重写没有意义。

**性能**：差。等于把文件完整读一遍、写一遍：HDD 上远慢于簇移动；SSD 上有额外写入与寿命成本。一个 50GB 文件就要 100GB 的读写量。

**实现工作量**：中。复制本身不难，难在“安全替换”：ACL/ADS/硬链接/稀疏/压缩/EFS/时间戳/文件 ID/占用检测/回滚/校验。

**风险**：中。任何一步做错都会造成数据丢失或权限变化；必须“原文件在验证通过前绝不删除”。

**可维护性**：中。每个 Windows 文件系统特性都是一个分支，测试面很大。

**是否“性能和兼容性都不错的折中”？** **兼容性不错（覆盖所有簇大小），性能不好（全量读写），整理效果有限（只能单文件）。** 它适合作为“用户明确点选某个大文件”的补充功能，不适合作为默认整理策略。

### 2.3 路线 C：离线 / 镜像（libntfs-3g 或自研 NTFS 解析）

**能覆盖的簇大小**：**上游 libntfs-3g 最多 64KB**；128KB 直接被拒绝。

代码级证据（`libntfs-3g/bootsect.c`，`ntfs_boot_sector_is_ntfs`）：

```c
i = (u32)le16_to_cpu(b->bpb.bytes_per_sector) * b->bpb.sectors_per_cluster;
if (i > 65536) {
    ntfs_log_error("Unexpected cluster size (%d).\n", i);
    goto not_ntfs;
}
```

也就是说：**128KB 簇的卷连“识别为 NTFS”这一步都过不了**，不是“某个函数有隐患”，而是库的入口校验就拒绝。要支持 128KB，必须修改库源码并重新验证所有写入路径。

其他事实：

- `ntfs-3g` 的挂载形态依赖 FUSE；Windows 没有 FUSE，挂载路线不可用；
- `ntfsprogs`（`mkntfs`、`ntfsclone`、`ntfscp` 等）虽然不依赖 FUSE，但构建系统是 autotools + POSIX（`unistd.h`、`pread`、`ioctl`、pthread），没有官方 Windows 移植；
- MSYS2 包仓库检索（`packages.msys2.org/search?q=ntfs-3g`）返回空结果，**未发现现成的 MinGW 构建**（该页面为动态渲染，结论以“未发现”为准，不代表绝对不存在）；
- 即使成功编译，写入还要面对：NTFS 日志（`$LogFile`）兼容、卷脏位、Windows 缓存、独占访问、断电一致性。

**整理效果**：理论最强（可以真正移动任意簇、整理元数据）。

**性能**：取决于自研程度；离线全卷扫描/搬运的 I/O 量很大。

**实现工作量**：极大。要自己实现或裁剪移植 NTFS 解析、Runlist 编解码、MFT/属性更新、位图分配、事务/回滚，并做大量破坏性测试。

**风险**：极高。任何元数据写错都可能让卷无法挂载；恢复成本高。

**可维护性**：低。需要长期跟踪 NTFS 细节与 Windows 版本差异。

**结论**：**不值得作为主线投入。** 只有在“必须离线修复/整理镜像，且用户能接受专用工具 + 完整备份”的条件下才考虑，且第一版也只做“镜像只读分析”。

### 2.4 对比表

| 维度 | A 只读分析 | B 单文件重写 | C 离线/镜像 |
|---|---|---|---|
| 覆盖簇大小 | **全部**（64KB 实测；128KB 待验证） | **全部**（与簇大小无关） | **≤64KB**（128KB 被 libntfs 拒绝） |
| 整理效果 | 无（只分析与建议） | 单文件可能连续；不处理元数据/占用文件 | 理论最强 |
| 性能 | **好**（秒级读位图） | 差（全量读写，SSD 磨损） | 取决于实现，I/O 大 |
| 实现工作量 | **小** | 中 | 极大 |
| 风险 | **极低**（只读） | 中（需严格回滚） | 极高（元数据损坏） |
| 可维护性 | **高** | 中 | 低 |
| 对现有架构改动 | 小（新增分析器 + UI） | 中（新增安全替换流程） | 极大（几乎重写引擎） |
| 建议 | **主线** | **可选补充** | **不做** |

---

## 3. 推荐方案与边界

### 3.1 主线：A（分析全覆盖）+ B（可选单文件）

**为什么是 A：**

1. 它直接解决了用户最痛的点：大簇卷上“不知道到底碎不碎、系统说 0% 不可信”；
2. 它**不写盘**，不会与第三方整理工具、系统缓存、卷锁冲突；
3. 它覆盖所有簇大小，且已经有 64KB 实测支撑；
4. 它能给 B 提供决策依据（最大连续空闲区、文件碎片分布、是否值得重写）。

**为什么 B 只能做补充：**

- 它是唯一能在“不碰 NTFS 元数据”的前提下，对大簇卷产生真实整理效果的手段；
- 但它无法全卷整理，也不能保证成功，性能代价高；
- 因此必须“用户明确选择文件 + 前置条件检查 + 校验 + 可回滚”，绝不能做成默认或自动。

**明确边界：**

- A 永远只读；任何“整理”都必须由用户显式触发；
- B 只处理**普通、非系统、可独占打开、非压缩/加密/稀疏/硬链接、ADS 可安全复制**的文件；
- B 在替换前必须完成哈希校验，原文件在验证通过前不删除；
- 大簇卷上 B 的收益用“是否减少跨区段数量”衡量，不承诺“消除碎片”；
- **在线系统卷写入不实现**；C 的写入分支不进入产品。

### 3.2 与 v1.2.0 的差异（用户能立刻感知的部分）

| 场景 | v1.2.0 现在 | 路线 A 落地后 |
|---|---|---|
| 64KB 簇卷碎片率 | 依赖 `defrag /A`，常显示 `--`（实测返回“0%”，不可信） | 自己读 Runlist 计算，显示真实碎片文件数/碎片率 |
| 64KB 簇卷空闲情况 | 只有“可用/共” | 增加“最大连续空闲区”“空闲区间数”等可执行信息 |
| 128KB 簇卷 | 只能提示“可能不支持” | 同样能分析（待 VHD 验证） |
| 大簇卷“为什么不能整理” | 一行提示 + 日志 | 明确说明“系统整理接口不支持该簇大小”，并给出“可整理上限” |
| 结果判定 | 已改为看证据（v1.2.0） | 分析结果不再依赖不可信的系统退出码 |

### 3.3 不做什么（本轮明确排除）

- 不把 `FSCTL_MOVE_FILE` 当作核心方案；
- 不假设 libntfs-3g “稍作修改就能用”；
- 不在线写系统卷；
- 不做“无 dry-run、无日志、无回滚”的元数据操作；
- 不在本轮修改 v1.2.0 的任何代码行为。

---

## 4. 路线 A 的设计要点（只读分析器）

### 4.1 模块划分（建议目录）

```
src/AutoDefrag.Analysis/
  Volume/
    RawVolume.cs          // \\.\X: 或镜像文件的随机读
    BootSector.cs         // BPB：bytes/sector、sectors/cluster、簇大小、MFT LCN
    NtfsVolumeData.cs     // FSCTL_GET_NTFS_VOLUME_DATA 封装
  Bitmap/
    VolumeBitmap.cs       // FSCTL_GET_VOLUME_BITMAP + 位图游程统计
  Runlist/
    RetrievalPointers.cs  // FSCTL_GET_RETRIEVAL_POINTERS 解析
    FragmentStats.cs      // 碎片数、最大区段、碎片大小分布
  Analyze/
    VolumeAnalyzer.cs     // 汇总：卷级 + 文件级
    FreeSpacePlanner.cs   // 最大连续空闲、可整理上限
  Report/
    AnalysisReport.cs     // 结构化结果（UI + JSON 导出）
```

### 4.2 核心数据结构（原型级）

```csharp
sealed record VolumeLayout(
    long TotalClusters, long FreeClusters, int ClusterBytes,
    long MaxFreeRunClusters, int FreeRunCount,
    double FreeFragmentScore);          // 0..1，越高越碎

sealed record FileFragment(
    ulong FileId, string Path, long SizeBytes,
    int ExtentCount, long LargestExtentClusters,
    double FragmentationPercent);       // (1 - 最大区段/总簇数) * 100

sealed record FreeExtent(long StartLcn, long LengthClusters);

sealed class AnalysisReport
{
    public VolumeLayout Volume;
    public IReadOnlyList<FreeExtent> TopFreeExtents;
    public IReadOnlyList<FileFragment> TopFragmentedFiles;
    public long RecommendedMoveUpperBoundClusters; // 可整理上限
    public string Conclusion;                       // 通俗结论
}
```

### 4.3 分析流程（伪代码）

```
读取卷几何（Boot + FSCTL_GET_NTFS_VOLUME_DATA）
位图 = FSCTL_GET_VOLUME_BITMAP(起始 LCN=0, 足够大的缓冲)
空闲游程 = 扫描位图，统计 0 位连续段
对候选文件：
    Runlist = FSCTL_GET_RETRIEVAL_POINTERS(文件句柄)
    若 Runlist 解析失败 → 记为“无法分析”，不猜测
    碎片度 = 1 - 最大区段簇数 / 文件总簇数
汇总：
    卷级空闲碎片化 = 1 - 最大连续空闲 / 总空闲
    可整理上限 = min(最大连续空闲, 空闲总量 - 安全余量)
    结论 = 用通俗中文描述“碎不碎、值不值得整理、系统能不能整理”
```

### 4.4 大簇场景的特殊处理

- **最小移动单位是 1 簇**：64KB 簇卷上，移动 1 个簇就是 64KB；“碎片数”对比 4KB 卷天然小 16 倍，**不能拿 4KB 卷的碎片率阈值直接套用**。
- **“小于 2 簇的碎片不值得移动”**：大簇卷上 2 簇 = 128KB，移动成本高于收益。
- **空闲区至少要容纳 1 个簇**才有意义；`MaxFreeRunClusters` 是判断“能否整理”的关键指标。
- **文件碎片统计的分母用“簇”而不是“字节”**，避免大簇卷被低估。
- **超过某个大小的文件单独标注**：系统 defrag 自己就声明“大于 64MB 的文件碎片不计入统计”，我们的分析要明确覆盖它，而不是沿用系统口径。

### 4.5 128KB 簇的待验证点

| 验证点 | 方法 | 通过标准 |
|---|---|---|
| `FSCTL_GET_NTFS_VOLUME_DATA` 返回 `BytesPerCluster=131072` | VHD 上调用 | 数值正确 |
| `FSCTL_GET_VOLUME_BITMAP` 正常返回 | 同上 | 位图覆盖全部簇，空闲数与卷报告一致 |
| `FSCTL_GET_RETRIEVAL_POINTERS` 正常解析 | 在大文件上调用 | 区段数与实际布局一致 |
| MFT 记录仍为 1024 字节 | 读 `$MFT` 首记录 | `clusters_per_mft_record` 为负值编码 |
| 位图大小与缓冲上限 | 32TB/128KB ≈ 2.68 亿簇 ≈ 32MB 位图 | 缓冲分配正确，不截断 |
| 128KB 卷上系统 defrag 是否可用 | 管理员 + VHD | 记录实际返回码（只记录，不修盘） |

VHD 测试命令（**需要管理员**，只用于测试卷，绝不对生产卷执行）：

```powershell
diskpart
create vdisk file="C:\Temp\ntfs128.vhd" maximum=512 type=fixed
select vdisk file="C:\Temp\ntfs128.vhd"
attach vdisk
create partition primary
format fs=ntfs quick unit=128
assign letter=Q
exit
# 只读测试完成后：
# select vdisk file="C:\Temp\ntfs128.vhd" / detach vdisk / delete
```

**注意**：本轮环境不是管理员，以上测试**尚未执行**；结论中所有 128KB 相关表述都按“待验证”处理。

---

## 5. 路线 B 的安全设计要点（若采纳为补充功能）

### 5.1 前置条件（任一不满足即拒绝）

- 文件位于同一 NTFS 卷，且不是系统文件/元数据；
- 能以 `share=0` 独占打开（否则“正在使用” → 跳过）；
- 硬链接数 = 1（`GetFileInformationByHandle`）；
- 非压缩、非加密、非稀疏、非重解析点；
- ADS 已枚举并可完整复制（或用户明确同意丢弃）；
- 目标空闲区 ≥ 文件大小 × 1.05，且来自**同一连续空闲区**；
- 有足够时间与电量（笔记本）。

### 5.2 流程（复制 → 校验 → 替换）

```
1. 读取原文件元数据（大小、时间、ACL、ADS 列表、属性）
2. 在同卷创建临时文件，预分配 AllocationSize（尽量一次拿到连续区间）
3. 流式复制数据（大缓冲，顺序 I/O），复制 ADS
4. FlushFileBuffers
5. 逐块哈希校验（源 vs 临时文件），校验 ACL/ADS 是否完整
6. 关闭源句柄 → 用 ReplaceFileW 或“重命名旧文件 → 重命名新文件”替换
7. 保留旧文件直到新文件通过一次完整读回校验，再删除
8. 失败：删除临时文件，原文件保持不动；记录日志
```

### 5.3 不可回避的限制

- 无法保证新位置连续：NTFS 可能仍拆成多个区段；只能“尽量”；
- 无法处理正在使用的文件、页面文件、注册表、`$MFT` 等；
- 需要双倍空间，峰值 I/O 是文件大小的 2 倍以上；
- 文件 ID 会变化，依赖 File ID 的备份/去重/链接工具可能受影响；
- 时间戳、所有者、审计 SACL 需要显式还原（实测 `CopyFileW` 不保留显式 ACL）。

---

## 6. 安全约束（适用于任何写路径）

| 阶段 | 允许的操作 | 禁止 |
|---|---|---|
| 阶段 1：只读分析 | 读位图、读 Runlist、读 MFT（只读） | 任何写盘 |
| 阶段 2：镜像 / 离线卷写入 | 在**副本镜像**上演练移动、校验、回滚 | 对生产卷写入 |
| 阶段 3：真实离线卷 | 用户显式确认 + 完整备份 + dry-run 通过 | 在线系统卷写入 |

强制要求（写路径一旦启动）：

1. **dry-run 默认开启**：输出移动计划（源 LCN、目标 LCN、涉及文件、Runlist 更新点、风险等级），不实际写；
2. **写前备份**：至少备份 `$Bitmap`、`$MFT` 前若干记录、目标文件 Runlist；
3. **操作前后对比**：记录每个文件的 Runlist 变化，写入日志；
4. **失败即停**：任何校验失败立刻停止，保留原状，不“继续尝试”；
5. **不做在线系统卷写入**：该能力标记为高风险，产品中不实现。

---

## 7. 实施路线图

> 以“先分析、后写入；先镜像、后真实卷”为原则。所有阶段都不影响已发布的 v1.2.0。

| 阶段 | 内容 | 交付物 | 验收标准 |
|---|---|---|---|
| **P0（MVP）** | 只读分析器：卷几何、位图统计、文件 Runlist、碎片指标；UI 展示；替换对 `defrag /A` 的依赖 | 分析模块 + 主界面信息 + JSON 报告 | 4KB / 64KB 卷结果与位图一致；128KB 用 VHD 通过；不写盘 |
| **P1** | 空闲布局规划与建议：最大连续空闲、可整理上限、Top 碎片文件、通俗结论 | 规划器 + 报告 | 结论与实测位图一致；大簇卷不再显示“0%” |
| **P2** | 单文件重写整理（可选、dry-run、校验、回滚）；严格白名单与跳过规则 | 单独入口 + 安全替换流程 | 测试文件校验通过；失败可完整回滚；不触碰系统文件 |
| **P3（可选）** | 镜像只读分析（离线镜像作为输入） | 镜像读取适配 | 能读镜像并给出与实卷一致的分析 |
| **不做** | 在线卷写入、libntfs-3g 集成、FSCTL_MOVE_FILE 作为核心 | — | — |

**第一版 MVP 的范围（严格）**：

- 只做“**能分析 128KB 簇卷**”，不做任何写入；
- 覆盖 4KB / 64KB / 128KB 的卷几何、位图、Runlist、碎片指标；
- 输出通俗结论与可整理上限；
- 不改动 v1.2.0 的整理行为与发布产物。

---

## 8. 风险清单

| 风险 | 影响 | 缓解 |
|---|---|---|
| 把“分析成功”误读为“已整理” | 用户误判 | UI 明确区分“分析/整理”，结果文案不承诺 |
| 位图是时间点快照 | 规划结果过期 | 写前重新读取；失败即停 |
| 大簇卷阈值套用 4KB 经验 | 结论失真 | 以“簇”为单位统计；单独阈值 |
| 128KB 未实测 | 结论过度承诺 | 标注“待验证”；VHD 验收后才宣称支持 |
| B 路线 ACL/ADS/硬链接丢失 | 权限或数据语义变化 | 显式还原 + 硬链接/ADS 检测 + 不满足即跳过 |
| B 路线空间不足/中途失败 | 临时文件残留、原文件风险 | 预分配检查 + 校验后替换 + 失败回滚 |
| 断电/崩溃 | 元数据或临时文件不一致 | 写路径仅在离线卷/镜像；保留原文件 |
| 与第三方整理工具并发 | 卷锁冲突、结果异常 | 检测运行中的 defrag 进程；提示后退出 |
| SSD 额外写入 | 寿命与性能 | B 路线默认关闭，仅用户显式选择 |

---

## 9. 待验证清单（明确未做）

1. `FSCTL_MOVE_FILE` 在 64KB / 128KB 簇卷上的**实际返回码**（需管理员 + 测试 VHD，本轮禁止写盘，未执行）；
2. `FSCTL_GET_VOLUME_BITMAP` / `FSCTL_GET_RETRIEVAL_POINTERS` 在 **128KB** 簇卷上的实测（需 VHD）；
3. libntfs-3g 在 Windows 上以 MSVC/MinGW 编译的可行性（**结论：不推荐**；仅在需要镜像分支时做 spike）；
4. 128KB 卷上系统 defrag 的行为（只记录，不修复）；
5. B 路线在压缩/加密/稀疏/硬链接文件上的跳过规则覆盖率。

---

## 10. 证据来源

- Microsoft Learn — [Defragmenting Files](https://learn.microsoft.com/en-us/windows/win32/fileio/defragmenting-files)（移动粒度、16KB 对齐、支持的文件/流类型；未提簇大小上限）
- Microsoft Learn — [FSCTL_MOVE_FILE](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_move_file)（未提簇大小上限）
- Microsoft Learn — [FSCTL_GET_VOLUME_BITMAP](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_volume_bitmap)（位图 1=已分配；需要卷句柄）
- Microsoft Learn — [NTFS overview](https://learn.microsoft.com/zh-cn/windows-server/storage/file-server/ntfs-overview)（簇大小与最大卷/文件）
- 第三方 — [NT Defragging](https://www.digiater.nl/openvms/decus/vmslt99a/nt/defrag.htm)（`FSCTL_MOVE_FILE` 不支持 >4KB 簇，`STATUS_INVALID_DEVICE_REQUEST`）
- OSR 社区 — [FSCTL_MOVE_FILE returns Invalid Function](https://community.osr.com/t/fsctl-move-file-returns-invalid-function/27007)（间接证据）
- ntfs-3g 源码 — [bootsect.c](https://raw.githubusercontent.com/tuxera/ntfs-3g/master/libntfs-3g/bootsect.c)（`cluster size > 65536` 直接拒绝）
- ntfs-3g 源码 — [lcnalloc.c](https://raw.githubusercontent.com/tuxera/ntfs-3g/master/libntfs-3g/lcnalloc.c)（分配器以 `vol->nr_clusters`、`$Bitmap` 为基准）
- MSYS2 包检索 — [packages.msys2.org](https://packages.msys2.org/search?q=ntfs-3g)（检索无结果，未发现现成 Windows 构建）

---

## 11. 本轮验证记录（可复现）

- 卷几何：`fsutil fsinfo ntfsinfo A:` → `Bytes Per Cluster = 65,536`；
- 只读 IOCTL 探针：普通用户打开 `\\.\A:` 卷句柄，成功读取 `FSCTL_GET_NTFS_VOLUME_DATA`、`FSCTL_GET_VOLUME_BITMAP`、`FSCTL_GET_RETRIEVAL_POINTERS`；
- 位图统计与卷报告的空闲簇数一致（9,291,628）；
- `defrag A: /A` 在非管理员下返回 `0x89000024`（与 C: 相同，说明该错误与簇大小无关，是权限/调用条件问题），管理员下的历史日志显示 exit=0 但“总碎片空间 0%”；
- 复制语义：`CopyFileW` 保留 ADS、丢失显式 ACL；
- **未做任何写盘、未移动任何簇、未修改 v1.2.0 代码。**
