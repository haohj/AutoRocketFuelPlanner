using PeterHan.PLib.Options;
using UnityEngine;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图 =========================
     * 这个文件很短，建议顺序：
     * 1) OnSpawn：拿到 craft 引用并设置首轮延迟。
     * 2) Update：看三段 guard（对象判空、开关判断、时间窗口）。
     * 3) TickAutoSync 调用：真正业务在 RocketAutoFuelService 里。
     * 4) catch -> ReportRealtimeSyncFailure：异常上报与熔断入口。
     *
     * 你可以把它理解成“节流调度器”，而不是计算器。
     * ==============================================================
     */
    /// <summary>
    /// 兼容版实时联动组件：
    /// - 挂在每台火箭上；
    /// - 用低频 Update 轮询代替高风险 Harmony 高频补丁；
    /// - 负责调用 RocketAutoFuelService.TickAutoSync。
    /// </summary>
    internal sealed class AutoFuelRuntimeSync : KMonoBehaviour
    {
        // 轮询间隔：值越小越实时，但CPU开销越高。
        private const float SyncIntervalSeconds = 0.25f;
        // 下一次允许执行同步的时间戳。
        private float nextSyncTime;
        // 所属火箭对象。
        private Clustercraft craft;

        protected override void OnSpawn()
        {
            base.OnSpawn();
            // 组件挂在 Clustercraft 同物体上，直接取即可。
            craft = GetComponent<Clustercraft>();
            // 给 1 秒缓冲，避免生成瞬间和其他系统初始化抢时序。
            nextSyncTime = Time.unscaledTime + 1f;
        }

        /// <summary>
        /// 低频轮询：
        /// 1) 先做全部开关/熔断判断；
        /// 2) 到达时间窗口才执行一次同步；
        /// 3) 异常上报给服务层做熔断统计。
        /// </summary>
        private void Update()
        {
            if (craft == null)
            {
                return;
            }

            Config cfg = SingletonOptions<Config>.Instance;
            if (!cfg.EnableRealtimeSync || !RocketAutoFuelService.IsRealtimeSyncAllowed())
            {
                return;
            }

            if (Time.unscaledTime < nextSyncTime)
            {
                return;
            }

            // 先推迟下一次窗口，避免一次异常导致紧密重试。
            nextSyncTime = Time.unscaledTime + SyncIntervalSeconds;
            try
            {
                RocketAutoFuelService.TickAutoSync(craft);
            }
            catch (System.Exception e)
            {
                RocketAutoFuelService.ReportRealtimeSyncFailure(e);
            }
        }
    }
}
