using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace BenScr.MinecraftClone
{
    [RequireComponent(typeof(Camera))]
    public class UnderwaterPostEffect : MonoBehaviour
    {
        [SerializeField] private Shader underwaterShader;
        [SerializeField] private Color tintColor = new Color(0.1f, 0.4f, 0.8f, 1f);
        [SerializeField, Range(0f, 1f)] private float maxStrength = 0.6f;
        [SerializeField, Range(0.25f, 10f)] private float transitionSpeed = 3f;
        [SerializeField] private bool syncWithFluidTint = true;

        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");

        private Material underwaterMaterial;
        private Canvas overlayCanvas;
        private Image overlayImage;
        private PlayerController player;
        private Camera targetCamera;
        private float currentStrength;
        private bool tintInitializedFromFluid;
        private Color cachedFluidTint;

        public void Initialize(PlayerController controller)
        {
            player = controller;
            TryInitializeTintFromFluid();
        }

        private void Awake()
        {
            targetCamera = GetComponent<Camera>();
            if (targetCamera != null)
            {
                targetCamera.depthTextureMode |= DepthTextureMode.Depth;
            }

            if (underwaterShader == null)
            {
                underwaterShader = Shader.Find("Hidden/UnderwaterTint");
            }

            if (underwaterShader != null)
            {
                underwaterMaterial = new Material(underwaterShader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            InitializeOverlayFallback();
        }

        private void Start()
        {
            if (player == null)
            {
                player = GetComponentInParent<PlayerController>();
            }

            TryInitializeTintFromFluid();
        }

        private void OnDestroy()
        {
            if (underwaterMaterial != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(underwaterMaterial);
                }
                else
                {
                    DestroyImmediate(underwaterMaterial);
                }
            }

            if (overlayCanvas != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(overlayCanvas.gameObject);
                }
                else
                {
                    DestroyImmediate(overlayCanvas.gameObject);
                }
            }
        }

        private void Update()
        {
            if (player == null)
            {
                player = GetComponentInParent<PlayerController>();
            }

            bool isUnderwater = player != null && player.isHeadInFluid;
            float targetStrength = isUnderwater ? maxStrength : 0f;
            currentStrength = Mathf.MoveTowards(currentStrength, targetStrength, transitionSpeed * Time.deltaTime);

            if (isUnderwater)
            {
                TryInitializeTintFromFluid();
            }

            UpdateOverlayTint();
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (underwaterMaterial == null || currentStrength <= 0.001f)
            {
                Graphics.Blit(source, destination);
                return;
            }

            underwaterMaterial.SetColor(TintColorId, tintColor);
            underwaterMaterial.SetFloat(StrengthId, currentStrength);
            Graphics.Blit(source, destination, underwaterMaterial);
        }

        private void InitializeOverlayFallback()
        {
            bool usingScriptablePipeline = GraphicsSettings.currentRenderPipeline != null;
            if (!usingScriptablePipeline || targetCamera == null)
            {
                return;
            }

            GameObject overlayRoot = new GameObject("UnderwaterOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            overlayRoot.transform.SetParent(targetCamera.transform, false);

            overlayCanvas = overlayRoot.GetComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            overlayCanvas.worldCamera = targetCamera;
            overlayCanvas.planeDistance = targetCamera.nearClipPlane + 0.01f;
            overlayCanvas.sortingOrder = short.MaxValue;

            CanvasScaler scaler = overlayRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            GameObject imageObject = new GameObject("Tint", typeof(RectTransform), typeof(Image));
            imageObject.transform.SetParent(overlayRoot.transform, false);

            RectTransform rect = imageObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            overlayImage = imageObject.GetComponent<Image>();
            overlayImage.raycastTarget = false;
            overlayImage.color = Color.clear;
        }

        private void UpdateOverlayTint()
        {
            if (overlayImage == null)
            {
                return;
            }

            Color overlayColor = tintColor;
            overlayColor.a = currentStrength;
            overlayImage.color = overlayColor;
        }

        private void TryInitializeTintFromFluid()
        {
            if (!syncWithFluidTint)
            {
                return;
            }

            if (AssetsContainer.Instance == null || AssetsContainer.Instance.FluidMaterial == null)
            {
                return;
            }

            Material fluidMaterial = AssetsContainer.Instance.FluidMaterial;
            if (!fluidMaterial.HasProperty("_TintColor"))
            {
                return;
            }

            Color fluidColor = fluidMaterial.GetColor("_TintColor");
            fluidColor.a = 1f;
            if (!tintInitializedFromFluid || !ApproximatelyEqual(fluidColor, cachedFluidTint))
            {
                tintColor = fluidColor;
                cachedFluidTint = fluidColor;
                tintInitializedFromFluid = true;
            }
        }

        private static bool ApproximatelyEqual(Color a, Color b)
        {
            const float epsilon = 0.0025f;
            return Mathf.Abs(a.r - b.r) < epsilon &&
                   Mathf.Abs(a.g - b.g) < epsilon &&
                   Mathf.Abs(a.b - b.b) < epsilon &&
                   Mathf.Abs(a.a - b.a) < epsilon;
        }
    }
}