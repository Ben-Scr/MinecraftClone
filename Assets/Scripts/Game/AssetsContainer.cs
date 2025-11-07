using UnityEngine;
using UnityEngine.Rendering;

namespace BenScr.MCC
{
    public class AssetsContainer : MonoBehaviour
    {
        public Block[] blocks;

        public Material blockMaterial;
        public Material fluidMaterial;
        [SerializeField] private Shader defaultFluidShader;
        [SerializeField] private Color defaultFluidTint = new Color(0.2f, 0.45f, 0.85f, 0.65f);
        [SerializeField] private Color defaultFoamColor = new Color(0.85f, 0.95f, 1f, 1f);
        [SerializeField, Range(0f, 1f)] private float defaultFoamStrength = 0.35f;
        [SerializeField] private Vector4 defaultWaveSpeed = new Vector4(0.05f, 0.04f, -0.03f, 0.02f);
        [SerializeField] private float defaultWaveScale = 1f;

        [SerializeField] private int blockTexResolution = 16;

        public static AssetsContainer instance;

        public static int TEXTURE_BLOCKS_ROWS;
        public static int TEXTURE_BLOCKS_COLS;
        public static float BLOCK_W;
        public static float BLOCK_H;
        public static float TEXTURE_WIDTH;
        public static float TEXTURE_HEIGHT;


        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(this.gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(this.gameObject);


            InitBlocks();
            InitTextureValues();
            //ConfigureFluidMaterial();
        }

        private void ConfigureFluidMaterial()
        {
            if (fluidMaterial == null)
            {
                Debug.LogWarning("Fluid material is not assigned on the AssetsContainer.");
                return;
            }

            //Shader shader = defaultFluidShader != null ? defaultFluidShader : Shader.Find("BenScr/Fluid/Water");

            //if (shader != null)
            //{
            //    if (fluidMaterial.shader != shader)
            //    {
            //        fluidMaterial.shader = shader;
            //    }
            //}
            //else
            //{
            //    Debug.LogWarning("Water shader 'BenScr/Fluid/Water' not found. Fluid material will use its assigned shader.");
            //}

            if (fluidMaterial.HasProperty("_Color"))
            {
                fluidMaterial.SetColor("_Color", defaultFluidTint);
            }

            if (fluidMaterial.HasProperty("_FoamColor"))
            {
                fluidMaterial.SetColor("_FoamColor", defaultFoamColor);
            }

            if (fluidMaterial.HasProperty("_FoamStrength"))
            {
                fluidMaterial.SetFloat("_FoamStrength", defaultFoamStrength);
            }

            if (fluidMaterial.HasProperty("_WaveSpeed"))
            {
                fluidMaterial.SetVector("_WaveSpeed", defaultWaveSpeed);
            }

            if (fluidMaterial.HasProperty("_WaveScale"))
            {
                fluidMaterial.SetFloat("_WaveScale", defaultWaveScale);
            }

            fluidMaterial.renderQueue = (int)RenderQueue.Transparent;
        }

        private void InitTextureValues()
        {
            if (blockMaterial == null)
            {
                Debug.LogError("Block material is not assigned on the AssetsContainer.");
                return;
            }

            Texture mainTex = blockMaterial.mainTexture;

            if (mainTex == null)
            {
                Debug.LogError("Block material does not have a main texture assigned.");
                return;
            }

            int resolution = Mathf.Max(1, blockTexResolution);

            int cols = mainTex.width / resolution;
            int rows = mainTex.height / resolution;

            if (cols <= 0 || rows <= 0)
            {
                Debug.LogError($"Invalid block texture resolution {resolution} for atlas size {mainTex.width}x{mainTex.height}.");
                return;
            }

            if ((mainTex.width % resolution) != 0 || (mainTex.height % resolution) != 0)
            {
                Debug.LogWarning(
                    $"Block atlas size {mainTex.width}x{mainTex.height} is not an even multiple of tile resolution {resolution}. " +
                    "UVs may be misaligned.");
            }

            TEXTURE_BLOCKS_COLS = cols;
            TEXTURE_BLOCKS_ROWS = rows;
            BLOCK_W = 1f / TEXTURE_BLOCKS_COLS;
            BLOCK_H = 1f / TEXTURE_BLOCKS_ROWS;
            TEXTURE_WIDTH = mainTex.width;
            TEXTURE_HEIGHT = mainTex.height;
        }


        private void InitBlocks()
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                blocks[i].id = (ushort)i;
            }
        }

        public static Block GetBlock(int id)
        {
            if (id < 0 || id >= instance.blocks.Length)
            {
                Debug.LogWarning("Block ID out of range: " + id);
                return null;
            }

            return instance.blocks[id];
        }
    }
}