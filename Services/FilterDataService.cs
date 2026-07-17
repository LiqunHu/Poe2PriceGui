using System.IO;
using System.Linq;
using System.Text.Json;

namespace Poe2PriceGui.Services;

/// <summary>
/// 为过滤器编辑器提供来自 Xiletrade 数据文件的物品类别、基础类型、通货等可选列表。
/// 列表项同时保留英文值（用于写入 .filter 文件）和中文显示名（用于 UI 下拉框）。
/// 中文优先读取 Xiletrade 的 zh-CN 数据文件；缺失时回退到 data/translations/translations_zh-CN.json；
/// 仍缺失时显示英文原值。
/// </summary>
public static class FilterDataService
{
    private static readonly Lazy<DataCache> Cache = new(() => new DataCache());

    /// <summary>物品类别（英文值 + 中文显示名，可直接用于 Class 条件）。</summary>
    public static IReadOnlyList<LocalizedItem> ItemClasses => Cache.Value.ItemClasses;

    /// <summary>通货名称（英文值 + 中文显示名，可直接用于 BaseType 条件）。</summary>
    public static IReadOnlyList<LocalizedItem> CurrencyNames => Cache.Value.CurrencyNames;

    /// <summary>所有基础类型名称（英文值 + 中文显示名）。</summary>
    public static IReadOnlyList<LocalizedItem> BaseTypeNames => Cache.Value.BaseTypeNames;

    /// <summary>按物品类别 ID 分组的基础类型名称（英文值）。</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> BasesByClassId => Cache.Value.BasesByClassIdReadOnly;

    /// <summary>物品类别名称 → 类别 ID 的映射。</summary>
    public static IReadOnlyDictionary<string, int> ClassNameToId => Cache.Value.ClassNameToId;

    /// <summary>
    /// 带中文显示的下拉项。
    /// </summary>
    public class LocalizedItem
    {
        /// <summary>POE 语法中的英文值。</summary>
        public string English { get; set; } = "";

        /// <summary>UI 中显示的中文名称（无翻译时与 English 相同）。</summary>
        public string Chinese { get; set; } = "";

        public override string ToString() => Chinese;
    }

    private class DataCache
    {
        public List<LocalizedItem> ItemClasses { get; } = new();
        public List<LocalizedItem> CurrencyNames { get; } = new();
        public List<LocalizedItem> BaseTypeNames { get; } = new();
        public Dictionary<int, List<string>> BasesByClassId { get; } = new();
        public IReadOnlyDictionary<int, IReadOnlyList<string>> BasesByClassIdReadOnly => BasesByClassId.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<string>)kvp.Value);
        public Dictionary<string, int> ClassNameToId { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _translations = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _classTranslations = new(StringComparer.OrdinalIgnoreCase);

        public DataCache()
        {
            Load();
        }

        private void Load()
        {
            try
            {
                var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Xiletrade", "Data", "Lang");
                var enUsDir = Path.Combine(dataDir, "en-US");
                var zhCnDir = Path.Combine(dataDir, "zh-CN");

                LoadTranslations(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "translations", "translations_zh-CN.json"));
                InitializeClassFallbackTranslations();

                LoadItemClasses(Path.Combine(enUsDir, "ItemClass.json"));
                LoadCurrencies(Path.Combine(enUsDir, "Currency.json"), Path.Combine(zhCnDir, "Currency.json"));
                LoadBases(Path.Combine(enUsDir, "Bases.json"), Path.Combine(zhCnDir, "Bases.json"));
            }
            catch
            {
                // 数据文件缺失或损坏时不影响过滤器功能，仅下拉列表为空。
            }
        }

        private void LoadTranslations(string path)
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var key = prop.Name;
                var value = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    continue;
                _translations[key] = value;
            }
        }

        /// <summary>
        /// 常见 Class 的兜底中文映射（Xiletrade 未提供 zh-CN 的 ItemClass.json 时使用）。
        /// </summary>
        private void InitializeClassFallbackTranslations()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Life Flasks"] = "生命药剂",
                ["Mana Flasks"] = "魔力药剂",
                ["Hybrid Flasks"] = "复合药剂",
                ["Currency"] = "通货",
                ["Stackable Currency"] = "可堆叠通货",
                ["Amulets"] = "项链",
                ["Rings"] = "戒指",
                ["Belts"] = "腰带",
                ["Claws"] = "爪",
                ["Daggers"] = "匕首",
                ["Wands"] = "魔杖",
                ["One Hand Swords"] = "单手剑",
                ["Thrusting One Hand Swords"] = "细剑",
                ["One Hand Axes"] = "单手斧",
                ["One Hand Maces"] = "单手锤",
                ["Bows"] = "弓",
                ["Staves"] = "长杖",
                ["Two Hand Swords"] = "双手剑",
                ["Two Hand Axes"] = "双手斧",
                ["Two Hand Maces"] = "双手锤",
                ["Skill Gems"] = "技能宝石",
                ["Support Gems"] = "辅助宝石",
                ["Quivers"] = "箭袋",
                ["Gloves"] = "手套",
                ["Boots"] = "鞋子",
                ["Body Armours"] = "胸甲",
                ["Helmets"] = "头盔",
                ["Shields"] = "盾牌",
                ["Sceptres"] = "权杖",
                ["Utility Flasks"] = "功能药剂",
                ["Critical Utility Flasks"] = "暴击功能药剂",
                ["Maps"] = "地图",
                ["Map Fragments"] = "地图碎片",
                ["Hideout Doodads"] = "藏身处装饰",
                ["Microtransactions"] = "商城道具",
                ["Jewels"] = "珠宝",
                ["Divination Cards"] = "命运卡",
                ["Labyrinth Items"] = "迷宫物品",
                ["Labyrinth Trinkets"] = "迷宫饰品",
                ["Labyrinth Map Items"] = "迷宫地图物品",
                ["Misc Map Items"] = "杂项地图物品",
                ["Leaguestones"] = "联盟石",
                ["Pantheon Souls"] = "万神殿灵魂",
                ["Pieces"] = "碎片",
                ["Abyss Jewels"] = "深渊珠宝",
                ["Incursion Items"] = "神庙物品",
                ["Delve Socketable Currency"] = " delve 可镶嵌通货",
                ["Incubators"] = "孵化器",
                ["Shards"] = "碎片",
                ["Shard Hearts"] = "碎片之心",
                ["Rune Daggers"] = "符文匕首",
                ["Warstaves"] = "战杖",
                ["Delve Stackable Socketable Currency"] = "Delve 可堆叠可镶嵌通货",
                ["Atlas Upgrade Items"] = "异界升级物品",
                ["Metamorph Samples"] = " metamorph 样本",
                ["Hidden Items"] = "隐藏物品",
                ["Contracts"] = "契约",
                ["Heist Gear"] = "抢劫装备",
                ["Heist Tools"] = "抢劫工具",
                ["Heist Cloaks"] = "抢劫斗篷",
                ["Heist Brooches"] = "抢劫胸针",
                ["Blueprints"] = "蓝图",
                ["Trinkets"] = "饰品",
                ["Heist Targets"] = "抢劫目标",
                ["Expedition Logbooks"] = "探险日志",
                ["Archnemesis Mods"] = " archnemesis 模组",
                ["Instance Local Items"] = "实例本地物品",
                ["Sentinels"] = "哨兵",
                ["Memories"] = "记忆",
                ["Relics"] = "圣物",
                ["Sanctified Relics"] = "圣化圣物",
                ["Breachstones"] = "裂隙石",
                ["Vault Keys"] = "宝库钥匙",
                ["Sanctum Research"] = "圣所研究",
                ["Tinctures"] = "酊剂",
                ["Corpses"] = "尸体",
                ["Charms"] = "护符",
                ["Embers of the Allflame"] = "万焰余烬",
                ["Gold"] = "金币",
                ["Idols"] = "神像",
                ["Wombgifts"] = "子宫赠礼",
                ["Small Relics"] = "小型圣物",
                ["Medium Relics"] = "中型圣物",
                ["Large Relics"] = "大型圣物",
                ["Fishing Rods"] = "鱼竿",
            };

            foreach (var kvp in map)
                _classTranslations[kvp.Key] = kvp.Value;
        }

        private LocalizedItem CreateItem(string english, string? chinese = null)
        {
            var trimmed = english?.Trim() ?? "";
            var display = chinese?.Trim();

            if (string.IsNullOrWhiteSpace(display))
            {
                _translations.TryGetValue(trimmed, out display);
            }

            return new LocalizedItem
            {
                English = trimmed,
                Chinese = string.IsNullOrWhiteSpace(display) ? trimmed : display
            };
        }

        private void LoadItemClasses(string path)
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("item_class", out var array)) return;

            foreach (var item in array.EnumerateArray())
            {
                var name = item.GetProperty("name_en").GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (ItemClasses.Any(x => string.Equals(x.English, name, StringComparison.OrdinalIgnoreCase))) continue;

                _classTranslations.TryGetValue(name, out var chinese);
                var localized = CreateItem(name, chinese);
                ItemClasses.Add(localized);

                if (item.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                    ClassNameToId[name] = id;
            }
            ItemClasses.Sort((a, b) => string.Compare(a.Chinese, b.Chinese, StringComparison.OrdinalIgnoreCase));
        }

        private void LoadCurrencies(string enPath, string zhPath)
        {
            if (!File.Exists(enPath)) return;

            List<string> zhTexts = new();
            if (File.Exists(zhPath))
            {
                try
                {
                    using var zhDoc = JsonDocument.Parse(File.ReadAllText(zhPath));
                    if (zhDoc.RootElement.TryGetProperty("result", out var zhResult) && zhResult.GetArrayLength() > 0)
                    {
                        foreach (var entry in zhResult[0].GetProperty("entries").EnumerateArray())
                            zhTexts.Add(entry.GetProperty("text").GetString() ?? "");
                    }
                }
                catch { /* 忽略中文文件解析错误 */ }
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(enPath));
            if (!doc.RootElement.TryGetProperty("result", out var resultArray) || resultArray.GetArrayLength() == 0)
                return;

            var enEntries = resultArray[0].GetProperty("entries").EnumerateArray().ToList();
            for (int i = 0; i < enEntries.Count; i++)
            {
                var text = enEntries[i].GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var chinese = i < zhTexts.Count ? zhTexts[i] : null;
                CurrencyNames.Add(CreateItem(text, chinese));
            }
            CurrencyNames.Sort((a, b) => string.Compare(a.Chinese, b.Chinese, StringComparison.OrdinalIgnoreCase));
        }

        private void LoadBases(string enPath, string zhPath)
        {
            if (!File.Exists(enPath)) return;

            Dictionary<string, string> zhNameMap = new(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(zhPath))
            {
                try
                {
                    using var zhDoc = JsonDocument.Parse(File.ReadAllText(zhPath));
                    if (zhDoc.RootElement.TryGetProperty("result", out var zhResult) && zhResult.GetArrayLength() > 0)
                    {
                        foreach (var item in zhResult[0].GetProperty("data").EnumerateArray())
                        {
                            var enName = item.GetProperty("name_en").GetString();
                            var zhName = item.GetProperty("name").GetString();
                            if (!string.IsNullOrWhiteSpace(enName) && !string.IsNullOrWhiteSpace(zhName))
                                zhNameMap[enName] = zhName;
                        }
                    }
                }
                catch { /* 忽略中文文件解析错误 */ }
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(enPath));
            if (!doc.RootElement.TryGetProperty("result", out var resultArray) || resultArray.GetArrayLength() == 0)
                return;

            foreach (var item in resultArray[0].GetProperty("data").EnumerateArray())
            {
                var name = item.GetProperty("name_en").GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                zhNameMap.TryGetValue(name, out var chinese);
                BaseTypeNames.Add(CreateItem(name, chinese));

                if (item.TryGetProperty("id_class", out var classProp) && classProp.TryGetInt32(out var classId))
                {
                    if (!BasesByClassId.TryGetValue(classId, out var list))
                    {
                        list = new List<string>();
                        BasesByClassId[classId] = list;
                    }
                    if (!list.Contains(name))
                        list.Add(name);
                }
            }

            BaseTypeNames.Sort((a, b) => string.Compare(a.Chinese, b.Chinese, StringComparison.OrdinalIgnoreCase));
            foreach (var list in BasesByClassId.Values)
                list.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }
}
