using HarmonyLib;
using KMod;
using PeterHan.PLib.Core;
using PeterHan.PLib.Options;
using System;
using System.Collections;
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
                PUtil.InitLibrary(true);
                new POptions().RegisterOptions(this, typeof(Config));
                Debug.Log("[AutoRocketFuelPlanner] Mod loaded.");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] OnLoad failed: " + e);
            }
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
    [HarmonyPatch(typeof(Clustercraft), "SetDestination")]
    internal static class ClustercraftSetDestinationPatch
    {
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
    [HarmonyPatch(typeof(Clustercraft), "GetDescriptors")]
    internal static class ClustercraftGetDescriptorsPatch
    {
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
}
