using UnityEngine;
using UnityEngine.Serialization;

namespace BenScr.MinecraftClone
{
    using System.Collections.Generic;

    public class AssetsContainer : MonoBehaviour
    {
        [FormerlySerializedAs("blocks")]
        public BlockData[] Blocks;

        [FormerlySerializedAs("blockMaterial")]
        public Material BlockMaterial;
        [FormerlySerializedAs("damageStages")]
        public Sprite[] DamageStages;

        [FormerlySerializedAs("fluidMaterial")]
        public Material FluidMaterial;
        public Material LavaFluidMaterial;
        [FormerlySerializedAs("transparentMaterial")]
        public Material TransparentMaterial;
        [FormerlySerializedAs("damageStagePrefab")]
        public GameObject DamageStagePrefab;

        [SerializeField] private ItemData[] additionalItems;

        public static AssetsContainer Instance;
        private ItemData[] itemsById;
        private Texture2DArray blockTextureArray;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            InitBlocks();
            InitItems();
        }

        private void InitItems()
        {
            var items = new List<ItemData>();
            var uniqueItems = new HashSet<ItemData>();

            for (int i = 0; i < Blocks.Length; i++)
            {
                ItemData item = Blocks[i] != null ? Blocks[i].ItemData : null;
                if (item != null && uniqueItems.Add(item))
                    items.Add(item);
            }

            if (additionalItems != null)
            {
                for (int i = 0; i < additionalItems.Length; i++)
                {
                    ItemData item = additionalItems[i];
                    if (item != null && uniqueItems.Add(item))
                        items.Add(item);
                }
            }

            itemsById = items.ToArray();
            for (int i = 0; i < itemsById.Length; i++)
                itemsById[i].Id = i;
        }


        private void InitBlocks()
        {
            for (int i = 0; i < Blocks.Length; i++)
            {
                if (Blocks[i] == null)
                    continue;

                Blocks[i].id = (ushort)i;
                Blocks[i].RebuildFaceTextureCache();

                if (i == Chunk.BLOCK_LAVA && LavaFluidMaterial == null && Blocks[i].ModelMaterialOverride != null)
                    LavaFluidMaterial = Blocks[i].ModelMaterialOverride;
            }

            blockTextureArray = BlockTextureArrayBuilder.BuildAndAssign(Blocks);
            ApplyBlockTextureArray(blockTextureArray);
            FallingBlockEntity.EnsureMeshCacheForBlocks(Blocks);
        }

        private void ApplyBlockTextureArray(Texture2DArray textureArray)
        {
            ApplyBlockTextureArray(BlockMaterial, textureArray);
            ApplyBlockTextureArray(FluidMaterial, textureArray);
            ApplyBlockTextureArray(GetLavaFluidMaterial(), textureArray);
            ApplyBlockTextureArray(TransparentMaterial, textureArray);
        }

        private static void ApplyBlockTextureArray(Material material, Texture textureArray)
        {
            if (material == null || textureArray == null || !material.HasProperty(BlockTextureArrayBuilder.TextureArrayProperty))
            {
                return;
            }

            material.SetTexture(BlockTextureArrayBuilder.TextureArrayProperty, textureArray);
        }

        public Material GetLavaFluidMaterial()
        {
            return LavaFluidMaterial != null ? LavaFluidMaterial : FluidMaterial;
        }

        private void OnDestroy()
        {
            if (Instance != this)
                return;

            Instance = null;

            if (blockTextureArray != null)
                Destroy(blockTextureArray);

            FallingBlockEntity.ClearMeshCache();
        }

        public static BlockData GetBlock(int id)
        {
            if (id < 0 || id >= Instance.Blocks.Length)
            {
                Debug.LogWarning("Block ID out of range: " + id);
                return null;
            }

            return Instance.Blocks[id];
        }

        public static ItemData GetItem(int id)
        {
            if (Instance == null ||
                Instance.itemsById == null ||
                id < 0 ||
                id >= Instance.itemsById.Length)
            {
                return null;
            }

            return Instance.itemsById[id];
        }

        public static bool TryGetItemId(ItemData itemData, out int id)
        {
            id = -1;
            if (Instance == null || Instance.itemsById == null || itemData == null)
                return false;

            int candidate = itemData.Id;
            if (candidate >= 0 &&
                candidate < Instance.itemsById.Length &&
                Instance.itemsById[candidate] == itemData)
            {
                id = candidate;
                return true;
            }

            id = System.Array.IndexOf(Instance.itemsById, itemData);
            return id >= 0;
        }
    }
}
