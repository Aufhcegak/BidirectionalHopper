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

        /// <summary>自动落盘间隔（tick）：每 6000 tick≈100 秒写一次，任何退出方式都不丢数据。</summary>
        private const int AutoFlushInterval = 6000;

        /// <summary>单轮最长的帧样本数。</summary>
        private const int MaxFrameRing = 1024;

        /*********
        ** 状态
        *********/

        private static readonly Stopwatch Sw = new();
        private static readonly float[] FrameRing = new float[MaxFrameRing];
        private static int FrameRingPos;
        private static int TickCount;

        private static readonly Dictionary<string, double> Timings = new();
        private static readonly Dictionary<string, int> Counts = new();
        private static readonly Dictionary<string, double> Maxes = new();
        private static readonly List<(string Name, long ElapsedMs)> Stack = new();
        private static string OutputPath = "";
        private static StreamWriter? Writer;

        /*********
        ** 对外接口
        *********/

        /// <summary>最近长帧的 tick 号与帧耗时（运行中只累积，落盘由 FlushDay 统一做）。</summary>
        private static readonly List<(int Tick, double Ms)> PendingLag = new();

        /// <summary>时间切换帧（每 10 分钟）：游戏时间变化那一帧的耗时（timeOfDay, ms）。
        /// 卡顿是否在切换帧，看这个序列的耗时分布即可——正常应 ≈ 普通帧。</summary>
        private static readonly List<(int TimeOfDay, double Ms)> TimeSwitchFrames = new();

        /// <summary>上一次看到的游戏时间（检测切换帧用）。</summary>
        private static int LastTimeOfDay = -1;

        /// <summary>切换帧探针：记录该帧 minutesElapsed 调用次数与总耗时（统计调用次数和耗时分布）。</summary>
        private static int SwitchFrameMinutesElapsedCalls;
        private static double SwitchFrameMinutesElapsedMs;
        private static readonly List<(int TimeOfDay, int Calls, double Ms)> SwitchFrameStats = new();

        /// <summary>由 HopperPatch 的 minutesElapsed prefix 调用：切换帧内统计每次调用耗时。</summary>
        internal static void OnMinutesElapsedStart()
        {
            SwitchFrameMinutesElapsedCalls++;
        }

        /// <summary>切换帧开始时由 OnTick 调用：重置计数器。</summary>
        internal static void BeginSwitchFrame()
        {
            SwitchFrameMinutesElapsedCalls = 0;
            SwitchFrameMinutesElapsedMs = 0;
        }

        /// <summary>切换帧结束时由 OnTick 调用：记录统计。</summary>
        internal static void EndSwitchFrame(int timeOfDay)
        {
            SwitchFrameStats.Add((timeOfDay, SwitchFrameMinutesElapsedCalls, SwitchFrameMinutesElapsedMs));
        }

        /// <summary>由 ModEntry 在 UpdateTicked 每帧调用。</summary>
        internal static void OnTick()
        {
            TickCount++;

            if (Sw.IsRunning)
                Sw.Stop();
            double ms = Sw.Elapsed.TotalMilliseconds;
            Sw.Restart();

            // 环形缓冲：记录最近一帧的耗时（500ms 上限，防异常值污染）。
            FrameRing[FrameRingPos] = (float)Math.Min(ms, 500.0);
            FrameRingPos = (FrameRingPos + 1) % MaxFrameRing;

            // 时间切换帧：游戏时间变化的那一帧（原版 passTimeForObjects 批量推进机器，
            // 是卡顿高发帧）。单独记录其耗时，落盘后直接看出切换帧还卡不卡。
            int timeOfDay = Game1.timeOfDay;
            if (LastTimeOfDay != -1 && timeOfDay != LastTimeOfDay)
            {
                EndSwitchFrame(LastTimeOfDay);
                TimeSwitchFrames.Add((timeOfDay, ms));
                BeginSwitchFrame();
            }
            LastTimeOfDay = timeOfDay;

            // 运行中不做任何文件写：只在内存里累积长帧，白天结束时统一落盘。
            if (ms > LagThresholdMs)
                PendingLag.Add((TickCount, ms));

            // 定期自动落盘（100 秒一次）：直接退进程/强杀也不丢数据。
            if (TickCount % AutoFlushInterval == 0)
                FlushDay();
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

        /// <summary>游戏会话结束（每天结束时调用）：把运行期累积的统计与长帧数据统一落盘。</summary>
        internal static void FlushDay()
        {
            if (!IsEnabled())
                return;

            try
            {
                // 时间切换帧（每 10 分钟）：专门记录其耗时，验证卡顿是否在切换帧。
                foreach ((int timeOfDay, double ms) in TimeSwitchFrames)
                    FlushLine("switch", $"{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{timeOfDay},{ms:F1}");
                TimeSwitchFrames.Clear();

                // 切换帧内部统计：minutesElapsed 调用次数（地图对象量）与耗时。
                foreach ((int timeOfDay, int calls, double ms) in SwitchFrameStats)
                    FlushLine("switchdetail", $"{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{timeOfDay},{calls},{ms:F1}");
                SwitchFrameStats.Clear();

                // 长帧列表（运行期只在内存累积，这里才写文件，避免热路径写冲突卡死）。
                foreach ((int tick, double ms) in PendingLag)
                    FlushLine("lag", $"{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{tick},{ms:F1}");
                PendingLag.Clear();

                // 实时帧耗时窗口（最近 MaxFrameRing 帧）。
                int n = Math.Min(MaxFrameRing, FrameRingPos);
                if (n == 0)
                    n = MaxFrameRing;
                int start = FrameRingPos + MaxFrameRing - n;
                for (int i = 0; i < n; i++)
                {
                    int idx = (start + i) % MaxFrameRing;
                    if (FrameRing[idx] > 0)
                        FlushLine("frame",
                            $"{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{TickCount - n + i},{FrameRing[idx]:F1}");
                }

                // 环节统计。
                foreach (var kv in Timings.OrderByDescending(p => p.Value))
                {
                    int calls = Counts.GetValueOrDefault(kv.Key);
                    double avg = calls > 0 ? kv.Value / calls : 0;
                    FlushLine("timing",
                        $"{Game1.player?.Name ?? "?"},{Game1.Date?.ToString() ?? "?"},{kv.Key},{calls},{kv.Value:F2},{avg:F3},{Maxes.GetValueOrDefault(kv.Key):F2}");
                }

                Timings.Clear();
                Counts.Clear();
                Maxes.Clear();
                Array.Clear(FrameRing, 0, MaxFrameRing);
                FrameRingPos = 0;
            }
            catch (Exception ex)
            {
                ModEntry.Instance.Monitor.Log($"性能采样器写文件失败：{ex.Message}", StardewModdingAPI.LogLevel.Warn);
            }
        }

        /*********
        ** 内部
        *********/

        private static bool IsEnabled()
        {
            return OutputPath.Length > 0;
        }

        /// <summary>共享写入：所有行经同一个持久句柄，写完立即 Flush（不 Close）避免文件被占用。</summary>
        private static void FlushLine(string kind, string fields)
        {
            if (Writer == null)
                return;
            Writer.WriteLine($"{kind},{fields}");
            Writer.Flush();
        }

        /// <summary>初始化输出文件并写入 CSV 表头。</summary>
        internal static void Init(string dir)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                OutputPath = Path.Combine(dir, $"perfmon-{stamp}.csv");
                // 同一个 StreamWriter 实例全程持有，append 模式只开一次，避免句柄叠加冲突。
                Writer = new StreamWriter(OutputPath, append: false);
                Writer.WriteLine("kind,player,date,name,calls,total_ms,avg_ms,max_ms");
                Writer.Flush();
            }
            catch (Exception ex)
            {
                OutputPath = "";
                ModEntry.Instance.Monitor.Log($"性能采样器初始化失败（不记录）：{ex.Message}", StardewModdingAPI.LogLevel.Warn);
            }
        }
    }
}
