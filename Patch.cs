using HarmonyLib;
using KMod;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图 =========================
     * 这个文件主要回答“什么时候触发业务逻辑”：
     * 1) 先看 Patch.OnLoad
     *    - Mod 生命周期入口，PLib 初始化与配置注册。
     * 2) 再看 ClustercraftOnSpawnPatch
     *    - 火箭生成时挂载实时同步组件 + 初次计算。
     * 3) 再看 ClustercraftSetDestinationPatch
     *    - 目标变化时重算。
     * 4) 最后看 ClustercraftGetDescriptorsPatch
     *    - UI 信息展示补丁，不参与核心计算，只做可视化。
     *
     * 如果你在排查崩溃，优先看这个文件和异常日志输出。
     * ==============================================================
     */
    /// <summary>
    /// Mod 入口与 Harmony 补丁注册集合。
    /// 这里尽量把所有补丁都做异常保护，防止单点失败导致游戏启动崩溃。
    /// </summary>
    public sealed class Patch : UserMod2
    {
        /// <summary>
        /// ONI 加载 Mod 时调用。
        /// 主要做三件事：
        /// 1) 调用基类初始化；
        /// 2) 初始化 PLib；
        /// 3) 注册配置界面。
        /// </summary>
        public override void OnLoad(Harmony harmony)
        {
            try
            {
                base.OnLoad(harmony);
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Base OnLoad failed: " + e);
                return;
            }

            try
            {
                PUtil.InitLibrary(true);
                new POptions().RegisterOptions(this, typeof(Config));
            }
            catch (Exception e)
            {
                // PLib 初始化失败时仍允许核心逻辑运行，只是配置菜单不可用。
                Debug.LogWarning("[AutoRocketFuelPlanner] PLib init/register failed, running with fallback config. " + e);
            }

            ClustercraftSetDestinationPatch.TryPatch(harmony);
            ClustercraftGetDescriptorsPatch.TryPatch(harmony);
            DetailsScreenSelectPatch.TryPatch(harmony);
            DetailsScreenDeselectPatch.TryPatch(harmony);
            Debug.Log("[AutoRocketFuelPlanner] Mod loaded.");
        }
    }

    /// <summary>
    /// 火箭生成后：
    /// - 挂载实时同步组件（兼容版轮询）；
    /// - 执行一次基于距离的初始补给计算。
    /// </summary>
    [HarmonyPatch(typeof(Clustercraft), "OnSpawn")]
    internal static class ClustercraftOnSpawnPatch
    {
        private static void Postfix(Clustercraft __instance)
        {
            try
            {
                __instance.gameObject.AddOrGet<AutoFuelRuntimeSync>();
                RocketAutoFuelService.TryApplyToCraft(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] OnSpawn patch failed: " + e);
            }
        }
    }

    /// <summary>
    /// 目的地变化后触发一次重算。
    /// 这是最稳定、最低风险的业务触发点之一。
    /// </summary>
    internal static class ClustercraftSetDestinationPatch
    {
        /// <summary>
        /// 兼容式手动补丁注册：
        /// - 找到候选方法才打补丁；
        /// - 找不到只记录 warning，不抛异常。
        /// </summary>
        internal static void TryPatch(Harmony harmony)
        {
            Type craftType = typeof(Clustercraft);
            string[] candidateNames =
            {
                "SetDestination",
                "SetTargetDestination",
                "SetTravelDestination",
                "SetRocketDestination"
            };

            foreach (string methodName in candidateNames)
            {
                MethodInfo method = craftType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == methodName && !m.IsStatic);
                if (method != null)
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(typeof(ClustercraftSetDestinationPatch), nameof(Postfix))
                    );
                    Debug.Log("[AutoRocketFuelPlanner] Destination patch target found: " + methodName);
                    return;
                }
            }

            Debug.LogWarning("[AutoRocketFuelPlanner] No destination method found on Clustercraft, destination sync patch skipped.");
        }

        private static void Postfix(Clustercraft __instance)
        {
            try
            {
                RocketAutoFuelService.TryApplyToCraft(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] SetDestination patch failed: " + e);
            }
        }
    }

    /// <summary>
    /// 把自动加注状态拼到火箭详情描述里。
    /// 这里通过反射构造 Descriptor，以适配不同构造签名。
    /// </summary>
    internal static class ClustercraftGetDescriptorsPatch
    {
        internal static void TryPatch(Harmony harmony)
        {
            Type craftType = typeof(Clustercraft);
            string[] candidateNames =
            {
                "GetDescriptors",
                "GetDescription",
                "GetTooltipDescriptors"
            };

            foreach (string methodName in candidateNames)
            {
                MethodInfo method = craftType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == methodName && !m.IsStatic);
                if (method != null)
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(typeof(ClustercraftGetDescriptorsPatch), nameof(Postfix))
                    );
                    Debug.Log("[AutoRocketFuelPlanner] Descriptor patch target found: " + methodName);
                    return;
                }
            }

            Debug.LogWarning("[AutoRocketFuelPlanner] No descriptor method found on Clustercraft, UI detail patch skipped.");
        }

        private static void Postfix(Clustercraft __instance, object __result)
        {
            try
            {
                IList list = __result as IList;
                if (__instance == null || list == null)
                {
                    return;
                }

                string text = RocketAutoFuelService.BuildDetailsText(__instance);
                object descriptor = CreateDescriptor(text);
                if (descriptor != null)
                {
                    list.Add(descriptor);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] GetDescriptors patch failed: " + e);
            }
        }

        /// <summary>
        /// 反射创建 Descriptor：
        /// - 只要求第一个参数是 string（描述文本）；
        /// - 其余参数给默认值，提升跨版本兼容性。
        /// </summary>
        private static object CreateDescriptor(string text)
        {
            Type descriptorType = AccessTools.TypeByName("Descriptor");
            if (descriptorType == null)
            {
                return null;
            }

            ConstructorInfo[] constructors = descriptorType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (ConstructorInfo constructor in constructors)
            {
                ParameterInfo[] parameters = constructor.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string))
                {
                    continue;
                }

                object[] args = new object[parameters.Length];
                args[0] = text;
                for (int i = 1; i < parameters.Length; i++)
                {
                    args[i] = GetDefaultValue(parameters[i].ParameterType);
                }

                try
                {
                    return constructor.Invoke(args);
                }
                catch
                {
                    // Try next constructor.
                }
            }

            return null;
        }

        /// <summary>
        /// 为构造函数参数生成保守默认值。
        /// </summary>
        private static object GetDefaultValue(Type type)
        {
            if (type == typeof(string))
            {
                return string.Empty;
            }

            if (type == typeof(bool))
            {
                return false;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    /// <summary>
    /// DetailsScreen 选中目标时的补丁：
    /// - 检测是否选中燃料舱/氧化剂舱；
    /// - 注入自定义 UI 面板到原有 UI 下方。
    /// </summary>
    internal static class DetailsScreenSelectPatch
    {
        /// <summary>
        /// 兼容式手动补丁注册。
        /// </summary>
        internal static void TryPatch(Harmony harmony)
        {
            Type detailsScreenType = typeof(DetailsScreen);
            string[] candidateNames =
            {
                "OnSelectTarget",
                "SelectTarget",
                "SetTarget",
                "ShowDetails"
            };

            foreach (string methodName in candidateNames)
            {
                MethodInfo method = detailsScreenType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == methodName && !m.IsStatic);

                if (method != null)
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(typeof(DetailsScreenSelectPatch), nameof(Postfix))
                    );
                    Debug.Log("[AutoRocketFuelPlanner] DetailsScreen select patch target found: " + methodName);
                    return;
                }
            }

            Debug.LogWarning("[AutoRocketFuelPlanner] No select method found on DetailsScreen, UI injection patch skipped.");
        }

        /// <summary>
        /// 后置补丁：在目标选中后注入 UI。
        /// </summary>
        private static void Postfix(DetailsScreen __instance, GameObject target)
        {
            try
            {
                if (target != null)
                {
                    FuelControlPanelManager.OnTargetSelected(target);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] DetailsScreen select patch failed: " + e);
            }
        }
    }

    /// <summary>
    /// DetailsScreen 取消选中时的补丁：
    /// - 清理注入的 UI 面板。
    /// </summary>
    internal static class DetailsScreenDeselectPatch
    {
        /// <summary>
        /// 兼容式手动补丁注册。
        /// </summary>
        internal static void TryPatch(Harmony harmony)
        {
            Type detailsScreenType = typeof(DetailsScreen);
            string[] candidateNames =
            {
                "OnDeselectTarget",
                "DeselectTarget",
                "ClearTarget",
                "HideDetails"
            };

            foreach (string methodName in candidateNames)
            {
                MethodInfo method = detailsScreenType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == methodName && !m.IsStatic);

                if (method != null)
                {
                    harmony.Patch(
                        method,
                        postfix: new HarmonyMethod(typeof(DetailsScreenDeselectPatch), nameof(Postfix))
                    );
                    Debug.Log("[AutoRocketFuelPlanner] DetailsScreen deselect patch target found: " + methodName);
                    return;
                }
            }

            // 备用方案：补丁 Update 方法来检测取消选中
            MethodInfo updateMethod = detailsScreenType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Update" && !m.IsStatic);

            if (updateMethod != null)
            {
                harmony.Patch(
                    updateMethod,
                    postfix: new HarmonyMethod(typeof(DetailsScreenDeselectPatch), nameof(UpdatePostfix))
                );
                Debug.Log("[AutoRocketFuelPlanner] DetailsScreen Update patch applied for deselection detection");
                return;
            }

            Debug.LogWarning("[AutoRocketFuelPlanner] No deselect method found on DetailsScreen, UI cleanup patch skipped.");
        }

        /// <summary>
        /// 后置补丁：在目标取消选中后清理 UI。
        /// </summary>
        private static void Postfix(DetailsScreen __instance)
        {
            try
            {
                FuelControlPanelManager.OnTargetDeselected();
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] DetailsScreen deselect patch failed: " + e);
            }
        }

        /// <summary>
        /// Update 后置补丁：检测是否已取消选中。
        /// </summary>
        private static void UpdatePostfix(DetailsScreen __instance)
        {
            try
            {
                // 通过反射检查当前是否有选中的目标
                FieldInfo selectedTargetField = typeof(DetailsScreen).GetField("selectedTarget",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (selectedTargetField != null)
                {
                    GameObject selectedTarget = selectedTargetField.GetValue(__instance) as GameObject;
                    if (selectedTarget == null)
                    {
                        FuelControlPanelManager.OnTargetDeselected();
                    }
                }
            }
            catch (Exception e)
            {
                // 静默处理 Update 补丁错误，避免日志刷屏
            }
        }
    }
}
