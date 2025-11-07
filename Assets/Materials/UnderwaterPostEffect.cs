using UnityEngine;

namespace BenScr.MCC
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
        }

        private void Update()
        {
            if (player == null)
            {
                player = GetComponentInParent<PlayerController>();
            }

            bool isUnderwater = player != null && player.IsInFluid;
            float targetStrength = isUnderwater ? maxStrength : 0f;
            currentStrength = Mathf.MoveTowards(currentStrength, targetStrength, transitionSpeed * Time.deltaTime);

            if (isUnderwater)
            {
                TryInitializeTintFromFluid();
            }
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

        private void TryInitializeTintFromFluid()
        {
            if (!syncWithFluidTint)
            {
                return;
            }

            if (AssetsContainer.instance == null || AssetsContainer.instance.fluidMaterial == null)
            {
                return;
            }

            Material fluidMaterial = AssetsContainer.instance.fluidMaterial;
            if (!fluidMaterial.HasProperty("_Color"))
            {
                return;
            }

            Color fluidColor = fluidMaterial.GetColor("_Color");
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