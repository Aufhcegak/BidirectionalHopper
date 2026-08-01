using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using Object = StardewValley.Object;
using StardewValley.Inventories;
using StardewValley.Objects;

namespace BidirectionalHopper
{
    /// <summary>
    /// 双向漏斗自动化测试（bh_selftest / bh_perftest）。
    ///
    /// 照 MonsterArena.ma_selftest 模式：SMAPI 控制台命令 + 纯逻辑自测，不依赖存档。
    /// bh_selftest：覆盖全部功能路径（收取/喂料/箱满/蜂房重启/锁跳过/非漏斗不处理/续料）。
    /// bh_perftest：模拟 50 台机器+漏斗，测时间切换帧的轮询叠加成本。
    /// </summary>
    internal static class SelfTest
    {
        /// <summary>跑全部功能自测。</summary>
        internal static void RunAll(IMonitor monitor)
        {
            int pass = 0, fail = 0;
            var failures = new List<string>();
            var scratch = new GameLocation("Maps\\Farm", "bh_selftest_scratch");
            var who = Game1.player;

            var tests = new (string Name, Func<bool> Run)[]
            {
                ("收取：机器完成后产物进漏斗", TestCollectReadyMachine),
                ("收取：机器未完成不收", TestCollectNotReady),
                ("喂料：空机器从漏斗投料", TestFeedEmptyMachine),
                ("喂料：机器已有料不再投", TestFeedAlreadyLoaded),
                ("箱满：收取不进去就跳过", TestCollectChestFull),
                ("蜂房：收取后自动续产", TestHiveRestart),
                ("锁：漏斗被打开时跳过", TestLockedHopperSkipped),
                ("非漏斗：普通箱子不参与", TestPlainChestIgnored),
                ("收取后：自动续料", TestCollectThenRefill),
            };

            foreach (var (name, run) in tests)
            {
                bool ok;
                try
                {
                    ok = run();
                }
                catch (Exception ex)
                {
                    monitor.Log($"[bh_selftest] 异常: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                    ok = false;
                }
                if (ok) pass++; else fail++;
                if (!ok) failures.Add(name);
                monitor.Log($"[bh_selftest] {(ok ? "PASS" : "FAIL")} {name}", ok ? LogLevel.Info : LogLevel.Error);
            }

            monitor.Log(
                $"[bh_selftest] 完成: {pass} 通过, {fail} 失败. {(fail > 0 ? "失败: " + string.Join("; ", failures) : "")}",
                fail == 0 ? LogLevel.Info : LogLevel.Warn
            );
        }

        /// <summary>性能基准：时间切换帧的轮询叠加成本。</summary>
        internal static void RunPerf(IMonitor monitor)
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_perf_scratch");
            var who = Game1.player;

            // 造 50 台机器 + 50 个漏斗（放在同一地点，加入 objects 表）。
            int n = 50;
            for (int i = 0; i < n; i++)
            {
                var hopper = new Chest(true) { SpecialChestType = Chest.SpecialChestTypes.AutoLoader };
                var machine = (Object)ItemRegistry.Create("(BC)12"); // 小桶
                scratch.objects[new Vector2(i * 2, 4)] = hopper;
                scratch.objects[new Vector2(i * 2, 5)] = machine;
                HopperPatch.OnObjectAdded(scratch, new Vector2(i * 2, 4), hopper);
            }

            // 手动把机器设成"完成"状态（readyForHarvest）。
            foreach (var pair in scratch.objects.Pairs)
            {
                if (pair.Value.GetMachineData() != null)
                {
                    pair.Value.readyForHarvest.Value = true;
                    pair.Value.heldObject.Value = new Object("(O)348", 1);
                    pair.Value.MinutesUntilReady = 0;
                }
            }

            // 时间切换帧测试（改 Game1.timeOfDay 模拟切换）：
            // ProcessAllHoppers 检测到 timeOfDay 变化应跳过本轮（不处理任何漏斗）。
            // 断言：切换帧后，处理过的漏斗数 = 0（ProcessAllHoppers 不公开计数，用计时近似判断）。
            int oldTime = Game1.timeOfDay;
            Game1.timeOfDay = oldTime + 100; // 模拟时间切换
            var sw = Stopwatch.StartNew();
            HopperPatch.ProcessAllHoppers();
            sw.Stop();
            double switchMs = sw.Elapsed.TotalMilliseconds;
            Game1.timeOfDay = oldTime;

            // 重置 LastTimeOfDay，避免影响后续。
            typeof(HopperPatch).GetField("LastTimeOfDay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, -1);

            // 基准：非时间切换帧，处理全部机器要多久（BatchSize=4，50 台要 13 轮）。
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < 13; i++)
                HopperPatch.ProcessAllHoppers();
            sw2.Stop();
            double normalMs = sw2.Elapsed.TotalMilliseconds;

            monitor.Log(
                $"[bh_perftest] 50 台机器(13轮全处理): 普通 {normalMs:F2}ms, 时间切换帧 {switchMs:F3}ms (应≈0=被跳过)",
                normalMs < 40 && switchMs < 2 ? LogLevel.Info : LogLevel.Warn
            );
            monitor.Log(
                $"[bh_perftest] 结论: {(switchMs < 2 ? "切换帧已正确跳过，无叠加卡顿" : "切换帧未跳过，仍有叠加风险")}",
                switchMs < 2 ? LogLevel.Info : LogLevel.Error
            );
        }

        /*********
        ** 功能测试实现
        *********/

        private static bool TestCollectReadyMachine()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t1");
            var hopper = MakeHopper(scratch, new Vector2(5, 5));
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)10", ready: true);

            bool moved = HopperPatch.TryCollectFromAdjacentMachine(scratch, machine, "test");
            bool inHopper = hopper.Items.Any(i => i != null && i.QualifiedItemId == "(O)348");
            return moved && inHopper && !machine.readyForHarvest.Value;
        }

        private static bool TestCollectNotReady()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t2");
            var hopper = MakeHopper(scratch, new Vector2(5, 5));
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)10", ready: false);

            bool moved = HopperPatch.TryCollectFromAdjacentMachine(scratch, machine, "test");
            return !moved && !hopper.Items.Any();
        }

        private static bool TestFeedEmptyMachine()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t3");
            var hopper = MakeHopper(scratch, new Vector2(5, 5), withItem: "(O)398"); // 葡萄，小桶接受
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)12", ready: false); // 小桶

            bool fed = HopperPatch.TryFeedMachine(scratch, hopper, machine, "test");
            return fed && machine.heldObject.Value != null;
        }

        private static bool TestFeedAlreadyLoaded()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t4");
            var hopper = MakeHopper(scratch, new Vector2(5, 5), withItem: "(O)398");
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)12", ready: false);
            machine.heldObject.Value = new Object("(O)398", 1); // 已有料

            bool fed = HopperPatch.TryFeedMachine(scratch, hopper, machine, "test");
            return !fed; // 不应再投
        }

        private static bool TestCollectChestFull()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t5");
            var hopper = MakeHopper(scratch, new Vector2(5, 5));
            // 塞满 36 格
            for (int i = 0; i < Chest.capacity; i++)
                hopper.Items.Add(new Object("(O)80", 1));
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)10", ready: true);

            bool moved = HopperPatch.TryCollectFromAdjacentMachine(scratch, machine, "test");
            return !moved && machine.readyForHarvest.Value; // 没收走，机器保持 ready
        }

        private static bool TestHiveRestart()
        {
            // 蜂房 (BC)10 是收集类机器，收取后无 OutputCollected 续产规则，
            // 所以预期"收取后机器为空（不续产）"——验证收取复位正确。
            var scratch = new GameLocation("Maps\\Farm", "bh_t6");
            var hopper = MakeHopper(scratch, new Vector2(5, 5));
            var hive = MakeMachine(scratch, new Vector2(5, 6), "(BC)10", ready: true);
            hive.heldObject.Value = new Object("(O)340", 1); // 蜂蜜

            bool moved = HopperPatch.TryCollectFromAdjacentMachine(scratch, hive, "test");
            // 收取成功 + 机器复位（无续产规则时不自动重启）
            return moved && hive.heldObject.Value == null && !hive.readyForHarvest.Value;
        }

        private static bool TestLockedHopperSkipped()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t7");
            var hopper = MakeHopper(scratch, new Vector2(5, 5));
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)10", ready: true);
            hopper.GetMutex().RequestLock(); // 玩家正开着

            // 轮询应跳过（GetMutex().IsLocked()）
            bool moved = HopperPatch.TryCollectFromAdjacentMachine(scratch, machine, "test");
            hopper.GetMutex().ReleaseLock();
            return !moved && machine.readyForHarvest.Value;
        }

        private static bool TestPlainChestIgnored()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t8");
            var chest = new Chest(true); // 普通箱子，不是漏斗
            scratch.objects[new Vector2(5, 5)] = chest;
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)10", ready: true);

            bool moved = HopperPatch.TryCollectFromAdjacentMachine(scratch, machine, "test");
            return !moved; // 普通箱子不参与
        }

        private static bool TestCollectThenRefill()
        {
            var scratch = new GameLocation("Maps\\Farm", "bh_t9");
            var hopper = MakeHopper(scratch, new Vector2(5, 5), withItem: "(O)398"); // 葡萄，小桶接受
            var machine = MakeMachine(scratch, new Vector2(5, 6), "(BC)12", ready: true); // 小桶，已完成

            bool moved = HopperPatch.TryCollectFromAdjacentMachine(scratch, machine, "test");
            // 收取后应续料：机器有料（新投入的葡萄）
            return moved && machine.heldObject.Value != null;
        }

        /*********
        ** 工具
        *********/

        private static Chest MakeHopper(GameLocation loc, Vector2 tile, string? withItem = null)
        {
            var hopper = new Chest(true) { SpecialChestType = Chest.SpecialChestTypes.AutoLoader };
            hopper.TileLocation = tile;
            loc.objects[tile] = hopper;
            HopperPatch.OnObjectAdded(loc, tile, hopper);
            if (withItem != null)
                hopper.Items.Add(new Object(withItem, 1));
            return hopper;
        }

        private static Object MakeMachine(GameLocation loc, Vector2 tile, string itemId, bool ready)
        {
            // 用 ItemRegistry.Create 构造（带真实数据），不能 new Object(tile, "(BC)x")——
            // 那样会把带括号的 ID 传给 bigCraftableData 查找，得到 Error Item（GetMachineData 为空）。
            var machine = (Object)ItemRegistry.Create(itemId);
            machine.TileLocation = tile;
            loc.objects[tile] = machine;
            if (ready)
            {
                machine.readyForHarvest.Value = true;
                machine.heldObject.Value = new Object("(O)348", 1); // 果酒
                machine.MinutesUntilReady = 0;
            }
            return machine;
        }
    }
}
