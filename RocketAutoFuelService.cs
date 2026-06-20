using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图 =========================
     * 推荐按下面顺序看本文件（这是核心流程文件）：
     * 1) TryApplyToCraft / TickAutoSync
     *    - 先理解两个入口：低频入口 vs 实时入口。
     * 2) SyncByDetectedInput
     *    - 核心主流程：读输入 -> 判定改动源 -> 反算 -> 回写 -> 更新快照。
     * 3) TryResolveTargetDistance / TryReadMassFromTank / TryApplyMassToTank
     *    - 看“怎么从游戏对象读写值”。
     * 4) TryWriteMass / TryReadMass / GetTankCandidates
     *    - 看反射策略与候选筛选细节。
     * 5) ReportRealtimeSyncFailure / IsRealtimeSyncAllowed
     *    - 看熔断降级机制，理解为什么不会一直刷异常。
     *
     * 阅读前建议先看 FuelCalculator.cs；
     * 阅读后建议看 Patch.cs 与 AutoFuelRuntimeSync.cs（看触发时机）。
     * ==============================================================
     */
    /// <summary>
    /// 运行时协调层（Runtime Orchestrator）：
    /// - 读取游戏对象上的“当前输入值”（距离/燃料/氧化剂）；
    /// - 判定用户最近改了哪一个输入；
    /// - 调用 FuelCalculator 反算剩余参数；
    /// - 把结果写回对应 Tank；
    /// - 维护状态快照，供详情面板显示。
    /// </summary>
    internal static class RocketAutoFuelService
    {
        /// <summary>
        /// 每台火箭的同步状态缓存：
        /// 用来和本帧读到的值做差异比较，判定“最近被用户改动的输入”。
        /// </summary>
        private struct SyncState
        {
            /// <summary>
            /// 是否已经建立过基线（首次同步前为 false）。
            /// </summary>
            public bool Initialized;
            /// <summary>
            /// 上次已确认的目标距离。
            /// </summary>
            public float Distance;
            /// <summary>
            /// 上次已确认的燃料目标值。
            /// </summary>
            public float Fuel;
            /// <summary>
            /// 上次已确认的氧化剂目标值。
            /// </summary>
            public float Oxidizer;
        }

        // key=craft instance id；value=最近一次对外展示用快照。
        private static readonly Dictionary<int, AutoFuelSnapshot> LastSnapshots = new Dictionary<int, AutoFuelSnapshot>();
        // key=craft instance id；value=输入变化检测基线。
        private static readonly Dictionary<int, SyncState> SyncStates = new Dictionary<int, SyncState>();
        // 实时联动异常计数器（用于熔断降级）。
        private static int realtimeSyncErrorCount;
        // 熔断标记：true 时实时联动停用，仅保留低频同步入口。
        private static bool realtimeSyncCircuitBroken;

        // 反射读取距离时用于名字匹配的关键词。
        private static readonly string[] DistanceKeywords = { "distance", "travel", "range", "path", "target" };
        // 反射定位燃料 Tank/字段时使用。
        private static readonly string[] FuelKeywords = { "fuel" };
        // 反射定位氧化剂 Tank/字段时使用。
        private static readonly string[] OxidizerKeywords = { "oxidizer", "oxylite", "ox" };
        // 可写字段优先关键词：尽量写“目标值”而不是“当前存量”。
        private static readonly string[] WritableMassKeywords = { "target", "desired", "user", "requested", "fill", "amount", "mass" };
        // 读取时优先寻找“目标设定”类字段。
        private static readonly string[] ReadableMassPriorityKeywords = { "target", "desired", "requested", "user", "fill" };
        // 读取兜底：再尝试 amount/mass 类字段。
        private static readonly string[] ReadableMassFallbackKeywords = { "amount", "mass" };
        // 距离判定阈值：低于该值认为不是“用户主动改动”。
        private const float DistanceEpsilon = 0.5f;
        // 质量判定阈值：防止浮点抖动引发误判。
        private const float MassEpsilon = 0.1f;

        /// <summary>
        /// 提供给轮询组件：当前是否允许执行实时联动。
        /// </summary>
        public static bool IsRealtimeSyncAllowed()
        {
            return !realtimeSyncCircuitBroken;
        }

        /// <summary>
        /// 实时联动异常上报入口。
        /// 连续异常后可自动熔断，降级为保守模式。
        /// </summary>
        public static void ReportRealtimeSyncFailure(Exception e)
        {
            realtimeSyncErrorCount++;
            Debug.LogError("[AutoRocketFuelPlanner] 实时联动异常: " + e);
            if (!ConfigAccess.Get().AutoDisableRealtimeSyncOnErrors)
            {
                return;
            }

            if (realtimeSyncErrorCount >= 3 && !realtimeSyncCircuitBroken)
            {
                realtimeSyncCircuitBroken = true;
                Debug.LogWarning("[AutoRocketFuelPlanner] 实时联动已自动降级：仅保留目标变化时计算。");
            }
        }

        /// <summary>
        /// 获取某台火箭最新计算快照（供详情面板显示）。
        /// </summary>
        public static bool TryGetSnapshot(Clustercraft craft, out AutoFuelSnapshot snapshot)
        {
            snapshot = default(AutoFuelSnapshot);
            if (craft == null)
            {
                return false;
            }

            return LastSnapshots.TryGetValue(craft.GetInstanceID(), out snapshot);
        }

        /// <summary>
        /// 低频入口（OnSpawn/SetDestination）：
        /// 强制以“距离输入”为主进行一次完整回填。
        /// </summary>
        public static void TryApplyToCraft(Clustercraft craft)
        {
            SyncByDetectedInput(craft, true);
        }

        /// <summary>
        /// 实时入口（轮询组件调用）：
        /// 自动检测用户改了哪个输入，并只回填剩余参数。
        /// </summary>
        public static void TickAutoSync(Clustercraft craft)
        {
            SyncByDetectedInput(craft, false);
        }

        /// <summary>
        /// 通过组件类型名做引擎识别（反射弱耦合方案）。
        /// 好处：跨版本/跨模组耦合更低。
        /// </summary>
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

        /// <summary>
        /// 构造详情文本，追加到火箭 descriptors 中展示。
        /// </summary>
        public static string BuildDetailsText(Clustercraft craft)
        {
            if (!TryGetSnapshot(craft, out AutoFuelSnapshot snapshot))
            {
                return "自动加注: 暂无计算记录";
            }

            string fallbackNote = snapshot.UsedFallbackDistance ? " (兜底距离)" : string.Empty;
            string fuelState = snapshot.FuelApplied ? $"{snapshot.AppliedFuelKg:F1}kg" : "未写入";
            string oxidizerState = snapshot.OxidizerApplied ? $"{snapshot.AppliedOxidizerKg:F1}kg" : "未写入";
            string syncState = realtimeSyncCircuitBroken ? "降级模式" : "实时联动";
            return $"自动加注 | 模式: {syncState} | 来源: {snapshot.InputKind} | 引擎: {snapshot.Plan.EngineKind} | 目标: {snapshot.Plan.TargetDistance:F0}{fallbackNote} | 燃料: {fuelState} | 氧化剂: {oxidizerState}";
        }

        /// <summary>
        /// 核心同步流程：
        /// 1) 读取当前距离/燃料/氧化剂；
        /// 2) 比较缓存，判断用户改了哪项输入；
        /// 3) 反算其余参数并写回；
        /// 4) 更新缓存和快照。
        /// </summary>
        private static void SyncByDetectedInput(Clustercraft craft, bool forceDistanceMode)
        {
            if (craft == null || !ConfigAccess.Get().EnableAutoApply)
            {
                return;
            }

            int id = craft.GetInstanceID();
            RocketEngineKind engineKind = DetectEngineKind(craft);
            FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

            // 从游戏对象中尽力读取三类输入。
            bool hasDistance = TryResolveTargetDistance(craft, out float currentDistance);
            bool hasFuel = TryReadMassFromTank(craft, FuelKeywords, out float currentFuel);
            bool hasOxidizer = TryReadMassFromTank(craft, OxidizerKeywords, out float currentOxidizer);

            bool usedFallbackDistance = false;
            if (!hasDistance)
            {
                // 距离读不到时仍给出可执行方案，避免整个流程失效。
                currentDistance = ConfigAccess.Get().FallbackTargetDistance;
                hasDistance = true;
                usedFallbackDistance = true;
            }

            SyncState state = SyncStates.TryGetValue(id, out SyncState existingState) ? existingState : default(SyncState);
            PlanInputKind inputKind = PlanInputKind.Distance;

            if (!forceDistanceMode && state.Initialized)
            {
                // 通过阈值比较检测“本轮被用户改动”的输入来源。
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
                    // 无显著变化：不做写回，减少抖动和性能开销。
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
                    // 用户改了燃料，则只回填其他项（主要是氧化剂/距离）。
                    applyOxidizer = true;
                    break;

                case PlanInputKind.Oxidizer:
                    if (!hasOxidizer || !profile.RequiresOxidizer)
                    {
                        return;
                    }

                    plan = FuelCalculator.CreatePlanFromOxidizer(currentOxidizer, engineKind, profile);
                    // 用户改了氧化剂，则回填燃料（距离由计划值展示/缓存）。
                    applyFuel = true;
                    break;

                default:
                    plan = FuelCalculator.CreatePlanFromDistance(currentDistance, engineKind, profile);
                    // 用户改了距离或强制模式：通常两项都更新。
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
                // 若本轮未写某项，则优先沿用当前读值，避免缓存漂移。
                Fuel = applyFuel ? appliedFuel : (hasFuel ? currentFuel : plan.FuelKg),
                Oxidizer = applyOxidizer ? appliedOxidizer : (hasOxidizer ? currentOxidizer : plan.OxidizerKg)
            };
        }

        /// <summary>
        /// 通过字段/属性/无参方法反射读取目标距离。
        /// </summary>
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

            // 字段/属性读取失败后，再尝试无参方法（如 GetXxxDistance）。
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

        /// <summary>
        /// 写入某类 Tank 的目标质量。
        /// </summary>
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

        /// <summary>
        /// 读取某类 Tank 当前可识别质量目标。
        /// </summary>
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

        /// <summary>
        /// 获取某类 Tank 组件候选集合（按类型名关键词筛选）。
        /// </summary>
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

        /// <summary>
        /// 按 Storage 容量和 MaxAutoFillPercent 对目标质量进行安全钳制。
        /// </summary>
        private static float ClampByStorage(GameObject tankObject, float massKg)
        {
            float result = Mathf.Max(0f, massKg);
            float maxPercent = Mathf.Clamp(ConfigAccess.Get().MaxAutoFillPercent, 1f, 100f) / 100f;
            Storage storage = tankObject.GetComponent<Storage>();
            if (storage != null && storage.capacityKg > 0f)
            {
                result = Mathf.Min(result, storage.capacityKg * maxPercent);
            }

            return result;
        }

        /// <summary>
        /// 通过反射写入 Tank 目标质量：
        /// - 先尝试字段/属性；
        /// - 再尝试单参数 float 方法。
        /// </summary>
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

        /// <summary>
        /// 通过反射读取 Tank 质量：
        /// - 优先读“目标值”类字段；
        /// - 失败后读“通用质量”类字段。
        /// </summary>
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
