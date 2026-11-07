# AutoDefrag

Windows 固定磁盘定期优化工具的桌面版（WPF + .NET 10），单文件绿色版，目标机无需安装 .NET 运行时。

- 版本：**v1.1.0**
- 发布形态：`win-x64` 自包含单文件（压缩）
- 可执行文件：`publish/AutoDefrag.exe`（约 66.8 MB）

## 功能

- **磁盘多选**：主界面勾选需要维护的本地固定磁盘，勾选/取消后立即写入配置，下次启动保持；不会出现“操作一次就丢失状态”。
- **碎片率探测**：调用系统 `defrag <盘> /A` 读取碎片率并记录。
- **一键优化**：调用 `defrag <盘> /O /H`；主进程以普通权限运行，真正执行整理时由同一个 exe 以管理员身份启动 worker（会触发 UAC 提示）。
- **CPU 守护**：系统 CPU 持续高于阈值时临时挂起 `defrag.exe`，CPU 回落后自动恢复；阈值可在界面调整。
- **每日调度**：可设置每日执行时间；支持“启动后立即执行一次”（时间填 `0`），以及当天尚未执行时的启动补跑开关。
- **托盘常驻**：关闭窗口最小化到托盘；托盘菜单支持打开主界面、立即优化、暂停/恢复每日调度、查看日志、退出，菜单状态随操作实时更新。
- **外观**：跟随系统的浅色/深色主题，也可手动切换；支持选择界面字体（列表为系统已安装字体，选中立即生效并保存）。
- **开机自启**：写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`（值名 `AutoDefrag`），指向当前 exe。
- **日志与结果**：应用日志、引擎日志、最近一次结果与进度状态均落盘，便于排查。

## 使用

1. 双击 `AutoDefrag.exe` 启动（主界面为普通权限，不会一启动就弹 UAC）。
2. 在左侧磁盘列表勾选目标磁盘，点击“探测碎片率”或“立即优化”。
3. 点击“立即优化”时 Windows 会请求管理员权限：这是 `defrag.exe` 执行整理所必需的。
4. 关闭窗口默认最小化到托盘；需要彻底退出时使用托盘菜单的“退出”。

### 数据与配置

- 优先使用 exe 所在目录：`Data/config.json`、`Data/last_run.txt`、`Data/last_result.txt`、`Data/drive_status.json`、`Logs/app.log`、`Logs/engine.log`。
- 若 exe 所在目录不可写（例如放在 `Program Files`），自动回退到 `%LocalAppData%\AutoDefrag`。
- 旧版 PowerShell 版本的 `Data/config.json` 可直接读取；缺少的新字段使用默认值，原有值保留。

### 命令行（内部使用）

同一个 exe 支持 worker 模式，供主进程提权调用，一般无需手动使用：

```
AutoDefrag.exe --worker clean --drives "C:,A:" --threshold 70
AutoDefrag.exe --worker status
```

## 构建

需要 Windows 10/11 x64 与 .NET 10 SDK：

```powershell
dotnet publish -c Release src/AutoDefrag/AutoDefrag.csproj -o publish -p:EnableCompressionInSingleFile=true
```

产物为 `publish/AutoDefrag.exe` 单文件，自包含、已嵌入图标，无 trimming。

## 注意事项与风险

- **首次启动可能立即开始整理**：默认调度开关开启、每日时间为 `03:00`、且当天尚未执行时，启动会触发一次补跑；首次运行还会默认勾选全部本地固定磁盘。建议首次启动前先取消不需要的磁盘，或在托盘中暂停调度，观察后再执行。
- 整理耗时取决于磁盘容量、碎片情况与介质类型：SSD 通常很快，机械硬盘可能持续很久。请避免在关键工作或移动办公时运行，笔记本请保持供电。
- 同一磁盘请勿同时运行多个整理工具（例如同时运行本程序与其他碎片整理软件），以免互相争抢卷锁。
- CPU 守护会在高负载时**临时挂起** `defrag.exe`，属于预期行为；任务管理器里可能看到该进程暂停。
- 每日执行锁按“当天是否已执行”记录；删除 `Data/last_run.txt` 会导致下次启动重新判定当天是否需要执行。
- 本程序调用 Windows 自带的 `defrag.exe`，不包含第三方磁盘驱动；对磁盘的写操作由系统组件完成。

## 目录

```
src/AutoDefrag/      WPF 源码、资源与清单
tools/make-icon.ps1  图标生成脚本
publish/             发布产物（AutoDefrag.exe，不纳入版本库）
```

`bin/`、`obj/`、`publish/`、运行期 `Data/`、`Logs/` 均已通过 `.gitignore` 排除。
