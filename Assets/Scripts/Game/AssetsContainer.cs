using UnityEngine;

namespace BenScr.MCC
{
    public class AssetsContainer : MonoBehaviour
    {
        public Block[] blocks;

        public Material blockMaterial;
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
        }

        private void InitTextureValues()
        {
            int resolution = blockTexResolution;

            Texture mainTex = blockMaterial.mainTexture;

            TEXTURE_BLOCKS_COLS = (int)(1f / mainTex.texelSize.x / resolution) + 1;
            TEXTURE_BLOCKS_ROWS = (int)(1f / mainTex.texelSize.y / resolution) + 1;
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