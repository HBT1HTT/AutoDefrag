using System.IO;
using System.Media;

namespace AutoDefrag;

/// <summary>
/// 整理完成提示音：运行时合成约 0.95 秒的上行双音（E5 → B5，正弦 + 少量谐波，带淡入淡出），
/// 直接以内存 WAV 播放，不落盘、不依赖第三方音频素材，随 exe 一起编译进单文件。
/// 系统静音、无音频设备或播放失败时只记一条“已跳过”，不抛异常、不阻塞界面。
/// </summary>
public static class CompletionSound
{
    private const int SampleRate = 44100;
    private const double DurationSeconds = 0.95;

    /// <summary>峰值电平：约 -7.5 dBFS，安静环境下清楚但不刺耳。</summary>
    private const double PeakLevel = 0.42;

    private static readonly object Gate = new();
    private static SoundPlayer? _player;

    /// <summary>异步播放（SoundPlayer.Play 自身在后台线程播放，立即返回）。</summary>
    public static void Play()
    {
        try
        {
            var player = GetPlayer();
            if (player is null)
            {
                Logger.Info("完成提示音已跳过：当前系统没有可用的声音输出");
                return;
            }

            player.Play();
            Logger.Info("完成提示音已播放");
        }
        catch (Exception ex)
        {
            Logger.Info("完成提示音已跳过：播放失败（" + ex.GetType().Name + "）");
        }
    }

    private static SoundPlayer? GetPlayer()
    {
        lock (Gate)
        {
            if (_player is not null) return _player;

            var stream = new MemoryStream(BuildWav(), writable: false);
            var player = new SoundPlayer(stream);
            player.Load();   // 预加载，播放时不再解析 WAV 头
            _player = player;
            return _player;
        }
    }

    /// <summary>合成 44.1kHz / 16bit / 单声道 WAV 字节。</summary>
    private static byte[] BuildWav()
    {
        var total = (int)(SampleRate * DurationSeconds);
        var samples = new short[total];

        for (var i = 0; i < total; i++)
        {
            var t = i / (double)SampleRate;

            // 第一声 E5（659Hz）短促起音，第二声 B5（988Hz）稍长收尾，构成“上行 + 完成”的听感
            var value = 0.55 * Note(t, 0.00, 0.34, 659.25)
                        + 0.78 * Note(t, 0.235, 0.70, 987.77)
                        + 0.10 * Note(t, 0.235, 0.55, 1975.53);   // 高八度泛音，增加清脆感

            samples[i] = (short)Math.Clamp(value * PeakLevel * short.MaxValue, short.MinValue, short.MaxValue);
        }

        using var ms = new MemoryStream(44 + total * 2);
        using var bw = new BinaryWriter(ms);
        var dataBytes = total * 2;

        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);                       // fmt chunk 大小
        bw.Write((short)1);                 // PCM
        bw.Write((short)1);                 // 单声道
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);           // 字节率 = 采样率 × 声道 × 位深/8
        bw.Write((short)2);                 // 块对齐
        bw.Write((short)16);                // 位深
        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);

        foreach (var sample in samples) bw.Write(sample);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>单个音：短起音 + 指数衰减 + 结尾淡出，避免爆音；含二次谐波让音色更接近提示铃。</summary>
    private static double Note(double t, double start, double duration, double frequency)
    {
        var local = t - start;
        if (local < 0 || local > duration) return 0;

        var attack = Math.Min(1, local / 0.018);
        var release = Math.Min(1, (duration - local) / 0.10);
        var envelope = attack * release * Math.Exp(-2.4 * local / duration);

        var phase = 2 * Math.PI * frequency * local;
        var tone = Math.Sin(phase) + 0.28 * Math.Sin(phase * 2);
        return tone * envelope;
    }
}
