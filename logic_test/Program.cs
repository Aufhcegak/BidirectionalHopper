using System.Reflection;
using System.Text.Json;
using BidirectionalHopper;
using StardewModdingAPI;
using StardewValley;

// ============================================================
// BidirectionalHopper 纯逻辑测试
// 直接链接 PerfMonitor.cs（计时/环/切换帧统计——卡顿诊断的核心）与 ModConfig.cs。
// HopperPatch 依赖 Chest/GameLocation（无头环境构造 NRE），其行为由无头 SMAPI
// 集成测试（bh_selftest）覆盖。
// 跑法：cd logic_test && dotnet run -c Release
// ============================================================

int fails = 0, pass = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok || detail == null ? "" : "  << " + detail));
    if (ok) pass++; else fails++;
}
void CheckEq<T>(string name, T got, T expected)
{
    bool ok = Equals(got, expected);
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : $"  << got={got} expected={expected}"));
    if (ok) pass++; else fails++;
}

// ---- 反射读私有静态字段（PerfMonitor 的统计状态） ----
object? GetField(string name)
{
    return typeof(PerfMonitor).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
}
int GetInt(string name) => (int)(GetField(name) ?? 0);
double GetDouble(string name) => (double)(GetField(name) ?? 0.0);
double GetMax(string name)
{
    var maxes = (Dictionary<string, double>?)GetField(name);
    return maxes == null ? 0 : maxes.Values.FirstOrDefault();
}
string GetOutputPath() => (string)(GetField("OutputPath") ?? "");

// ============================================================
// 第一组：计时环节 Start/End —— 计数、累计、最大值、嵌套
// ============================================================
{
    // 空栈 End 不崩
    PerfMonitor.End();
    Check("timing: 空栈 End 不崩", true);

    // 单环节两次调用
    PerfMonitor.OnTick(); // 先启动 Stopwatch(OnTick 里 Sw.Restart(); 测试不跑游戏循环,手动启动)
    PerfMonitor.Start("t1");
    Thread.Sleep(5);
    PerfMonitor.End();
    PerfMonitor.Start("t1");
    Thread.Sleep(8);
    PerfMonitor.End();
    CheckEq("timing: 计数 2", (int)((Dictionary<string, int>?)GetField("Counts") ?? new()!).GetValueOrDefault("t1"), 2);
    double total = (double)((Dictionary<string, double>?)GetField("Timings") ?? new()!).GetValueOrDefault("t1");
    Check("timing: 累计 ≥ 5ms", total >= 5.0, $"total={total:F1}");
    Check("timing: 最大值 ≥ 5ms", GetMax("Maxes") >= 5.0, $"max={GetMax("Maxes"):F1}");

    // 嵌套:外层包含内层耗时
    PerfMonitor.Start("outer");
    PerfMonitor.Start("inner");
    Thread.Sleep(5);
    PerfMonitor.End(); // inner
    Thread.Sleep(5);
    PerfMonitor.End(); // outer
    CheckEq("timing: 嵌套计数", (int)((Dictionary<string, int>?)GetField("Counts") ?? new()!).GetValueOrDefault("outer"), 1);
    CheckEq("timing: 内层计数", (int)((Dictionary<string, int>?)GetField("Counts") ?? new()!).GetValueOrDefault("inner"), 1);
    Check("timing: 外层累计 ≥ 内层", (double)((Dictionary<string, double>?)GetField("Timings") ?? new()!).GetValueOrDefault("outer")
        >= (double)((Dictionary<string, double>?)GetField("Timings") ?? new()!).GetValueOrDefault("inner"),
        "outer < inner");

    // 异常路径:Start 后不 End 的孤儿状态不应导致下一次 End 错乱(栈后进先出,孤儿沉底)
    PerfMonitor.Start("orphan");
    PerfMonitor.Start("t2");
    PerfMonitor.End(); // 弹 t2
    PerfMonitor.End(); // 弹 orphan
    Check("timing: 孤儿栈不崩", true);
    PerfMonitor.FlushDay(); // 清掉累积,避免污染后续
}

// ============================================================
// 第二组：FlushDay —— 未 Init 时 no-op、清空统计
// ============================================================
{
    // 未 Init:OutputPath 空 → FlushDay 直接返回
    string before = GetOutputPath();
    Check("flush: 未 Init 路径空", before.Length == 0);
    PerfMonitor.FlushDay(); // 不崩即可
    Check("flush: 未 Init FlushDay 不崩", true);
}

// ============================================================
// 第三组：Init + FlushDay —— CSV 写出、统计清空、长帧检测
// ============================================================
{
    string dir = Path.Combine(Path.GetTempPath(), "hopper_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try
    {
        PerfMonitor.Init(dir);
        Check("flush: Init 后路径非空", GetOutputPath().Length > 0);

        // 记录一次耗时环节
        PerfMonitor.Start("probe");
        Thread.Sleep(3);
        PerfMonitor.End();

        // 长帧检测:OnTick 测量两次调用间隔,50ms 阈值
        PerfMonitor.OnTick();
        Thread.Sleep(80);
        PerfMonitor.OnTick();
        Check("flush: 长帧被记录", ((List<(int, double)>?)GetField("PendingLag"))!.Count > 0);

        PerfMonitor.FlushDay();
        string[] files = Directory.GetFiles(dir, "perfmon-*.csv");
        Check("flush: CSV 已写出", files.Length == 1);
        // PerfMonitor 故意持有 Writer 句柄不关(防别处打开冲突),测试复制一份再读
        string copy = Path.Combine(dir, "copy.csv");
        File.Copy(files[0], copy);
        string content = File.ReadAllText(copy);
        Check("flush: CSV 有表头", content.StartsWith("kind,player,date,name,calls,total_ms,avg_ms,max_ms"));
        Check("flush: CSV 有 timing 行", content.Contains("timing,"));
        Check("flush: CSV 有 lag 行", content.Contains("lag,"));
        Check("flush: CSV 有 frame 行", content.Contains("frame,"));

        // FlushDay 后统计清空
        CheckEq("flush: 清空计数", (int)((Dictionary<string, int>?)GetField("Counts") ?? new()!).Count, 0);
        CheckEq("flush: 清空累计", (int)((Dictionary<string, double>?)GetField("Timings") ?? new()!).Count, 0);
        CheckEq("flush: 清空长帧", (int)((List<(int, double)>?)GetField("PendingLag"))!.Count, 0);
        CheckEq("flush: 环指针归零", GetInt("FrameRingPos"), 0);
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

// ============================================================
// 第四组：切换帧检测 —— timeOfDay 变化触发记录
// ============================================================
{
    string dir = Path.Combine(Path.GetTempPath(), "hopper_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try
    {
        PerfMonitor.Init(dir);
        int old = Game1.timeOfDay;
        PerfMonitor.OnTick();               // 首帧:LastTimeOfDay = old
        Game1.timeOfDay = old + 100;        // 模拟时间切换
        PerfMonitor.OnTick();               // 检测到变化 → 记录切换帧
        Game1.timeOfDay = old;

        Check("switch: 切换帧已记录", ((List<(int, double)>?)GetField("TimeSwitchFrames"))!.Count > 0);
        Check("switch: 切换帧时间正确", ((List<(int, double)>?)GetField("TimeSwitchFrames"))![0].Item1 == old + 100);
        PerfMonitor.FlushDay();
        string[] files = Directory.GetFiles(dir, "perfmon-*.csv");
        File.Copy(files[0], Path.Combine(dir, "copy.csv"));
        Check("switch: CSV 有 switch 行", files.Length == 1 && File.ReadAllText(Path.Combine(dir, "copy.csv")).Contains("switch,"));
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
        Game1.timeOfDay = 600;
    }
}

// ============================================================
// 第五组：切换帧统计 —— minutesElapsed 调用计数
// ============================================================
{
    // 清空 SwitchFrameStats(上一组测试遗留;字段是 readonly,拿实例调 Clear)
    ((List<(int, int, double)>?)typeof(PerfMonitor).GetField("SwitchFrameStats", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null))!.Clear();
    PerfMonitor.BeginSwitchFrame();
    PerfMonitor.OnMinutesElapsedStart();
    PerfMonitor.OnMinutesElapsedStart();
    PerfMonitor.OnMinutesElapsedStart();
    PerfMonitor.EndSwitchFrame(1400);
    var stats = (List<(int, int, double)>?)GetField("SwitchFrameStats");
    Check("switchdetail: 记录 3 次调用", stats!.Count == 1, $"count={stats!.Count}");
    CheckEq("switchdetail: 次数 3", stats[0].Item2, 3);

    PerfMonitor.BeginSwitchFrame();
    PerfMonitor.EndSwitchFrame(1500);
    stats = (List<(int, int, double)>?)GetField("SwitchFrameStats");
    CheckEq("switchdetail: 重开后清零", stats[1].Item2, 0);

    string dir = Path.Combine(Path.GetTempPath(), "hopper_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try
    {
        PerfMonitor.Init(dir);
        PerfMonitor.FlushDay();
        string[] files = Directory.GetFiles(dir, "perfmon-*.csv");
        File.Copy(files[0], Path.Combine(dir, "copy.csv"));
        Check("switchdetail: CSV 有 switchdetail 行", files.Length == 1 && File.ReadAllText(Path.Combine(dir, "copy.csv")).Contains("switchdetail,"));
    }
    finally
    {
        try { Directory.Delete(dir, true); } catch { }
    }
    PerfMonitor.FlushDay();
}

// ============================================================
// 第六组：ModConfig —— 默认值、JSON 往返、null 容错
// ============================================================
{
    var cfg = new ModConfig();
    Check("config: 默认开加料", cfg.EnableFeeding);
    Check("config: 默认开收取", cfg.EnableCollecting);
    Check("config: 默认不含普通箱", !cfg.IncludePlainChestsAboveHoppers);
    CheckEq("config: 默认间隔 60", cfg.AutomationInterval, 60);
    CheckEq("config: 默认下投间隔 10", cfg.FeedDownIntervalMinutes, 10);
    Check("config: 默认关日志", !cfg.VerboseLogging);

    // JSON 往返(config.json 格式)
    cfg.AutomationInterval = 30;
    cfg.VerboseLogging = true;
    string json = JsonSerializer.Serialize(cfg);
    var back = JsonSerializer.Deserialize<ModConfig>(json)!;
    CheckEq("config: 往返间隔", back.AutomationInterval, 30);
    Check("config: 往返日志", back.VerboseLogging);
    Check("config: 往返收取", back.EnableCollecting);

    // 缺字段 JSON(老配置)→ 默认值
    var minimal = JsonSerializer.Deserialize<ModConfig>("{\"VerboseLogging\":true}")!;
    Check("config: 缺字段保默认", minimal.EnableFeeding && minimal.AutomationInterval == 60);
    Check("config: 缺字段仍读日志", minimal.VerboseLogging);
}

// ============================================================
// 第七组：PerfMonitor 环形缓冲 —— 容量与回绕
// ============================================================
{
    // 环容量 1024:记录循环前的基数,写 1030 次后增量必须恰好 1030
    int capacity = 1024;
    int ticksBefore = GetInt("TickCount");
    int posBefore = GetInt("FrameRingPos");
    for (int i = 0; i < capacity + 6; i++)
        PerfMonitor.OnTick();
    CheckEq("ring: tick 增量 1030", GetInt("TickCount") - ticksBefore, capacity + 6);
    // 环位置增量 = tick 增量 mod 容量(回绕正确)
    CheckEq("ring: 位置回绕 mod 正确", (GetInt("FrameRingPos") - posBefore + capacity) % capacity, (capacity + 6) % capacity);
}

Console.WriteLine($"\n总计: PASS={pass} FAIL={fails}");
return fails == 0 ? 0 : 1;

// ---- PerfMonitor 引用 ModEntry.Instance.Monitor:测试用 stub 提供(不链接真 ModEntry) ----
namespace BidirectionalHopper
{
    internal class ModEntry
    {
        internal static ModEntry Instance { get; private set; } = new();
        internal IMonitor Monitor { get; private set; } = new StubMonitor();
    }
    internal class StubMonitor : IMonitor
    {
        public bool IsExiting => false;
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level = LogLevel.Trace) { }
        public void LogOnce(string message, LogLevel level = LogLevel.Trace) { }
        public void VerboseLog(string message) { }
        public void VerboseLog(ref StardewModdingAPI.Framework.Logging.VerboseLogStringHandler message) { }
    }
}
