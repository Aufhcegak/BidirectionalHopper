using System;
using StardewModdingAPI;

namespace BidirectionalHopper
{
    /// <summary>Generic Mod Config Menu 的 API 接口（仅声明本 Mod 用到的方法）。</summary>
    /// <remarks>通过 <c>helper.ModRegistry.GetApi&lt;IGenericModConfigMenuApi&gt;("spacechase0.GenericModConfigMenu")</c> 获取。</remarks>
    public interface IGenericModConfigMenuApi
    {
        /// <summary>注册一个 Mod 的配置菜单。</summary>
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

        /// <summary>添加段落标题。</summary>
        void AddSectionTitle(IManifest mod, Func<string> text, Func<string>? tooltip = null);

        /// <summary>添加说明文字。</summary>
        void AddParagraph(IManifest mod, Func<string> text);

        /// <summary>添加布尔开关。</summary>
        void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string>? tooltip = null, string? fieldId = null);

        /// <summary>添加整数输入。</summary>
        void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string>? tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string>? formatValue = null, string? fieldId = null);
    }
}
