using UnityEngine;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图 =========================
     * 推荐按下面顺序看本文件：
     * A. CreatePlan(...)                  -> 对外主入口（距离输入）
     * B. ResolveProfile(...)              -> 先搞清楚参数从哪里来
     * C. CreatePlanFromDistance(...)      -> 正向公式
     * D. CreatePlanFromFuel/Oxidizer(...) -> 反向公式
     * E. FuelToDistance/FuelToOxidizer/...-> 反算底层细节
     * F. GetManualProfile/GetOptimalProfile-> 模式切换与默认参数策略
     *
     * 阅读本文件前建议先看 FuelPlan.cs；
     * 阅读完本文件后建议看 RocketAutoFuelService.cs（看计算如何落地写回）。
     * ==============================================================
     */
    /// <summary>
    /// 统一的“火箭补给数学层”：
    /// - 负责把“距离 / 燃料 / 氧化剂”三个量互相换算；
    /// - 负责把配置（冗余、安全附加、全局缩放）应用到最终值；
    /// - 不直接触碰任何游戏组件（Tank、Clustercraft），只做纯计算。
    /// </summary>
    internal static class FuelCalculator
    {
        /// <summary>
        /// 引擎计算档案（已经是“可直接参与公式”的参数集合）。
        /// 这个结构体是计算中间层，来源可以是：
        /// 1) 自动最优内置档案；
        /// 2) 用户自定义引擎档案；
        /// 3) 手动模式下由全局参数推导出来的档案。
        /// </summary>
        internal struct EngineProfile
        {
            /// <summary>
            /// 1kg 燃料可支持的飞行距离（核心斜率）。
            /// 值越大，达到相同距离所需燃料越少。
            /// </summary>
            public readonly float DistancePerKgFuel;
            /// <summary>
            /// 氧化剂/燃料质量比（仅在需要氧化剂的引擎上使用）。
            /// </summary>
            public readonly float OxidizerPerKgFuel;
            /// <summary>
            /// 燃料冗余百分比（用于把理论值放大成更保守的目标值）。
            /// </summary>
            public readonly float FuelMarginPercent;
            /// <summary>
            /// 氧化剂冗余百分比。
            /// </summary>
            public readonly float OxidizerMarginPercent;
            /// <summary>
            /// 当前引擎是否需要氧化剂。
            /// </summary>
            public readonly bool RequiresOxidizer;

            public EngineProfile(
                float distancePerKgFuel,
                float oxidizerPerKgFuel,
                float fuelMarginPercent,
                float oxidizerMarginPercent,
                bool requiresOxidizer
            )
            {
                DistancePerKgFuel = distancePerKgFuel;
                OxidizerPerKgFuel = oxidizerPerKgFuel;
                FuelMarginPercent = fuelMarginPercent;
                OxidizerMarginPercent = oxidizerMarginPercent;
                RequiresOxidizer = requiresOxidizer;
            }
        }

        /// <summary>
        /// 标准入口：已知目标距离，计算燃料和氧化剂。
        /// 这个方法会先解析引擎档案，再执行距离->补给计算。
        /// </summary>
        public static FuelPlan CreatePlan(float targetDistance, RocketEngineKind engineKind)
        {
            EngineProfile profile = ResolveProfile(engineKind);
            return CreatePlanFromDistance(targetDistance, engineKind, profile);
        }

        /// <summary>
        /// 已知距离进行正向计算：
        /// 距离 -> 理论燃料 -> 冗余放大 -> 全局微调
        /// 并按引擎类型决定是否需要氧化剂。
        /// </summary>
        public static FuelPlan CreatePlanFromDistance(float targetDistance, RocketEngineKind engineKind, EngineProfile profile)
        {
            Config cfg = ConfigAccess.Get();
            // 防止负距离污染后续计算。
            float safeDistance = Mathf.Max(0f, targetDistance);

            // 防止除零：至少按 1 处理。
            float safeDistancePerKg = Mathf.Max(1f, profile.DistancePerKgFuel);
            // 理论燃料（不含冗余/全局缩放）。
            float fuelBase = safeDistance / safeDistancePerKg;
            // 仅需氧化剂引擎才计算氧化剂理论量。
            float oxidizerBase = profile.RequiresOxidizer ? fuelBase * Mathf.Max(0f, profile.OxidizerPerKgFuel) : 0f;

            // 实际冗余 = 引擎冗余 + 全局附加冗余。
            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float oxidizerMargin = Mathf.Max(0f, profile.OxidizerMarginPercent + cfg.AdditionalSafetyMarginPercent);
            // 把理论量放大成“目标设定量”。
            float fuelWithMargin = fuelBase * (1f + fuelMargin / 100f);
            float oxidizerWithMargin = oxidizerBase * (1f + oxidizerMargin / 100f);

            // 全局微调：用于一键偏保守/偏省料。
            float globalFuelMultiplier = 1f + cfg.GlobalFuelAdjustmentPercent / 100f;
            // 防止出现负倍率导致非法结果。
            fuelWithMargin *= Mathf.Max(0f, globalFuelMultiplier);
            oxidizerWithMargin *= Mathf.Max(0f, globalFuelMultiplier);

            return new FuelPlan(safeDistance, engineKind, fuelWithMargin, oxidizerWithMargin);
        }

        /// <summary>
        /// 已知燃料进行反算：
        /// 燃料 -> 距离，同时按引擎规则估算对应氧化剂。
        /// </summary>
        public static FuelPlan CreatePlanFromFuel(float fuelKg, RocketEngineKind engineKind, EngineProfile profile)
        {
            float safeFuel = Mathf.Max(0f, fuelKg);
            float distance = FuelToDistance(safeFuel, profile);
            float oxidizer = profile.RequiresOxidizer ? FuelToOxidizer(safeFuel, profile) : 0f;
            return new FuelPlan(distance, engineKind, safeFuel, oxidizer);
        }

        /// <summary>
        /// 已知氧化剂进行反算（仅适用于需要氧化剂引擎）：
        /// 氧化剂 -> 燃料 -> 距离。
        /// </summary>
        public static FuelPlan CreatePlanFromOxidizer(float oxidizerKg, RocketEngineKind engineKind, EngineProfile profile)
        {
            if (!profile.RequiresOxidizer)
            {
                // 对不需要氧化剂的引擎，此输入没有物理意义，返回零计划。
                return new FuelPlan(0f, engineKind, 0f, 0f);
            }

            float safeOxidizer = Mathf.Max(0f, oxidizerKg);
            float fuel = OxidizerToFuel(safeOxidizer, profile);
            float distance = FuelToDistance(fuel, profile);
            return new FuelPlan(distance, engineKind, fuel, safeOxidizer);
        }

        /// <summary>
        /// 根据当前配置解析最终生效档案：
        /// - 自动最优模式：走内置/自定义引擎档案；
        /// - 手动模式：走全局参数+引擎系数推导。
        /// </summary>
        public static EngineProfile ResolveProfile(RocketEngineKind engineKind)
        {
            Config cfg = ConfigAccess.Get();
            return cfg.UsePerEngineOptimalProfiles
                ? GetOptimalProfile(engineKind, cfg)
                : GetManualProfile(engineKind, cfg);
        }

        /// <summary>
        /// 手动模式：用全局参数拼出一个引擎档案。
        /// 这里仍区分“是否需要氧化剂”。
        /// </summary>
        private static EngineProfile GetManualProfile(RocketEngineKind engineKind, Config cfg)
        {
            bool requiresOxidizer = engineKind == RocketEngineKind.Petroleum || engineKind == RocketEngineKind.Hydrogen;
            float distancePerKg = Mathf.Max(1f, cfg.DistancePerKgFuel * GetEngineDistanceFactor(engineKind, cfg));
            return new EngineProfile(
                distancePerKg,
                cfg.OxidizerPerKgFuel,
                cfg.FuelMarginPercent,
                cfg.OxidizerMarginPercent,
                requiresOxidizer
            );
        }

        /// <summary>
        /// 自动最优模式：
        /// - 若开启自定义引擎档案，优先使用用户填写值；
        /// - 否则使用内置推荐值（按常见玩法经验预设）。
        /// </summary>
        private static EngineProfile GetOptimalProfile(RocketEngineKind engineKind, Config cfg)
        {
            if (cfg.EnableCustomEngineProfiles)
            {
                switch (engineKind)
                {
                    case RocketEngineKind.Steam:
                        return new EngineProfile(cfg.SteamDistancePerKgFuel, 0f, cfg.SteamFuelMarginPercent, 0f, false);
                    case RocketEngineKind.Petroleum:
                        return new EngineProfile(
                            cfg.PetroleumDistancePerKgFuel,
                            cfg.PetroleumOxidizerPerKgFuel,
                            cfg.PetroleumFuelMarginPercent,
                            cfg.PetroleumOxidizerMarginPercent,
                            true
                        );
                    case RocketEngineKind.Hydrogen:
                        return new EngineProfile(
                            cfg.HydrogenDistancePerKgFuel,
                            cfg.HydrogenOxidizerPerKgFuel,
                            cfg.HydrogenFuelMarginPercent,
                            cfg.HydrogenOxidizerMarginPercent,
                            true
                        );
                    case RocketEngineKind.Sugar:
                        return new EngineProfile(cfg.SugarDistancePerKgFuel, 0f, cfg.SugarFuelMarginPercent, 0f, false);
                    case RocketEngineKind.Radbolt:
                        return new EngineProfile(cfg.RadboltDistancePerKgFuel, 0f, cfg.RadboltFuelMarginPercent, 0f, false);
                    default:
                        // 未识别引擎：用石油参数作为保守兜底。
                        return new EngineProfile(cfg.PetroleumDistancePerKgFuel, 1f, 10f, 10f, true);
                }
            }

            switch (engineKind)
            {
                case RocketEngineKind.Steam:
                    return new EngineProfile(34f, 0f, 12f, 0f, false);
                case RocketEngineKind.Petroleum:
                    return new EngineProfile(40f, 1f, 8f, 8f, true);
                case RocketEngineKind.Hydrogen:
                    return new EngineProfile(72f, 1f, 6f, 6f, true);
                case RocketEngineKind.Sugar:
                    return new EngineProfile(28f, 0f, 12f, 0f, false);
                case RocketEngineKind.Radbolt:
                    return new EngineProfile(88f, 0f, 5f, 0f, false);
                default:
                    // 未识别引擎的内置兜底。
                    return new EngineProfile(40f, 1f, 10f, 10f, true);
            }
        }

        /// <summary>
        /// 反算：燃料 -> 距离。
        /// 注意会“反向去掉”全局微调和冗余，回到理论燃料后再乘距离系数。
        /// </summary>
        private static float FuelToDistance(float fuelKg, EngineProfile profile)
        {
            Config cfg = ConfigAccess.Get();
            float global = Mathf.Max(0f, 1f + cfg.GlobalFuelAdjustmentPercent / 100f);
            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            // 防止分母接近 0。
            float fuelBase = fuelKg / Mathf.Max(0.0001f, global * (1f + fuelMargin / 100f));
            return fuelBase * Mathf.Max(1f, profile.DistancePerKgFuel);
        }

        /// <summary>
        /// 反算：燃料 -> 氧化剂。
        /// 这里用“冗余后的比率”做换算，保证和正向计算一致。
        /// </summary>
        private static float FuelToOxidizer(float fuelKg, EngineProfile profile)
        {
            Config cfg = ConfigAccess.Get();
            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float oxidizerMargin = Mathf.Max(0f, profile.OxidizerMarginPercent + cfg.AdditionalSafetyMarginPercent);
            // 比率按“氧化剂冗余 / 燃料冗余”的相对关系修正。
            float ratio = Mathf.Max(0f, profile.OxidizerPerKgFuel) * (1f + oxidizerMargin / 100f) / Mathf.Max(0.0001f, (1f + fuelMargin / 100f));
            return fuelKg * ratio;
        }

        /// <summary>
        /// 反算：氧化剂 -> 燃料。
        /// 与 FuelToOxidizer 互逆。
        /// </summary>
        private static float OxidizerToFuel(float oxidizerKg, EngineProfile profile)
        {
            Config cfg = ConfigAccess.Get();
            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float oxidizerMargin = Mathf.Max(0f, profile.OxidizerMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float ratio = Mathf.Max(0f, profile.OxidizerPerKgFuel) * (1f + oxidizerMargin / 100f) / Mathf.Max(0.0001f, (1f + fuelMargin / 100f));
            if (ratio <= 0f)
            {
                // 不可逆时返回 0，避免无穷或 NaN。
                return 0f;
            }

            return oxidizerKg / ratio;
        }

        /// <summary>
        /// 手动模式下的引擎距离系数选择器。
        /// </summary>
        private static float GetEngineDistanceFactor(RocketEngineKind engineKind, Config cfg)
        {
            switch (engineKind)
            {
                case RocketEngineKind.Steam:
                    return cfg.SteamDistanceFactor;
                case RocketEngineKind.Petroleum:
                    return cfg.PetroleumDistanceFactor;
                case RocketEngineKind.Hydrogen:
                    return cfg.HydrogenDistanceFactor;
                case RocketEngineKind.Sugar:
                    return cfg.SugarDistanceFactor;
                case RocketEngineKind.Radbolt:
                    return cfg.RadboltDistanceFactor;
                default:
                    return 1f;
            }
        }
    }
}
