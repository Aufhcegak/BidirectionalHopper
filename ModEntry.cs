using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using Object = StardewValley.Object;

namespace BidirectionalHopper
{
    internal sealed class ModEntry : Mod
    {
        internal static ModEntry Instance { get; private set; } = null!;
        internal ModConfig Config { get; private set; } = null!;

        public override void Entry(IModHelper helper)
        {
            Instance = this;
            this.Config = helper.ReadConfig<ModConfig>();

            // 性能采样器（诊断期）：输出文件在 Mods/BidirectionalHopper/perfmon-*.csv
            PerfMonitor.Init(this.Helper.DirectoryPath);

            // 安装 Harmony 补丁
            var harmony = new Harmony(this.ModManifest.UniqueID);
            HopperPatch.Apply(harmony);

            // 存档加载后重建缓存
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            // 对象变化时实时更新缓存
            helper.Events.World.ObjectListChanged += OnObjectListChanged;
            // 返回标题时清缓存
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            // 节流轮询漏斗（主线程，照《Automate》做法）
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            // 每天结束时落盘采样数据
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            // GMCM（可选）
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            // 自动化测试命令（照 MonsterArena.ma_selftest 模式）
            helper.ConsoleCommands.Add("bh_selftest", "双向漏斗功能自测：覆盖收取/喂料/箱满/蜂房重启/锁跳过/非漏斗/续料等全部路径。", (_, _) => SelfTest.RunAll(this.Monitor));
            helper.ConsoleCommands.Add("bh_perftest", "双向漏斗性能基准：50 台机器模拟时间切换帧，测轮询叠加成本。", (_, _) => SelfTest.RunPerf(this.Monitor));

            this.Monitor.Log(
                $"Bidirectional Hopper 已启用：加料={(this.Config.EnableFeeding ? "开" : "关")}，收取={(this.Config.EnableCollecting ? "开" : "关")}。",
                LogLevel.Info
            );
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            HopperPatch.RebuildCache();
        }

        /// <summary>每帧喂给采样器，由它判断是否需要检查长帧。</summary>
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            PerfMonitor.OnTick();
            // 冻结期标记的机器：主线程立即收（同一 tick，功能优先）
            HopperPatch.ProcessPendingMachines();
            if (!e.IsMultipleOf((uint)this.Config.AutomationInterval))
                return;
            HopperPatch.ProcessAllHoppers();
        }

        /// <summary>每天结束（睡觉结算）时把采样数据落盘。</summary>
        private void OnDayEnding(object? sender, DayEndingEventArgs e)
        {
            PerfMonitor.FlushDay();
        }

        private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            foreach (var pair in e.Added)
                HopperPatch.OnObjectAdded(e.Location, pair.Key, pair.Value);
            foreach (var pair in e.Removed)
                HopperPatch.OnObjectRemoved(e.Location, pair.Key, pair.Value);
        }

        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            PerfMonitor.FlushDay(); // 下号也落盘：光测 bug 不睡觉时数据不会丢
            HopperPatch.RebuildCache(); // 清空缓存
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gmcm == null)
                return;

            gmcm.Register(
                mod: this.ModManifest,
                reset: () => this.Config = new ModConfig(),
                save: () => this.Helper.WriteConfig(this.Config)
            );

            gmcm.AddSectionTitle(this.ModManifest, () => "双向漏斗");
            gmcm.AddParagraph(this.ModManifest, () => "改造原版自动加料器（Hopper）：机器造好的瞬间立刻收取产物、立刻投入下一份原料。只处理上/下各一格。");

            gmcm.AddBoolOption(
                mod: this.ModManifest,
                getValue: () => this.Config.EnableFeeding,
                setValue: v => this.Config.EnableFeeding = v,
                name: () => "启用加料",
                tooltip: () => "机器加工完成后，自动从漏斗投入下一份原料。"
            );

            gmcm.AddBoolOption(
                mod: this.ModManifest,
                getValue: () => this.Config.EnableCollecting,
                setValue: v => this.Config.EnableCollecting = v,
                name: () => "启用收取",
                tooltip: () => "机器加工完成的瞬间，自动把产物收进漏斗。"
            );

            gmcm.AddBoolOption(
                mod: this.ModManifest,
                getValue: () => this.Config.VerboseLogging,
                setValue: v => this.Config.VerboseLogging = v,
                name: () => "调试日志",
                tooltip: () => "把每次物品转移写入 SMAPI 控制台，用于排查问题。"
            );
        }

        internal void Verbose(string message)
        {
            if (this.Config.VerboseLogging)
                this.Monitor.Log(message, LogLevel.Debug);
        }
    }
}
