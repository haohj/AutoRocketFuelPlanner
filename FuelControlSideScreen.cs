using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace AutoRocketFuelPlanner
{
    /*
     * ========================= File Reading Guide =========================
     * This file handles in-game UI injection. Recommended reading order:
     * 1) FuelControlInjector.TryInject
     *    - Understand how to detect fuel/oxidizer tanks and inject UI.
     * 2) FuelControlPanel.BuildUI
     *    - See how UI is built (sliders, labels, etc.).
     * 3) FuelControlPanel.OnFuelSliderChanged / OnOxidizerSliderChanged
     *    - See how slider value changes trigger reverse calculations and UI updates.
     * 4) FuelControlPanel.RefreshFromGame
     *    - See how to read data from game objects and update UI display.
     * 5) FuelControlPanelManager
     *    - See how to manage multiple panels' lifecycle and refresh.
     * ======================================================================
     */
    /// <summary>
    /// Fuel/Oxidizer control panel UI injector:
    /// - Detects player's selected fuel/oxidizer tank;
    /// - Injects custom slider and parameter display below original UI;
    /// - Implements real-time parameter linkage and calculation.
    /// </summary>
    internal static class FuelControlInjector
    {
        private static GameObject injectedPanel;
        private static FuelControlPanel controlPanel;

        /// <summary>
        /// Try to inject UI below selected fuel/oxidizer tank.
        /// </summary>
        public static void TryInject(GameObject target)
        {
            try
            {
                Debug.Log("[AutoRocketFuelPlanner] TryInject called for target: " + (target != null ? target.name : "null"));

                // Clean up previous injection
                Cleanup();

                if (target == null)
                {
                    Debug.Log("[AutoRocketFuelPlanner] Target is null, skipping");
                    return;
                }

                // List all components on target for debugging
                Debug.Log("[AutoRocketFuelPlanner] Components on target:");
                foreach (Component component in target.GetComponents<Component>())
                {
                    if (component != null)
                    {
                        Debug.Log("[AutoRocketFuelPlanner]   - " + component.GetType().Name);
                    }
                }

                // Check if selected object is fuel/oxidizer tank
                if (!IsFuelOrOxidizerTank(target))
                {
                    Debug.Log("[AutoRocketFuelPlanner] Target is not a fuel/oxidizer tank, skipping");
                    return;
                }

                Debug.Log("[AutoRocketFuelPlanner] Target is fuel/oxidizer tank, proceeding with injection");

                // 获取火箭对象（Clustercraft）
                // 注意：燃料舱/氧化剂舱可能不在 Clustercraft 的子对象中
                // 所以我们尝试多种方式查找
                Clustercraft craft = target.GetComponentInParent<Clustercraft>();

                if (craft == null)
                {
                    Debug.Log("[AutoRocketFuelPlanner] 在父对象中找不到 Clustercraft，尝试从场景中查找火箭...");
                    // 尝试从场景中查找任何 Clustercraft
                    Clustercraft[] allCrafts = UnityEngine.Object.FindObjectsByType<Clustercraft>(FindObjectsSortMode.None);
                    if (allCrafts.Length > 0)
                    {
                        craft = allCrafts[0]; // 使用找到的第一个火箭
                        Debug.Log("[AutoRocketFuelPlanner] 从场景中找到火箭: " + craft.name);
                    }
                    else
                    {
                        Debug.Log("[AutoRocketFuelPlanner] 场景中没有找到火箭，将使用基本模式（无自动计算）");
                        // 不 return，继续执行，但 UI 将以只读模式显示
                    }
                }
                else
                {
                    Debug.Log("[AutoRocketFuelPlanner] 在父对象中找到 Clustercraft: " + craft.name);
                }

                // Get current active SideScreen
                DetailsScreen detailsScreen = target.GetComponentInParent<DetailsScreen>();
                if (detailsScreen == null)
                {
                    Debug.Log("[AutoRocketFuelPlanner] DetailsScreen not found in parent, searching in scene");
                    // Try to find from scene
                    detailsScreen = UnityEngine.Object.FindFirstObjectByType<DetailsScreen>();
                }

                if (detailsScreen == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find DetailsScreen anywhere");
                    return;
                }

                Debug.Log("[AutoRocketFuelPlanner] Found DetailsScreen: " + detailsScreen.name);

                // Get current active SideScreenContent
                SideScreenContent activeScreen = GetActiveSideScreen(detailsScreen);
                if (activeScreen == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find active SideScreen");
                    return;
                }

                Debug.Log("[AutoRocketFuelPlanner] Found active SideScreen: " + activeScreen.name);

                // ========== 新的布局策略 ==========
                // 找到 DetailsScreen 的根容器，在其底部添加新面板
                // 这样不会破坏原有 UI 的结构

                // 获取 DetailsScreen 的根 RectTransform
                RectTransform detailsScreenRect = detailsScreen.GetComponent<RectTransform>();
                if (detailsScreenRect == null)
                {
                    Debug.LogError("[AutoRocketFuelPlanner] DetailsScreen 没有 RectTransform 组件");
                    return;
                }

                Debug.Log("[AutoRocketFuelPlanner] DetailsScreen 大小: " + detailsScreenRect.sizeDelta);
                Debug.Log("[AutoRocketFuelPlanner] DetailsScreen 位置: " + detailsScreenRect.anchoredPosition);

                // 创建新面板，放在 DetailsScreen 的底部
                // 注意：不设置为 SideScreen 的子对象，而是设置为 DetailsScreen 的子对象
                injectedPanel = new GameObject("AutoFuelControlPanel");
                injectedPanel.transform.SetParent(detailsScreenRect, false); // 设置为 DetailsScreen 的子对象

                Debug.Log("[AutoRocketFuelPlanner] Created panel under DetailsScreen");

                // 设置新面板的位置和大小
                RectTransform rectTransform = injectedPanel.AddComponent<RectTransform>();

                // 锚点设置：固定在底部，宽度填充
                rectTransform.anchorMin = new Vector2(0, 0); // 底部
                rectTransform.anchorMax = new Vector2(1, 0); // 底部
                rectTransform.pivot = new Vector2(0.5f, 0); // 底部中心

                // 位置：在 DetailsScreen 的底部
                rectTransform.anchoredPosition = new Vector2(0, 0);

                // 大小：宽度自适应，高度固定
                float panelHeight = 180;
                rectTransform.sizeDelta = new Vector2(0, panelHeight);

                // 添加深色背景，使其更明显且与原有 UI 区分开
                Image bgImage = injectedPanel.AddComponent<Image>();
                bgImage.color = new Color(0.12f, 0.12f, 0.14f, 1f); // 深色背景

                // 添加顶部边框线
                GameObject borderLine = new GameObject("TopBorder");
                borderLine.transform.SetParent(injectedPanel.transform, false);
                Image borderImage = borderLine.AddComponent<Image>();
                borderImage.color = new Color(0.3f, 0.3f, 0.35f, 1f); // 灰色边框
                RectTransform borderRect = borderLine.GetComponent<RectTransform>();
                borderRect.anchorMin = new Vector2(0, 1); // 顶部
                borderRect.anchorMax = new Vector2(1, 1); // 顶部
                borderRect.pivot = new Vector2(0.5f, 1); // 顶部中心
                borderRect.anchoredPosition = Vector2.zero;
                borderRect.sizeDelta = new Vector2(0, 2); // 2像素高的边框线

                // Create control panel
                controlPanel = injectedPanel.AddComponent<FuelControlPanel>();
                controlPanel.Initialize(craft, target);

                Debug.Log("[AutoRocketFuelPlanner] Fuel control panel injected successfully");
                Debug.Log("[AutoRocketFuelPlanner] Panel active: " + injectedPanel.activeInHierarchy);
                Debug.Log("[AutoRocketFuelPlanner] Panel position: " + rectTransform.anchoredPosition);
                Debug.Log("[AutoRocketFuelPanelris] Panel size: " + rectTransform.sizeDelta);
                Debug.Log("[AutoRocketFuelPlanner] Panel parent: " + detailsScreenRect.name);
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to inject fuel control panel: " + e);
                Cleanup();
            }
        }

        /// <summary>
        /// Clean up injected UI.
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
        /// Check if target is fuel or oxidizer tank.
        /// </summary>
        private static bool IsFuelOrOxidizerTank(GameObject target)
        {
            Debug.Log("[AutoRocketFuelPlanner] Checking if target is fuel/oxidizer tank: " + target.name);

            foreach (Component component in target.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                string typeName = component.GetType().Name.ToLowerInvariant();
                Debug.Log("[AutoRocketFuelPlanner]   Checking component: " + component.GetType().Name + " (lowercase: " + typeName + ")");

                if (typeName.Contains("tank") && (typeName.Contains("fuel") || typeName.Contains("oxidizer")))
                {
                    Debug.Log("[AutoRocketFuelPlanner]   ✓ Found fuel/oxidizer tank component: " + component.GetType().Name);
                    return true;
                }
            }

            Debug.Log("[AutoRocketFuelPlanner]   ✗ No fuel/oxidizer tank component found");
            return false;
        }

        /// <summary>
        /// Get current active SideScreenContent.
        /// </summary>
        private static SideScreenContent GetActiveSideScreen(DetailsScreen detailsScreen)
        {
            Debug.Log("[AutoRocketFuelPlanner] Looking for active SideScreen in DetailsScreen");

            // Try to get activeSideScreen field via reflection
            FieldInfo field = typeof(DetailsScreen).GetField("activeSideScreen",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field != null)
            {
                Debug.Log("[AutoRocketFuelPlanner] Found activeSideScreen field");
                SideScreenContent screen = field.GetValue(detailsScreen) as SideScreenContent;
                if (screen != null)
                {
                    Debug.Log("[AutoRocketFuelPlanner] Active SideScreen: " + screen.name);
                    return screen;
                }
                else
                {
                    Debug.Log("[AutoRocketFuelPlanner] activeSideScreen field is null");
                }
            }
            else
            {
                Debug.Log("[AutoRocketFuelPlanner] activeSideScreen field not found, trying alternative");
            }

            // Alternative: find SideScreenContent in DetailsScreen's children
            SideScreenContent[] screens = detailsScreen.GetComponentsInChildren<SideScreenContent>(true);
            Debug.Log("[AutoRocketFuelPlanner] Found " + screens.Length + " SideScreenContent components in children");

            foreach (SideScreenContent screen in screens)
            {
                Debug.Log("[AutoRocketFuelPlanner]   Checking: " + screen.name + " (active: " + screen.gameObject.activeInHierarchy + ")");
                if (screen.gameObject.activeInHierarchy)
                {
                    Debug.Log("[AutoRocketFuelPlanner] ✓ Found active SideScreen: " + screen.name);
                    return screen;
                }
            }

            Debug.Log("[AutoRocketFuelPlanner] ✗ No active SideScreen found");
            return null;
        }
    }

    /// <summary>
    /// Fuel/Oxidizer control panel component:
    /// - Displays distance, fuel, and oxidizer sliders;
    /// - Implements real-time linkage: adjusts other parameters when one changes;
    /// - Integrates with RocketAutoFuelService.
    /// </summary>
    internal class FuelControlPanel : MonoBehaviour
    {
        private Clustercraft currentCraft;
        private GameObject currentTarget;

        // UI elements
        private Slider distanceSlider;
        private Slider fuelSlider;
        private Slider oxidizerSlider;
        private Text distanceLabel;
        private Text fuelLabel;
        private Text oxidizerLabel;
        private Text statusLabel;

        // Lock to prevent recursive updates
        private bool isUpdatingFromGame;
        private bool isUpdatingFromSlider;

        // Slider debounce
        private float lastSliderCallbackTime;
        private const float SliderDebounceSeconds = 0.1f;

        // Keyword definitions (consistent with RocketAutoFuelService)
        private static readonly string[] FuelKeywords = { "fuel" };
        private static readonly string[] OxidizerKeywords = { "oxidizer", "oxylite", "ox" };

        /// <summary>
        /// <summary>
        /// 初始化面板
        /// </summary>
        /// <param name="craft">火箭对象，可以为 null（基本模式）</param>
        /// <param name="target">选中的燃料舱/氧化剂舱游戏对象</param>
        public void Initialize(Clustercraft craft, GameObject target)
        {
            // 保存引用
            currentCraft = craft;
            currentTarget = target;

            // 记录初始化信息
            Debug.Log("[AutoRocketFuelPlanner] FuelControlPanel 初始化:");
            Debug.Log("[AutoRocketFuelPlanner]   - Craft: " + (craft != null ? craft.name : "null（基本模式）"));
            Debug.Log("[AutoRocketFuelPlanner]   - Target: " + target.name);

            // 如果没有火箭，显示提示
            if (craft == null)
            {
                Debug.Log("[AutoRocketFuelPlanner]   - 模式: 基本模式（只显示滑动条，无自动计算）");
            }
            else
            {
                Debug.Log("[AutoRocketFuelPlanner]   - 模式: 完整模式（自动计算启用）");
            }

            // 构建 UI 元素
            BuildUI();

            // 从游戏读取初始数据并更新 UI
            RefreshFromGame();

            Debug.Log("[AutoRocketFuelPlanner] FuelControlPanel 初始化完成");
        }

        /// <summary>
        /// 构建 UI 元素
        /// 注意：这个方法创建所有的滑动条、标签和布局
        /// </summary>
        private void BuildUI()
        {
            // 创建主面板容器
            // 这个容器将包含所有的 UI 元素
            GameObject panel = new GameObject("AutoFuelMainPanel");
            panel.transform.SetParent(transform, false);

            // 添加垂直布局组件
            // 这会让所有子元素垂直排列，自动处理间距
            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 2f; // 元素之间的间距（紧凑）
            layout.padding = new RectOffset(8, 8, 6, 6); // 内边距
            layout.childForceExpandWidth = true; // 子元素宽度扩展到最大
            layout.childForceExpandHeight = false; // 子元素高度不扩展

            // 添加内容大小适配器
            // 这会让面板根据内容自动调整大小
            ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 设置面板的 RectTransform
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero; // 底部
            panelRect.anchorMax = Vector2.one; // 顶部
            panelRect.sizeDelta = Vector2.zero; // 大小自动

            // ========== 创建 UI 元素 ==========

            // 标题标签
            statusLabel = CreateLabel(panel.transform, "自动加注控制", true);
            statusLabel.color = new Color(0.8f, 0.9f, 1f, 1f); // 浅蓝色标题
            statusLabel.fontSize = 12; // 字体大小

            // 目标距离滑动条
            distanceLabel = CreateLabel(panel.transform, "目标距离: -- 格", false);
            distanceSlider = CreateSlider(panel.transform, 0f, 100000f, OnDistanceSliderChanged);

            // 燃料目标滑动条
            fuelLabel = CreateLabel(panel.transform, "燃料目标: -- kg", false);
            fuelSlider = CreateSlider(panel.transform, 0f, 1000f, OnFuelSliderChanged);

            // 氧化剂目标滑动条
            oxidizerLabel = CreateLabel(panel.transform, "氧化剂目标: -- kg", false);
            oxidizerSlider = CreateSlider(panel.transform, 0f, 1000f, OnOxidizerSliderChanged);

            Debug.Log("[AutoRocketFuelPlanner] BuildUI 完成，创建了所有 UI 元素");
        }

        /// <summary>
        /// 创建标签
        /// </summary>
        /// <param name="parent">父对象</param>
        /// <param name="text">标签文本</param>
        /// <param name="isTitle">是否是标题（影响样式）</param>
        /// <returns>创建的 Text 组件</returns>
        private Text CreateLabel(Transform parent, string text, bool isTitle = false)
        {
            // 创建新的 GameObject 作为标签
            GameObject go = new GameObject("Label");
            go.transform.SetParent(parent, false);

            // 添加 Text 组件
            Text textComponent = go.AddComponent<Text>();
            textComponent.text = text;

            // 根据是否是标题设置不同的对齐方式
            textComponent.alignment = isTitle ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;

            // 设置字体大小
            textComponent.fontSize = isTitle ? 14 : 11;

            // 尝试设置字体
            // ONI 可能有自己的字体，我们先尝试 Unity 默认字体
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null)
            {
                textComponent.font = font;
            }

            // 设置文本颜色（白色，因为背景是深色）
            if (isTitle)
            {
                textComponent.color = Color.white; // 标题白色
            }
            else
            {
                textComponent.color = new Color(0.8f, 0.8f, 0.8f, 1f); // 普通文本浅灰色
            }

            // 设置标签的高度
            RectTransform rectTransform = go.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(0, isTitle ? 24 : 18);

            return textComponent;
        }

        /// <summary>
        /// 创建分隔线
        /// </summary>
        private void CreateSeparator(Transform parent)
        {
            GameObject go = new GameObject("Separator");
            go.transform.SetParent(parent, false);

            Image image = go.AddComponent<Image>();
            image.color = new Color(0.3f, 0.3f, 0.3f, 1f);

            RectTransform rectTransform = go.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(0, 2);

            // Add horizontal layout element
            LayoutElement layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 2;
        }

        /// <summary>
        /// 创建滑动条
        /// </summary>
        /// <param name="parent">父对象</param>
        /// <param name="min">最小值</param>
        /// <param name="max">最大值</param>
        /// <param name="onChanged">值变化时的回调函数</param>
        /// <returns>创建的 Slider 组件</returns>
        private Slider CreateSlider(Transform parent, float min, float max,
            UnityEngine.Events.UnityAction<float> onChanged)
        {
            // 创建滑动条的主 GameObject
            GameObject go = new GameObject("Slider");
            go.transform.SetParent(parent, false);

            // 添加布局元素，设置滑动条的高度
            LayoutElement layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 20; // 滑动条高度（更紧凑）

            // ========== 创建滑动条的背景 ==========
            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(go.transform, false);
            Image bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f, 1f); // 深灰色背景
            RectTransform bgRect = bgGo.GetComponent<RectTransform>();
            // 设置背景的位置和大小（垂直居中，左右填充）
            bgRect.anchorMin = new Vector2(0, 0.3f);
            bgRect.anchorMax = new Vector2(1, 0.7f);
            bgRect.sizeDelta = Vector2.zero;

            // ========== 创建填充区域（显示当前值） ==========
            GameObject fillAreaGo = new GameObject("Fill Area");
            fillAreaGo.transform.SetParent(go.transform, false);
            RectTransform fillAreaRect = fillAreaGo.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.3f);
            fillAreaRect.anchorMax = new Vector2(1, 0.7f);
            fillAreaRect.sizeDelta = Vector2.zero;

            // 创建填充条（显示选择的值）
            GameObject fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(fillAreaGo.transform, false);
            Image fillImage = fillGo.AddComponent<Image>();
            fillImage.color = new Color(0.2f, 0.6f, 1f, 1f); // 蓝色填充
            RectTransform fillRect = fillGo.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.sizeDelta = Vector2.zero;

            // ========== 创建滑块手柄 ==========
            GameObject handleAreaGo = new GameObject("Handle Slide Area");
            handleAreaGo.transform.SetParent(go.transform, false);
            RectTransform handleAreaRect = handleAreaGo.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.sizeDelta = Vector2.zero;

            // 创建手柄
            GameObject handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(handleAreaGo.transform, false);
            Image handleImage = handleGo.AddComponent<Image>();
            handleImage.color = new Color(0.8f, 0.8f, 0.8f, 1f); // 浅灰色手柄
            RectTransform handleRect = handleGo.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(15, 0); // 手柄宽度

            // ========== 添加 Slider 组件并配置 ==========
            Slider slider = go.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImage;
            slider.minValue = min;
            slider.maxValue = max;
            slider.onValueChanged.AddListener(onChanged);

            // 设置滑动条的整体大小
            RectTransform goRect = go.GetComponent<RectTransform>();
            goRect.sizeDelta = new Vector2(0, 20); // 滑动条高度

            return slider;
        }

        /// <summary>
        /// <summary>
        /// 每帧更新：从游戏读取最新数据并刷新 UI
        /// 注意：只有在有火箭对象时才会自动刷新
        /// </summary>
        private void Update()
        {
            // 如果没有火箭对象或者是从滑动条触发的更新，跳过
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
        /// 从游戏对象读取数据并更新 UI 显示
        /// </summary>
        public void RefreshFromGame()
        {
            // 如果没有火箭对象，显示基本状态并返回
            if (currentCraft == null)
            {
                Debug.Log("[AutoRocketFuelPlanner] RefreshFromGame: 没有火箭对象，跳过数据读取");
                // 仍然更新状态标签，提示用户
                if (statusLabel != null)
                {
                    statusLabel.text = "自动加注控制 (基本模式 - 无火箭)";
                    statusLabel.color = new Color(0.7f, 0.7f, 0.7f, 1f); // 灰色
                }
                return;
            }

            isUpdatingFromGame = true;

            try
            {
                // Read current values from game
                bool hasDistance = TryResolveTargetDistance(currentCraft, out float currentDistance);
                bool hasFuel = TryReadMassFromTank(currentCraft, FuelKeywords, out float currentFuel);
                bool hasOxidizer = TryReadMassFromTank(currentCraft, OxidizerKeywords, out float currentOxidizer);

                // Update slider ranges
                float maxFuel = GetTankCapacity(currentCraft, FuelKeywords);
                float maxOxidizer = GetTankCapacity(currentCraft, OxidizerKeywords);

                distanceSlider.minValue = 0f;
                distanceSlider.maxValue = 100000f;

                fuelSlider.minValue = 0f;
                fuelSlider.maxValue = maxFuel;

                oxidizerSlider.minValue = 0f;
                oxidizerSlider.maxValue = maxOxidizer;

                // Update slider values (without triggering callback)
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

                // Update label display
                distanceLabel.text = $"目标距离: {(hasDistance ? currentDistance.ToString("F0") : "--")} 格";
                fuelLabel.text = $"燃料目标: {(hasFuel ? currentFuel.ToString("F1") : "--")} / {maxFuel:F0} kg";
                oxidizerLabel.text = $"氧化剂目标: {(hasOxidizer ? currentOxidizer.ToString("F1") : "--")} / {maxOxidizer:F0} kg";

                // Update status
                if (hasFuel || hasOxidizer)
                {
                    statusLabel.text = "自动加注控制 (活跃)";
                    statusLabel.color = new Color(0.3f, 0.9f, 0.3f, 1f); // Green
                }
                else
                {
                    statusLabel.text = "自动加注控制 (等待数据)";
                    statusLabel.color = new Color(0.8f, 0.8f, 0.3f, 1f); // Yellow
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to refresh UI from game: " + e);
                statusLabel.text = "自动加注控制 (错误)";
                statusLabel.color = new Color(0.9f, 0.3f, 0.3f, 1f); // Red
            }

            isUpdatingFromGame = false;
        }

        /// <summary>
        /// Target distance slider value changed callback.
        /// </summary>
        private void OnDistanceSliderChanged(float value)
        {
            if (isUpdatingFromGame || currentCraft == null)
            {
                return;
            }

            // Simple debounce
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

                // Detect engine type
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // Use distance to calculate fuel and oxidizer
                FuelPlan plan = FuelCalculator.CreatePlanFromDistance(value, engineKind, profile);

                // Write fuel and oxidizer
                bool fuelApplied = TryApplyMassToTank(currentCraft, plan.FuelKg, FuelKeywords, out float appliedFuel);
                bool oxidizerApplied = TryApplyMassToTank(currentCraft, plan.OxidizerKg, OxidizerKeywords, out float appliedOxidizer);

                // Update sliders and labels
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

                // Update status
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
        /// Fuel slider value changed callback.
        /// </summary>
        private void OnFuelSliderChanged(float value)
        {
            if (isUpdatingFromGame || currentCraft == null)
            {
                return;
            }

            // Simple debounce
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

                // Detect engine type
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // Use fuel to calculate distance and oxidizer
                FuelPlan plan = FuelCalculator.CreatePlanFromFuel(value, engineKind, profile);

                // Update distance display
                distanceSlider.SetValueWithoutNotify(plan.TargetDistance);
                distanceLabel.text = $"目标距离: {plan.TargetDistance:F0} 格";

                // Write oxidizer
                if (profile.RequiresOxidizer)
                {
                    bool oxidizerApplied = TryApplyMassToTank(currentCraft, plan.OxidizerKg, OxidizerKeywords, out float appliedOxidizer);
                    if (oxidizerApplied)
                    {
                        oxidizerSlider.SetValueWithoutNotify(appliedOxidizer);
                        oxidizerLabel.text = $"氧化剂目标: {appliedOxidizer:F1} kg";
                    }
                }

                // Update status
                statusLabel.text = "自动加注控制 (燃料模式)";
                statusLabel.color = new Color(0.3f, 0.6f, 1f, 1f); // Blue

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
        /// Oxidizer slider value changed callback.
        /// </summary>
        private void OnOxidizerSliderChanged(float value)
        {
            if (isUpdatingFromGame || currentCraft == null)
            {
                return;
            }

            // Simple debounce
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

                // Detect engine type
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // Use oxidizer to calculate fuel
                FuelPlan plan = FuelCalculator.CreatePlanFromOxidizer(value, engineKind, profile);

                // Update distance display
                distanceSlider.SetValueWithoutNotify(plan.TargetDistance);
                distanceLabel.text = $"目标距离: {plan.TargetDistance:F0} 格";

                // Write fuel
                bool fuelApplied = TryApplyMassToTank(currentCraft, plan.FuelKg, FuelKeywords, out float appliedFuel);
                if (fuelApplied)
                {
                    fuelSlider.SetValueWithoutNotify(appliedFuel);
                    fuelLabel.text = $"燃料目标: {appliedFuel:F1} kg";
                }

                // Update status
                statusLabel.text = "自动加注控制 (氧化剂模式)";
                statusLabel.color = new Color(0.9f, 0.6f, 0.3f, 1f); // Orange

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
        /// Read target distance via reflection (consistent with RocketAutoFuelService).
        /// </summary>
        private static bool TryResolveTargetDistance(Clustercraft craft, out float distance)
        {
            distance = 0f;
            Type type = craft.GetType();

            string[] distanceKeywords = { "distance", "travel", "range", "path", "target" };

            // Try to read fields/properties
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

            // Try to call no-parameter methods
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
        /// Read mass from Tank (consistent with RocketAutoFuelService).
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
        /// Get Tank candidate set (consistent with RocketAutoFuelService).
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
        /// Write mass to Tank (consistent with RocketAutoFuelService).
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
        /// Write mass (consistent with RocketAutoFuelService).
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
        /// Read mass (consistent with RocketAutoFuelService).
        /// </summary>
        private static bool TryReadMass(Component tankComponent, out float value)
        {
            value = 0f;
            Type type = tankComponent.GetType();
            string[] readableMassPriorityKeywords = { "target", "desired", "requested", "user", "fill" };
            string[] readableMassFallbackKeywords = { "amount", "mass" };

            // Priority: read target value
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

            // Fallback: read generic mass value
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
        /// Get Tank capacity.
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

            return 1000f; // Fallback value
        }

        /// <summary>
        /// Detect engine type (consistent with RocketAutoFuelService).
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
        /// Clean up when destroyed.
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
    /// Panel manager:
    /// - Injects UI when DetailsScreen selects target;
    /// - Cleans up UI when deselecting;
    /// - Provides global refresh interface.
    /// </summary>
    internal static class FuelControlPanelManager
    {
        /// <summary>
        /// Called when DetailsScreen selects target.
        /// </summary>
        public static void OnTargetSelected(GameObject target)
        {
            FuelControlInjector.TryInject(target);
        }

        /// <summary>
        /// Called when DetailsScreen deselects target.
        /// </summary>
        public static void OnTargetDeselected()
        {
            FuelControlInjector.Cleanup();
        }

        /// <summary>
        /// Force clean up all panels (called when game exits).
        /// </summary>
        public static void CleanupAll()
        {
            FuelControlInjector.Cleanup();
        }
    }
}
