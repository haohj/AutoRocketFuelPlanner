namespace AutoRocketFuelPlanner
{
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

    internal readonly struct FuelPlan
    {
        public readonly float TargetDistance;
        public readonly RocketEngineKind EngineKind;
        public readonly float FuelKg;
        public readonly float OxidizerKg;

        public FuelPlan(float targetDistance, RocketEngineKind engineKind, float fuelKg, float oxidizerKg)
        {
            TargetDistance = targetDistance;
            EngineKind = engineKind;
            FuelKg = fuelKg;
            OxidizerKg = oxidizerKg;
        }
    }

    internal readonly struct AutoFuelSnapshot
    {
        public readonly FuelPlan Plan;
        public readonly PlanInputKind InputKind;
        public readonly float AppliedFuelKg;
        public readonly float AppliedOxidizerKg;
        public readonly bool UsedFallbackDistance;
        public readonly bool FuelApplied;
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
