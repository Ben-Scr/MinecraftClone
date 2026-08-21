using System;
using System.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace BenScr.MinecraftClone
{
    public class BuildManager : MonoBehaviour
    {
        private static readonly Vector3 halfExtents = new Vector3(0.499f, 0.499f, 0.499f);

        [SerializeField] private float maxInteractionDistance = 5;
        [SerializeField] private GameObject highlightBlock;
        [SerializeField] private GameObject damageStagePrefab;
        [SerializeField] private float breakBlockCooldown = 0.1f;
        [SerializeField] private float placeBlockCooldown = 0.1f;

        [Header("TNT")]
        [SerializeField, Min(0.05f)] private float tntFuseSeconds = 3f;
        [SerializeField, Min(0.1f)] private float tntDestructionRadius = 4f;
        [SerializeField, Min(1)] private int tntMaxDestroyedBlocks = 512;
        [SerializeField] private bool tntDestroyFluids;
        [SerializeField] private bool tntDestroyIndestructibleBlocks;
        [SerializeField] private bool tntDropDestroyedBlocks;
        [SerializeField] private bool tntPrimeNearbyTnt = true;
        [SerializeField, Min(0.05f)] private float tntChainedFuseSeconds = 1.2f;

        private Slot selectedBlockItemSlot;
        private BlockData GetSelectedBlock()
        {
            BlockItemData blockItemData = (BlockItemData)selectedBlockItemSlot?.Item.ItemData;
            return blockItemData?.Block;
        }

        private Vector3 highlightPosition;
        private Vector3 placeBlockPosition;


        private float breakBlockTimer = 0f;
        private float placeBlockTimer = 0f;

        private bool isActive = true;
        private bool highlightBlockActive = false;
        private bool blockInRange = false;

        private void OnEnable()
        {
            InventoryManager.OnSwitchSlot += OnSwitchSlot;
            InventoryManager.OnUpdateSlot += OnSwitchSlot;

            PlayerController.OnSwitchGameMode += OnSwitchGameMode;
            TerrainUtility.OnDestroyBlock += OnDestroyBlock;
        }
        private void OnDisable()
        {
            InventoryManager.OnSwitchSlot -= OnSwitchSlot;
            InventoryManager.OnUpdateSlot -= OnSwitchSlot;

            PlayerController.OnSwitchGameMode -= OnSwitchGameMode;
            TerrainUtility.OnDestroyBlock -= OnDestroyBlock;
        }

        private void OnDestroyBlock(BlockData blockData, Vector3 position)
        {
            if (blockData.ItemData)
            {
                Vector3 dropPosition = position + Vector3.one * 0.5f;
                if (!DroppedItemManager.TryDropAt(blockData.ItemData, 1, blockData.ItemData.MaxDuration, dropPosition))
                    Debug.LogWarning("Failed to drop item: " + blockData.ItemData.name);
            }

            Debug.Log("Destroyed Block: " + (blockData?.ItemData?.name ?? "null") + " at: " + position);
        }

        private void Update()
        {
            if (!PlayerController.Instance || GameController.IsFrozen || !isActive) return;

            breakBlockTimer += Time.deltaTime;
            placeBlockTimer += Time.deltaTime;

            if (blockInRange)
            {
                if ((PlayerController.Instance.IsFlying ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0)) && breakBlockTimer > breakBlockCooldown)
                {
                    breakBlockTimer = 0f;

                    if (PlayerController.Instance.GameMode == GameMode.Creative)
                        TerrainUtility.DestroyBlock(highlightPosition);
                    else
                        TerrainUtility.DamageBlock(highlightPosition, 1);
                }

                if (Input.GetMouseButton(1) && placeBlockTimer > placeBlockCooldown)
                {
                    placeBlockTimer = 0f;

                    if (!TryPrimeHighlightedTnt())
                    {
                        BlockData selectedBlock = GetSelectedBlock();
                        if (selectedBlock != null && CanPlaceBlockAt(placeBlockPosition))
                        {
                            TerrainUtility.SetBlock(
                                placeBlockPosition,
                                selectedBlock.id,
                                GetPlacementRotationY(selectedBlock));

                            if (PlayerController.Instance.GameMode != GameMode.Creative)
                                InventoryManager.RemoveItem(selectedBlockItemSlot, 1);
                        }
                    }
                }
            }

            UpdateHighlightBlock();
        }

        private bool TryPrimeHighlightedTnt()
        {
            Vector3Int blockPosition = ChunkUtility.SnapPosition(highlightPosition);
            if (ChunkUtility.GetBlockAtPosition(blockPosition) != Chunk.BLOCK_TNT)
                return false;

            return FallingBlockSimulator.TryPrimeTntBlock(blockPosition, CreateTntExplosionSettings());
        }

        private FallingBlockSimulator.TntExplosionSettings CreateTntExplosionSettings()
        {
            return new FallingBlockSimulator.TntExplosionSettings
            {
                FuseSeconds = tntFuseSeconds,
                DestructionRadius = tntDestructionRadius,
                MaxDestroyedBlocks = tntMaxDestroyedBlocks,
                DestroyFluids = tntDestroyFluids,
                DestroyIndestructibleBlocks = tntDestroyIndestructibleBlocks,
                DropDestroyedBlocks = tntDropDestroyedBlocks,
                PrimeNearbyTnt = tntPrimeNearbyTnt,
                ChainedFuseSeconds = tntChainedFuseSeconds
            };
        }

        private int GetPlacementRotationY(BlockData block)
        {
            if (block == null || !block.RotateOnPlace || Camera.main == null)
                return 0;

            Vector3 forward = Camera.main.transform.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.0001f)
                return 0;

            forward.Normalize();

            if (Mathf.Abs(forward.x) > Mathf.Abs(forward.z))
                return forward.x >= 0f ? 90 : 270;

            return forward.z >= 0f ? 0 : 180;
        }

        private static bool CanPlaceBlockAt(Vector3 worldPosition)
        {
            Vector3Int blockPosition = ChunkUtility.SnapPosition(worldPosition);

            if (FallingBlockSimulator.IsWorldPositionBlockedByFallingEntity(blockPosition))
                return false;

            int existingBlockId = ChunkUtility.GetBlockAtPosition(blockPosition);
            BlockData existingBlock = AssetsContainer.GetBlock(existingBlockId);
            if (existingBlockId != Chunk.BLOCK_AIR && (existingBlock == null || !existingBlock.IsFluid))
                return false;

            Vector3 center = (Vector3)blockPosition + Vector3.one * 0.5f;
            return !Physics.CheckBox(
                center,
                halfExtents,
                Quaternion.identity,
                LayerMask.GetMask("Player"),
                QueryTriggerInteraction.Ignore);
        }

        private void UpdateHighlightBlock()
        {
            Transform cam = Camera.main.transform;

            Vector3 origin = cam.position;
            Vector3 dir = cam.forward.normalized;

            Vector3Int current = new Vector3Int(
                Mathf.FloorToInt(origin.x),
                Mathf.FloorToInt(origin.y),
                Mathf.FloorToInt(origin.z)
            );

            int stepX = dir.x >= 0 ? 1 : -1;
            int stepY = dir.y >= 0 ? 1 : -1;
            int stepZ = dir.z >= 0 ? 1 : -1;

            float tDeltaX = dir.x == 0 ? float.PositiveInfinity : Mathf.Abs(1f / dir.x);
            float tDeltaY = dir.y == 0 ? float.PositiveInfinity : Mathf.Abs(1f / dir.y);
            float tDeltaZ = dir.z == 0 ? float.PositiveInfinity : Mathf.Abs(1f / dir.z);

            float nextBoundaryX = stepX > 0 ? current.x + 1f : current.x;
            float nextBoundaryY = stepY > 0 ? current.y + 1f : current.y;
            float nextBoundaryZ = stepZ > 0 ? current.z + 1f : current.z;

            float tMaxX = dir.x == 0 ? float.PositiveInfinity : Mathf.Abs((nextBoundaryX - origin.x) / dir.x);
            float tMaxY = dir.y == 0 ? float.PositiveInfinity : Mathf.Abs((nextBoundaryY - origin.y) / dir.y);
            float tMaxZ = dir.z == 0 ? float.PositiveInfinity : Mathf.Abs((nextBoundaryZ - origin.z) / dir.z);

            Vector3Int previous = current;
            Vector3Int hitNormal = Vector3Int.zero;

            float traveled = 0f;

            while (traveled <= maxInteractionDistance)
            {
                int blockID = ChunkUtility.GetBlockAtPosition(current);
                BlockData block = AssetsContainer.GetBlock(blockID);

                if (blockID != Chunk.BLOCK_AIR && block != null && !block.IsFluid)
                {
                    highlightPosition = current;
                    placeBlockPosition = current + hitNormal;

                    highlightBlock.transform.position = (Vector3)current + Vector3.one * 0.5f;

                    highlightBlock.SetActive(highlightBlockActive);
                    blockInRange = true;

                    if (Input.GetKeyDown(KeyCode.E))
                    {
                        Chunk chunk = ChunkUtility.GetChunkAtPosition(current);
                        Debug.Log("Highlighted block: " + AssetsContainer.GetBlock(blockID).name);
                        Debug.Log("In Chunk at position " + chunk.Coordinate + " AirOnly:" + chunk.IsAirOnly
                            + " HighestGroundlevel:" + chunk.HighestGroundLevel + " LowestGroundlevel:"
                            + chunk.LowestGroundLevel + " IsTop:" + chunk.IsTop
                            + " IsGenerated:" + chunk.IsGenerated);
                    }

                    return;
                }

                previous = current;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ)
                    {
                        current.x += stepX;
                        traveled = tMaxX;
                        tMaxX += tDeltaX;
                        hitNormal = new Vector3Int(-stepX, 0, 0);
                    }
                    else
                    {
                        current.z += stepZ;
                        traveled = tMaxZ;
                        tMaxZ += tDeltaZ;
                        hitNormal = new Vector3Int(0, 0, -stepZ);
                    }
                }
                else
                {
                    if (tMaxY < tMaxZ)
                    {
                        current.y += stepY;
                        traveled = tMaxY;
                        tMaxY += tDeltaY;
                        hitNormal = new Vector3Int(0, -stepY, 0);
                    }
                    else
                    {
                        current.z += stepZ;
                        traveled = tMaxZ;
                        tMaxZ += tDeltaZ;
                        hitNormal = new Vector3Int(0, 0, -stepZ);
                    }
                }
            }

            highlightBlock.SetActive(false);
            blockInRange = false;
        }

        private void OnSwitchSlot(Slot slot)
        {
            if (slot?.Item?.ItemData is BlockItemData)
            {
                isActive = true;
                selectedBlockItemSlot = slot;
                highlightBlockActive = true;
            }
            else
            {
                selectedBlockItemSlot = null;
                highlightBlockActive = false;
                //Deactivate();
            }
        }
        private void OnSwitchGameMode(GameMode gameMode)
        {
            if (gameMode == GameMode.Spectator)
            {
                Deactivate();
            }
            else
            {
                isActive = true;
            }
        }

        private void Deactivate()
        {
            isActive = false;
            breakBlockTimer = 0;
            placeBlockTimer = 0;

            highlightBlock.SetActive(false);
        }
    }
}
