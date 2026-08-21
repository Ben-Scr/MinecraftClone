using System.Collections.Generic;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public static class BlockTextureArrayBuilder
    {
        public const string TextureArrayProperty = "_BlockTextures";

        private const int FallbackLayer = 0;

        public static Texture2DArray BuildAndAssign(BlockData[] blocks)
        {
            List<Sprite> sprites = CollectSprites(blocks, out Dictionary<Sprite, int> spriteLayers);
            AssignLayers(blocks, spriteLayers);

            if (sprites.Count == 0)
                return null;

            return BuildTextureArray(sprites);
        }

        private static List<Sprite> CollectSprites(BlockData[] blocks, out Dictionary<Sprite, int> spriteLayers)
        {
            var sprites = new List<Sprite>();
            spriteLayers = new Dictionary<Sprite, int>();

            if (blocks == null)
                return sprites;

            Vector2Int expectedSize = Vector2Int.zero;

            for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
            {
                BlockData block = blocks[blockIndex];
                if (block == null)
                    continue;

                for (int face = 0; face <= (int)BlockData.BlockFace.Right; face++)
                {
                    BlockData.FaceTextureData textureData = block.GetTexture(face);
                    CollectSprite(textureData.Texture, sprites, spriteLayers, ref expectedSize);
                    CollectSprite(textureData.OverlayTexture, sprites, spriteLayers, ref expectedSize);
                }
            }

            return sprites;
        }

        private static void CollectSprite(
            Sprite sprite,
            List<Sprite> sprites,
            Dictionary<Sprite, int> spriteLayers,
            ref Vector2Int expectedSize)
        {
            if (sprite == null || sprite.texture == null || spriteLayers.ContainsKey(sprite))
                return;

            Rect rect = GetCopyRect(sprite);
            Vector2Int spriteSize = new Vector2Int(
                Mathf.RoundToInt(rect.width),
                Mathf.RoundToInt(rect.height));

            if (spriteSize.x <= 0 || spriteSize.y <= 0)
                return;

            if (expectedSize == Vector2Int.zero)
            {
                expectedSize = spriteSize;
            }
            else if (spriteSize != expectedSize)
            {
                Debug.LogWarning(
                    $"Skipping block texture '{sprite.name}' because it is {spriteSize.x}x{spriteSize.y}, " +
                    $"but the texture array is {expectedSize.x}x{expectedSize.y}.");
                return;
            }

            int layer = sprites.Count + 1;
            spriteLayers.Add(sprite, layer);
            sprites.Add(sprite);
        }

        private static void AssignLayers(BlockData[] blocks, Dictionary<Sprite, int> spriteLayers)
        {
            if (blocks == null)
                return;

            for (int i = 0; i < blocks.Length; i++)
            {
                BlockData block = blocks[i];
                if (block == null)
                    continue;

                block.AssignTextureLayers(sprite =>
                {
                    if (sprite == null || !spriteLayers.TryGetValue(sprite, out int layer))
                        return FallbackLayer;

                    return layer;
                });
            }
        }

        private static Texture2DArray BuildTextureArray(List<Sprite> sprites)
        {
            Sprite firstSprite = sprites[0];
            Rect firstRect = GetCopyRect(firstSprite);
            int tileWidth = Mathf.RoundToInt(firstRect.width);
            int tileHeight = Mathf.RoundToInt(firstRect.height);
            int layerCount = sprites.Count + 1;

            var textureArray = new Texture2DArray(
                tileWidth,
                tileHeight,
                layerCount,
                TextureFormat.RGBA32,
                false,
                false)
            {
                name = "Runtime Block Texture Array",
                filterMode = firstSprite.texture.filterMode,
                wrapMode = TextureWrapMode.Repeat,
                anisoLevel = firstSprite.texture.anisoLevel,
            };

            FillFallbackLayer(textureArray, tileWidth, tileHeight);

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(
                tileWidth,
                tileHeight,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB);
            var readableTexture = new Texture2D(tileWidth, tileHeight, TextureFormat.RGBA32, false, false)
            {
                filterMode = textureArray.filterMode,
                wrapMode = TextureWrapMode.Clamp,
            };

            try
            {
                for (int i = 0; i < sprites.Count; i++)
                    CopySpriteToLayer(sprites[i], textureArray, i + 1, readableTexture, renderTexture);

                textureArray.Apply(false, false);
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
                DestroyTemporary(readableTexture);
            }

            return textureArray;
        }

        private static void FillFallbackLayer(Texture2DArray textureArray, int width, int height)
        {
            Color32[] pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.magenta;

            textureArray.SetPixels32(pixels, FallbackLayer);
        }

        private static void CopySpriteToLayer(
            Sprite sprite,
            Texture2DArray textureArray,
            int layer,
            Texture2D readableTexture,
            RenderTexture renderTexture)
        {
            Rect rect = GetCopyRect(sprite);
            Texture2D sourceTexture = sprite.texture;
            int width = Mathf.RoundToInt(rect.width);
            int height = Mathf.RoundToInt(rect.height);

            Vector2 scale = new Vector2(rect.width / sourceTexture.width, rect.height / sourceTexture.height);
            Vector2 offset = new Vector2(rect.x / sourceTexture.width, rect.y / sourceTexture.height);

            Graphics.Blit(sourceTexture, renderTexture, scale, offset);

            RenderTexture.active = renderTexture;
            readableTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            readableTexture.Apply(false, false);
            textureArray.SetPixels32(readableTexture.GetPixels32(), layer);
        }

        private static Rect GetCopyRect(Sprite sprite)
        {
            Texture2D texture = sprite.texture;
            if (texture.width == 32 && texture.height == 32)
                return new Rect(0f, 0f, 32f, 32f);

            return sprite.textureRect;
        }

        private static void DestroyTemporary(Texture2D texture)
        {
            if (texture == null)
                return;

            if (Application.isPlaying)
                Object.Destroy(texture);
            else
                Object.DestroyImmediate(texture);
        }
    }
}
