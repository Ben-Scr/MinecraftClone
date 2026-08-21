using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace BenScr.MinecraftClone
{
    public enum BlockRenderType
    {
        Cube = 0,
        CustomModel = 1,
    }

    [CreateAssetMenu(fileName = "BlockData", menuName = "Scriptable Objects/Blocks/Block")]
    public class BlockData : ScriptableObject
    {
        public enum BlockFace
        {
            Back = 0,
            Front = 1,
            Top = 2,
            Bottom = 3,
            Left = 4,
            Right = 5,
        }

        [Serializable]
        public struct FaceTextureData
        {
            [FormerlySerializedAs("texture")]
            public Sprite Texture;
            public Sprite OverlayTexture;

            [FormerlySerializedAs("uvMin")]
            [HideInInspector] public Vector2 UvMin;
            [FormerlySerializedAs("uvMax")]
            [HideInInspector] public Vector2 UvMax;
            [HideInInspector] public int TextureLayer;
            [HideInInspector] public int OverlayTextureLayer;

            public void RebuildUvCache()
            {
                if (Texture == null || Texture.texture == null)
                {
                    UvMin = Vector2.zero;
                    UvMax = Vector2.one;
                    return;
                }

                Rect rect = Texture.textureRect;
                Texture2D atlasTexture = Texture.texture;

                UvMin = new Vector2(rect.xMin / atlasTexture.width, rect.yMin / atlasTexture.height);
                UvMax = new Vector2(rect.xMax / atlasTexture.width, rect.yMax / atlasTexture.height);
            }

            public readonly bool Matches(in FaceTextureData other)
            {
                return TextureLayer == other.TextureLayer &&
                       OverlayTextureLayer == other.OverlayTextureLayer;
            }
        }

        internal ushort id;
        [FormerlySerializedAs("durability")]
        public int Durability = 5;
        public bool IsIndestructible;
        [FormerlySerializedAs("isTransparent")]
        public bool IsTransparent;
        [FormerlySerializedAs("isFluid")]
        public bool IsFluid;

        [Header("Lighting")]
        [Range(0, 15)]
        public int LightEmission;

        [Header("Physics")]
        public bool FallsWhenUnsupported;

        [Header("Rendering")]
        public BlockRenderType RenderType = BlockRenderType.Cube;
        public bool IsFullBlock = true;
        public bool RotateOnPlace;
        public bool GenerateModelCollider = true;
        public GameObject ModelPrefab;
        public Material ModelMaterialOverride;
        public Vector3 ModelPositionOffset;
        public Vector3 ModelRotationOffset;
        public Vector3 ModelScale = Vector3.one;

        [FormerlySerializedAs("itemData")]
        public ItemData ItemData;

        [FormerlySerializedAs("destroyEffect")]
        public GameObject DestroyEffect;

        [Serializable]
        private struct FaceTextureSet
        {
            [FormerlySerializedAs("back")]
            public FaceTextureData Back;
            [FormerlySerializedAs("front")]
            public FaceTextureData Front;
            [FormerlySerializedAs("top")]
            public FaceTextureData Top;
            [FormerlySerializedAs("bottom")]
            public FaceTextureData Bottom;
            [FormerlySerializedAs("left")]
            public FaceTextureData Left;
            [FormerlySerializedAs("right")]
            public FaceTextureData Right;

            public FaceTextureData Get(int face)
            {
                return (BlockFace)face switch
                {
                    BlockFace.Back => Back,
                    BlockFace.Front => Front,
                    BlockFace.Top => Top,
                    BlockFace.Bottom => Bottom,
                    BlockFace.Left => Left,
                    BlockFace.Right => Right,
                    _ => Back,
                };
            }

            public void Set(int face, FaceTextureData data)
            {
                switch ((BlockFace)face)
                {
                    case BlockFace.Back:
                        Back = data;
                        break;
                    case BlockFace.Front:
                        Front = data;
                        break;
                    case BlockFace.Top:
                        Top = data;
                        break;
                    case BlockFace.Bottom:
                        Bottom = data;
                        break;
                    case BlockFace.Left:
                        Left = data;
                        break;
                    case BlockFace.Right:
                        Right = data;
                        break;
                }
            }
        }

        [SerializeField] private FaceTextureSet faceTextures;

        public FaceTextureData GetTexture(int face)
        {
            if (face < 0 || face > (int)BlockFace.Right)
            {
                Debug.LogWarning("Invalid face: " + face);
                return faceTextures.Get((int)BlockFace.Back);
            }

            return faceTextures.Get(face);
        }

        public bool UsesCustomModel => RenderType == BlockRenderType.CustomModel;

        public bool OccludesNeighborFaces =>
            !IsTransparent && (IsFullBlock || RenderType == BlockRenderType.Cube);

        public void RebuildFaceTextureCache()
        {
            for (int i = 0; i <= (int)BlockFace.Right; i++)
            {
                FaceTextureData faceTexture = faceTextures.Get(i);
                faceTexture.RebuildUvCache();
                faceTextures.Set(i, faceTexture);
            }
        }

        public void AssignTextureLayers(Func<Sprite, int> textureLayerResolver)
        {
            for (int i = 0; i <= (int)BlockFace.Right; i++)
            {
                FaceTextureData faceTexture = faceTextures.Get(i);
                faceTexture.TextureLayer = textureLayerResolver != null
                    ? textureLayerResolver(faceTexture.Texture)
                    : 0;
                faceTexture.OverlayTextureLayer = textureLayerResolver != null
                    ? textureLayerResolver(faceTexture.OverlayTexture)
                    : 0;
                faceTextures.Set(i, faceTexture);
            }
        }

        private void OnValidate()
        {
            LightEmission = Mathf.Clamp(LightEmission, 0, 15);
            RebuildFaceTextureCache();
        }
    }
}
