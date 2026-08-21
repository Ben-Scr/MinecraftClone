using System;
using System.Collections.Generic;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public class PlacedBlockManager : MonoBehaviour
    {
        private static PlacedBlockManager instance;

        public static PlacedBlockManager Instance
        {
            get
            {
                if (instance != null)
                    return instance;

                instance = FindFirstObjectByType<PlacedBlockManager>();
                if (instance != null)
                    return instance;

                GameObject managerObject = new GameObject(nameof(PlacedBlockManager));
                instance = managerObject.AddComponent<PlacedBlockManager>();
                return instance;
            }
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
        }

        public static void PlaceOrUpdate(Chunk chunk, Vector3 localPosition, int blockId, int rotationY)
        {
            if (chunk == null)
                return;

            if (!TrySnapLocalPosition(localPosition, out Vector3Int snappedLocalPosition))
                return;

            BlockData block = AssetsContainer.GetBlock(blockId);
            if (block == null || !block.UsesCustomModel)
            {
                RemoveAt(chunk, snappedLocalPosition);
                return;
            }

            if (chunk.PlacedBlocks == null)
                chunk.PlacedBlocks = new List<PlacedBlockData>();

            PlacedBlockData data = FindAt(chunk.PlacedBlocks, snappedLocalPosition);
            if (data == null)
            {
                data = new PlacedBlockData();
                chunk.PlacedBlocks.Add(data);
            }

            data.BlockId = blockId;
            data.LocalPosition = snappedLocalPosition;
            data.RotationY = NormalizeRotationY(rotationY);

            chunk.HasChanged = true;
            Instance.RefreshView(chunk, data);
        }

        public static void RemoveAt(Chunk chunk, Vector3 localPosition)
        {
            if (!TrySnapLocalPosition(localPosition, out Vector3Int snappedLocalPosition))
                return;

            RemoveAt(chunk, snappedLocalPosition);
        }

        public static void RemoveAt(Chunk chunk, Vector3Int localPosition)
        {
            if (chunk?.PlacedBlocks == null)
                return;

            for (int i = chunk.PlacedBlocks.Count - 1; i >= 0; i--)
            {
                PlacedBlockData data = chunk.PlacedBlocks[i];
                if (data == null || data.LocalPosition != localPosition)
                    continue;

                DestroyView(data.View);
                chunk.PlacedBlocks.RemoveAt(i);
                chunk.HasChanged = true;
            }
        }

        public static bool TryGetDataAt(Chunk chunk, Vector3Int localPosition, out PlacedBlockData placedBlockData)
        {
            placedBlockData = null;

            if (chunk?.PlacedBlocks == null)
                return false;

            PlacedBlockData data = FindAt(chunk.PlacedBlocks, localPosition);
            if (data == null)
                return false;

            placedBlockData = data.Clone();
            return true;
        }

        public static void RefreshChunk(Chunk chunk)
        {
            if (chunk?.PlacedBlocks == null || chunk.PlacedBlocks.Count == 0)
                return;

            Instance.RefreshChunkInternal(chunk, spawnMissingViews: true);
        }

        public static void PrepareForSave()
        {
            if (TerrainGenerator.Chunks == null)
                return;

            foreach (Chunk chunk in TerrainGenerator.Chunks.Values)
            {
                Instance.RefreshChunkInternal(chunk, spawnMissingViews: false);
            }
        }

        private void RefreshChunkInternal(Chunk chunk, bool spawnMissingViews)
        {
            if (chunk?.PlacedBlocks == null)
                return;

            for (int i = chunk.PlacedBlocks.Count - 1; i >= 0; i--)
            {
                PlacedBlockData data = chunk.PlacedBlocks[i];
                if (!IsStillValidInChunk(chunk, data))
                {
                    DestroyView(data?.View);
                    chunk.PlacedBlocks.RemoveAt(i);
                    chunk.HasChanged = true;
                    continue;
                }

                if (spawnMissingViews)
                    RefreshView(chunk, data);
            }
        }

        private void RefreshView(Chunk chunk, PlacedBlockData data)
        {
            if (chunk == null || data == null)
                return;

            BlockData block = AssetsContainer.GetBlock(data.BlockId);
            if (block == null || !block.UsesCustomModel || block.ModelPrefab == null)
            {
                DestroyView(data.View);
                data.View = null;
                return;
            }

            Transform viewParent = chunk.GameObject != null
                ? chunk.GameObject.transform
                : transform;

            if (data.View == null)
            {
                data.View = Instantiate(
                    block.ModelPrefab,
                    GetWorldPosition(chunk, data, block),
                    GetWorldRotation(data, block),
                    viewParent);

                data.View.name = $"{block.name}_{data.LocalX}_{data.LocalY}_{data.LocalZ}";
                data.View.transform.localScale = block.ModelScale;
                ApplyMaterialOverride(data.View, block.ModelMaterialOverride);

                if (block.GenerateModelCollider)
                    EnsureCollider(data.View);
            }
            else if (data.View.transform.parent != viewParent)
            {
                data.View.transform.SetParent(viewParent, worldPositionStays: true);
            }

            data.View.transform.SetPositionAndRotation(
                GetWorldPosition(chunk, data, block),
                GetWorldRotation(data, block));
            data.View.transform.localScale = block.ModelScale;
        }

        private static bool IsStillValidInChunk(Chunk chunk, PlacedBlockData data)
        {
            if (chunk?.Blocks == null || data == null || !data.IsValid)
                return false;

            Vector3Int localPosition = data.LocalPosition;
            if (!ChunkUtility.IsInsideChunk(localPosition))
                return false;

            if (chunk.Blocks[localPosition.x, localPosition.y, localPosition.z] != data.BlockId)
                return false;

            BlockData block = AssetsContainer.GetBlock(data.BlockId);
            return block != null && block.UsesCustomModel;
        }

        private static Vector3 GetWorldPosition(Chunk chunk, PlacedBlockData data, BlockData block)
        {
            Quaternion placementRotation = Quaternion.Euler(0f, data.RotationY, 0f);
            Vector3 blockCenter = chunk.Position + (Vector3)data.LocalPosition + Vector3.one * 0.5f;
            return blockCenter + placementRotation * block.ModelPositionOffset;
        }

        private static Quaternion GetWorldRotation(PlacedBlockData data, BlockData block)
        {
            Quaternion placementRotation = Quaternion.Euler(0f, data.RotationY, 0f);
            Quaternion modelRotation = Quaternion.Euler(block.ModelRotationOffset);
            return placementRotation * modelRotation;
        }

        private static void ApplyMaterialOverride(GameObject view, Material material)
        {
            if (view == null || material == null)
                return;

            Renderer[] renderers = view.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
                renderers[i].sharedMaterial = material;
        }

        private static void EnsureCollider(GameObject view)
        {
            if (view.GetComponentInChildren<Collider>() != null)
                return;

            if (!TryGetRendererBounds(view, out Bounds bounds))
            {
                BoxCollider fallbackCollider = view.AddComponent<BoxCollider>();
                fallbackCollider.center = Vector3.zero;
                fallbackCollider.size = Vector3.one;
                return;
            }

            BoxCollider collider = view.AddComponent<BoxCollider>();
            collider.center = view.transform.InverseTransformPoint(bounds.center);

            Vector3 scale = view.transform.lossyScale;
            collider.size = new Vector3(
                SafeDivide(bounds.size.x, scale.x),
                SafeDivide(bounds.size.y, scale.y),
                SafeDivide(bounds.size.z, scale.z));
        }

        private static bool TryGetRendererBounds(GameObject view, out Bounds bounds)
        {
            Renderer[] renderers = view.GetComponentsInChildren<Renderer>();
            bounds = default;

            if (renderers.Length == 0)
                return false;

            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            return true;
        }

        private static float SafeDivide(float value, float divisor)
        {
            return Mathf.Abs(divisor) < 0.0001f ? value : value / divisor;
        }

        private static bool TrySnapLocalPosition(Vector3 localPosition, out Vector3Int snappedLocalPosition)
        {
            snappedLocalPosition = new Vector3Int(
                Mathf.FloorToInt(localPosition.x),
                Mathf.FloorToInt(localPosition.y),
                Mathf.FloorToInt(localPosition.z));

            return ChunkUtility.IsInsideChunk(snappedLocalPosition);
        }

        private static PlacedBlockData FindAt(List<PlacedBlockData> placedBlocks, Vector3Int localPosition)
        {
            for (int i = 0; i < placedBlocks.Count; i++)
            {
                PlacedBlockData data = placedBlocks[i];
                if (data != null && data.LocalPosition == localPosition)
                    return data;
            }

            return null;
        }

        private static int NormalizeRotationY(int rotationY)
        {
            int snapped = Mathf.RoundToInt(rotationY / 90f) * 90;
            snapped %= 360;
            return snapped < 0 ? snapped + 360 : snapped;
        }

        private static void DestroyView(GameObject view)
        {
            if (view == null)
                return;

            if (Application.isPlaying)
                Destroy(view);
            else
                DestroyImmediate(view);
        }
    }

    [Serializable]
    public sealed class PlacedBlockData
    {
        public int BlockId;
        public int LocalX;
        public int LocalY;
        public int LocalZ;
        public int RotationY;

        [NonSerialized]
        public GameObject View;

        public Vector3Int LocalPosition
        {
            get => new Vector3Int(LocalX, LocalY, LocalZ);
            set
            {
                LocalX = value.x;
                LocalY = value.y;
                LocalZ = value.z;
            }
        }

        public bool IsValid =>
            BlockId > Chunk.BLOCK_AIR &&
            LocalX >= 0 &&
            LocalY >= 0 &&
            LocalZ >= 0 &&
            LocalX < Chunk.CHUNK_SIZE &&
            LocalY < Chunk.CHUNK_HEIGHT &&
            LocalZ < Chunk.CHUNK_SIZE;

        public PlacedBlockData Clone()
        {
            return new PlacedBlockData
            {
                BlockId = BlockId,
                LocalX = LocalX,
                LocalY = LocalY,
                LocalZ = LocalZ,
                RotationY = RotationY,
            };
        }
    }
}
