using UnityEngine;
using UnityEngine.Rendering;

namespace BenScr.MCC
{
    public class AssetsContainer : MonoBehaviour
    {
        public Block[] blocks;

        public Material blockMaterial;
        [SerializeField] private int blockTexResolution = 16;

        public Material fluidMaterial;

        public static AssetsContainer instance;

        public static int TEXTURE_BLOCKS_ROWS;
        public static int TEXTURE_BLOCKS_COLS;
        public static float BLOCK_W;
        public static float BLOCK_H;
        public static float TEXTURE_WIDTH;
        public static float TEXTURE_HEIGHT;


        private void Awake()
        {
            instance = this;

            InitBlocks();
            InitTextureValues();
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