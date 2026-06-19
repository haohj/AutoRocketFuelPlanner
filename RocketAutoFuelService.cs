using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using PeterHan.PLib.Options;

namespace AutoRocketFuelPlanner
{
    internal static class RocketAutoFuelService
    {
        private struct SyncState
        {
            public bool Initialized;
            public float Distance;
            public float Fuel;
            public float Oxidizer;
        }

        private static readonly Dictionary<int, AutoFuelSnapshot> LastSnapshots = new Dictionary<int, AutoFuelSnapshot>();
        private static readonly Dictionary<int, SyncState> SyncStates = new Dictionary<int, SyncState>();
        private static readonly string[] DistanceKeywords = { "distance", "travel", "range", "path", "target" };
        private static readonly string[] FuelKeywords = { "fuel" };
        private static readonly string[] OxidizerKeywords = { "oxidizer", "oxylite", "ox" };
        private static readonly string[] WritableMassKeywords = { "target", "desired", "user", "requested", "fill", "amount", "mass" };
        private static readonly string[] ReadableMassPriorityKeywords = { "target", "desired", "requested", "user", "fill" };
        private static readonly string[] ReadableMassFallbackKeywords = { "amount", "mass" };
        private const float DistanceEpsilon = 0.5f;
        private const float MassEpsilon = 0.1f;

        public static bool TryGetSnapshot(Clustercraft craft, out AutoFuelSnapshot snapshot)
        {
            snapshot = default(AutoFuelSnapshot);
            if (craft == null)
            {
                return false;
            }

            return LastSnapshots.TryGetValue(craft.GetInstanceID(), out snapshot);
        }

        public static void TryApplyToCraft(Clustercraft craft)
        {
            SyncByDetectedInput(craft, true);
        }

        public static void TickAutoSync(Clustercraft craft)
        {
            SyncByDetectedInput(craft, false);
        }

        private static RocketEngineKind DetectEngineKind(Clustercraft craft)
        {
            List<Component> components = craft.GetComponentsInChildren<Component>(true)
                .Where(c => c != null)
                .ToList();

            foreach (Component component in components)
            {
                string typeName = component.GetType().Name.ToLowerInvariant();
                if (typeName.Contains("steam") && typeName.Contains("engine"))
                {
                    return RocketEngineKind.Steam;
                }

                if (typeName.Contains("petroleum") && typeName.Contains("engine"))
                {
                    return RocketEngineKind.Petroleum;
                }

                if ((typeName.Contains("liquidhydrogen") || typeName.Contains("hydrogen")) && typeName.Contains("engine"))
                {
                    return RocketEngineKind.Hydrogen;
                }

                if ((typeName.Contains("sugar") || typeName.Contains("sucrose")) && typeName.Contains("engine"))
                {
                    return RocketEngineKind.Sugar;
                }

                if ((typeName.Contains("radbolt") || typeName.Contains("nuclear")) && typeName.Contains("engine"))
                {
                    return RocketEngineKind.Radbolt;
                }
            }

            return RocketEngineKind.Unknown;
        }

        public static string BuildDetailsText(Clustercraft craft)
        {
            if (!TryGetSnapshot(craft, out AutoFuelSnapshot snapshot))
            {
                return "自动加注: 暂无计算记录";
            }

            string fallbackNote = snapshot.UsedFallbackDistance ? " (兜底距离)" : string.Empty;
            string fuelState = snapshot.FuelApplied ? $"{snapshot.AppliedFuelKg:F1}kg" : "未写入";
            string oxidizerState = snapshot.OxidizerApplied ? $"{snapshot.AppliedOxidizerKg:F1}kg" : "未写入";
            return $"自动加注 | 来源: {snapshot.InputKind} | 引擎: {snapshot.Plan.EngineKind} | 目标: {snapshot.Plan.TargetDistance:F0}{fallbackNote} | 燃料: {fuelState} | 氧化剂: {oxidizerState}";
        }

        private static void SyncByDetectedInput(Clustercraft craft, bool forceDistanceMode)
        {
            if (craft == null || !SingletonOptions<Config>.Instance.EnableAutoApply)
            {
                return;
            }

            int id = craft.GetInstanceID();
            RocketEngineKind engineKind = DetectEngineKind(craft);
            FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

            bool hasDistance = TryResolveTargetDistance(craft, out float currentDistance);
            bool hasFuel = TryReadMassFromTank(craft, FuelKeywords, out float currentFuel);
            bool hasOxidizer = TryReadMassFromTank(craft, OxidizerKeywords, out float currentOxidizer);

            bool usedFallbackDistance = false;
            if (!hasDistance)
            {
                currentDistance = SingletonOptions<Config>.Instance.FallbackTargetDistance;
                hasDistance = true;
                usedFallbackDistance = true;
            }

            SyncState state = SyncStates.TryGetValue(id, out SyncState existingState) ? existingState : default(SyncState);
            PlanInputKind inputKind = PlanInputKind.Distance;

            if (!forceDistanceMode && state.Initialized)
            {
                bool distanceChanged = hasDistance && Mathf.Abs(currentDistance - state.Distance) > DistanceEpsilon;
                bool fuelChanged = hasFuel && Mathf.Abs(currentFuel - state.Fuel) > MassEpsilon;
                bool oxidizerChanged = hasOxidizer && Mathf.Abs(currentOxidizer - state.Oxidizer) > MassEpsilon;

                if (distanceChanged)
                {
                    inputKind = PlanInputKind.Distance;
                }
                else if (fuelChanged)
                {
                    inputKind = PlanInputKind.Fuel;
                }
                else if (oxidizerChanged && profile.RequiresOxidizer)
                {
                    inputKind = PlanInputKind.Oxidizer;
                }
                else
                {
                    return;
                }
            }

            FuelPlan plan;
            bool applyFuel = false;
            bool applyOxidizer = false;
            switch (inputKind)
            {
                case PlanInputKind.Fuel:
                    if (!hasFuel)
                    {
                        return;
                    }

                    plan = FuelCalculator.CreatePlanFromFuel(currentFuel, engineKind, profile);
                    applyOxidizer = true;
                    break;

                case PlanInputKind.Oxidizer:
                    if (!hasOxidizer || !profile.RequiresOxidizer)
                    {
                        return;
                    }

                    plan = FuelCalculator.CreatePlanFromOxidizer(currentOxidizer, engineKind, profile);
                    applyFuel = true;
                    break;

                default:
                    plan = FuelCalculator.CreatePlanFromDistance(currentDistance, engineKind, profile);
                    applyFuel = true;
                    applyOxidizer = true;
                    break;
            }

            bool fuelApplied = false;
            bool oxidizerApplied = false;
            float appliedFuel = hasFuel ? currentFuel : 0f;
            float appliedOxidizer = hasOxidizer ? currentOxidizer : 0f;
            if (applyFuel)
            {
                fuelApplied = TryApplyMassToTank(craft, plan.FuelKg, FuelKeywords, out appliedFuel);
            }

            if (applyOxidizer)
            {
                oxidizerApplied = TryApplyMassToTank(craft, plan.OxidizerKg, OxidizerKeywords, out appliedOxidizer);
            }

            LastSnapshots[id] = new AutoFuelSnapshot(
                plan,
                inputKind,
                appliedFuel,
                appliedOxidizer,
                usedFallbackDistance,
                fuelApplied,
                oxidizerApplied
            );

            SyncStates[id] = new SyncState
            {
                Initialized = true,
                Distance = plan.TargetDistance,
                Fuel = applyFuel ? appliedFuel : (hasFuel ? currentFuel : plan.FuelKg),
                Oxidizer = applyOxidizer ? appliedOxidizer : (hasOxidizer ? currentOxidizer : plan.OxidizerKg)
            };
        }

        private static bool TryResolveTargetDistance(Clustercraft craft, out float distance)
        {
            distance = 0f;
            Type type = craft.GetType();
            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!DistanceKeywords.Any(name.Contains))
                {
                    continue;
                }

                if (ReflectionHelpers.TryReadFloat(craft, member, out float value) && value > 0f)
                {
                    distance = value;
                    return true;
                }
            }

            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo method in methods)
            {
                if (method.GetParameters().Length != 0)
                {
                    continue;
                }

                string methodName = method.Name.ToLowerInvariant();
                if (!DistanceKeywords.Any(methodName.Contains))
                {
                    continue;
                }

                if (method.ReturnType != typeof(float) && method.ReturnType != typeof(int) && method.ReturnType != typeof(double))
                {
                    continue;
                }

                try
                {
                    object result = method.Invoke(craft, null);
                    if (result == null)
                    {
                        continue;
                    }

                    float value = Convert.ToSingle(result);
                    if (value > 0f)
                    {
                        distance = value;
                        return true;
                    }
                }
                catch
                {
                    // Try next method.
                }
            }

            return false;
        }

        private static bool TryApplyMassToTank(Clustercraft craft, float plannedMassKg, string[] tankKeywords, out float finalAppliedKg)
        {
            finalAppliedKg = 0f;
            List<Component> candidates = GetTankCandidates(craft, tankKeywords);

            foreach (Component candidate in candidates)
            {
                float clamped = ClampByStorage(candidate.gameObject, plannedMassKg);
                if (TryWriteMass(candidate, clamped))
                {
                    finalAppliedKg = clamped;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadMassFromTank(Clustercraft craft, string[] tankKeywords, out float massKg)
        {
            massKg = 0f;
            foreach (Component candidate in GetTankCandidates(craft, tankKeywords))
            {
                if (TryReadMass(candidate, out float value))
                {
                    massKg = Mathf.Max(0f, value);
                    return true;
                }
            }

            return false;
        }

        private static List<Component> GetTankCandidates(Clustercraft craft, string[] tankKeywords)
        {
            return craft.GetComponentsInChildren<Component>(true)
                .Where(c =>
                {
                    if (c == null)
                    {
                        return false;
                    }

                    string typeName = c.GetType().Name.ToLowerInvariant();
                    return typeName.Contains("tank") && tankKeywords.Any(typeName.Contains);
                })
                .ToList();
        }

        private static float ClampByStorage(GameObject tankObject, float massKg)
        {
            float result = Mathf.Max(0f, massKg);
            float maxPercent = Mathf.Clamp(SingletonOptions<Config>.Instance.MaxAutoFillPercent, 1f, 100f) / 100f;
            Storage storage = tankObject.GetComponent<Storage>();
            if (storage != null && storage.capacityKg > 0f)
            {
                result = Mathf.Min(result, storage.capacityKg * maxPercent);
            }

            return result;
        }

        private static bool TryWriteMass(Component tankComponent, float targetMass)
        {
            Type type = tankComponent.GetType();
            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!WritableMassKeywords.Any(name.Contains))
                {
                    continue;
                }

                if (ReflectionHelpers.TryWriteFloat(tankComponent, member, targetMass))
                {
                    return true;
                }
            }

            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo method in methods)
            {
                if (method.GetParameters().Length != 1 || method.GetParameters()[0].ParameterType != typeof(float))
                {
                    continue;
                }

                string name = method.Name.ToLowerInvariant();
                if (!WritableMassKeywords.Any(name.Contains))
                {
                    continue;
                }

                try
                {
                    method.Invoke(tankComponent, new object[] { targetMass });
                    return true;
                }
                catch
                {
                    // Try next setter candidate.
                }
            }

            return false;
        }

        private static bool TryReadMass(Component tankComponent, out float value)
        {
            value = 0f;
            Type type = tankComponent.GetType();
            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!ReadableMassPriorityKeywords.Any(name.Contains))
                {
                    continue;
                }

                if (ReflectionHelpers.TryReadFloat(tankComponent, member, out float mass) && mass >= 0f)
                {
                    value = mass;
                    return true;
                }
            }

            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!ReadableMassFallbackKeywords.Any(name.Contains))
                {
                    continue;
                }

                if (ReflectionHelpers.TryReadFloat(tankComponent, member, out float mass) && mass >= 0f)
                {
                    value = mass;
                    return true;
                }
            }

            return false;
        }
    }
}
