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
                // Clean up previous injection
                Cleanup();

                if (target == null)
                {
                    return;
                }

                // Check if selected object is fuel/oxidizer tank
                if (!IsFuelOrOxidizerTank(target))
                {
                    return;
                }

                // Get rocket object
                Clustercraft craft = target.GetComponentInParent<Clustercraft>();
                if (craft == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find Clustercraft for target: " + target.name);
                    return;
                }

                // Get current active SideScreen
                DetailsScreen detailsScreen = target.GetComponentInParent<DetailsScreen>();
                if (detailsScreen == null)
                {
                    // Try to find from scene
                    detailsScreen = UnityEngine.Object.FindFirstObjectByType<DetailsScreen>();
                }

                if (detailsScreen == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find DetailsScreen");
                    return;
                }

                // Get current active SideScreenContent
                SideScreenContent activeScreen = GetActiveSideScreen(detailsScreen);
                if (activeScreen == null)
                {
                    Debug.LogWarning("[AutoRocketFuelPlanner] Could not find active SideScreen");
                    return;
                }

                // Create custom panel at the bottom of current SideScreen content
                injectedPanel = new GameObject("AutoFuelControlPanel");
                injectedPanel.transform.SetParent(activeScreen.transform, false);

                // Set position at the bottom
                RectTransform rectTransform = injectedPanel.AddComponent<RectTransform>();
                rectTransform.anchorMin = new Vector2(0, 0);
                rectTransform.anchorMax = new Vector2(1, 0);
                rectTransform.pivot = new Vector2(0.5f, 0);
                rectTransform.sizeDelta = new Vector2(0, 180); // Height

                // Create control panel
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
        /// Get current active SideScreenContent.
        /// </summary>
        private static SideScreenContent GetActiveSideScreen(DetailsScreen detailsScreen)
        {
            // Try to get activeSideScreen field via reflection
            FieldInfo field = typeof(DetailsScreen).GetField("activeSideScreen",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (field != null)
            {
                return field.GetValue(detailsScreen) as SideScreenContent;
            }

            // Alternative: find SideScreenContent in DetailsScreen's children
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
        /// Initialize panel.
        /// </summary>
        public void Initialize(Clustercraft craft, GameObject target)
        {
            currentCraft = craft;
            currentTarget = target;
            BuildUI();
            RefreshFromGame();
        }

        /// <summary>
        /// Build UI elements.
        /// </summary>
        private void BuildUI()
        {
            // Create main panel container
            GameObject panel = new GameObject("AutoFuelMainPanel");
            panel.transform.SetParent(transform, false);

            // Add vertical layout
            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // Add content size fitter
            ContentSizeFitter fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.sizeDelta = Vector2.zero;

            // Status label
            statusLabel = CreateLabel(panel.transform, "Auto-Fuel Control", true);
            statusLabel.color = new Color(0.8f, 0.9f, 1f, 1f); // Light blue title

            // Add separator
            CreateSeparator(panel.transform);

            // Target distance slider
            distanceLabel = CreateLabel(panel.transform, "Target Distance: -- units");
            distanceSlider = CreateSlider(panel.transform, 0f, 100000f, OnDistanceSliderChanged);

            // Fuel target slider
            fuelLabel = CreateLabel(panel.transform, "Fuel Target: -- kg");
            fuelSlider = CreateSlider(panel.transform, 0f, 1000f, OnFuelSliderChanged);

            // Oxidizer target slider
            oxidizerLabel = CreateLabel(panel.transform, "Oxidizer Target: -- kg");
            oxidizerSlider = CreateSlider(panel.transform, 0f, 1000f, OnOxidizerSliderChanged);

            // Add description label
            CreateLabel(panel.transform, "Drag sliders to adjust, other values auto-calculate", false)
                .color = new Color(0.6f, 0.6f, 0.6f, 1f); // Gray description text
        }

        /// <summary>
        /// Create label.
        /// </summary>
        private Text CreateLabel(Transform parent, string text, bool isTitle = false)
        {
            GameObject go = new GameObject("Label");
            go.transform.SetParent(parent, false);

            Text textComponent = go.AddComponent<Text>();
            textComponent.text = text;
            textComponent.alignment = isTitle ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;

            // Set font size
            textComponent.fontSize = isTitle ? 14 : 11;

            // Try to set font (Unity default or ONI font)
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null)
            {
                textComponent.font = font;
            }

            RectTransform rectTransform = go.GetComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(0, isTitle ? 28 : 20);

            return textComponent;
        }

        /// <summary>
        /// Create separator.
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
        /// Create slider.
        /// </summary>
        private Slider CreateSlider(Transform parent, float min, float max,
            UnityEngine.Events.UnityAction<float> onChanged)
        {
            GameObject go = new GameObject("Slider");
            go.transform.SetParent(parent, false);

            // Add layout element
            LayoutElement layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 30;

            // Create background
            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(go.transform, false);
            Image bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            RectTransform bgRect = bgGo.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.25f);
            bgRect.anchorMax = new Vector2(1, 0.75f);
            bgRect.sizeDelta = Vector2.zero;

            // Create fill area
            GameObject fillAreaGo = new GameObject("Fill Area");
            fillAreaGo.transform.SetParent(go.transform, false);
            RectTransform fillAreaRect = fillAreaGo.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.sizeDelta = Vector2.zero;

            GameObject fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(fillAreaGo.transform, false);
            Image fillImage = fillGo.AddComponent<Image>();
            fillImage.color = new Color(0.3f, 0.6f, 1f, 1f); // Blue fill
            RectTransform fillRect = fillGo.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.sizeDelta = Vector2.zero;

            // Create slider handle
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

            // Add slider component
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
        /// Per-frame update: read latest data from game and refresh UI.
        /// </summary>
        private void Update()
        {
            if (currentCraft == null || isUpdatingFromSlider)
            {
                return;
            }

            // Refresh every 0.5 seconds (avoid performance issues from per-frame refresh)
            if (Time.frameCount % 30 == 0)
            {
                RefreshFromGame();
            }
        }

        /// <summary>
        /// Read data from game objects and update UI display.
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
                distanceLabel.text = $"Target Distance: {(hasDistance ? currentDistance.ToString("F0") : "--")} units";
                fuelLabel.text = $"Fuel Target: {(hasFuel ? currentFuel.ToString("F1") : "--")} / {maxFuel:F0} kg";
                oxidizerLabel.text = $"Oxidizer Target: {(hasOxidizer ? currentOxidizer.ToString("F1") : "--")} / {maxOxidizer:F0} kg";

                // Update status
                if (hasFuel || hasOxidizer)
                {
                    statusLabel.text = "Auto-Fuel Control (Active)";
                    statusLabel.color = new Color(0.3f, 0.9f, 0.3f, 1f); // Green
                }
                else
                {
                    statusLabel.text = "Auto-Fuel Control (Waiting for data)";
                    statusLabel.color = new Color(0.8f, 0.8f, 0.3f, 1f); // Yellow
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to refresh UI from game: " + e);
                statusLabel.text = "Auto-Fuel Control (Error)";
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
                distanceLabel.text = $"Target Distance: {value:F0} units";

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
                    fuelLabel.text = $"Fuel Target: {appliedFuel:F1} kg";
                }

                if (oxidizerApplied)
                {
                    oxidizerSlider.SetValueWithoutNotify(appliedOxidizer);
                    oxidizerLabel.text = $"Oxidizer Target: {appliedOxidizer:F1} kg";
                }

                // Update status
                statusLabel.text = "Auto-Fuel Control (Distance Mode)";
                statusLabel.color = new Color(0.3f, 0.9f, 0.3f, 1f);

                Debug.Log($"[AutoRocketFuelPlanner] Distance changed to {value:F0}, applied fuel: {appliedFuel:F1}, oxidizer: {appliedOxidizer:F1}");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to process distance change: " + e);
                statusLabel.text = "Auto-Fuel Control (Calculation Error)";
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
                fuelLabel.text = $"Fuel Target: {value:F1} kg";

                // Detect engine type
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // Use fuel to calculate distance and oxidizer
                FuelPlan plan = FuelCalculator.CreatePlanFromFuel(value, engineKind, profile);

                // Update distance display
                distanceSlider.SetValueWithoutNotify(plan.TargetDistance);
                distanceLabel.text = $"Target Distance: {plan.TargetDistance:F0} units";

                // Write oxidizer
                if (profile.RequiresOxidizer)
                {
                    bool oxidizerApplied = TryApplyMassToTank(currentCraft, plan.OxidizerKg, OxidizerKeywords, out float appliedOxidizer);
                    if (oxidizerApplied)
                    {
                        oxidizerSlider.SetValueWithoutNotify(appliedOxidizer);
                        oxidizerLabel.text = $"Oxidizer Target: {appliedOxidizer:F1} kg";
                    }
                }

                // Update status
                statusLabel.text = "Auto-Fuel Control (Fuel Mode)";
                statusLabel.color = new Color(0.3f, 0.6f, 1f, 1f); // Blue

                Debug.Log($"[AutoRocketFuelPlanner] Fuel changed to {value:F1}, distance: {plan.TargetDistance:F0}, oxidizer: {plan.OxidizerKg:F1}");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to process fuel change: " + e);
                statusLabel.text = "Auto-Fuel Control (Calculation Error)";
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
                oxidizerLabel.text = $"Oxidizer Target: {value:F1} kg";

                // Detect engine type
                RocketEngineKind engineKind = DetectEngineKind(currentCraft);
                FuelCalculator.EngineProfile profile = FuelCalculator.ResolveProfile(engineKind);

                // Use oxidizer to calculate fuel
                FuelPlan plan = FuelCalculator.CreatePlanFromOxidizer(value, engineKind, profile);

                // Update distance display
                distanceSlider.SetValueWithoutNotify(plan.TargetDistance);
                distanceLabel.text = $"Target Distance: {plan.TargetDistance:F0} units";

                // Write fuel
                bool fuelApplied = TryApplyMassToTank(currentCraft, plan.FuelKg, FuelKeywords, out float appliedFuel);
                if (fuelApplied)
                {
                    fuelSlider.SetValueWithoutNotify(appliedFuel);
                    fuelLabel.text = $"Fuel Target: {appliedFuel:F1} kg";
                }

                // Update status
                statusLabel.text = "Auto-Fuel Control (Oxidizer Mode)";
                statusLabel.color = new Color(0.9f, 0.6f, 0.3f, 1f); // Orange

                Debug.Log($"[AutoRocketFuelPlanner] Oxidizer changed to {value:F1}, distance: {plan.TargetDistance:F0}, fuel: {plan.FuelKg:F1}");
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoRocketFuelPlanner] Failed to process oxidizer change: " + e);
                statusLabel.text = "Auto-Fuel Control (Calculation Error)";
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
