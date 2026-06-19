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
    public sealed class Patch : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            PUtil.InitLibrary(true);
            new POptions().RegisterOptions(this, typeof(Config));
            Debug.Log("[AutoRocketFuelPlanner] Mod loaded.");
        }
    }

    [HarmonyPatch(typeof(Clustercraft), "OnSpawn")]
    internal static class ClustercraftOnSpawnPatch
    {
        private static void Postfix(Clustercraft __instance)
        {
            RocketAutoFuelService.TryApplyToCraft(__instance);
        }
    }

    [HarmonyPatch(typeof(Clustercraft), "SetDestination")]
    internal static class ClustercraftSetDestinationPatch
    {
        private static void Postfix(Clustercraft __instance)
        {
            RocketAutoFuelService.TryApplyToCraft(__instance);
        }
    }

    [HarmonyPatch(typeof(Clustercraft), "Sim200ms")]
    internal static class ClustercraftSim200msPatch
    {
        private static void Postfix(Clustercraft __instance)
        {
            RocketAutoFuelService.TickAutoSync(__instance);
        }
    }

    [HarmonyPatch(typeof(Clustercraft), "GetDescriptors")]
    internal static class ClustercraftGetDescriptorsPatch
    {
        private static void Postfix(Clustercraft __instance, object __result)
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
