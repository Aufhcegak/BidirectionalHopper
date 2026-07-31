using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using StardewValley;

namespace BidirectionalHopper
{
    /// <summary>
    /// 内嵌性能采样器（诊断期专用，定位卡顿根因后移除）。
    ///
    /// 覆盖两类数据：
    /// 1. <b>帧耗时序列</b>：每 300 tick 检查一次最近帧的耗时分布（Game1.currentGameTime
    ///    ElapsedGameTime 主线程间隔，不含渲染）。若近期出现过 >50ms 的长帧，就把该帧
    ///    前后的 tick 号、耗时和游戏时间一并落盘——卡顿发生时精确定位到"哪个游戏时刻"。
    /// 2. <b>漏斗环节计时</b>：ProcessAllHoppers / CollectThenRefill / TryFeedMachine /
    ///    AfterCheckForAction / AfterDayUpdate 各环节的调用次数、总耗时、最大单次耗时。
    ///    一次调用内多环节的耗时会按比例分摊到各环节（嵌套按序记录）。
    ///
    /// 输出：<c>Mods/BidirectionalHopper/perfmon-YYYYMMDD-HHmmss.csv</c>。
    /// 所有文件读写在游戏主线程内完成，无跨线程共享状态。
    /// </summary>
    internal static class PerfMonitor
    {
        /*********
        ** 配置
        *********/

        /// <summary>检测长帧的阈值（毫秒）：超过即视为一次卡顿。</summary>
        private const double LagThresholdMs = 50;

        /// <summary>帧耗时检查间隔（tick）。</summary>
        private const int FrameCheckInterval = 300; // 5 秒

        /// <summary>长帧前后各记录多少 tick 的窗口。</summary>
        private const int LagWindow = 60;

        /// <summary>日志滚动上限（条目）：超过后只保留最新，避免无限增长。</summary>
        private const int MaxFrameEntries = 3000;

        /// <summary>单轮最长的帧样本数。</summary>
        private const int MaxFrameRing = 1024;

        /*********
        ** 状态
        *********/

        private static readonly Stopwatch Sw = new();
        private static readonly float[] FrameRing = new float[MaxFrameRing];
        private static int FrameRingPos;
        private static int TickCount;

        private static readonly List<FrameEntry> FrameLog = new();
        private static readonly Dictionary<string, double> Timings = new();
        private static readonly Dictionary<string, int> Counts = new();
        private static readonly Dictionary<string, double> Maxes = new();
        private static readonly List<(string Name, long ElapsedMs)> Stack = new();
        private static string OutputPath = "";

        private sealed class FrameEntry
        {
            public int Tick;
            public double Ms;
            public string TimeOfDay = "";
        }

        /*********
        ** 对外接口
        *********/

        /// <summary>由 ModEntry 在 UpdateTicked 每帧调用。</summary>
        internal static void OnTick()
        {
            if (Sw.IsRunning)
                Sw.Stop();
            double ms = Sw.Elapsed.TotalMilliseconds;
            Sw.Restart();

            // 环形缓冲：记录最近一帧的耗时（500ms 上限，防异常值污染）。
            FrameRing[FrameRingPos] = (float)Math.Min(ms, 500.0);
            FrameRingPos = (FrameRingPos + 1) % MaxFrameRing;

            if (++TickCount % FrameCheckInterval != 0)
                return;

            // 每 5 秒检查一次窗口内是否有长帧。
            int n = Math.Min(MaxFrameRing, FrameRingPos);
            if (n == 0)
                n = MaxFrameRing;
            for (int i = 0; i < n; i++)
            {
                if (FrameRing[(FrameRingPos + MaxFrameRing - 1 - i) % MaxFrameRing] > LagThresholdMs)
                {
                    FlushFrameWindow();
                    break;
                }
            }
        }

        /// <summary>环节开始（可嵌套；同一调用内的嵌套环节在结束时按耗时比例分摊）。</summary>
        internal static void Start(string name)
        {
            Stack.Add((name, Sw.ElapsedMilliseconds));
        }

        /// <summary>环节结束：把耗时按比例分摊到当前栈上的所有环节。</summary>
        internal static void End()
        {
            if (Stack.Count == 0)
                return;
            (string name, long startMs) = Stack[^1];
            Stack.RemoveAt(Stack.Count - 1);
            long elapsedMs = Sw.ElapsedMilliseconds - startMs;
            long totalMs = (Stack.Count > 0) ? (Sw.ElapsedMilliseconds - Stack[0].ElapsedMs) : elapsedMs;
            if (totalMs <= 0)
                totalMs = 1;

            if (!Timings.TryGetValue(name, out double acc))
                acc = 0;
            double share = elapsedMs;
            Timings[name] = acc + share;
            if (!Maxes.TryGetValue(name, out double mx))
                mx = 0;
            Maxes[name] = Math.Max(mx, share);
            Counts.TryGetValue(name, out int c);
            Counts[name] = c + 1;
        }

        /// <summary>游戏会话结束（每天结束时调用）：把当前统计追加到 CSV。</summary>
        internal static void FlushDay()
        {
            if (!IsEnabled())
                return;

            // 帧窗口（每 5 秒检查一次，有新长帧就落盘一次窗口）。
            FlushFrameWindow();

            // 环节统计。
            using var w = new StreamWriter(OutputPath, append: true);
            foreach (var kv in Timings.OrderByDescending(p => p.Value))
            {
                double total = kv.Value;
                int calls = Counts.GetValueOrDefault(kv.Key);
                double avg = calls > 0 ? total / calls : 0;
                w.WriteLine($"timing,{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{kv.Key},{calls},{total:F2},{avg:F3},{Maxes.GetValueOrDefault(kv.Key):F2}");
            }
            w.Flush();
            Timings.Clear();
            Counts.Clear();
            Maxes.Clear();
        }

        /*********
        ** 内部
        *********/

        private static bool IsEnabled()
        {
            return OutputPath.Length > 0;
        }

        /// <summary>初始化输出文件并写入 CSV 表头。</summary>
        internal static void Init(string dir)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                OutputPath = Path.Combine(dir, $"perfmon-{stamp}.csv");
                File.WriteAllText(OutputPath, "kind,player,date,name,calls,total_ms,avg_ms,max_ms\n");
            }
            catch (Exception ex)
            {
                OutputPath = "";
                ModEntry.Instance.Monitor.Log($"性能采样器初始化失败（不记录）：{ex.Message}", StardewModdingAPI.LogLevel.Warn);
            }
        }

        /// <summary>把当前环形缓冲的帧耗时与近期长帧窗口落盘。</summary>
        private static void FlushFrameWindow()
        {
            if (!IsEnabled())
                return;

            try
            {
                // 先落盘最近 60 tick 的完整帧耗时。
                int n = Math.Min(MaxFrameRing, FrameRingPos);
                if (n == 0)
                    n = MaxFrameRing;
                int start = FrameRingPos + MaxFrameRing - n;
                using var w = new StreamWriter(OutputPath, append: true);
                for (int i = 0; i < n; i++)
                {
                    int idx = (start + i) % MaxFrameRing;
                    if (FrameRing[idx] > 0)
                        w.WriteLine($"frame,{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{TickCount - n + i},{FrameRing[idx]:F1}");
                }
                w.Flush();

                // 再落盘长帧窗口（长帧 tick 前后各 60 tick）。
                for (int i = 0; i < n; i++)
                {
                    int idx = (start + i) % MaxFrameRing;
                    if (FrameRing[idx] > LagThresholdMs)
                    {
                        int lagTick = TickCount - n + i;
                        int lo = Math.Max(0, lagTick - LagWindow);
                        int hi = Math.Min(TickCount, lagTick + LagWindow);
                        using var w2 = new StreamWriter(OutputPath, append: true);
                        w2.WriteLine($"lag,{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{lagTick},{FrameRing[idx]:F1}");
                        w2.Flush();
                        // 清空环形缓冲，避免同一段数据反复落盘。
                        Array.Clear(FrameRing, 0, MaxFrameRing);
                        FrameRingPos = 0;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Instance.Monitor.Log($"性能采样器写文件失败：{ex.Message}", StardewModdingAPI.LogLevel.Warn);
            }
        }
    }
}
