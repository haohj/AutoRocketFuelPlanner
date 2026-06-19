using UnityEngine;
using PeterHan.PLib.Options;

namespace AutoRocketFuelPlanner
{
    internal static class FuelCalculator
    {
        internal readonly struct EngineProfile
        {
            public readonly float DistancePerKgFuel;
            public readonly float OxidizerPerKgFuel;
            public readonly float FuelMarginPercent;
            public readonly float OxidizerMarginPercent;
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

        public static FuelPlan CreatePlan(float targetDistance, RocketEngineKind engineKind)
        {
            EngineProfile profile = ResolveProfile(engineKind);
            return CreatePlanFromDistance(targetDistance, engineKind, profile);
        }

        public static FuelPlan CreatePlanFromDistance(float targetDistance, RocketEngineKind engineKind, EngineProfile profile)
        {
            Config cfg = SingletonOptions<Config>.Instance;
            float safeDistance = Mathf.Max(0f, targetDistance);

            float safeDistancePerKg = Mathf.Max(1f, profile.DistancePerKgFuel);
            float fuelBase = safeDistance / safeDistancePerKg;
            float oxidizerBase = profile.RequiresOxidizer ? fuelBase * Mathf.Max(0f, profile.OxidizerPerKgFuel) : 0f;

            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float oxidizerMargin = Mathf.Max(0f, profile.OxidizerMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float fuelWithMargin = fuelBase * (1f + fuelMargin / 100f);
            float oxidizerWithMargin = oxidizerBase * (1f + oxidizerMargin / 100f);

            float globalFuelMultiplier = 1f + cfg.GlobalFuelAdjustmentPercent / 100f;
            fuelWithMargin *= Mathf.Max(0f, globalFuelMultiplier);
            oxidizerWithMargin *= Mathf.Max(0f, globalFuelMultiplier);

            return new FuelPlan(safeDistance, engineKind, fuelWithMargin, oxidizerWithMargin);
        }

        public static FuelPlan CreatePlanFromFuel(float fuelKg, RocketEngineKind engineKind, EngineProfile profile)
        {
            float safeFuel = Mathf.Max(0f, fuelKg);
            float distance = FuelToDistance(safeFuel, profile);
            float oxidizer = profile.RequiresOxidizer ? FuelToOxidizer(safeFuel, profile) : 0f;
            return new FuelPlan(distance, engineKind, safeFuel, oxidizer);
        }

        public static FuelPlan CreatePlanFromOxidizer(float oxidizerKg, RocketEngineKind engineKind, EngineProfile profile)
        {
            if (!profile.RequiresOxidizer)
            {
                return new FuelPlan(0f, engineKind, 0f, 0f);
            }

            float safeOxidizer = Mathf.Max(0f, oxidizerKg);
            float fuel = OxidizerToFuel(safeOxidizer, profile);
            float distance = FuelToDistance(fuel, profile);
            return new FuelPlan(distance, engineKind, fuel, safeOxidizer);
        }

        public static EngineProfile ResolveProfile(RocketEngineKind engineKind)
        {
            Config cfg = SingletonOptions<Config>.Instance;
            return cfg.UsePerEngineOptimalProfiles
                ? GetOptimalProfile(engineKind, cfg)
                : GetManualProfile(engineKind, cfg);
        }

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
                    return new EngineProfile(40f, 1f, 10f, 10f, true);
            }
        }

        private static float FuelToDistance(float fuelKg, EngineProfile profile)
        {
            Config cfg = SingletonOptions<Config>.Instance;
            float global = Mathf.Max(0f, 1f + cfg.GlobalFuelAdjustmentPercent / 100f);
            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float fuelBase = fuelKg / Mathf.Max(0.0001f, global * (1f + fuelMargin / 100f));
            return fuelBase * Mathf.Max(1f, profile.DistancePerKgFuel);
        }

        private static float FuelToOxidizer(float fuelKg, EngineProfile profile)
        {
            Config cfg = SingletonOptions<Config>.Instance;
            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float oxidizerMargin = Mathf.Max(0f, profile.OxidizerMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float ratio = Mathf.Max(0f, profile.OxidizerPerKgFuel) * (1f + oxidizerMargin / 100f) / Mathf.Max(0.0001f, (1f + fuelMargin / 100f));
            return fuelKg * ratio;
        }

        private static float OxidizerToFuel(float oxidizerKg, EngineProfile profile)
        {
            Config cfg = SingletonOptions<Config>.Instance;
            float fuelMargin = Mathf.Max(0f, profile.FuelMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float oxidizerMargin = Mathf.Max(0f, profile.OxidizerMarginPercent + cfg.AdditionalSafetyMarginPercent);
            float ratio = Mathf.Max(0f, profile.OxidizerPerKgFuel) * (1f + oxidizerMargin / 100f) / Mathf.Max(0.0001f, (1f + fuelMargin / 100f));
            if (ratio <= 0f)
            {
                return 0f;
            }

            return oxidizerKg / ratio;
        }

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
