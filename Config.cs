using Newtonsoft.Json;
using PeterHan.PLib.Options;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图 =========================
     * 这个文件参数很多，建议按分组读，不要从上到下硬啃：
     * 1) 主开关：
     *    EnableAutoApply / UsePerEngineOptimalProfiles / EnableRealtimeSync / AutoDisableRealtimeSyncOnErrors
     * 2) 手动模式参数：
     *    DistancePerKgFuel + 各引擎 DistanceFactor + OxidizerPerKgFuel + Margin
     * 3) 全局修正：
     *    GlobalFuelAdjustmentPercent / AdditionalSafetyMarginPercent / MaxAutoFillPercent
     * 4) 自动最优下的自定义档案：
     *    EnableCustomEngineProfiles + 各引擎 DistancePerKgFuel / Margin / OxidizerRatio
     * 5) 最后看构造函数默认值（理解“开箱行为”）。
     *
     * 对照阅读建议：
     * - 和 FuelCalculator.ResolveProfile() 一起看，最容易理解每个参数在哪生效。
     * ==============================================================
     */
    /// <summary>
    /// Mod 全局配置：
    /// - 通过 PLib 展示到游戏设置界面；
    /// - 通过 Json 序列化到配置文件；
    /// - 供 FuelCalculator / RuntimeSync 在运行时读取。
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    [ConfigFile]
    [RestartRequired]
    public sealed class Config : SingletonOptions<Config>
    {
        // ===== 主开关 =====
        [Option("启用自动加注", "当火箭目标发生变化时，自动计算并应用燃料/氧化剂目标量", "自动火箭加注")]
        [JsonProperty]
        public bool EnableAutoApply { get; set; }

        [Option("按火箭类型自动最优计算", "开启后将按引擎类型自动套用内置最优参数（推荐）", "自动火箭加注")]
        [JsonProperty]
        public bool UsePerEngineOptimalProfiles { get; set; }

        [Option("启用实时联动同步", "玩家手动调整距离/燃料/氧化剂时，自动反算其余参数", "自动火箭加注")]
        [JsonProperty]
        public bool EnableRealtimeSync { get; set; }

        [Option("实时联动自动安全降级", "实时联动连续异常后自动降级为仅目标变化时计算", "自动火箭加注")]
        [JsonProperty]
        public bool AutoDisableRealtimeSyncOnErrors { get; set; }

        // ===== 手动模式参数（当 UsePerEngineOptimalProfiles = false 时生效） =====
        [Option("每千克燃料可飞行距离", "用于估算飞行距离与燃料关系（按你的引擎/玩法微调）", "自动火箭加注", Format = "F2")]
        [Limit(1f, 500f)]
        [JsonProperty]
        public float DistancePerKgFuel { get; set; }

        [Option("蒸汽引擎距离系数", "蒸汽引擎的距离系数 = 基础距离 * 该系数", "自动火箭加注", Format = "F2")]
        [Limit(0.1f, 5f)]
        [JsonProperty]
        public float SteamDistanceFactor { get; set; }

        [Option("石油引擎距离系数", "石油引擎的距离系数 = 基础距离 * 该系数", "自动火箭加注", Format = "F2")]
        [Limit(0.1f, 5f)]
        [JsonProperty]
        public float PetroleumDistanceFactor { get; set; }

        [Option("液氢引擎距离系数", "液氢引擎的距离系数 = 基础距离 * 该系数", "自动火箭加注", Format = "F2")]
        [Limit(0.1f, 5f)]
        [JsonProperty]
        public float HydrogenDistanceFactor { get; set; }

        [Option("糖引擎距离系数", "糖引擎的距离系数 = 基础距离 * 该系数", "自动火箭加注", Format = "F2")]
        [Limit(0.1f, 5f)]
        [JsonProperty]
        public float SugarDistanceFactor { get; set; }

        [Option("辐射引擎距离系数", "辐射引擎的距离系数 = 基础距离 * 该系数", "自动火箭加注", Format = "F2")]
        [Limit(0.1f, 5f)]
        [JsonProperty]
        public float RadboltDistanceFactor { get; set; }

        // ===== 通用质量参数 =====
        [Option("氧化剂/燃料质量比", "每 1kg 燃料对应需要的氧化剂质量", "自动火箭加注", Format = "F2")]
        [Limit(0f, 20f)]
        [JsonProperty]
        public float OxidizerPerKgFuel { get; set; }

        [Option("燃料安全冗余(%)", "为燃料计算结果追加冗余百分比，降低到站不足风险", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float FuelMarginPercent { get; set; }

        [Option("氧化剂安全冗余(%)", "为氧化剂计算结果追加冗余百分比", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float OxidizerMarginPercent { get; set; }

        [Option("最大自动填充比例(%)", "自动设置时不会超过油箱容量的该比例", "自动火箭加注", Format = "F0")]
        [Limit(1f, 100f)]
        [JsonProperty]
        public float MaxAutoFillPercent { get; set; }

        [Option("手动兜底目标距离", "当无法读取火箭目标距离时使用（单位：格）", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100000f)]
        [JsonProperty]
        public float FallbackTargetDistance { get; set; }

        // ===== 全局修正参数（会叠加到自动/手动两种模式） =====
        [Option("全局燃料量微调(%)", "在最终结果上统一增减（正数=多加，负数=少加）", "自动火箭加注", Format = "F0")]
        [Limit(-50f, 100f)]
        [JsonProperty]
        public float GlobalFuelAdjustmentPercent { get; set; }

        [Option("全局安全冗余附加(%)", "在当前冗余基础上额外增加安全冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 50f)]
        [JsonProperty]
        public float AdditionalSafetyMarginPercent { get; set; }

        // ===== 自动最优模式下的“用户自定义引擎档案”开关 =====
        [Option("启用引擎参数自定义档案", "开启后，自动最优模式将优先使用你为每种引擎设置的参数", "自动火箭加注")]
        [JsonProperty]
        public bool EnableCustomEngineProfiles { get; set; }

        // ===== 蒸汽引擎自定义档案 =====
        [Option("蒸汽-每千克燃料距离", "蒸汽引擎专用距离参数", "自动火箭加注", Format = "F2")]
        [Limit(1f, 500f)]
        [JsonProperty]
        public float SteamDistancePerKgFuel { get; set; }

        [Option("蒸汽-燃料冗余(%)", "蒸汽引擎专用燃料冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float SteamFuelMarginPercent { get; set; }

        // ===== 石油引擎自定义档案 =====
        [Option("石油-每千克燃料距离", "石油引擎专用距离参数", "自动火箭加注", Format = "F2")]
        [Limit(1f, 500f)]
        [JsonProperty]
        public float PetroleumDistancePerKgFuel { get; set; }

        [Option("石油-氧化剂/燃料比", "石油引擎专用氧化剂比率", "自动火箭加注", Format = "F2")]
        [Limit(0f, 20f)]
        [JsonProperty]
        public float PetroleumOxidizerPerKgFuel { get; set; }

        [Option("石油-燃料冗余(%)", "石油引擎专用燃料冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float PetroleumFuelMarginPercent { get; set; }

        [Option("石油-氧化剂冗余(%)", "石油引擎专用氧化剂冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float PetroleumOxidizerMarginPercent { get; set; }

        // ===== 液氢引擎自定义档案 =====
        [Option("液氢-每千克燃料距离", "液氢引擎专用距离参数", "自动火箭加注", Format = "F2")]
        [Limit(1f, 800f)]
        [JsonProperty]
        public float HydrogenDistancePerKgFuel { get; set; }

        [Option("液氢-氧化剂/燃料比", "液氢引擎专用氧化剂比率", "自动火箭加注", Format = "F2")]
        [Limit(0f, 20f)]
        [JsonProperty]
        public float HydrogenOxidizerPerKgFuel { get; set; }

        [Option("液氢-燃料冗余(%)", "液氢引擎专用燃料冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float HydrogenFuelMarginPercent { get; set; }

        [Option("液氢-氧化剂冗余(%)", "液氢引擎专用氧化剂冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float HydrogenOxidizerMarginPercent { get; set; }

        // ===== 糖引擎自定义档案 =====
        [Option("糖-每千克燃料距离", "糖引擎专用距离参数", "自动火箭加注", Format = "F2")]
        [Limit(1f, 500f)]
        [JsonProperty]
        public float SugarDistancePerKgFuel { get; set; }

        [Option("糖-燃料冗余(%)", "糖引擎专用燃料冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float SugarFuelMarginPercent { get; set; }

        // ===== 辐射引擎自定义档案 =====
        [Option("辐射-每千克燃料距离", "辐射引擎专用距离参数", "自动火箭加注", Format = "F2")]
        [Limit(1f, 1000f)]
        [JsonProperty]
        public float RadboltDistancePerKgFuel { get; set; }

        [Option("辐射-燃料冗余(%)", "辐射引擎专用燃料冗余", "自动火箭加注", Format = "F0")]
        [Limit(0f, 100f)]
        [JsonProperty]
        public float RadboltFuelMarginPercent { get; set; }

        /// <summary>
        /// 默认值即“开箱即用推荐配置”：
        /// - 默认开启自动最优；
        /// - 默认开启实时联动与安全降级；
        /// - 自定义引擎档案默认关闭（避免新手被大量参数淹没）。
        /// </summary>
        public Config()
        {
            EnableAutoApply = true;
            UsePerEngineOptimalProfiles = true;
            EnableRealtimeSync = true;
            AutoDisableRealtimeSyncOnErrors = true;
            DistancePerKgFuel = 40f;
            SteamDistanceFactor = 0.9f;
            PetroleumDistanceFactor = 1f;
            HydrogenDistanceFactor = 1.8f;
            SugarDistanceFactor = 0.7f;
            RadboltDistanceFactor = 2.2f;
            OxidizerPerKgFuel = 1f;
            FuelMarginPercent = 5f;
            OxidizerMarginPercent = 5f;
            MaxAutoFillPercent = 100f;
            FallbackTargetDistance = 10000f;
            GlobalFuelAdjustmentPercent = 0f;
            AdditionalSafetyMarginPercent = 0f;
            EnableCustomEngineProfiles = false;
            SteamDistancePerKgFuel = 34f;
            SteamFuelMarginPercent = 12f;
            PetroleumDistancePerKgFuel = 40f;
            PetroleumOxidizerPerKgFuel = 1f;
            PetroleumFuelMarginPercent = 8f;
            PetroleumOxidizerMarginPercent = 8f;
            HydrogenDistancePerKgFuel = 72f;
            HydrogenOxidizerPerKgFuel = 1f;
            HydrogenFuelMarginPercent = 6f;
            HydrogenOxidizerMarginPercent = 6f;
            SugarDistancePerKgFuel = 28f;
            SugarFuelMarginPercent = 12f;
            RadboltDistancePerKgFuel = 88f;
            RadboltFuelMarginPercent = 5f;
        }
    }
}
