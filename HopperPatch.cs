using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Object = StardewValley.Object;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using StardewValley.Objects;

namespace BidirectionalHopper
{
    /// <summary>
    /// 双向漏斗的 Harmony 补丁。
    ///
    /// 平滑做法完全照 Pathoschild《Automate》（已读其源码确认）：
    /// 1. <b>不</b> patch 机器热路径（onReadyForHarvest / AttemptAutoLoad）——那些跑在
    ///    <c>passTimeForObjects</c> 的 objects.Lock() 冻结期内，写对象表会排队到 Unlock 集中爆发卡顿。
    /// 2. 由 ModEntry 在 UpdateTicked 主线程按 <c>AutomationInterval</c>（默认 60 tick≈1s，同 Automate）轮询。
    /// 3. 漏斗被锁（玩家正开着）就整体跳过这一组（HasLockedContainers 语义）。
    /// 4. 收取做成<b>纯机器逻辑</b>：直接 NetField 复位 + OutputCollected 规则，<b>不</b>播音效、
    ///    <b>不</b>触发 minutesElapsed(0) 工作动画/光源/抖动 —— 这就是 Automate 不卡的关键差异。
    /// 5. 读箱子用 <c>Chest.GetItemsForPlayer()</c>（Automate 的容器清单），不是 <c>Chest.Items</c>。
    /// 6. 喂料直接用原版 <c>AttemptAutoLoad(IInventory, Farmer)</c>（同步、轻量、特效正确）。
    ///    <b>不要</b>先 probe 扫描一遍再投：probe 和真实投料各自都要跑一遍机器输出规则匹配，
    ///    双重扫描的 GameStateQuery 条件求值就是此前顿卡的主因。日志原料用 <c>lastInputItem</c> 取。
    /// 7. checkForAction 的 postfix 必须拦 justCheckingForActivity=true：原版每帧用它探测光标下的
    ///    可交互物体，不拦的话鼠标悬停在已完成机器上会每帧跑一次完整收取。
    /// </summary>
    internal static class HopperPatch
    {
        /// <summary>缓存：地点 → 该地点中作为漏斗使用的箱子位置集合。</summary>
        private static readonly Dictionary<GameLocation, HashSet<Vector2>> HopperCache = new();

        /// <summary>当前客户端是否是修改世界状态的权威（主机或单人游戏）。</summary>
        private static bool IsAuthority => Context.IsMainPlayer;

        /// <summary>安装全部补丁。只挂低频事件（玩家交互 / 每天早晨兜底），不挂热路径。</summary>
        internal static void Apply(Harmony harmony)
        {
            // 玩家与机器交互后：顺手收一次产物（即时反馈，触发频率低，不在冻结期）。
            // 传参签名与原版 checkForAction(Farmer who, bool justCheckingForActivity) 一致，
            // postfix 里用 justCheckingForActivity 拦掉每帧的光标可交互性探测。
            harmony.Patch(
                original: AccessTools.Method(typeof(Object), "checkForAction", new[] { typeof(Farmer), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(HopperPatch), nameof(AfterCheckForAction))
            );

            // 每天早上兜底一次（处理睡觉期间成熟、错过轮询的机器）。
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.DayUpdate), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(HopperPatch), nameof(AfterDayUpdate))
            );
        }

        /*********
        ** 节流轮询（主线程，由 ModEntry.UpdateTicked 调用）
        *********/

        /// <summary>轮询游标（跨轮询连续推进，把多个漏斗摊到多帧）。</summary>
        private static int Cursor;

        /// <summary>每轮最多处理的漏斗数（把开销封顶在常数，多台分摊到多帧，不叠加成尖峰）。</summary>
        private const int BatchSize = 4;

        /// <summary>上一次看到的游戏时间（用于检测 10 分钟切换帧）。</summary>
        private static int LastTimeOfDay = -1;

        /// <summary>主线程节流轮询：把当前地图缓存的漏斗统一收产物 + 投原料。
        /// 照 Automate.MachineGroup.Automate：先查锁跳过整组，再分 Done(收) / Empty(喂) 处理。</summary>
        internal static void ProcessAllHoppers()
        {
            if (!IsAuthority || !Context.IsWorldReady)
                return;

            ModConfig config = ModEntry.Instance.Config;
            if (!config.EnableFeeding && !config.EnableCollecting)
                return;

            // 时间切换帧（每 10 分钟）：原版 passTimeForObjects 正在批量推进所有机器倒计时，
            // 这帧本身就很重。跳过本轮，把漏斗收/投挪到下一帧，避免两件重活叠加成卡顿尖峰。
            int timeOfDay = Game1.timeOfDay;
            if (LastTimeOfDay != -1 && timeOfDay != LastTimeOfDay)
            {
                LastTimeOfDay = timeOfDay;
                return;
            }
            LastTimeOfDay = timeOfDay;

            GameLocation? location = Game1.currentLocation;
            if (location == null || !HopperCache.TryGetValue(location, out HashSet<Vector2>? hoppers) || hoppers.Count == 0)
                return;

            PerfMonitor.Start("ProcessAllHoppers");
            try
            {
                var list = new List<Vector2>(hoppers);
                if (Cursor >= list.Count)
                    Cursor = 0;

                int processed = 0;
                for (int n = 0; n < list.Count && processed < BatchSize; n++)
                {
                    Vector2 hopperTile = list[(Cursor + n) % list.Count];

                    if (!location.objects.TryGetValue(hopperTile, out Object hopperObj) || hopperObj is not Chest hopper)
                    { processed++; continue; }

                    // 照 Automate：每个漏斗只查一次锁；箱子被锁（玩家正开着它）就整个跳过这一组。
                    if (hopper.GetMutex().IsLocked())
                    { processed++; continue; }

                    Vector2 downTile = new(hopperTile.X, hopperTile.Y + 1f);
                    if (location.objects.TryGetValue(downTile, out Object machine) && machine.GetMachineData() != null)
                    {
                        bool ready = machine.readyForHarvest.Value && machine.heldObject.Value != null && machine.MinutesUntilReady == 0;
                        if (ready && config.EnableCollecting)
                            CollectThenRefill(location, machine, hopper, config, "tick/down");
                        else if (!ready && config.EnableFeeding)
                            TryFeedMachine(location, hopper, machine, "tick/down");
                    }
                    processed++;
                }

                Cursor = (Cursor + processed) % list.Count;
            }
            finally
            {
                PerfMonitor.End();
            }
        }

        /*********
        ** 公共缓存维护（由 ModEntry 调用）
        *********/

        internal static void OnObjectAdded(GameLocation location, Vector2 tile, Object obj)
        {
            if (!IsHopper(obj))
                return;
            if (!HopperCache.TryGetValue(location, out HashSet<Vector2>? set))
            {
                set = new HashSet<Vector2>();
                HopperCache[location] = set;
            }
            set.Add(tile);
        }

        internal static void OnObjectRemoved(GameLocation location, Vector2 tile, Object obj)
        {
            if (IsHopper(obj) && HopperCache.TryGetValue(location, out HashSet<Vector2>? set))
            {
                set.Remove(tile);
                if (set.Count == 0)
                    HopperCache.Remove(location);
            }
        }

        internal static void RebuildCache()
        {
            HopperCache.Clear();
            Utility.ForEachLocation(location =>
            {
                foreach (var pair in location.objects.Pairs)
                {
                    if (IsHopper(pair.Value))
                    {
                        if (!HopperCache.TryGetValue(location, out HashSet<Vector2>? set))
                        {
                            set = new HashSet<Vector2>();
                            HopperCache[location] = set;
                        }
                        set.Add(pair.Key);
                    }
                }
                return true;
            });
        }

        /*********
        ** 补丁方法（低频）
        *********/

        /// <summary>玩家与机器交互后：尝试收一次产物（即时反馈）。
        /// <b>必须拦截 justCheckingForActivity=true 的调用</b>：原版每帧都调
        /// <c>Game1.updateCursorTileHint() → isActionableTile → isActionable → checkForAction(probe)</c>
        /// 来判断光标下物体是否可交互；若不拦截，鼠标悬停在已完成机器上会每帧跑一次完整收取逻辑（规则匹配 + 物品分配）。</summary>
        private static void AfterCheckForAction(bool justCheckingForActivity, Object __instance, bool __result)
        {
            if (justCheckingForActivity || !IsAuthority || !__result)
                return;
            if (!ModEntry.Instance.Config.EnableCollecting)
                return;

            PerfMonitor.Start("AfterCheckForAction");
            try
            {
                GameLocation? location = __instance.Location;
                if (location != null)
                    TryCollectFromAdjacentMachine(location, __instance, "checkForAction");
            }
            finally
            {
                PerfMonitor.End();
            }
        }

        /// <summary>每天早上兜底一次。</summary>
        private static void AfterDayUpdate(GameLocation __instance, int dayOfMonth)
        {
            if (!IsAuthority)
                return;

            ModConfig config = ModEntry.Instance.Config;
            if (!config.EnableFeeding && !config.EnableCollecting)
                return;
            if (!HopperCache.TryGetValue(__instance, out HashSet<Vector2>? hoppers))
                return;

            PerfMonitor.Start("AfterDayUpdate");
            try
            {
                foreach (Vector2 hopperTile in hoppers)
                {
                    if (!__instance.objects.TryGetValue(hopperTile, out Object hopperObj) || hopperObj is not Chest hopper)
                        continue;
                    if (hopper.GetMutex().IsLocked())
                        continue;

                    Vector2 downTile = new(hopperTile.X, hopperTile.Y + 1f);
                    if (__instance.objects.TryGetValue(downTile, out Object machine) && machine.GetMachineData() != null)
                    {
                        bool ready = machine.readyForHarvest.Value && machine.heldObject.Value != null && machine.MinutesUntilReady == 0;
                        if (ready && config.EnableCollecting)
                            CollectThenRefill(__instance, machine, hopper, config, "dayStart/down");
                        else if (!ready && config.EnableFeeding)
                            TryFeedMachine(__instance, hopper, machine, "dayStart/down");
                    }
                }
            }
            finally
            {
                PerfMonitor.End();
            }
        }

        /*********
        ** 核心逻辑
        *********/

        /// <summary>处理与某台机器相邻（正上一格）的漏斗收取。</summary>
        internal static bool TryCollectFromAdjacentMachine(GameLocation location, Object machine, string reason)
        {
            Vector2 tile = machine.TileLocation;
            if (TryGetHopperAt(location, new Vector2(tile.X, tile.Y - 1f), out Chest? hopper))
                return CollectThenRefill(location, machine, hopper, ModEntry.Instance.Config, $"{reason}/above");
            return false;
        }

        /// <summary>机器完成后：把产物收进漏斗，再尝试从漏斗投入下一份原料。
        ///
        /// 收取 = <b>纯机器逻辑</b>（照 Automate.DataBasedObjectMachine.OnOutputCollected / 原版
        /// CheckForActionOnMachine 的机器复位段）：直接复位 NetField，<b>不</b>播 "coin"/"Ship" 音效、
        /// <b>不</b>走 OutputMachine→minutesElapsed(0) 的工作动画/光源 —— 这是消掉卡顿的关键。
        /// 蜂房类"收取后自动重启"机器仍按 OutputCollected 规则续产（行为正确性，Automate 同样保留）。</summary>
        private static bool CollectThenRefill(GameLocation location, Object machine, Chest hopper, ModConfig config, string reason)
        {
            PerfMonitor.Start("CollectThenRefill");
            try
            {
                return CollectThenRefillCore(location, machine, hopper, config, reason);
            }
            finally
            {
                PerfMonitor.End();
            }
        }

        private static bool CollectThenRefillCore(GameLocation location, Object machine, Chest hopper, ModConfig config, string reason)
        {
            Object held = machine.heldObject.Value;
            if (held == null || !machine.readyForHarvest.Value || machine.MinutesUntilReady != 0)
                return false;

            MachineData? machineData = machine.GetMachineData(); // 缓存：下面收取/续产/喂料都要用

            IInventory items = hopper.GetItemsForPlayer();

            // 1) 收产物：直接入箱；只收得进一部分就更新余量，收不进就放弃。
            int originalStack = held.Stack;
            Item leftover = Utility.addItemToThisInventoryList(held, items, GetCapacity(hopper));
            if (leftover != null && leftover.Stack >= originalStack)
                return false; // 箱子满了，一个都没收进去

            bool emptied = leftover == null;
            if (emptied)
            {
                machine.heldObject.Value = null;
                machine.readyForHarvest.Value = false;
                machine.showNextIndex.Value = false;
                machine.ResetParentSheetIndex();
            }
            else
            {
                held.Stack = leftover.Stack;
                machine.heldObject.Value = held;
            }
            OnTransferred(location, machine.TileLocation, held, hopper, $"collect/{reason}");

            if (!emptied)
                return true; // 机器里还有，不重启也不喂料

            // 2) 蜂房类"收取后自动重启"机器：按 OutputCollected 规则直接续产（Automate 同样保留这步）。
            // 只在规则引擎真有可能命中时才做 getOne()（原版 CheckForActionOnMachine 同序：先复位再查规则）。
            if (machineData != null
                && MachineDataUtility.TryGetMachineOutputRule(machine, machineData, MachineOutputTrigger.OutputCollected, held.getOne(), Game1.MasterPlayer, location, out MachineOutputRule rule, out _, out _, out _))
            {
                machine.OutputMachine(machineData, rule, machine.lastInputItem.Value, Game1.MasterPlayer, location, probe: false);
                ModEntry.Instance.Verbose($"[{reason}] {machine.DisplayName} 收取后自动重启。");
                return true;
            }

            // 3) 普通机器：喂下一份原料。
            if (config.EnableFeeding)
                TryFeedMachine(location, hopper, machine, reason);

            return true;
        }

        /// <summary>从漏斗向正下方机器投入一份原料。
        /// 直接调用原版 <c>AttemptAutoLoad(IInventory, Farmer)</c>（同步、无锁、特效正确）。
        /// 原版内部会对漏斗逐格跑 <c>performObjectDropInAction</c>；<b>不要</b>先自己 probe 扫描一遍再调
        /// —— 那会把每格物品的机器输出规则匹配（含 GameStateQuery 条件求值）跑两遍，是此前卡顿的主因之一。
        /// 投料成功后从 <c>lastInputItem</c> 拿实际消耗的原料用于日志。
        /// 调用方保证本机是机器（已用 <c>GetMachineData() != null</c> 把关）。</summary>
        internal static bool TryFeedMachine(GameLocation location, Chest hopper, Object machine, string reason)
        {
            PerfMonitor.Start("TryFeedMachine");
            try
            {
                if (machine.heldObject.Value != null)
                    return false; // already loaded

                if (machine.AttemptAutoLoad(hopper.GetItemsForPlayer(), Game1.MasterPlayer))
                {
                    OnTransferred(location, machine.TileLocation, machine.lastInputItem.Value, hopper, $"feedMachine/{reason}");
                    return true;
                }
                return false;
            }
            finally
            {
                PerfMonitor.End();
            }
        }

        /*********
        ** 工具
        *********/

        /// <summary>判断对象是否为原版自动加料器（Hopper）。</summary>
        private static bool IsHopper(Object obj)
        {
            return obj is Chest chest && chest.SpecialChestType == Chest.SpecialChestTypes.AutoLoader;
        }

        /// <summary>获取指定位置上的漏斗（从缓存快速查找）。</summary>
        private static bool TryGetHopperAt(GameLocation location, Vector2 tile, out Chest hopper)
        {
            hopper = null!;
            if (!HopperCache.TryGetValue(location, out HashSet<Vector2>? set) || !set.Contains(tile))
                return false;

            if (location.objects.TryGetValue(tile, out Object obj) && obj is Chest chest && chest.SpecialChestType == Chest.SpecialChestTypes.AutoLoader)
            {
                hopper = chest;
                return true;
            }
            return false;
        }

        /// <summary>漏斗容量（原版漏斗即普通 36 格箱子）。</summary>
        private static int GetCapacity(Chest chest)
        {
            return Chest.capacity;
        }

        /// <summary>转移成功后的日志反馈。<b>故意不播音效</b>：连续多台收取时反复触发音频引擎是卡顿来源之一。</summary>
        private static void OnTransferred(GameLocation location, Vector2 tile, Item? item, Chest hopper, string reason)
        {
            if (ModEntry.Instance.Config.VerboseLogging && item != null)
            {
                ModEntry.Instance.Verbose(
                    $"[{reason}] {location.NameOrUniqueName} ({tile.X},{tile.Y})：{item.DisplayName} x{item.Stack} 经由漏斗 ({hopper.TileLocation.X},{hopper.TileLocation.Y}) 转移。"
                );
            }
        }
    }
}
