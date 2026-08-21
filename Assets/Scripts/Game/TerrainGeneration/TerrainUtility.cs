using System;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public static class TerrainUtility
    {
        public static Action<BlockData, Vector3> OnDestroyBlock;

        public static void SetBlock(Vector3 position, int blockId)
        {
            SetBlock(position, blockId, 0);
        }

        public static void SetBlock(Vector3 position, int blockId, int rotationY)
        {
            Chunk chunk = ChunkUtility.GetChunkAtPosition(position);

            if (chunk != null)
            {
                Vector3Int worldBlockPosition = ChunkUtility.SnapPosition(position);
                chunk.HasChanged = true;
                Vector3 localPosition = position - chunk.Position;
                int oldBlockId = GetBlockId(chunk, localPosition);
                PlacedBlockManager.RemoveAt(chunk, localPosition);
                chunk.SetBlock(localPosition, blockId, update: true, prioritizeMesh: true);
                PlacedBlockManager.PlaceOrUpdate(chunk, localPosition, blockId, rotationY);
                FluidSimulator.NotifyBlockChanged(worldBlockPosition, oldBlockId, blockId);
                FallingBlockSimulator.NotifyBlockChanged(worldBlockPosition, oldBlockId, blockId);
            }
            else
            {
                Debug.LogWarning("Position is outside of world: " + position);
            }
        }

        public static void DestroyBlock(Vector3 position)
        {
            Chunk chunk = ChunkUtility.GetChunkAtPosition(position);
            Vector3 localPosition = position - chunk.Position;

            BlockData block = chunk.GetBlock(localPosition);
            if (block == null || block.IsFluid)
                return;

            DestroyDamageTexture(localPosition, chunk);
        }

        public static void DamageBlock(Vector3 position, int damage)
        {
            Chunk chunk = ChunkUtility.GetChunkAtPosition(position);

            Vector3 localPosition = position - chunk.Position;
            BlockData hitBlock = chunk.GetBlock(localPosition);
            if (hitBlock == null || hitBlock.IsIndestructible || hitBlock.IsFluid)
                return;

            ByteVector3 key = new ByteVector3((byte)localPosition.x, (byte)localPosition.y, (byte)localPosition.z);

            bool destroyed = false;

            if (!chunk.DamagedBlocks.ContainsKey(key))
            {
                chunk.HasChanged = true;

                if ((hitBlock.Durability - damage) < 1)
                {
                    destroyed = true;

                    BlockData block = chunk.GetBlock(localPosition);
                    int oldBlockId = GetBlockId(chunk, localPosition);
                    PlacedBlockManager.RemoveAt(chunk, localPosition);
                    chunk.SetBlock(
                        localPosition,
                        Chunk.BLOCK_AIR,
                        update: true,
                        prioritizeMesh: true);
                    FluidSimulator.NotifyBlockChanged(ChunkUtility.SnapPosition(position), oldBlockId, Chunk.BLOCK_AIR);
                    FallingBlockSimulator.NotifyBlockChanged(ChunkUtility.SnapPosition(position), oldBlockId, Chunk.BLOCK_AIR);
                    OnDestroyBlock?.Invoke(block, position);
                }
                else
                {
                    GameObject obj = GameObject.Instantiate(AssetsContainer.Instance.DamageStagePrefab, position + new Vector3(0.5f, 0.5f, 0.5f), Quaternion.identity);
                    DamagedBlock damagedBlock = new DamagedBlock(hitBlock.Durability - damage, obj);
                    chunk.DamagedBlocks.Add(key, damagedBlock);

                    UpdateDamageTexture(hitBlock.Durability, damagedBlock);
                }
            }
            else
            {
                DamagedBlock damagedBlock = chunk.DamagedBlocks[key];
                --damagedBlock.Health;

                if (damagedBlock.Health <= 0)
                {
                    DestroyDamageTexture(localPosition, chunk);
                    destroyed = true;
                }
                else
                {
                    UpdateDamageTexture(hitBlock.Durability, damagedBlock);
                }
            }

            if (destroyed && hitBlock.DestroyEffect)
            {
                GameObject.Destroy(GameObject.Instantiate(hitBlock.DestroyEffect, position + new Vector3(0.5f, 0.5f, 0.5f), Quaternion.identity), 1.0f);
            }
        }

        private static void DestroyDamageTexture(Vector3 localPosition, Chunk chunk)
        {
            BlockData block = chunk.GetBlock(localPosition);
            int oldBlockId = GetBlockId(chunk, localPosition);
            PlacedBlockManager.RemoveAt(chunk, localPosition);
            chunk.SetBlock(
                localPosition,
                Chunk.BLOCK_AIR,
                update: true,
                prioritizeMesh: true);
            Vector3 worldPosition = localPosition + chunk.Position;
            FluidSimulator.NotifyBlockChanged(ChunkUtility.SnapPosition(worldPosition), oldBlockId, Chunk.BLOCK_AIR);
            FallingBlockSimulator.NotifyBlockChanged(ChunkUtility.SnapPosition(worldPosition), oldBlockId, Chunk.BLOCK_AIR);
            ByteVector3 key = (ByteVector3)localPosition;

            if (chunk.DamagedBlocks.TryGetValue(key, out DamagedBlock damagedBlock))
            {
                GameObject.Destroy(damagedBlock.DamageStage);
                chunk.DamagedBlocks.Remove(key);
            }

            OnDestroyBlock?.Invoke(block, localPosition + chunk.Position);
        }

        private static int GetBlockId(Chunk chunk, Vector3 localPosition)
        {
            Vector3Int blockPosition = new Vector3Int(
                Mathf.FloorToInt(localPosition.x),
                Mathf.FloorToInt(localPosition.y),
                Mathf.FloorToInt(localPosition.z));

            if (chunk == null ||
                chunk.Blocks == null ||
                !ChunkUtility.IsInsideChunk(blockPosition))
            {
                return Chunk.BLOCK_AIR;
            }

            return chunk.Blocks[blockPosition.x, blockPosition.y, blockPosition.z];
        }

        private static void UpdateDamageTexture(int durability, DamagedBlock damagedBlock)
        {
            int stagesLength = AssetsContainer.Instance.DamageStages.Length;

            int health = Math.Clamp(damagedBlock.Health, 0, durability);
            int damaged = durability - health;

            int stageIndex = (damaged * stagesLength) / (durability + 1);
            stageIndex = Math.Clamp(stageIndex, 0, stagesLength - 1);


            damagedBlock.DamageStage.GetComponent<MeshRenderer>().material.mainTexture = AssetsContainer.Instance.DamageStages[stageIndex].texture;
        }
    }
}
