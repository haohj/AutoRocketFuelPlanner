using System;
using PeterHan.PLib.Options;
using UnityEngine;

namespace AutoRocketFuelPlanner
{
    /// <summary>
    /// 配置读取安全访问器：
    /// - 优先读取 PLib 单例配置；
    /// - 若配置系统未就绪或读取异常，回退到内置默认配置；
    /// - 避免初始化早期因为配置读取失败触发连锁异常。
    /// </summary>
    internal static class ConfigAccess
    {
        private static readonly Config Fallback = new Config();
        private static bool warned;

        public static Config Get()
        {
            try
            {
                Config cfg = SingletonOptions<Config>.Instance;
                return cfg ?? Fallback;
            }
            catch (Exception e)
            {
                if (!warned)
                {
                    warned = true;
                    Debug.LogWarning("[AutoRocketFuelPlanner] Config system not ready, using fallback defaults. " + e.Message);
                }

                return Fallback;
            }
        }
    }
}
