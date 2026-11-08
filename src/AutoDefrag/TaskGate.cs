using System.Threading;

namespace AutoDefrag;

/// <summary>
/// 任务级互斥：同一时间只允许一个整理/检查/预览任务操作磁盘。
/// worker 进程、UI 预览线程、命令行 dry-run 都走这里，跨进程生效（同一会话的 Local 命名空间）。
/// 进程异常退出时互斥量由系统自动释放，不会留下死锁。
/// </summary>
public static class TaskGate
{
    private const string MutexName = @"Local\AutoDefrag.TaskGate";

    /// <summary>本进程当前持有的互斥量（只有真正执行任务时才持有）。</summary>
    private static Mutex? _held;

    /// <summary>尝试进入任务；timeoutMs=0 表示立刻返回。</summary>
    public static bool TryEnter(out string? error, int timeoutMs = 0)
    {
        error = null;

        // 同一进程内已经持有时直接拒绝：Mutex 对同一线程是可重入的，
        // 否则嵌套调用会覆盖 _held，导致第一次的句柄泄漏、IsBusy 也会误报“空闲”。
        if (_held is not null)
        {
            error = "已有另一个整理/检查/预览任务正在运行";
            return false;
        }

        try
        {
            var mutex = new Mutex(false, MutexName);
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(timeoutMs);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者异常退出：系统已释放，当前线程接管所有权
                acquired = true;
            }

            if (acquired)
            {
                _held = mutex;
                return true;
            }

            mutex.Dispose();
            error = "已有另一个整理/检查/预览任务正在运行";
            return false;
        }
        catch (Exception ex)
        {
            error = "无法建立任务互斥: " + ex.Message;
            return false;
        }
    }

    public static void Leave()
    {
        var mutex = _held;
        _held = null;
        if (mutex is null) return;
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }

    /// <summary>UI 侧探测：不等待，立刻返回是否已有任务在跑。</summary>
    public static bool IsBusy(out string? error)
    {
        if (!TryEnter(out error, 0))
            return true;

        Leave();
        error = null;
        return false;
    }
}
