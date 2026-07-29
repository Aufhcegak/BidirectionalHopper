namespace BidirectionalHopper
{
    /// <summary>Mod 配置，可在游戏目录 <c>Mods/BidirectionalHopper/config.json</c> 中修改。</summary>
    public class ModConfig
    {
        /// <summary>是否启用：把上方漏斗（自动加料器）内的物品送入下方容器。</summary>
        public bool EnableFeeding { get; set; } = true;

        /// <summary>是否启用：把下方机器加工完成的产物收进上方漏斗。</summary>
        public bool EnableCollecting { get; set; } = true;

        /// <summary>漏斗上方的普通箱子也参与加料/收取（原版漏斗只向上服务，此开关让箱子也支持向下服务）。</summary>
        public bool IncludePlainChestsAboveHoppers { get; set; } = false;

        /// <summary>加料方向为下时，尝试送入下方漏斗/箱子的间隔（分钟）。加料方向为上时始终按原版频率（每 60 分钟）执行。</summary>
        public int FeedDownIntervalMinutes { get; set; } = 10;

        /// <summary>是否在收取时播放音效。</summary>
        public bool PlaySounds { get; set; } = true;

        /// <summary>调试：把每次转移写入 SMAPI 控制台。</summary>
        public bool VerboseLogging { get; set; } = false;
    }
}
