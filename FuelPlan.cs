namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图（从这里开始） =========================
     * 1) 先看 RocketEngineKind / PlanInputKind：理解“引擎类型”和“输入来源”的枚举语义。
     * 2) 再看 FuelPlan：这是“单次计算输出”的最小数据单元。
     * 3) 最后看 AutoFuelSnapshot：这是“运行时快照”，包含了实际写入结果与状态标记。
     *
     * 建议下一步阅读：
     * - 先看 FuelCalculator.cs（看这些字段是如何被计算出来的）
     * - 再看 RocketAutoFuelService.cs（看这些字段如何被写回游戏对象）
     * ========================================================================
     */
    internal enum RocketEngineKind
    {
        Unknown,
        Steam,
        Petroleum,
        Hydrogen,
        Sugar,
        Radbolt
    }

    internal enum PlanInputKind
    {
        Distance,
        Fuel,
        Oxidizer
    }

    internal struct FuelPlan
    {
        // 本次计划覆盖的目标距离（格）。
        public readonly float TargetDistance;
        // 当前识别到的引擎类型。
        public readonly RocketEngineKind EngineKind;
        // 计算得到的目标燃料质量（kg）。
        public readonly float FuelKg;
        // 计算得到的目标氧化剂质量（kg）。
        public readonly float OxidizerKg;

        public FuelPlan(float targetDistance, RocketEngineKind engineKind, float fuelKg, float oxidizerKg)
        {
            TargetDistance = targetDistance;
            EngineKind = engineKind;
            FuelKg = fuelKg;
            OxidizerKg = oxidizerKg;
        }
    }

    internal struct AutoFuelSnapshot
    {
        // 本轮完整计划（包含目标距离与补给值）。
        public readonly FuelPlan Plan;
        // 本轮以哪个输入作为“主输入”进行反算。
        public readonly PlanInputKind InputKind;
        // 实际写回成功的燃料目标值（kg）。
        public readonly float AppliedFuelKg;
        // 实际写回成功的氧化剂目标值（kg）。
        public readonly float AppliedOxidizerKg;
        // 是否使用了兜底距离而不是游戏内真实距离。
        public readonly bool UsedFallbackDistance;
        // 本轮是否成功写回燃料。
        public readonly bool FuelApplied;
        // 本轮是否成功写回氧化剂。
        public readonly bool OxidizerApplied;

        public AutoFuelSnapshot(
            FuelPlan plan,
            PlanInputKind inputKind,
            float appliedFuelKg,
            float appliedOxidizerKg,
            bool usedFallbackDistance,
            bool fuelApplied,
            bool oxidizerApplied
        )
        {
            Plan = plan;
            InputKind = inputKind;
            AppliedFuelKg = appliedFuelKg;
            AppliedOxidizerKg = appliedOxidizerKg;
            UsedFallbackDistance = usedFallbackDistance;
            FuelApplied = fuelApplied;
            OxidizerApplied = oxidizerApplied;
        }
    }
}
