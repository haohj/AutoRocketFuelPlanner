using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PeterHan.PLib.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= 阅读顺序导图 =========================
     * 这个文件负责游戏内 UI 注入，推荐按下面顺序看：
     * 1) FuelControlInjector.TryInject
     *    - 理解如何检测燃料舱/氧化剂舱并注入 UI。
     * 2) FuelControlPanel.BuildUI
     *    - 看 UI 的构建方式（滑动条、标签等）。
     * 3) FuelControlPanel.OnFuelSliderChanged / OnOxidizerSliderChanged
     *    - 看滑动条值变化时如何触发反算并更新 UI。
     * 4) FuelControlPanel.RefreshFromGame
     *    - 看如何从游戏对象读取数据并更新 UI 显示。
     * 5) FuelControlPanelManager
     *    - 看如何管理多个面板的生命周期和刷新。
     * ==============================================================
     */
    /// <summary>
     /// 燃料/氧化剂控制面板 UI 注入器：
     * - 检测玩家选中的燃料舱/氧化剂舱；
     * - 在原有 UI 下方注入自定义滑动条和参数显示；
     * - 实现实时参数联动和计算。
     /// </summary>
    internal static class FuelControlInjector
    {
        private static GameObject injectedPanel;
        private static FuelControlPanel controlPanel;

        /// <summary>
        /// 尝试注入 UI 到选中的燃料舱/氧化剂舱下方。
        /// </summary>
        public static void TryInject(GameObject target)
        {
            try
            {
                // 清理上一次注入
                Cleanup();

                if (target == null)
                {
                    return;
                }

                // 检测选中对象是否是燃料舱/氧化剂舱
                if (!IsFuelOrOxidizerTank(target))
                {
                    return;
                }

                // 获取火箭对象
                Clustercraft craft = target.GetComponentInParent<Clustercraft>();
                if (craft == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find Clustercraft for target: " + target.name);
                    return;
                }

                // 获取当前活跃的 SideScreen
                DetailsScreen detailsScreen = target.GetComponentInParent<DetailsScreen>();
                if (detailsScreen == null)
                {
                    // 尝试从场景中查找
                    detailsScreen = UnityEngine.Object.FindObjectOfType<DetailsScreen>();
                }

                if (detailsScreen == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find DetailsScreen");
                    return;
                }

                // 获取当前活跃的 SideScreenContent
                SideScreenContent activeScreen = GetActiveSideScreen(detailsScreen);
                if (activeScreen == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find active SideScreen");
                    return;
                }

                // 在当前 SideScreen 内容底部创建自定义面板
                injectedPanel = new GameObject("AutoFuelControlPanel");
                injectedPanel.transform.SetParent(activeScreen.transform, false);

                // 设置位置在底部
                RectTransform rectTransform = injectedPanel.AddComponent<RectTransform>();
                rectTransform.anchorMin = new Vector2(0, 0);
                rectTransform.anchorMax = new Vector2(1, 0);
                rectTransform.pivot = new Vector2(0.5f, 0);
                rectTransform.sizeDelta = new Vector2(0, 180); // 高度

                // 创建控制面板
                controlPanel = injectedPanel.AddComponent<FuelControlPanel>();
                controlPanel.Initialize(craft, target);

                Debug.Log("[AutoRocketFuelPlanner] Fuel control panel injected successfully");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to inject fuel control panel: " + e);
                Cleanup();
            }
        }

        /// <summary>
        /// 清理注入的 UI。
        /// </summary>
        public static void Cleanup()
        {
            if (injectedPanel != null)
            {
                UnityEngine.Object.Destroy(injectedPanel);
                injectedPanel = null;
                controlPanel = null;
            }
        }

        /// <summary>
        /// 检测目标是否是燃料舱或氧化剂舱。
        /// </summary>
        private static bool IsFuelOrOxidizerTank(GameObject target)
        {
            foreach (Component component in target.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name.ToLowerInvariant();
                if (typeName.Contains("tank") && (typeName.Contains("fuel") || typeName.Contains("oxidizer")))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 获取当前活跃的 SideScreenContent。
        /// </summary>
        private static SideScreenContent GetActiveSideScreen(DetailsScreen detailsScreen)
        {
            // 尝试通过反射获取 activeSideScreen 字段
            FieldInfo field = typeof(DetailsScreen).GetField("activeSideScreen",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field != null)
            {
                return field.GetValue(detailsScreen) as SideScreenContent;
            }

            // 备用方案：查找 DetailsScreen 的子对象中的 SideScreenContent
            foreach (SideScreenContent screen in detailsScreen.GetComponentsInChildren<SideScreenContent>(true))
            {
                if (screen.gameObject.activeInHierarchy)
                {
                    return screen;
                }
            }

            return null;
        }
    }

    /// <summary>
     /// 燃料/氧化剂控制面板组件：
     /// - 显示距离、燃料、氧化剂三个滑动条；
     /// - 实现实时联动：调整一个参数时自动计算其他参数；
     /// - 与 RocketAutoFuelService 集成。
     /// </summary>
    internal class FuelControlPanel : MonoBehaviour
    {
        private Clustercraft currentCraft;
        private GameObject currentTarget;

        // UI 元素
        private Slider distanceSlider;
        private Slider fuelSlider;
        private Slider oxidizerSlider;
        private LocText distanceLabel;
        private LocText fuelLabel;
        private LocText oxidizerLabel;
        private LocText statusLabel;

        // 防止递归更新的锁
        private bool isUpdatingFromGame;
        private bool isUpdatingFromSlider;

        // 滑动条防抖
        private float lastSliderCallbackTime;
        private const float SliderDebounceSeconds = 0.1f;

        // 关键词定义（与 RocketAutoFuelService 保持一致）
        private static readonly string[] FuelKeywords = { "fuel" };
        private static readonly string[] OxidizerKeywords = { "oxidizer", "oxylite", "ox" };

        /// <summary>
        /// 初始化面板。
        /// </summary>
        public void Initialize(Clustercraft craft, GameObject target)
        {
            currentCraft = craft;
            currentTarget = target;
            BuildUI();
            RefreshFromGame();
        }

        /// <summary>
        /// 构建 UI 元素。
        /// </summary>
        private void BuildUI()
        {
            // 创建主面板容器
            GameObject panel = new GameObject("AutoFuelMainPanel");
            panel.transform.SetParent(transform, false);

            // 添加垂直布局
            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // 添加内容大小适配器
            ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.sizeDelta = Vector2.zero;

            // 状态标签
            statusLabel = CreateLabel(panel.transform, "自动加注控制", true);
            statusLabel.color = new Color(0.8f, 0.9f, 1f, 1f); // 浅蓝色标题

            // 添加分隔线
            CreateSeparator(panel.transform);

            // 目标距离滑动条
            distanceLabel = CreateLabel(panel.transform, "目标距离: -- 格");
            distanceSlider = CreateSlider(panel.transform, 0f, 100000f, OnDistanceSliderChanged);

            // 燃料目标滑动条
            fuelLabel = CreateLabel(panel.transform, "燃料目标: -- kg");
            fuelSlider = CreateSlider(panel.transform, 0f, 1000f, OnFuelSliderChanged);

            // 氧化剂目标滑动条
            oxidizerLabel = CreateLabel(panel.transform, "氧化剂目标: -- kg");
            oxidizerSlider = CreateSlider(panel.transform, 0f, 1000f, OnOxidizerSliderChanged);

            // 添加说明标签
            CreateLabel(panel.transform, "拖动滑动条调整参数，其他值自动计算", false)
                .color = new Color(0.6f, 0.6f, 0.6f, 1f); // 灰色说明文本
        }

        /// <summary>
        /// 创建标签。
        /// </summary>
        private LocText CreateLabel(Transform parent, string text, bool isTitle = false)
        {
            GameObject go = new GameObject("Label");
            go.transform.SetParent(parent, false);

            LocText locText = go.AddComponent<LocText>();
            locText.text = text;
            locText.enableWordWrapping = true;
            locText.alignment = isTitle ? TextAlignmentOptions.Center : TextAlignmentOptions.Left;

            // 设置字体大小
            locText.fontSize = isTitle ? 14 : 11;

            RectTransform rectTransform = go.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(0, isTitle ? 28 : 20);

            return locText;
        }

        /// <summary>
        /// 创建分隔线。
        /// </summary>
        private void CreateSeparator(Transform parent)
        {
            GameObject go = new GameObject("Separator");
            go.transform.SetParent(parent, false);

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.3f, 0.3f, 0.3f, 1f);

            RectTransform rectTransform = go.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(0, 2);

            // 添加水平布局元素
            LayoutElement layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 2;
        }

        /// <summary>
        /// 创建滑动条。
        /// </summary>
        private Slider CreateSlider(Transform parent, float min, float max,
            UnityEngine.Events.UnityAction<float> onChanged)
        {
            GameObject go = new GameObject("Slider");
            go.transform.SetParent(parent, false);

            // 添加布局元素
            LayoutElement layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 30;

            // 创建背景
            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(go.transform, false);
            Image bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            RectTransform bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.25f);
            bgRect.anchorMax = new Vector2(1, 0.75f);
            bgRect.sizeDelta = Vector2.zero;

            // 创建填充区域
            GameObject fillAreaGo = new GameObject("Fill Area");
            fillAreaGo.transform.SetParent(go.transform, false);
            RectTransform fillAreaRect = fillAreaGo.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.sizeDelta = Vector2.zero;

            GameObject fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(fillAreaGo.transform, false);
            Image fillImage = fillGo.AddComponent<Image>();
            fillImage.color = new Color(0.3f, 0.6f, 1f, 1f); // 蓝色填充
            RectTransform fillRect = fillGo.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.sizeDelta = Vector2.zero;

            // 创建滑块手柄
            GameObject handleAreaGo = new GameObject("Handle Slide Area");
            handleAreaGo.transform.SetParent(go.transform, false);
            RectTransform handleAreaRect = handleAreaGo.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.sizeDelta = Vector2.zero;

            GameObject handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(handleAreaGo.transform, false);
            Image handleImage = handleGo.AddComponent<Image>();
            handleImage.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            RectTransform handleRect = handleGo.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20, 0);

            // 添加滑动条组件
            Slider slider = go.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImage;
            slider.minValue = min;
            slider.maxValue = max;
            slider.onValueChanged.AddListener(onChanged);

            RectTransform goRect = go.GetComponent<RectTransform>();
            goRect.sizeDelta = new Vector2(0, 30);

            return slider;
        }

        /// <summary>
        /// 每帧更新：从游戏读取最新数据并刷新 UI。
        /// </summary>
        private void Update()
        {
            if (currentCraft == null || isUpdatingFromSlider)
            {
                return;
            }

            // 每 0.5 秒刷新一次（避免每帧刷新造成性能问题）
            if (Time.frameCount % 30 == 0)
            {
                RefreshFromGame();
            }
        }

        /// <summary>
        /// 从游戏对象读取数据并更新 UI 显示。
        /// </summary>
        public void RefreshFromGame()
        {
            if (currentCraft == null)
            {
                return;
            }

            isUpdatingFromGame = true;

            try
            {
                // 从游戏读取当前值
                bool hasDistance = TryResolveTargetDistance(currentCraft, out float currentDistance);
                bool hasFuel = TryReadMassFromTank(currentCraft, FuelKeywords, out float currentFuel);
                bool hasOxidizer = TryReadMassFromTank(currentCraft, OxidizerKeywords, out float currentOxidizer);

                // 更新滑动条范围
                float maxFuel = GetTankCapacity(currentCraft, FuelKeywords);
                float maxOxidizer = GetTankCapacity(currentCraft, OxidizerKeywords);

                distanceSlider.minValue = 0f;
                distanceSlider.maxValue = 100000f;

                fuelSlider.minValue = 0f;
                fuelSlider.maxValue = maxFuel;

                oxidizerSlider.minValue = 0f;
                oxidizerSlider.maxValue = maxOxidizer;

                // 更新滑动条值（不触发回调）
                if (hasDistance)
                {
                    distanceSlider.SetValueWithoutNotify(currentDistance);
                }

                if (hasFuel)
                {
                    fuelSlider.SetValueWithoutNotify(currentFuel);
                }

                if (hasOxidizer)
                {
                    oxidizerSlider.SetValueWithoutNotify(currentOxidizer);
                }

                // 更新标签显示
                distanceLabel.text = $"目标距离: {(hasDistance ? currentDistance.ToString("F0") : "--")} 格";
                fuelLabel.text = $"燃料目标: {(hasFuel ? currentFuel.ToString("F1") : "--")} / {maxFuel:F0} kg";
                oxidizerLabel.text = $"氧化剂目标: {(hasOxidizer ? currentOxidizer.ToString("F1") : "--")} / {maxOxidizer:F0} kg";

                // 更新状态
                if (hasFuel || hasOxidizer)
                {
                    statusLabel.text = "自动加注控制 (活跃)";
                    statusLabel.color = new Color(0.3f, 0.9f, 0.3f, 1f); // 绿色
                }
                else
                {
                    statusLabel.text = "自动加注控制 (等待数据)";
                    statusLabel.color = new Color(0.8f, 0.8f, 0.3f, 1f); // 黄色
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to refresh UI from game: " + e);
                statusLabel.text = "自动加注控制 (错误)";
                statusLabel.color = new Color(0.9f, 0.3f, 0.3f, 1f); // 红色
            }

            isUpdatingFromGame = false;
        }

        /// <summary>
        /// 目标距离滑动条变化回调。
        /// </summary>
        private void OnDistanceSliderChanged(float value)
        {
            if (isUpdatingFromGame || currentCraft == null)
            {
                return;
            }

            // 简单防抖
            float now = Time.unscaledTime;
            if (now - lastSliderCallbackTime < SliderDebounceSeconds)
            {
                lastSliderCallbackTime = now;
                return;
            }
            lastSliderCallbackTime = now;

            isUpdatingFromSlider = true;

            try
            {
                distanceLabel.text = $"目标距离: {value:F0} 格";

                // 检测引擎类型
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // 使用距离计算燃料和氧化剂
                FuelPlan plan = FuelCalculator.CreatePlanFromDistance(value, engineKind, profile);

                // 写入燃料和氧化剂
                bool fuelApplied = TryApplyMassToTank(currentCraft, plan.FuelKg, FuelKeywords, out float appliedFuel);
                bool oxidizerApplied = TryApplyMassToTank(currentCraft, plan.OxidizerKg, OxidizerKeywords, out float appliedOxidizer);

                // 更新滑动条和标签
                if (fuelApplied)
                {
                    fuelSlider.SetValueWithoutNotify(appliedFuel);
                    fuelLabel.text = $"燃料目标: {appliedFuel:F1} kg";
                }

                if (oxidizerApplied)
                {
                    oxidizerSlider.SetValueWithoutNotify(appliedOxidizer);
                    oxidizerLabel.text = $"氧化剂目标: {appliedOxidizer:F1} kg";
                }

                // 更新状态
                statusLabel.text = "自动加注控制 (距离模式)";
                statusLabel.color = new Color(0.3f, 0.9f, 0.3f, 1f);

                Debug.Log($"[AutoRocketFuelPlanner] Distance changed to {value:F0}, applied fuel: {appliedFuel:F1}, oxidizer: {appliedOxidizer:F1}");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to process distance change: " + e);
                statusLabel.text = "自动加注控制 (计算错误)";
                statusLabel.color = new Color(0.9f, 0.3f, 0.3f, 1f);
            }

            isUpdatingFromSlider = false;
        }

        /// <summary>
        /// 燃料滑动条变化回调。
        /// </summary>
        private void OnFuelSliderChanged(float value)
        {
            if (isUpdatingFromGame || currentCraft == null)
            {
                return;
            }

            // 简单防抖
            float now = Time.unscaledTime;
            if (now - lastSliderCallbackTime < SliderDebounceSeconds)
            {
                lastSliderCallbackTime = now;
                return;
            }
            lastSliderCallbackTime = now;

            isUpdatingFromSlider = true;

            try
            {
                fuelLabel.text = $"燃料目标: {value:F1} kg";

                // 检测引擎类型
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // 使用燃料计算距离和氧化剂
                FuelPlan plan = FuelCalculator.CreatePlanFromFuel(value, engineKind, profile);

                // 更新距离显示
                distanceSlider.SetValueWithoutNotify(plan.TargetDistance);
                distanceLabel.text = $"目标距离: {plan.TargetDistance:F0} 格";

                // 写入氧化剂
                if (profile.RequiresOxidizer)
                {
                    bool oxidizerApplied = TryApplyMassToTank(currentCraft, plan.OxidizerKg, OxidizerKeywords, out float appliedOxidizer);
                    if (oxidizerApplied)
                    {
                        oxidizerSlider.SetValueWithoutNotify(appliedOxidizer);
                        oxidizerLabel.text = $"氧化剂目标: {appliedOxidizer:F1} kg";
                    }
                }

                // 更新状态
                statusLabel.text = "自动加注控制 (燃料模式)";
                statusLabel.color = new Color(0.3f, 0.6f, 1f, 1f); // 蓝色

                Debug.Log($"[AutoRocketFuelPlanner] Fuel changed to {value:F1}, distance: {plan.TargetDistance:F0}, oxidizer: {plan.OxidizerKg:F1}");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to process fuel change: " + e);
                statusLabel.text = "自动加注控制 (计算错误)";
                statusLabel.color = new Color(0.9f, 0.3f, 0.3f, 1f);
            }

            isUpdatingFromSlider = false;
        }

        /// <summary>
        /// 氧化剂滑动条变化回调。
        /// </summary>
        private void OnOxidizerSliderChanged(float value)
        {
            if (isUpdatingFromGame || currentCraft == null)
            {
                return;
            }

            // 简单防抖
            float now = Time.unscaledTime;
            if (now - lastSliderCallbackTime < SliderDebounceSeconds)
            {
                lastSliderCallbackTime = now;
                return;
            }
            lastSliderCallbackTime = now;

            isUpdatingFromSlider = true;

            try
            {
                oxidizerLabel.text = $"氧化剂目标: {value:F1} kg";

                // 检测引擎类型
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // 使用氧化剂计算燃料
                FuelPlan plan = FuelCalculator.CreatePlanFromOxidizer(value, engineKind, profile);

                // 更新距离显示
                distanceSlider.SetValueWithoutNotify(plan.TargetDistance);
                distanceLabel.text = $"目标距离: {plan.TargetDistance:F0} 格";

                // 写入燃料
                bool fuelApplied = TryApplyMassToTank(currentCraft, plan.FuelKg, FuelKeywords, out float appliedFuel);
                if (fuelApplied)
                {
                    fuelSlider.SetValueWithoutNotify(appliedFuel);
                    fuelLabel.text = $"燃料目标: {appliedFuel:F1} kg";
                }

                // 更新状态
                statusLabel.text = "自动加注控制 (氧化剂模式)";
                statusLabel.color = new Color(0.9f, 0.6f, 0.3f, 1f); // 橙色

                Debug.Log($"[AutoRocketFuelPlanner] Oxidizer changed to {value:F1}, distance: {plan.TargetDistance:F0}, fuel: {plan.FuelKg:F1}");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to process oxidizer change: " + e);
                statusLabel.text = "自动加注控制 (计算错误)";
                statusLabel.color = new Color(0.9f, 0.3f, 0.3f, 1f);
            }

            isUpdatingFromSlider = false;
        }

        /// <summary>
        /// 通过反射读取目标距离（与 RocketAutoFuelService 保持一致）。
        /// </summary>
        private static bool TryResolveTargetDistance(Clustercraft craft, out float distance)
        {
            distance = 0f;
            Type type = craft.GetType();

            string[] distanceKeywords = { "distance", "travel", "range", "path", "target" };

            // 尝试读取字段/属性
            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!distanceKeywords.Any(name.Contains))
                {
                    continue;
                }

                if (ReflectionHelpers.TryReadFloat(craft, member, out float value) && value > 0f)
                {
                    distance = value;
                    return true;
                }
            }

            // 尝试调用无参方法
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo method in methods)
            {
                if (method.GetParameters().Length != 0)
                {
                    continue;
                }

                string methodName = method.Name.ToLowerInvariant();
                if (!distanceKeywords.Any(methodName.Contains))
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
                    if (result != null)
                    {
                        distance = Convert.ToSingle(result);
                        if (distance > 0f)
                        {
                            return true;
                        }
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
        /// 从 Tank 读取质量（与 RocketAutoFuelService 保持一致）。
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
        /// 获取 Tank 候选集合（与 RocketAutoFuelService 保持一致）。
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
        /// 写入质量到 Tank（与 RocketAutoFuelService 保持一致）。
        /// </summary>
        private static bool TryApplyMassToTank(Clustercraft craft, float plannedMassKg, string[] tankKeywords, out float finalAppliedKg)
        {
            finalAppliedKg = 0f;
            List<Component> candidates = GetTankCandidates(craft, tankKeywords);

            foreach (Component candidate in candidates)
            {
                float maxPercent = Mathf.Clamp(ConfigAccess.Get().MaxAutoFillPercent, 1f, 100f) / 100f;
                Storage storage = candidate.gameObject.GetComponent<Storage>();
                float maxCapacity = storage != null ? storage.capacityKg * maxPercent : 100000f;
                float clamped = Mathf.Clamp(plannedMassKg, 0f, maxCapacity);

                if (TryWriteMass(candidate, clamped))
                {
                    finalAppliedKg = clamped;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 写入质量（与 RocketAutoFuelService 保持一致）。
        /// </summary>
        private static bool TryWriteMass(Component tankComponent, float targetMass)
        {
            Type type = tankComponent.GetType();
            string[] writableMassKeywords = { "target", "desired", "user", "requested", "fill", "amount", "mass" };

            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!writableMassKeywords.Any(name.Contains))
                {
                    continue;
                }

                if (ReflectionHelpers.TryWriteFloat(tankComponent, member, targetMass))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 读取质量（与 RocketAutoFuelService 保持一致）。
        /// </summary>
        private static bool TryReadMass(Component tankComponent, out float value)
        {
            value = 0f;
            Type type = tankComponent.GetType();
            string[] readableMassPriorityKeywords = { "target", "desired", "requested", "user", "fill" };
            string[] readableMassFallbackKeywords = { "amount", "mass" };

            // 优先读取目标值
            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!readableMassPriorityKeywords.Any(name.Contains))
                {
                    continue;
                }

                if (ReflectionHelpers.TryReadFloat(tankComponent, member, out float mass) && mass >= 0f)
                {
                    value = mass;
                    return true;
                }
            }

            // 兜底读取通用质量值
            foreach (MemberInfo member in ReflectionHelpers.GetMembers(type))
            {
                string name = member.Name.ToLowerInvariant();
                if (!readableMassFallbackKeywords.Any(name.Contains))
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

        /// <summary>
        /// 获取 Tank 容量。
        /// </summary>
        private static float GetTankCapacity(Clustercraft craft, string[] keywords)
        {
            foreach (Component component in craft.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name.ToLowerInvariant();
                if (typeName.Contains("tank") && keywords.Any(typeName.Contains))
                {
                    Storage storage = component.gameObject.GetComponent<Storage>();
                    if (storage != null && storage.capacityKg > 0f)
                    {
                        return storage.capacityKg;
                    }
                }
            }

            return 1000f; // 兜底值
        }

        /// <summary>
        /// 检测引擎类型（与 RocketAutoFuelService 保持一致）。
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
        /// 清理时取消事件订阅。
        /// </summary>
        private void OnDestroy()
        {
            if (distanceSlider != null)
            {
                distanceSlider.onValueChanged.RemoveListener(OnDistanceSliderChanged);
            }

            if (fuelSlider != null)
            {
                fuelSlider.onValueChanged.RemoveListener(OnFuelSliderChanged);
            }

            if (oxidizerSlider != null)
            {
                oxidizerSlider.onValueChanged.RemoveListener(OnOxidizerSliderChanged);
            }
        }
    }

    /// <summary>
     /// 面板管理器：
     /// - 在 DetailsScreen 选中目标时注入 UI；
     /// - 在取消选中时清理 UI；
     /// - 提供全局刷新接口。
     /// </summary>
    internal static class FuelControlPanelManager
    {
        /// <summary>
        /// 当 DetailsScreen 选中目标时调用。
        /// </summary>
        public static void OnTargetSelected(GameObject target)
        {
            FuelControlInjector.TryInject(target);
        }

        /// <summary>
        /// 当 DetailsScreen 取消选中时调用。
        /// </summary>
        public static void OnTargetDeselected()
        {
            FuelControlInjector.Cleanup();
        }

        /// <summary>
        /// 强制清理所有面板（游戏退出时调用）。
        /// </summary>
        public static void CleanupAll()
        {
            FuelControlInjector.Cleanup();
        }
    }
}
