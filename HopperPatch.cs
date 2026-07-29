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
    /// 设计原则：
    /// 1. 即时触发：只在机器加工完成（minutesElapsed）、机器尝试加料（AttemptAutoLoad）
    ///    或玩家打开机器（checkForAction）时执行，不做全局定时轮询。
    /// 2. 只处理正上/正下一格，不是 3×3 范围。
    /// 3. 只由主机（IsMainPlayer）修改世界状态，避免联机锁冲突。
    /// 4. 缓存漏斗位置，避免每次遍历全地图 objects。
    /// </summary>
    internal static class HopperPatch
    {
        /// <summary>缓存：地点 → 该地点中作为漏斗使用的箱子位置集合。</summary>
        private static readonly Dictionary<GameLocation, HashSet<Vector2>> HopperCache = new();

        /// <summary>当前客户端是否是修改世界状态的权威（主机或单人游戏）。</summary>
        private static bool IsAuthority => Context.IsMainPlayer;

        /// <summary>安装全部补丁。</summary>
        internal static void Apply(Harmony harmony)
        {
            // 1) 机器尝试从头顶漏斗取原料时，顺带收取产物（原版方向：机器在漏斗下方）。
            harmony.Patch(
                original: AccessTools.Method(typeof(Object), nameof(Object.AttemptAutoLoad), new[] { typeof(Farmer) }),
                postfix: new HarmonyMethod(typeof(HopperPatch), nameof(AfterAttemptAutoLoad))
            );

            // 2) 玩家与机器交互后：尝试收一次产物。
            harmony.Patch(
                original: AccessTools.Method(typeof(Object), "checkForAction", new[] { typeof(Farmer), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(HopperPatch), nameof(AfterCheckForAction))
            );

            // 3) 机器倒计时结束的瞬间：立刻收产物、立刻投新原料。
            harmony.Patch(
                original: AccessTools.Method(typeof(Object), nameof(Object.minutesElapsed), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(HopperPatch), nameof(AfterMinutesElapsed))
            );

            // 4) 每天早上兜底一次（处理睡觉期间错过的情况）。
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.DayUpdate), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(HopperPatch), nameof(AfterDayUpdate))
            );
        }

        /*********
        ** 公共缓存维护（由 ModEntry 调用）
        *********/
        internal static void OnObjectAdded(GameLocation location, Vector2 tile, Object obj)
        {
            if (IsHopper(obj))
            {
                if (!HopperCache.TryGetValue(location, out HashSet<Vector2>? set))
                {
                    set = new HashSet<Vector2>();
                    HopperCache[location] = set;
                }
                set.Add(tile);
            }
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
        ** 补丁方法
        *********/

        /// <summary>机器向头顶漏斗请求加料之后：顺带收取其产物。</summary>
        private static void AfterAttemptAutoLoad(Object __instance, ref Task<bool> __result)
        {
            if (!IsAuthority)
                return;

            ModConfig config = ModEntry.Instance.Config;
            if (!config.EnableCollecting)
                return;

            GameLocation? location = __instance.Location;
            if (location == null)
                return;

            __result = __result.ContinueWith(loaded =>
            {
                try
                {
                    bool moved = TryCollectFromAdjacentMachine(location, __instance, "AttemptAutoLoad");
                    return loaded.Result || moved;
                }
                catch (Exception error)
                {
                    ModEntry.Instance.Monitor.Log($"AttemptAutoLoad 收取产物时出错：{error}", LogLevel.Error);
                    return loaded.Result;
                }
            });
        }

        /// <summary>玩家与机器交互后：尝试收一次产物。</summary>
        private static void AfterCheckForAction(Object __instance, bool __result)
        {
            if (!IsAuthority || !__result)
                return;

            ModConfig config = ModEntry.Instance.Config;
            if (!config.EnableCollecting)
                return;

            GameLocation? location = __instance.Location;
            if (location != null)
                TryCollectFromAdjacentMachine(location, __instance, "checkForAction");
        }

        /// <summary>机器倒计时结束的瞬间：立刻收产物、立刻投新原料（只处理正上/正下一格）。</summary>
        private static void AfterMinutesElapsed(Object __instance, int minutes)
        {
            if (!IsAuthority)
                return;

            ModConfig config = ModEntry.Instance.Config;
            if (!config.EnableCollecting && !config.EnableFeeding)
                return;

            // 只在“刚造好、产物待收”这一刻触发。
            if (!__instance.readyForHarvest.Value
                || __instance.MinutesUntilReady != 0
                || __instance.heldObject.Value == null)
            {
                return;
            }

            GameLocation? location = __instance.Location;
            if (location == null)
                return;

            Vector2 tile = __instance.TileLocation;

            // 上方漏斗：原版朝向（机器在漏斗下方），从漏斗取原料 + 收产物进漏斗。
            if (config.EnableFeeding || config.EnableCollecting)
            {
                if (TryGetHopperAt(location, new Vector2(tile.X, tile.Y - 1f), out Chest? aboveHopper))
                {
                    CollectThenRefill(location, __instance, aboveHopper, config, "instant/above");
                }
            }
        }

        /// <summary>每天早上兜底一次（处理睡觉期间错过的情况）。</summary>
        private static void AfterDayUpdate(GameLocation __instance, int dayOfMonth)
        {
            if (!IsAuthority)
                return;

            ModConfig config = ModEntry.Instance.Config;
            if (!config.EnableFeeding && !config.EnableCollecting)
                return;

            if (!HopperCache.TryGetValue(__instance, out HashSet<Vector2>? hoppers))
                return;

            foreach (Vector2 hopperTile in hoppers)
            {
                if (!__instance.objects.TryGetValue(hopperTile, out Object hopperObj) || hopperObj is not Chest hopper)
                    continue;

                Vector2 downTile = new(hopperTile.X, hopperTile.Y + 1f);
                if (__instance.objects.TryGetValue(downTile, out Object downObj) && downObj.GetMachineData() != null)
                {
                    if (downObj.readyForHarvest.Value && downObj.heldObject.Value != null && downObj.MinutesUntilReady == 0)
                        CollectThenRefill(__instance, downObj, hopper, config, "dayStart/down");
                    else if (config.EnableFeeding)
                        TryFeedMachine(__instance, hopper, downObj, "dayStart/down");
                }

                Vector2 upTile = new(hopperTile.X, hopperTile.Y - 1f);
                if (__instance.objects.TryGetValue(upTile, out Object upObj) && upObj.GetMachineData() != null
                    && upObj.readyForHarvest.Value && upObj.heldObject.Value != null && upObj.MinutesUntilReady == 0)
                {
                    TryCollectFromMachine(__instance, upObj, hopper.Items, hopper, "dayStart/up");
                }
            }
        }

        /*********
        ** 核心逻辑
        *********/

        /// <summary>处理与某台机器相邻的漏斗收取（正上/正下一格）。</summary>
        private static bool TryCollectFromAdjacentMachine(GameLocation location, Object machine, string reason)
        {
            ModConfig config = ModEntry.Instance.Config;
            bool moved = false;
            Vector2 tile = machine.TileLocation;

            // 上方漏斗：原版朝向，收产物进漏斗。
            if (config.EnableCollecting && TryGetHopperAt(location, new Vector2(tile.X, tile.Y - 1f), out Chest? aboveHopper))
            {
                moved |= TryCollectFromMachine(location, machine, aboveHopper.Items, aboveHopper, $"{reason}/above");
            }

            return moved;
        }

        /// <summary>把机器产物收进目标物品栏。</summary>
        private static bool TryCollectFromMachine(GameLocation location, Object machine, IInventory destination, Chest owner, string reason)
        {
            // 只收加工完成的机器产物。
            if (machine.GetMachineData() == null)
                return false;

            Object held = machine.heldObject.Value;
            if (held == null || !machine.readyForHarvest.Value || machine.MinutesUntilReady > 0)
                return false;

            int originalStack = held.Stack;
            Item leftover = Utility.addItemToThisInventoryList(held, destination, GetCapacity(owner));

            if (leftover == null)
            {
                machine.heldObject.Value = null;
                machine.readyForHarvest.Value = false;
                machine.showNextIndex.Value = false;
                machine.ResetParentSheetIndex();
                OnTransferred(location, machine.TileLocation, held, owner, $"collect/{reason}");
                return true;
            }

            if (leftover.Stack < originalStack)
            {
                held.Stack = leftover.Stack;
                machine.heldObject.Value = held;
                OnTransferred(location, machine.TileLocation, held, owner, $"collect-partial/{reason}");
                return true;
            }

            return false;
        }

        /// <summary>机器完成后：收产物进漏斗，再尝试从漏斗投入下一份原料。</summary>
        private static bool CollectThenRefill(GameLocation location, Object machine, Chest hopper, ModConfig config, string reason)
        {
            if (machine.heldObject.Value == null || !machine.readyForHarvest.Value || machine.MinutesUntilReady != 0)
                return false;

            Object collected = machine.heldObject.Value;
            int originalStack = collected.Stack;

            // 1) 收产物。
            if (config.EnableCollecting)
            {
                Item leftover = Utility.addItemToThisInventoryList(collected, hopper.Items, GetCapacity(hopper));
                if (leftover != null && leftover.Stack >= originalStack)
                    return false;

                if (leftover == null)
                {
                    machine.heldObject.Value = null;
                    machine.readyForHarvest.Value = false;
                    machine.showNextIndex.Value = false;
                    machine.ResetParentSheetIndex();
                }
                else
                {
                    collected.Stack = leftover.Stack;
                    machine.heldObject.Value = collected;
                }
                OnTransferred(location, machine.TileLocation, collected, hopper, $"collect/{reason}");
            }

            // 2) 处理“收取后自动重启”的机器（如蜂房）。
            MachineData? machineData = machine.GetMachineData();
            if (machineData != null
                && MachineDataUtility.TryGetMachineOutputRule(machine, machineData, MachineOutputTrigger.OutputCollected, collected.getOne(), Game1.MasterPlayer, location, out MachineOutputRule rule, out _, out _, out _))
            {
                machine.OutputMachine(machineData, rule, machine.lastInputItem.Value, Game1.MasterPlayer, location, probe: false);
                ModEntry.Instance.Verbose($"[{reason}] {machine.DisplayName} 收取后自动重启。");
                return true;
            }

            // 3) 投入下一份新原料。
            if (config.EnableFeeding)
            {
                TryFeedMachine(location, hopper, machine, reason);
            }

            return true;
        }

        /// <summary>从漏斗向正下方机器投入一份原料。</summary>
        private static bool TryFeedMachine(GameLocation location, Chest hopper, Object machine, string reason)
        {
            if (machine.GetMachineData() == null)
                return false;

            Object? input = hopper.Items.FirstOrDefault(i => i is Object o && o.Stack > 0) as Object;
            if (input == null)
                return false;

            bool loaded = machine.AttemptAutoLoad(hopper.Items, Game1.MasterPlayer);
            if (loaded)
            {
                OnTransferred(location, machine.TileLocation, input, hopper, $"feedMachine/{reason}");
                return true;
            }

            return false;
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

        /// <summary>漏斗容量：36 格（原版漏斗即普通 36 格箱子）。</summary>
        private static int GetCapacity(Chest chest)
        {
            return Chest.capacity;
        }

        /// <summary>转移成功后的反馈：音效与日志。</summary>
        private static void OnTransferred(GameLocation location, Vector2 tile, Item? item, Chest hopper, string reason)
        {
            ModConfig config = ModEntry.Instance.Config;

            if (config.PlaySounds)
                location.localSound("Ship");

            if (config.VerboseLogging && item != null)
            {
                ModEntry.Instance.Verbose(
                    $"[{reason}] {location.NameOrUniqueName} ({tile.X},{tile.Y})：{item.DisplayName} x{item.Stack} 经由漏斗 ({hopper.TileLocation.X},{hopper.TileLocation.Y}) 转移。"
                );
            }
        }
    }
}
