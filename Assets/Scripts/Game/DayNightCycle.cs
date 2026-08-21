using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace BenScr.MinecraftClone
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public sealed class DayNightCycle : MonoBehaviour
    {
        private const float LightingRefreshInterval = 0.1f;
        private const float EnvironmentRefreshThreshold = 0.01f;
        private const float EnvironmentRotationRefreshDegrees = 1f;
        private static readonly int WorldNightFactorProperty = Shader.PropertyToID("_WorldNightFactor");

        [Header("Clock")]
        [SerializeField, Min(0.1f)] private float fullDayLengthMinutes = 40f;
        [SerializeField, Range(0f, 24f)] private float startTimeOfDayHours = 10.5f;
        [SerializeField] private bool advanceTime = true;
        [SerializeField] private bool pauseWhenGameFrozen = true;

        [Header("Directional Sun / Moon")]
        [SerializeField] private Light sun;
        [SerializeField] private float sunAzimuth = -30f;
        [SerializeField, Min(0f)] private float maximumSunIntensity = 2.2f;
        [SerializeField, Min(0f)] private float moonIntensity = 0.08f;
        [SerializeField] private Color daylightSunColor = new Color(1f, 0.96f, 0.88f, 1f);
        [SerializeField] private Color sunriseSunColor = new Color(1f, 0.42f, 0.2f, 1f);
        [SerializeField] private Color moonColor = new Color(0.42f, 0.52f, 0.75f, 1f);

        [Header("Environment")]
        [SerializeField, Range(0f, 1f)] private float nightEnvironmentBrightness = 0.05f;
        [SerializeField, Min(0f)] private float dayAmbientIntensity = 1f;
        [SerializeField, Min(0f)] private float nightAmbientIntensity = 0.08f;
        [SerializeField] private Color dayAmbientSky = new Color(0.42f, 0.47f, 0.58f, 1f);
        [SerializeField] private Color dayAmbientEquator = new Color(0.22f, 0.25f, 0.32f, 1f);
        [SerializeField] private Color dayAmbientGround = new Color(0.11f, 0.13f, 0.17f, 1f);
        [SerializeField] private Color nightAmbientSky = new Color(0.012f, 0.02f, 0.045f, 1f);
        [SerializeField] private Color nightAmbientEquator = new Color(0.008f, 0.012f, 0.028f, 1f);
        [SerializeField] private Color nightAmbientGround = new Color(0.004f, 0.006f, 0.014f, 1f);

        [Header("Atmosphere")]
        [SerializeField] private Color dayFogColor = new Color(0.52f, 0.6f, 0.72f, 1f);
        [SerializeField] private Color twilightFogColor = new Color(0.58f, 0.3f, 0.25f, 1f);
        [SerializeField] private Color nightFogColor = new Color(0.012f, 0.022f, 0.05f, 1f);
        [SerializeField, Min(0f)] private float dayFogDensity = 0.0017f;
        [SerializeField, Min(0f)] private float nightFogDensity = 0.0045f;

        [Header("Skybox")]
        [SerializeField, Min(0f)] private float daySkyExposure = 1.5f;
        [SerializeField, Min(0f)] private float nightSkyExposure = 0.08f;
        [SerializeField] private Color daySkyTint = new Color(0.42f, 0.58f, 0.82f, 1f);
        [SerializeField] private Color twilightSkyTint = new Color(0.68f, 0.32f, 0.24f, 1f);
        [SerializeField] private Color nightSkyTint = new Color(0.035f, 0.06f, 0.14f, 1f);
        [SerializeField] private Color dayGroundColor = new Color(0.28f, 0.3f, 0.34f, 1f);
        [SerializeField] private Color nightGroundColor = new Color(0.012f, 0.018f, 0.035f, 1f);
        [SerializeField, Min(0.1f)] private float environmentRefreshInterval = 1f;

        private float timeOfDayHours;
        private float lastAppliedTimeOfDayHours = float.NaN;
        private float nextLightingRefreshTime;
        private float nextEnvironmentRefreshTime;
        private float lastEnvironmentDaylight;
        private float lastEnvironmentTwilight;
        private float lastEnvironmentTimeOfDayHours;
        private bool hasEnvironmentSample;
        private bool ownsGameLighting;
        private Material skyboxTemplate;
        private Material runtimeSkybox;
        private Scene ownerScene;

        public float TimeOfDayHours => timeOfDayHours;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetShaderState()
        {
            Shader.SetGlobalFloat(WorldNightFactorProperty, 0f);
        }

        private void OnEnable()
        {
            Shader.SetGlobalFloat(WorldNightFactorProperty, 0f);
            timeOfDayHours = Mathf.Repeat(startTimeOfDayHours, 24f);
            lastAppliedTimeOfDayHours = float.NaN;
            nextLightingRefreshTime = 0f;
            nextEnvironmentRefreshTime = 0f;
            hasEnvironmentSample = false;
            ownerScene = gameObject.scene;
            skyboxTemplate = null;

            if (sun == null)
                sun = GetComponent<Light>();

            if (!Application.isPlaying)
                return;

            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            RefreshOwnership();
        }

        private void OnDisable()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            RelinquishLighting();
            Shader.SetGlobalFloat(WorldNightFactorProperty, 0f);
        }

        private void Update()
        {
            if (!Application.isPlaying || !ownsGameLighting)
                return;

            if (advanceTime && (!pauseWhenGameFrozen || !GameController.IsFrozen))
            {
                float secondsPerDay = Mathf.Max(0.1f, fullDayLengthMinutes) * 60f;
                timeOfDayHours = Mathf.Repeat(
                    timeOfDayHours + Time.deltaTime * (24f / secondsPerDay),
                    24f);
            }

            if (Mathf.Approximately(timeOfDayHours, lastAppliedTimeOfDayHours) ||
                Time.unscaledTime < nextLightingRefreshTime)
            {
                return;
            }

            ApplyLighting(forceEnvironmentRefresh: false);
        }

        public void SetTimeOfDay(float hours)
        {
            timeOfDayHours = Mathf.Repeat(hours, 24f);
            if (ownsGameLighting)
                ApplyLighting(forceEnvironmentRefresh: true);
        }

        private void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            RefreshOwnership();
        }

        private void RefreshOwnership()
        {
            bool shouldOwnLighting = isActiveAndEnabled &&
                                     ownerScene.IsValid() &&
                                     ownerScene == SceneManager.GetActiveScene();
            if (!shouldOwnLighting)
            {
                RelinquishLighting();
                return;
            }

            if (!ownsGameLighting)
            {
                ownsGameLighting = true;
                RenderSettings.sun = sun;
                RenderSettings.ambientMode = AmbientMode.Trilight;
                CreateRuntimeSkybox();
            }

            ApplyLighting(forceEnvironmentRefresh: true);
        }

        private void RelinquishLighting()
        {
            if (ownsGameLighting)
            {
                ownsGameLighting = false;
                Shader.SetGlobalFloat(WorldNightFactorProperty, 0f);
            }

            DestroyRuntimeSkybox();
        }

        private void CreateRuntimeSkybox()
        {
            if (runtimeSkybox != null)
                return;

            if (skyboxTemplate == null)
                skyboxTemplate = RenderSettings.skybox;

            if (skyboxTemplate == null)
                return;

            runtimeSkybox = new Material(skyboxTemplate)
            {
                name = skyboxTemplate.name + " (Day Night Runtime)",
                hideFlags = HideFlags.DontSave
            };
            RenderSettings.skybox = runtimeSkybox;
        }

        private void DestroyRuntimeSkybox()
        {
            if (runtimeSkybox == null)
                return;

            if (Application.isPlaying)
                Destroy(runtimeSkybox);
            else
                DestroyImmediate(runtimeSkybox);
            runtimeSkybox = null;
        }

        private void ApplyLighting(bool forceEnvironmentRefresh)
        {
            if (sun == null)
                return;

            float orbitRadians = (timeOfDayHours - 6f) / 24f * Mathf.PI * 2f;
            float sunHeight = Mathf.Sin(orbitRadians);
            bool isSunAboveHorizon = sunHeight >= 0f;
            float lightOrbitAngle = timeOfDayHours / 24f * 360f - 90f;

            // At night the same directional light is flipped to the opposite side of the
            // sky and becomes a subtle cool moon, avoiding illumination from below.
            if (!isSunAboveHorizon)
                lightOrbitAngle += 180f;

            sun.transform.rotation = Quaternion.Euler(lightOrbitAngle, sunAzimuth, 0f);
            sun.enabled = true;

            float daylight = Smooth01(Mathf.InverseLerp(-0.12f, 0.18f, sunHeight));
            float solarStrength = Mathf.Pow(Mathf.Clamp01(sunHeight), 0.35f);
            float lunarStrength = Mathf.Pow(Mathf.Clamp01(-sunHeight), 0.6f);
            if (isSunAboveHorizon)
            {
                float daylightColorBlend = Smooth01(Mathf.InverseLerp(0f, 0.55f, sunHeight));
                sun.color = Color.Lerp(sunriseSunColor, daylightSunColor, daylightColorBlend);
                sun.intensity = maximumSunIntensity * solarStrength;
            }
            else
            {
                sun.color = moonColor;
                sun.intensity = moonIntensity * lunarStrength;
            }

            float environmentBrightness = Mathf.Lerp(nightEnvironmentBrightness, 1f, daylight);
            Shader.SetGlobalFloat(WorldNightFactorProperty, 1f - environmentBrightness);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientIntensity = Mathf.Lerp(nightAmbientIntensity, dayAmbientIntensity, daylight);
            RenderSettings.ambientSkyColor = Color.Lerp(nightAmbientSky, dayAmbientSky, daylight);
            RenderSettings.ambientEquatorColor = Color.Lerp(nightAmbientEquator, dayAmbientEquator, daylight);
            RenderSettings.ambientGroundColor = Color.Lerp(nightAmbientGround, dayAmbientGround, daylight);

            float twilight = 1f - Smooth01(Mathf.InverseLerp(0f, 0.35f, Mathf.Abs(sunHeight)));
            Color fogColor = Color.Lerp(nightFogColor, dayFogColor, daylight);
            RenderSettings.fogColor = Color.Lerp(fogColor, twilightFogColor, twilight * 0.55f);
            RenderSettings.fogDensity = Mathf.Lerp(nightFogDensity, dayFogDensity, daylight);

            UpdateSkybox(daylight, twilight);

            bool environmentChanged =
                !hasEnvironmentSample ||
                Mathf.Abs(daylight - lastEnvironmentDaylight) >= EnvironmentRefreshThreshold ||
                Mathf.Abs(twilight - lastEnvironmentTwilight) >= EnvironmentRefreshThreshold ||
                Mathf.Abs(Mathf.DeltaAngle(
                    lastEnvironmentTimeOfDayHours * 15f,
                    timeOfDayHours * 15f)) >= EnvironmentRotationRefreshDegrees;

            if (forceEnvironmentRefresh ||
                (environmentChanged && Time.unscaledTime >= nextEnvironmentRefreshTime))
            {
                DynamicGI.UpdateEnvironment();
                nextEnvironmentRefreshTime = Time.unscaledTime + environmentRefreshInterval;
                lastEnvironmentDaylight = daylight;
                lastEnvironmentTwilight = twilight;
                lastEnvironmentTimeOfDayHours = timeOfDayHours;
                hasEnvironmentSample = true;
            }

            lastAppliedTimeOfDayHours = timeOfDayHours;
            nextLightingRefreshTime = Time.unscaledTime + LightingRefreshInterval;
        }

        private void UpdateSkybox(float daylight, float twilight)
        {
            if (runtimeSkybox == null)
                return;

            Color tint = Color.Lerp(nightSkyTint, daySkyTint, daylight);
            tint = Color.Lerp(tint, twilightSkyTint, twilight * 0.65f);

            if (runtimeSkybox.HasProperty("_Exposure"))
                runtimeSkybox.SetFloat("_Exposure", Mathf.Lerp(nightSkyExposure, daySkyExposure, daylight));
            if (runtimeSkybox.HasProperty("_SkyTint"))
                runtimeSkybox.SetColor("_SkyTint", tint);
            if (runtimeSkybox.HasProperty("_Tint"))
                runtimeSkybox.SetColor("_Tint", tint);
            if (runtimeSkybox.HasProperty("_GroundColor"))
                runtimeSkybox.SetColor("_GroundColor", Color.Lerp(nightGroundColor, dayGroundColor, daylight));
            if (runtimeSkybox.HasProperty("_Rotation"))
                runtimeSkybox.SetFloat("_Rotation", timeOfDayHours / 24f * 360f);
        }

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private void OnValidate()
        {
            fullDayLengthMinutes = Mathf.Max(0.1f, fullDayLengthMinutes);
            startTimeOfDayHours = Mathf.Repeat(startTimeOfDayHours, 24f);
            maximumSunIntensity = Mathf.Max(0f, maximumSunIntensity);
            moonIntensity = Mathf.Max(0f, moonIntensity);
            nightEnvironmentBrightness = Mathf.Clamp01(nightEnvironmentBrightness);
            environmentRefreshInterval = Mathf.Max(0.1f, environmentRefreshInterval);

            if (sun == null)
                sun = GetComponent<Light>();
        }
    }
}
