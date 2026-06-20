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

            // 列出所有可能相关的方法，帮助调试
            Debug.Log("[AutoRocketFuelPlanner] Scanning Clustercraft methods:");
            MethodInfo[] allMethods = craftType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo m in allMethods)
            {
                string nameLower = m.Name.ToLowerInvariant();
                if (nameLower.Contains("destination") || nameLower.Contains("target") ||
                    nameLower.Contains("travel") || nameLower.Contains("set") ||
                    nameLower.Contains("route") || nameLower.Contains("path"))
                {
                    Debug.Log("[AutoRocketFuelPlanner]   - " + m.Name + " (Params: " + m.GetParameters().Length + ")");
                }
            }

            string[] candidateNames =
            {
                "OnClusterDestinationChanged",  // 根据日志，这是正确的方法！
                "SetDestination",
                "SetTargetDestination",
                "SetTravelDestination",
                "SetRocketDestination",
                "SetRoute",
                "SetPath",
                "SetTarget",
                "UpdateDestination",
                "ChangeDestination",
                "SelectDestination"
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

            // 列出可能相关的描述方法
            Debug.Log("[AutoRocketFuelPlanner] Scanning Clustercraft descriptor methods:");
            MethodInfo[] allMethods = craftType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo m in allMethods)
            {
                string nameLower = m.Name.ToLowerInvariant();
                if (nameLower.Contains("descriptor") || nameLower.Contains("description") ||
                    nameLower.Contains("tooltip") || nameLower.Contains("info") ||
                    nameLower.Contains("text") || nameLower.Contains("label"))
                {
                    Debug.Log("[AutoRocketFuelPlanner]   - " + m.Name + " (Returns: " + m.ReturnType.Name + ", Params: " + m.GetParameters().Length + ")");
                }
            }

            string[] candidateNames =
            {
                "GetDescriptors",
                "GetDescription",
                "GetTooltipDescriptors",
                "GetTooltip",
                "GetInfo",
                "GetDetails",
                "GetStatus",
                "GetStatusString"
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

            // 列出所有 DetailsScreen 的方法，帮助调试
            Debug.Log("[AutoRocketFuelPlanner] Scanning DetailsScreen methods:");
            MethodInfo[] allMethods = detailsScreenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo m in allMethods)
            {
                // 只记录可能相关的方法
                string nameLower = m.Name.ToLowerInvariant();
                if (nameLower.Contains("select") || nameLower.Contains("target") ||
                    nameLower.Contains("detail") || nameLower.Contains("show") ||
                    nameLower.Contains("set") || nameLower.Contains("open"))
                {
                    Debug.Log("[AutoRocketFuelPlanner]   - " + m.Name + " (Params: " + m.GetParameters().Length + ")");
                }
            }

            string[] candidateNames =
            {
                "OnSelectObject",  // 根据日志，这是正确的方法！
                "OnSelectTarget",
                "SelectTarget",
                "SetTarget",
                "ShowDetails",
                "ShowTarget",
                "SelectObject",
                "SetSelectedTarget",
                "OnTargetSelected",
                "DisplayDetails",
                "OpenDetails"
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

            // 如果候选名都没找到，尝试通过参数类型匹配
            Debug.Log("[AutoRocketFuelPlanner] Trying parameter-based matching...");
            foreach (MethodInfo method in allMethods)
            {
                ParameterInfo[] parameters = method.GetParameters();
                // 查找接受 GameObject 参数的方法
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(GameObject))
                {
                    Debug.Log("[AutoRocketFuelPlanner] Found method with GameObject param: " + method.Name);
                    // 检查方法名是否可能与选择相关
                    string nameLower = method.Name.ToLowerInvariant();
                    if (!nameLower.Contains("get") && !nameLower.Contains("find") && !nameLower.Contains("create"))
                    {
                        try
                        {
                            harmony.Patch(
                                method,
                                postfix: new HarmonyMethod(typeof(DetailsScreenSelectPatch), nameof(Postfix))
                            );
                            Debug.Log("[AutoRocketFuelPlanner] DetailsScreen select patch applied to: " + method.Name);
                            return;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[AutoRocketFuelPlanner] Failed to patch " + method.Name + ": " + e.Message);
                        }
                    }
                }
            }

            Debug.LogWarning("[AutoRocketFuelPlanner] No select method found on DetailsScreen, UI injection patch skipped.");
        }

        /// <summary>
        /// 后置补丁：在目标选中后注入 UI。
        /// 这个方法会在游戏选中某个对象后被自动调用
        ///
        /// 重要说明：
        /// - OnSelectObject 的参数是 System.Object data（不是 GameObject）
        /// - data 可能是多种类型：GameObject、Component、KSelectable 等
        /// - 我们需要将其转换为 GameObject 才能操作游戏对象
        /// </summary>
        private static void Postfix(DetailsScreen __instance, object data)
        {
            try
            {
                // 第一步：记录 Postfix 被调用（用于调试）
                // 这会告诉我们补丁是否正确应用并触发
                Debug.Log("[AutoRocketFuelPlanner] >>> Postfix 被调用了! data=" +
                    (data != null
                        ? data.GetType().Name + " (" + data.ToString().Substring(0, Math.Min(50, data.ToString().Length)) + ")"
                        : "null（空值）"));

                // 第二步：检查 data 是否为空
                if (data == null)
                {
                    Debug.Log("[AutoRocketFuelPlanner] data 是 null（空值），跳过处理");
                    return;
                }

                // 第三步：尝试将 data 转换为 GameObject
                // 这是期望的最简单情况
                GameObject target = data as GameObject;
                Debug.Log("[AutoRocketFuelPlanner] 转换为 GameObject 后: target=" +
                    (target != null ? target.name : "null（转换失败）"));

                // 第四步：如果转换失败，尝试其他类型
                if (target == null)
                {
                    Debug.Log("[AutoRocketFuelPlanner] data 不是 GameObject，检查是否是 Component（组件）...");

                    // 尝试将 data 转换为 Component（组件）
                    // Component 是 GameObject 上的组件（如 Transform、Renderer 等）
                    if (data is Component component)
                    {
                        // 如果是 Component，获取其所属的 GameObject
                        target = component.gameObject;
                        Debug.Log("[AutoRocketFuelPlanner] data 是 Component（组件），获取到 GameObject: " + target.name);
                    }
                    else
                    {
                        // 如果都不是，记录类型信息用于调试
                        Debug.Log("[AutoRocketFuelPlanner] Data 既不是 GameObject 也不是 Component: " + data.GetType().FullName);

                        // 记录类型的继承层次，帮助理解这个对象是什么
                        Type dataType = data.GetType();
                        Debug.Log("[AutoRocketFuelPlanner] Data 类型层次结构:");
                        Debug.Log("[AutoRocketFuelPlanner]   -> " + dataType.FullName);

                        // 遍历继承链，查看所有父类型
                        while (dataType.BaseType != null)
                        {
                            dataType = dataType.BaseType;
                            Debug.Log("[AutoRocketFuelPlanner]   -> " + dataType.FullName);
                        }

                        // 未知类型，无法处理，返回
                        return;
                    }
                }

                // 第五步：如果成功获取到 GameObject，调用 UI 注入
                if (target != null)
                {
                    Debug.Log("[AutoRocketFuelPlanner] 成功获取 GameObject: " + target.name);
                    Debug.Log("[AutoRocketFuelPlanner] 调用 FuelControlPanelManager.OnTargetSelected()...");

                    // 调用 UI 管理器来注入自定义 UI
                    FuelControlPanelManager.OnTargetSelected(target);

                    Debug.Log("[AutoRocketFuelPlanner] OnTargetSelected() 调用完成");
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] DetailsScreen select patch 失败: " + e);
                Debug.LogError("[AutoRocketFuelPlanner] 错误堆栈: " + e.StackTrace);
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
                "DeselectAndClose",  // 根据日志，这个方法存在
                "OnDeselectTarget",
                "DeselectTarget",
                "ClearTarget",
                "HideDetails",
                "ClearSelection",
                "Deselect",
                "CloseDetails",
                "HidePanel"
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
            catch (Exception)
            {
                // Silently handle Update patch errors to avoid log spam
            }
        }
    }
}
