using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Unity.Profiling;
using BenScr.CubeDash;

namespace BenScr.MinecraftClone
{
    public struct ByteVector3
    {
        public byte X;
        public byte Y;
        public byte Z;

        public ByteVector3(byte x, byte y, byte z)
        {
            this.X = x;
            this.Y = y;
            this.Z = z;
        }

        public static explicit operator ByteVector3(Vector3 vector3)
        {
            return new ByteVector3((byte)vector3.x, (byte)vector3.y, (byte)vector3.z);
        }

        public bool Equals(ByteVector3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is ByteVector3 other && Equals(other);

        public override int GetHashCode()
            => X | (Y << 8) | (Z << 16);
    }

    [Serializable]
    public class DamagedBlock
    {
        public int Health;

        [System.NonSerialized]
        public GameObject DamageStage;

        public  DamagedBlock(int health, GameObject damageStage)
        {
            this.Health = health;
            this.DamageStage = damageStage;
        }
    }

    [Serializable]
    public class Chunk
    {
        private const int MaxPooledChunkViews = 128;
        private static readonly DynamicObjectPool<GameObject> ChunkViewPool = new();
        private static readonly ProfilerMarker BlockDataCompletionMarker = new("VoxelBuilder.Chunk.CompleteBlockData");
        private static readonly ProfilerMarker MeshSnapshotMarker = new("VoxelBuilder.Chunk.BuildMeshSnapshot");
        private static readonly ProfilerMarker MeshApplicationMarker = new("VoxelBuilder.Chunk.ApplyMesh");
        private static readonly ProfilerMarker ColliderCookingMarker = new("VoxelBuilder.Chunk.CookCollider");
        [System.NonSerialized]
        public const int CHUNK_SIZE = 32;
        [System.NonSerialized]
        public const int CHUNK_HEIGHT = 32;

        // Block ids must stay aligned with AssetsContainer.Blocks in the Game scene.
        [System.NonSerialized]
        public const int BLOCK_AIR = 0;
        [System.NonSerialized]
        public const int BLOCK_DIRT = 1;
        [System.NonSerialized]
        public const int BLOCK_GRASS = 2;
        [System.NonSerialized]
        public const int BLOCK_STONE = 3;
        [System.NonSerialized]
        public const int BLOCK_WOOD = 4;
        [System.NonSerialized]
        public const int BLOCK_LEAVES = 5;
        [System.NonSerialized]
        public const int BLOCK_MOSSY_BRICK_STONE = 6;
        [System.NonSerialized]
        public const int BLOCK_GLASS = 7;
        [System.NonSerialized]
        public const int BLOCK_SNOW_GRASS = 8;
        [System.NonSerialized]
        public const int BLOCK_AMETHYST = 9;
        [System.NonSerialized]
        public const int BLOCK_WOOD_BLANKET = 10;
        [System.NonSerialized]
        public const int BLOCK_TUFF = 11;
        [System.NonSerialized]
        public const int BLOCK_IRON = 12;
        [System.NonSerialized]
        public const int BLOCK_DIAMOND = 13;
        [System.NonSerialized]
        public const int BLOCK_WATER = 14;
        [System.NonSerialized]
        public const int BLOCK_CHEST = 15;
        [System.NonSerialized]
        public const int BLOCK_SAND = 16;
        [System.NonSerialized]
        public const int BLOCK_SANDSTONE = 17;
        [System.NonSerialized]
        public const int BLOCK_COARSE_DIRT = 18;
        [System.NonSerialized]
        public const int BLOCK_COBBLESTONE = 19;
        [System.NonSerialized]
        public const int BLOCK_BRICKS = 31;
        [System.NonSerialized]
        public const int BLOCK_BEDROCK = 21;
        [System.NonSerialized]
        public const int BLOCK_SNOW = 22;
        [System.NonSerialized]
        public const int BLOCK_LAVA = 58;
        [System.NonSerialized]
        public const int BLOCK_MAGMA = 59;
        [System.NonSerialized]
        public const int BLOCK_OBSIDIAN = 61;
        [System.NonSerialized]
        public const int BLOCK_QUARTZ = 26;
        [System.NonSerialized]
        public const int BLOCK_SLIME = 27;
        [System.NonSerialized]
        public const int BLOCK_TNT = 70;
        [System.NonSerialized]
        public const int BLOCK_ICE = 29;
        [System.NonSerialized]
        public const int BLOCK_CACTUS = 32;
        [System.NonSerialized]
        public const int BLOCK_CHERRY_LEAVES = 33;
        [System.NonSerialized]
        public const int BLOCK_CHERRY_LOG = 34;
        [System.NonSerialized]
        public const int BLOCK_COBWEB = 37;
        [System.NonSerialized]
        public const int BLOCK_CRACKED_DEEPSLATE_BRICKS = 38;
        [System.NonSerialized]
        public const int BLOCK_DEEPSLATE_DIAMOND_ORE = 40;
        [System.NonSerialized]
        public const int BLOCK_DEEPSLATE_EMERALD_ORE = 41;
        [System.NonSerialized]
        public const int BLOCK_DEEPSLATE_GOLD_ORE = 42;
        [System.NonSerialized]
        public const int BLOCK_DEEPSLATE_IRON_ORE = 43;
        [System.NonSerialized]
        public const int BLOCK_EMERALD_ORE = 44;
        [System.NonSerialized]
        public const int BLOCK_GLOWSTONE = 49;
        [System.NonSerialized]
        public const int BLOCK_GOLD_ORE = 51;
        [System.NonSerialized]
        public const int BLOCK_GRANITE = 52;
        [System.NonSerialized]
        public const int BLOCK_JUNGLE_LEAVES = 54;
        [System.NonSerialized]
        public const int BLOCK_JUNGLE_LOG = 55;
        [System.NonSerialized]
        public const int BLOCK_LAPIS_ORE = 57;
        [System.NonSerialized]
        public const int BLOCK_MELON = 60;
        [System.NonSerialized]
        public const int BLOCK_RED_SAND = 62;
        [System.NonSerialized]
        public const int BLOCK_RED_SANDSTONE = 63;
        [System.NonSerialized]
        public const int BLOCK_ROOTED_DIRT = 64;
        [System.NonSerialized]
        public const int BLOCK_SMOOTH_BASALT = 66;
        [System.NonSerialized]
        public const int BLOCK_SPRUCE_LOG = 68;
        [System.NonSerialized]
        public const int BLOCK_TUFF_BRICKS = 72;
        [System.NonSerialized]
        public const int BLOCK_GRAVEL = 73;

        public VoxelBuffer<byte> Blocks;

        [System.NonSerialized]
        public bool IsGenerated;
        public bool IsAirOnly = true;

        public short LowestGroundLevel = short.MaxValue;
        public short HighestGroundLevel = short.MinValue;
        public bool IsTop => (HighestGroundLevel - Position.y) < CHUNK_HEIGHT;
        public bool RequireChunkBelow => LowestGroundLevel < Position.y;

        public bool IsBottom => (LowestGroundLevel - Position.y) <= 0;
        public bool HasBlockData => Blocks != null;
        public bool IsBlockDataGenerationScheduled => isBlockDataGenerationScheduled;
        public bool IsBlockDataGenerationComplete => isBlockDataGenerationScheduled && pendingBlockDataHandle.IsCompleted;

        [System.NonSerialized]
        public GameObject GameObject;
        [System.NonSerialized]
        public MeshRenderer MeshRenderer;
        [System.NonSerialized]
        public MeshFilter MeshFilter;
        [System.NonSerialized]
        public MeshCollider MeshCollider;

        [System.NonSerialized]
        public GameObject FluidGameObject;
        [System.NonSerialized]
        public MeshRenderer FluidRenderer;
        [System.NonSerialized]
        public MeshFilter FluidFilter;
        [System.NonSerialized]
        public GameObject LavaFluidGameObject;
        [System.NonSerialized]
        public MeshRenderer LavaFluidRenderer;
        [System.NonSerialized]
        public MeshFilter LavaFluidFilter;

        [System.NonSerialized]
        public GameObject TransparentGameObject;
        [System.NonSerialized]
        public MeshRenderer TransparentRenderer;
        [System.NonSerialized]
        public MeshFilter TransparentFilter;
        [System.NonSerialized]
        public MeshCollider TransparentMeshCollider;

        public Vector3Int Coordinate;
        public Vector3 Position;

        public bool HasChanged;
        public Dictionary<ByteVector3, DamagedBlock> DamagedBlocks = new();
        public List<DroppedItemData> DroppedItems = new();
        public List<PlacedBlockData> PlacedBlocks = new();
        private int meshRequestVersion;
        private int meshRequestsInFlight;
        private bool interactiveMeshRequestInFlight;
        private bool interactiveMeshUpdatePending;
        private bool meshRebuildPending;
        private MeshRequestPriority pendingMeshPriority = MeshRequestPriority.Background;
        private int nonAirBlockCount;
        private int blockRevision = 1;
        private VoxelBuffer<Color32> blockTints;
        private bool blockTintsNeedLazyRebuild;
        private bool skylightInvalidationPending;
        private bool blockLightInvalidationPending;
        private Mesh solidMesh;
        private Mesh fluidMesh;
        private Mesh lavaFluidMesh;
        private Mesh transparentMesh;
        private GameObject chunkViewPoolKey;
        private MaterialPropertyBlock voxelLightingPropertyBlock;
        private byte[] voxelLighting;
        private NativeArray<int> pendingHeightMap;
        private NativeArray<byte> pendingBiomeMap;
        private NativeArray<byte> pendingSurfaceBiomeMap;
        private NativeArray<byte> pendingBiomeBlendMap;
        private NativeArray<byte> pendingDesertEdgeMap;
        private NativeArray<byte> pendingRiverMap;
        private NativeArray<int> pendingRiverSurfaceMap;
        private NativeArray<byte> pendingGeneratedBlocks;
        private NativeArray<uint> pendingGeneratedFluidFrontierMasks;
        private uint[] generatedFluidFrontierMasks;
        private bool generatedFluidFrontierMasksValid;
        private JobHandle pendingBlockDataHandle;
        private bool isBlockDataGenerationScheduled;
        private SharedTerrainColumnGeneration pendingTerrainColumnGeneration;
        private static readonly Dictionary<TerrainColumnGenerationKey, SharedTerrainColumnGeneration> SharedTerrainColumnGenerations = new();
        private static readonly Dictionary<Vector2Int, SkylightColumnInfo> SkylightColumnCache = new();
        private static int skylightColumnCacheWorldEpoch = int.MinValue;
        private static readonly Color32 WhiteBlockTint = new Color32(255, 255, 255, 255);
        private static readonly Color32 DefaultGrassTint = new Color32(150, 220, 82, 255);
        private static readonly Color32 ForestGrassTint = new Color32(92, 176, 68, 255);
        private static readonly Color32 JungleGrassTint = new Color32(48, 126, 52, 255);
        private static readonly Color32 DesertEdgeGrassTint = new Color32(215, 207, 95, 255);
        private static readonly Color32 SnowGrassTint = new Color32(172, 194, 134, 255);
        private static readonly Color32 RiverGrassTint = new Color32(100, 210, 94, 255);
        private static readonly Color32 LushCaveGrassTint = new Color32(72, 196, 86, 255);
        private static readonly Color32 DefaultLeavesTint = new Color32(88, 156, 72, 255);
        private static readonly Color32 ForestLeavesTint = new Color32(46, 120, 54, 255);
        private static readonly Color32 JungleLeavesTint = new Color32(28, 92, 40, 255);
        private static readonly Color32 DesertEdgeLeavesTint = new Color32(154, 164, 78, 255);
        private static readonly Color32 SnowLeavesTint = new Color32(112, 136, 96, 255);
        private static readonly Color32 RiverLeavesTint = new Color32(62, 148, 76, 255);
        private static readonly Color32 LushCaveLeavesTint = new Color32(42, 146, 64, 255);
        private static BlockData[] cachedTintedLeavesDefinitions;
        private static bool[] cachedTintedLeavesFlags;
        private static BlockData[] cachedSkylightDefinitions;
        private static bool[] cachedSkylightOcclusionFlags;
        private static BlockData[] cachedBlockLightDefinitions;
        private static byte[] cachedBlockLightEmissionLevels;
        private static readonly Bounds ChunkMeshBounds = new Bounds(
            new Vector3(CHUNK_SIZE * 0.5f, CHUNK_HEIGHT * 0.5f, CHUNK_SIZE * 0.5f),
            new Vector3(CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE));

        internal int BlockRevision => blockRevision;

        internal bool ConsumeSkylightInvalidationPending()
        {
            bool pending = skylightInvalidationPending;
            skylightInvalidationPending = false;
            return pending;
        }

        internal bool ConsumeBlockLightInvalidationPending()
        {
            bool pending = blockLightInvalidationPending;
            blockLightInvalidationPending = false;
            return pending;
        }

        internal bool TryGetGeneratedFluidFrontierMasks(out uint[] masks)
        {
            bool isValid = blockRevision == 1 && generatedFluidFrontierMasksValid;
            masks = isValid ? generatedFluidFrontierMasks : null;
            return isValid;
        }

        internal bool TryGetVoxelLighting(Vector3Int localPosition, out byte packedLighting)
        {
            packedLighting = 0;
            if (voxelLighting == null ||
                voxelLighting.Length != CHUNK_SIZE * CHUNK_HEIGHT * CHUNK_SIZE ||
                !ChunkUtility.IsInsideChunk(localPosition))
            {
                return false;
            }

            packedLighting = voxelLighting[
                localPosition.x +
                localPosition.y * CHUNK_SIZE +
                localPosition.z * CHUNK_SIZE * CHUNK_HEIGHT];
            return true;
        }

        public Chunk(int x, int y, int z)
        {
            Coordinate = new Vector3Int(x, y, z);
            Position = new Vector3(x * CHUNK_SIZE, y * CHUNK_HEIGHT, z * CHUNK_SIZE);
        }

        public void AddMeshCollider()
        {
            if (!IsGenerated || IsAirOnly || GameObject == null || TransparentGameObject == null)
                return;

            using (ColliderCookingMarker.Auto())
            {
                if (MeshCollider == null)
                    MeshCollider = GameObject.AddComponent<MeshCollider>();

                if (TransparentMeshCollider == null)
                    TransparentMeshCollider = TransparentGameObject.AddComponent<MeshCollider>();

                SetMeshCollidersEnabled(true);
            }
        }

        public void SetMeshCollidersEnabled(bool enabled)
        {
            if (MeshCollider != null)
            {
                MeshCollider.enabled = enabled;
                if (enabled)
                {
                    MeshCollider.sharedMesh = null;
                    MeshCollider.sharedMesh = solidMesh;
                }
            }

            if (TransparentMeshCollider != null)
            {
                TransparentMeshCollider.enabled = enabled;
                if (enabled)
                {
                    TransparentMeshCollider.sharedMesh = null;
                    TransparentMeshCollider.sharedMesh = transparentMesh;
                }
            }
        }

        public BlockData GetBlock(Vector3 localPosition)
        {
            Vector3Int blockPosition = new Vector3Int(
                      Mathf.FloorToInt(localPosition.x),
                      Mathf.FloorToInt(localPosition.y),
                      Mathf.FloorToInt(localPosition.z)

              );

            return AssetsContainer.GetBlock(Blocks[blockPosition.x, blockPosition.y, blockPosition.z]);
        }

        public void SetBlock(
            Vector3 localPosition,
            int blockId,
            bool update = true,
            bool prioritizeMesh = false)
        {
            Vector3Int blockPosition = new Vector3Int(
                        Mathf.FloorToInt(localPosition.x),
                        Mathf.FloorToInt(localPosition.y),
                        Mathf.FloorToInt(localPosition.z)
                );

            if (ChunkUtility.IsInsideChunk(blockPosition))
            {
                if (!SetBlockRaw(blockPosition, blockId))
                    return;

                if (update)
                {
                    TerrainGenerator.MarkChunkMeshDirty(this, blockPosition, prioritizeMesh);
                }
            }
        }

        public bool SetBlockRaw(Vector3Int blockPosition, int blockId)
        {
            if (Blocks == null || !ChunkUtility.IsInsideChunk(blockPosition))
                return false;

            byte previousBlockId = Blocks[blockPosition.x, blockPosition.y, blockPosition.z];
            byte nextBlockId = (byte)blockId;
            if (previousBlockId == nextBlockId)
                return false;

            bool[] skylightOcclusionFlags = GetSkylightOcclusionFlags();
            bool previousOccludes = (uint)previousBlockId >= (uint)skylightOcclusionFlags.Length ||
                                    skylightOcclusionFlags[previousBlockId];
            bool nextOccludes = (uint)nextBlockId >= (uint)skylightOcclusionFlags.Length ||
                                skylightOcclusionFlags[nextBlockId];
            skylightInvalidationPending |= previousOccludes != nextOccludes;

            byte[] emissionLevels = GetBlockLightEmissionLevels();
            byte previousEmission = (uint)previousBlockId < (uint)emissionLevels.Length
                ? emissionLevels[previousBlockId]
                : (byte)0;
            byte nextEmission = (uint)nextBlockId < (uint)emissionLevels.Length
                ? emissionLevels[nextBlockId]
                : (byte)0;
            blockLightInvalidationPending |= previousOccludes != nextOccludes ||
                                             previousEmission != nextEmission;

            Blocks[blockPosition.x, blockPosition.y, blockPosition.z] = nextBlockId;
            SetBlockTint(blockPosition, nextBlockId);
            UpdateNonAirBlockCount(previousBlockId, nextBlockId);
            unchecked
            {
                blockRevision++;
            }
            HasChanged = true;
            return true;
        }

        public void RecalculateBlockStats()
        {
            nonAirBlockCount = 0;

            if (Blocks == null)
            {
                IsAirOnly = true;
                return;
            }

            byte[] blockData = Blocks.Data;
            for (int i = 0; i < blockData.Length; i++)
            {
                if (blockData[i] != BLOCK_AIR)
                    nonAirBlockCount++;
            }

            IsAirOnly = nonAirBlockCount == 0;
        }

        internal int NonAirBlockCount => nonAirBlockCount;

        internal bool TryRestoreBlockStats(int savedNonAirBlockCount)
        {
            if (Blocks == null ||
                savedNonAirBlockCount < 0 ||
                savedNonAirBlockCount > Blocks.Data.Length)
            {
                return false;
            }

            nonAirBlockCount = savedNonAirBlockCount;
            IsAirOnly = nonAirBlockCount == 0;
            return true;
        }

        private void UpdateNonAirBlockCount(byte previousBlockId, byte nextBlockId)
        {
            if (previousBlockId != BLOCK_AIR)
                nonAirBlockCount = Mathf.Max(0, nonAirBlockCount - 1);

            if (nextBlockId != BLOCK_AIR)
                nonAirBlockCount++;

            IsAirOnly = nonAirBlockCount == 0;
        }

        public void Generate(MeshRequestPriority meshPriority = MeshRequestPriority.Background)
        {
            if (Blocks == null)
                return;

            bool wasGenerated = IsGenerated;
            IsGenerated = true;

            if (IsAirOnly)
            {
                ClearMeshes();
                RequestMeshData(meshPriority);
            }
            else
            {
                bool createdView = EnsureView();
                if (createdView && wasGenerated)
                    PlacedBlockManager.RefreshChunk(this);

                RequestMeshData(meshPriority);
            }

            if (!wasGenerated)
            {
                // A newly available upper chunk can turn a formerly unknown (dark)
                // streaming gap into a known sky path for chunks below it.
                TerrainGenerator.MarkChunkSkylightDependentsDirty(this);
                TerrainGenerator.MarkChunkBlockLightDependentsDirty(this);
                FluidSimulator.QueueChunkFluids(this);
                SaveController.RestoreLoadedFallingBlockChecks(Coordinate);
                SaveController.RestoreLoadedFallingBlocks();
                PlacedBlockManager.RefreshChunk(this);
            }
        }

        public void Prepare()
        {
            Blocks = null;
            generatedFluidFrontierMasks = null;
            generatedFluidFrontierMasksValid = false;
            blockTints = null;
            blockTintsNeedLazyRebuild = false;
            voxelLighting = null;
            IsGenerated = false;
            IsAirOnly = true;
        }

        public void Prepare_Load()
        {
            generatedFluidFrontierMasks = null;
            generatedFluidFrontierMasksValid = false;
            blockTints = null;
            blockTintsNeedLazyRebuild = true;
            voxelLighting = null;
            IsGenerated = false;
            RecordSkylightColumnInfo();
        }

        private bool EnsureView()
        {
            if (GameObject != null)
                return false;

            chunkViewPoolKey = TerrainGenerator.Instance.ChunkPrefab;
            GameObject = ChunkViewPool.Get(
                chunkViewPoolKey,
                chunkViewPoolKey,
                Position,
                Quaternion.identity);
            GameObject.name = $"Chunk_{Coordinate.x}_{Coordinate.y}_{Coordinate.z}";
            MeshFilter = GameObject.GetComponent<MeshFilter>();
            MeshRenderer = GameObject.GetComponent<MeshRenderer>();
            solidMesh = MeshFilter.sharedMesh;
            MeshCollider = GameObject.GetComponent<MeshCollider>();

            MeshRenderer.sharedMaterial = AssetsContainer.Instance.BlockMaterial;
            EnableVoxelLighting(MeshRenderer);

            GameObject.transform.position = Position;
            Position = GameObject.transform.position;

            FluidGameObject = GameObject.transform.GetChild(0).gameObject;
            FluidRenderer = FluidGameObject.GetComponent<MeshRenderer>();
            FluidFilter = FluidGameObject.GetComponent<MeshFilter>();
            fluidMesh = FluidFilter.sharedMesh;
            FluidRenderer.sharedMaterial = AssetsContainer.Instance.FluidMaterial;

            TransparentGameObject = GameObject.transform.GetChild(1).gameObject;
            TransparentRenderer = TransparentGameObject.GetComponent<MeshRenderer>();
            TransparentFilter = TransparentGameObject.GetComponent<MeshFilter>();
            transparentMesh = TransparentFilter.sharedMesh;
            TransparentMeshCollider = TransparentGameObject.GetComponent<MeshCollider>();
            TransparentRenderer.sharedMaterial = AssetsContainer.Instance.TransparentMaterial;
            EnableVoxelLighting(TransparentRenderer);

            Transform lavaTransform = GameObject.transform.Find("LavaFluid");
            if (lavaTransform != null)
            {
                LavaFluidGameObject = lavaTransform.gameObject;
                LavaFluidFilter = LavaFluidGameObject.GetComponent<MeshFilter>();
                LavaFluidRenderer = LavaFluidGameObject.GetComponent<MeshRenderer>();
                lavaFluidMesh = LavaFluidFilter != null ? LavaFluidFilter.sharedMesh : null;
                if (LavaFluidRenderer != null)
                    LavaFluidRenderer.sharedMaterial = AssetsContainer.Instance.GetLavaFluidMaterial();
            }

            ClearMeshes();
            return true;
        }

        internal void ReleaseViewToPool()
        {
            meshRequestVersion++;
            meshRequestsInFlight = 0;
            interactiveMeshRequestInFlight = false;
            interactiveMeshUpdatePending = false;
            meshRebuildPending = false;
            pendingMeshPriority = MeshRequestPriority.Background;

            if (GameObject == null)
                return;

            SetMeshCollidersEnabled(false);

            if (PlacedBlocks != null)
            {
                for (int i = 0; i < PlacedBlocks.Count; i++)
                {
                    PlacedBlockData placedBlock = PlacedBlocks[i];
                    if (placedBlock?.View == null)
                        continue;

                    UnityEngine.Object.Destroy(placedBlock.View);
                    placedBlock.View = null;
                }
            }

            if (DamagedBlocks != null)
            {
                foreach (DamagedBlock damagedBlock in DamagedBlocks.Values)
                {
                    if (damagedBlock?.DamageStage != null)
                        UnityEngine.Object.Destroy(damagedBlock.DamageStage);
                }

                DamagedBlocks.Clear();
            }

            GameObject view = GameObject;
            GameObject key = chunkViewPoolKey;
            bool retainedView;
            if (key != null)
                retainedView = ChunkViewPool.Release(key, view, MaxPooledChunkViews);
            else
            {
                PersistentObjectPool.Store(view, "Chunk Views");
                retainedView = true;
            }

            if (!retainedView)
                DestroyRuntimeMeshesForReleasedView();

            GameObject = null;
            MeshFilter = null;
            MeshRenderer = null;
            MeshCollider = null;
            FluidGameObject = null;
            FluidRenderer = null;
            FluidFilter = null;
            LavaFluidGameObject = null;
            LavaFluidRenderer = null;
            LavaFluidFilter = null;
            TransparentGameObject = null;
            TransparentRenderer = null;
            TransparentFilter = null;
            TransparentMeshCollider = null;
            solidMesh = null;
            fluidMesh = null;
            lavaFluidMesh = null;
            transparentMesh = null;
            chunkViewPoolKey = null;
        }

        private void DestroyRuntimeMeshesForReleasedView()
        {
            if (MeshFilter != null)
                MeshFilter.sharedMesh = null;
            if (FluidFilter != null)
                FluidFilter.sharedMesh = null;
            if (LavaFluidFilter != null)
                LavaFluidFilter.sharedMesh = null;
            if (TransparentFilter != null)
                TransparentFilter.sharedMesh = null;
            if (MeshCollider != null)
                MeshCollider.sharedMesh = null;
            if (TransparentMeshCollider != null)
                TransparentMeshCollider.sharedMesh = null;

            DestroyRuntimeMesh(solidMesh);
            DestroyRuntimeMesh(fluidMesh);
            DestroyRuntimeMesh(lavaFluidMesh);
            DestroyRuntimeMesh(transparentMesh);
        }

        private static void DestroyRuntimeMesh(Mesh mesh)
        {
            if (mesh == null ||
                string.IsNullOrEmpty(mesh.name) ||
                !mesh.name.StartsWith("Chunk_", StringComparison.Ordinal))
            {
                return;
            }

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(mesh);
            else
                UnityEngine.Object.DestroyImmediate(mesh);
        }

        internal void ReleaseRuntimeDataForStreaming()
        {
            ReleaseViewToPool();

            Blocks = null;
            blockTints = null;
            voxelLighting = null;
            generatedFluidFrontierMasks = null;
            generatedFluidFrontierMasksValid = false;
            blockTintsNeedLazyRebuild = false;
            skylightInvalidationPending = false;
            blockLightInvalidationPending = false;
            IsGenerated = false;

            DamagedBlocks?.Clear();
            DroppedItems?.Clear();
            PlacedBlocks?.Clear();
        }

        internal static void ReleaseColumnCaches(Vector3Int chunkCoordinate)
        {
            EnsureSkylightColumnCacheEpoch();
            SkylightColumnCache.Remove(new Vector2Int(chunkCoordinate.x, chunkCoordinate.z));
        }

        private void PrepareLavaFluidRenderer()
        {
            Transform lavaTransform = GameObject.transform.Find("LavaFluid");
            if (lavaTransform == null)
            {
                LavaFluidGameObject = new GameObject("LavaFluid");
                LavaFluidGameObject.transform.SetParent(GameObject.transform, false);
            }
            else
            {
                LavaFluidGameObject = lavaTransform.gameObject;
            }

            if (!LavaFluidGameObject.TryGetComponent(out LavaFluidFilter))
                LavaFluidFilter = LavaFluidGameObject.AddComponent<MeshFilter>();

            if (!LavaFluidGameObject.TryGetComponent(out LavaFluidRenderer))
                LavaFluidRenderer = LavaFluidGameObject.AddComponent<MeshRenderer>();

            LavaFluidRenderer.sharedMaterial = AssetsContainer.Instance.GetLavaFluidMaterial();
        }

        private void EnableVoxelLighting(MeshRenderer renderer)
        {
            if (renderer == null)
                return;

            voxelLightingPropertyBlock ??= new MaterialPropertyBlock();
            voxelLightingPropertyBlock.Clear();
            renderer.GetPropertyBlock(voxelLightingPropertyBlock);
            voxelLightingPropertyBlock.SetFloat("_UseVoxelLighting", 1f);
            renderer.SetPropertyBlock(voxelLightingPropertyBlock);
        }

        public void ScheduleBlockDataGeneration()
        {
            if (Blocks != null || isBlockDataGenerationScheduled)
                return;

            DisposePendingBlockDataGeneration(completeJob: false);

            LowestGroundLevel = short.MaxValue;
            HighestGroundLevel = short.MinValue;
            IsAirOnly = true;
            nonAirBlockCount = 0;

            JobHandle heightHandle = default;
            bool heightJobScheduled = false;
            bool blockJobScheduled = false;

            try
            {
            pendingGeneratedBlocks = new NativeArray<byte>(
                CHUNK_SIZE * CHUNK_HEIGHT * CHUNK_SIZE,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            pendingGeneratedFluidFrontierMasks = new NativeArray<uint>(
                CHUNK_SIZE * CHUNK_SIZE,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);

            NoiseSettings settings = NoiseSettings.Instance;
            settings.GetNoiseLayers(out var continentLayer, out var mountainLayer, out var detailLayer, out var ridgeLayer);
            settings.GetBiomeLayers(out var temperatureLayer, out var moistureLayer, out var erosionLayer);
            settings.GetRedDesertLayer(out var redDesertLayer);
            settings.GetTerrainVarietyLayers(out var landformLayer, out var cliffLayer, out _);
            settings.GetHydrologyLayers(out var riverLayer);

            var terrainColumnKey = new TerrainColumnGenerationKey(
                Coordinate.x,
                Coordinate.z,
                settings.Seed,
                TerrainGenerator.CurrentWorldEpoch);
            if (TryAcquireSharedTerrainColumnGeneration(terrainColumnKey, out SharedTerrainColumnGeneration sharedColumn))
            {
                AttachSharedTerrainColumnGeneration(sharedColumn);
                heightHandle = sharedColumn.HeightHandle;
                heightJobScheduled = true;
            }
            else
            {
            pendingHeightMap = new NativeArray<int>(CHUNK_SIZE * CHUNK_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            pendingBiomeMap = new NativeArray<byte>(CHUNK_SIZE * CHUNK_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            pendingSurfaceBiomeMap = new NativeArray<byte>(CHUNK_SIZE * CHUNK_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            pendingBiomeBlendMap = new NativeArray<byte>(CHUNK_SIZE * CHUNK_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            pendingDesertEdgeMap = new NativeArray<byte>(CHUNK_SIZE * CHUNK_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            pendingRiverMap = new NativeArray<byte>(CHUNK_SIZE * CHUNK_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            pendingRiverSurfaceMap = new NativeArray<int>(CHUNK_SIZE * CHUNK_SIZE, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            GenerateTerrainHeightMapJob heightJob = new GenerateTerrainHeightMapJob
            {
                HeightMap = pendingHeightMap,
                BiomeMap = pendingBiomeMap,
                SurfaceBiomeMap = pendingSurfaceBiomeMap,
                BiomeBlendMap = pendingBiomeBlendMap,
                DesertEdgeMap = pendingDesertEdgeMap,
                RiverMap = pendingRiverMap,
                RiverSurfaceMap = pendingRiverSurfaceMap,
                ChunkSize = CHUNK_SIZE,
                ChunkOrigin = new float2(Coordinate.x * CHUNK_SIZE, Coordinate.z * CHUNK_SIZE),
                ContinentLayer = continentLayer,
                MountainLayer = mountainLayer,
                DetailLayer = detailLayer,
                RidgeLayer = ridgeLayer,
                TemperatureLayer = temperatureLayer,
                MoistureLayer = moistureLayer,
                RedDesertLayer = redDesertLayer,
                ErosionLayer = erosionLayer,
                LandformLayer = landformLayer,
                CliffLayer = cliffLayer,
                RiverLayer = riverLayer,
                FlatlandsHeightMultiplier = settings.FlatlandsHeightMultiplier,
                MountainHeightMultiplier = settings.MountainHeightMultiplier,
                MountainBlendStart = settings.MountainBlendStart,
                MountainBlendSharpness = settings.MountainBlendSharpness,
                NoiseHeight = settings.NoiseHeight,
                GroundOffset = settings.GroundOffset,
                WaterLevel = settings.WaterLevel,
                BedrockLevel = settings.BedrockLevel != 0 ? settings.BedrockLevel : -256,
                BedrockThickness = Mathf.Max(1, settings.BedrockThickness),
                Seed = settings.Seed,
                OceanDepth = settings.OceanDepth,
                MinLandAboveWater = settings.MinLandAboveWater,
                OceanThreshold = settings.OceanThreshold,
                BeachThreshold = settings.BeachThreshold,
                PlainsFlattening = settings.PlainsFlattening,
                LandBias = settings.LandBias,
                BiomeNoiseOctaves = settings.BiomeNoiseOctaves > 0 ? Mathf.Clamp(settings.BiomeNoiseOctaves, 1, 4) : 3,
                BiomeContrast = settings.BiomeContrast > 0f ? Mathf.Clamp(settings.BiomeContrast, 0.5f, 2.5f) : 1.15f,
                BiomeTransitionWidth = settings.BiomeTransitionWidth > 0f ? Mathf.Clamp(settings.BiomeTransitionWidth, 0.02f, 0.30f) : 0.08f,
                LandformContrast = settings.LandformContrast > 0f ? Mathf.Clamp(settings.LandformContrast, 0.5f, 2.5f) : 1.12f,
                HillStrength = Mathf.Clamp01(settings.HillStrength > 0f ? settings.HillStrength : 0.65f),
                MountainRegionStrength = Mathf.Clamp01(settings.MountainRegionStrength > 0f ? settings.MountainRegionStrength : 0.90f),
                CliffStrength = Mathf.Clamp01(settings.CliffStrength > 0f ? settings.CliffStrength : 0.55f),
                CliffStepHeight = settings.CliffStepHeight > 0f ? settings.CliffStepHeight : 24f,
                TallMountainExtraHeight = settings.TallMountainExtraHeight > 0f ? Mathf.Clamp(settings.TallMountainExtraHeight, 0f, 0.80f) : 0.34f,
                GiantMountainExtraHeight = settings.GiantMountainExtraHeight > 0f ? Mathf.Clamp(settings.GiantMountainExtraHeight, 0f, 0.90f) : 0.42f,
                MountainTypeVariation = settings.MountainTypeVariation > 0f ? Mathf.Clamp01(settings.MountainTypeVariation) : 0.85f,
                PlateauMountainStrength = settings.PlateauMountainStrength > 0f ? Mathf.Clamp01(settings.PlateauMountainStrength) : 0.72f,
                PlateauMountainFlatness = settings.PlateauMountainFlatness > 0f ? Mathf.Clamp01(settings.PlateauMountainFlatness) : 0.82f,
                CoastLowlandWidth = settings.CoastLowlandWidth > 0f ? Mathf.Clamp(settings.CoastLowlandWidth, 0.02f, 0.35f) : 0.24f,
                CoastHeightScale = settings.CoastHeightScale > 0f ? Mathf.Clamp(settings.CoastHeightScale, 0f, 0.25f) : 0.04f,
                CoastMountainFade = settings.CoastMountainFade > 0f ? Mathf.Clamp01(settings.CoastMountainFade) : 0.95f,
                EnableRivers = settings.EnableRivers,
                RiverWidth = settings.RiverWidth > 0f ? settings.RiverWidth : 0.045f,
                RiverBankWidth = settings.RiverBankWidth > 0f ? settings.RiverBankWidth : 0.13f,
                RiverDepth = Mathf.Max(1, settings.RiverDepth > 0 ? settings.RiverDepth : 9),
                RiverMinLandDistance = settings.RiverMinLandDistance > 0f ? settings.RiverMinLandDistance : 0.075f,
                RiverMaxMountainMask = settings.RiverMaxMountainMask > 0f ? settings.RiverMaxMountainMask : 0.78f,
                EnableLakes = settings.EnableLakes || settings.LakeCellSize <= 0,
                LakeCellSize = Mathf.Max(64, settings.LakeCellSize > 0 ? settings.LakeCellSize : 180),
                LakeChance = settings.LakeChance > 0f ? Mathf.Clamp01(settings.LakeChance) : 0.42f,
                LakeMinRadius = settings.LakeMinRadius > 0f ? settings.LakeMinRadius : 12f,
                LakeMaxRadius = settings.LakeMaxRadius > 0f ? settings.LakeMaxRadius : 56f,
                LakeDepth = Mathf.Max(1, settings.LakeDepth > 0 ? settings.LakeDepth : 12),
                LakeShoreWidth = settings.LakeShoreWidth > 0f ? settings.LakeShoreWidth : 12f,
                LakeMinLandDistance = settings.LakeMinLandDistance > 0f ? settings.LakeMinLandDistance : 0.12f,
                LakeMaxMountainMask = settings.LakeMaxMountainMask > 0f ? settings.LakeMaxMountainMask : 0.46f,
            };

            heightHandle = heightJob.Schedule(pendingHeightMap.Length, 64);
            heightJobScheduled = true;

            sharedColumn = new SharedTerrainColumnGeneration(
                terrainColumnKey,
                pendingHeightMap,
                pendingBiomeMap,
                pendingSurfaceBiomeMap,
                pendingBiomeBlendMap,
                pendingDesertEdgeMap,
                pendingRiverMap,
                pendingRiverSurfaceMap,
                heightHandle);
            SharedTerrainColumnGeneration createdColumn = sharedColumn;
            lock (SharedTerrainColumnGenerations)
            {
                if (SharedTerrainColumnGenerations.TryGetValue(terrainColumnKey, out SharedTerrainColumnGeneration existingColumn))
                {
                    existingColumn.ReferenceCount++;
                    sharedColumn = existingColumn;
                }
                else
                {
                    SharedTerrainColumnGenerations.Add(terrainColumnKey, sharedColumn);
                }
            }

            if (!ReferenceEquals(createdColumn, sharedColumn))
            {
                createdColumn.Dispose();
                heightHandle = sharedColumn.HeightHandle;
            }

            AttachSharedTerrainColumnGeneration(sharedColumn);
            }

            NoiseSettings.CaveNoiseSettings cave = settings.CaveNoise;
            NoiseSettings.LushCaveBiomeSettings lushCaveBiome = settings.LushCaveBiome;
            GenerateBlocksJob generateBlocksJob = new GenerateBlocksJob
            {
                Blocks = pendingGeneratedBlocks,
                ChunkSize = CHUNK_SIZE,
                ChunkHeight = CHUNK_HEIGHT,
                GroundOffset = settings.GroundOffset,
                HeightMap = pendingHeightMap,
                BiomeMap = pendingBiomeMap,
                SurfaceBiomeMap = pendingSurfaceBiomeMap,
                BiomeBlendMap = pendingBiomeBlendMap,
                DesertEdgeMap = pendingDesertEdgeMap,
                RiverMap = pendingRiverMap,
                RiverSurfaceMap = pendingRiverSurfaceMap,
                ChunkCoordinate = new int3(Coordinate.x, Coordinate.y, Coordinate.z),
                CaveNoise = cave,
                EnableCaves = settings.EnableCaves,
                LushCaveBiome = lushCaveBiome,
                GenerateLushCaveTrees = TerrainGenerator.Instance == null || TerrainGenerator.Instance.AddTrees,
                NoiseOffset = settings.noiseOffset,
                CaveNoiseRuntimeOffset = settings.caveNoiseRuntimeOffset,
                WaterLevel = settings.WaterLevel,
                Seed = settings.Seed,
                BedrockLevel = settings.BedrockLevel != 0 ? settings.BedrockLevel : -256,
                BedrockThickness = Mathf.Max(1, settings.BedrockThickness),
                EnableOases = settings.EnableOases,
                OasisCellSize = Mathf.Max(32, settings.OasisCellSize),
                OasisChance = settings.OasisChance,
                OasisRadius = Mathf.Max(4, settings.OasisRadius),
                OasisWaterRadius = Mathf.Max(2, settings.OasisWaterRadius),
                EnableStructures = settings.EnableStructures,
                StructureCellSize = Mathf.Max(32, settings.StructureCellSize),
                StructureChance = settings.StructureChance,
                RuinStructureChance = settings.RuinStructureChance,
                CaveHorizontalFrequency = 1f / Mathf.Max(0.0001f, cave.Scale),
                CaveVerticalFrequency = 1f / Mathf.Max(0.0001f, cave.VerticalScale),
                TunnelHorizontalFrequency = 1f / Mathf.Max(0.0001f, cave.TunnelScale > 0f ? cave.TunnelScale : 78f),
                TunnelVerticalFrequency = 1f / Mathf.Max(0.0001f, cave.TunnelVerticalScale > 0f ? cave.TunnelVerticalScale : 34f),
                RoomFrequency = 1f / Mathf.Max(0.0001f, cave.RoomScale > 0f ? cave.RoomScale : 116f),
            };

            pendingBlockDataHandle = generateBlocksJob.Schedule(pendingHeightMap.Length, 16, heightHandle);
            blockJobScheduled = true;

            FindGeneratedFluidFrontierJob fluidFrontierJob = new FindGeneratedFluidFrontierJob
            {
                Blocks = pendingGeneratedBlocks,
                FrontierMasks = pendingGeneratedFluidFrontierMasks,
                ChunkSize = CHUNK_SIZE,
                ChunkHeight = CHUNK_HEIGHT
            };
            pendingBlockDataHandle = fluidFrontierJob.Schedule(
                pendingGeneratedFluidFrontierMasks.Length,
                32,
                pendingBlockDataHandle);

            pendingTerrainColumnGeneration.AddDependentBlockHandle(pendingBlockDataHandle);
            isBlockDataGenerationScheduled = true;
            TerrainGenerator.RegisterScheduledBlockDataChunk(this);
            }
            catch
            {
                if (blockJobScheduled)
                    pendingBlockDataHandle.Complete();

                if (heightJobScheduled)
                    heightHandle.Complete();

                DisposePendingBlockDataGeneration(completeJob: false);
                throw;
            }
        }

        public bool CompleteBlockDataGenerationIfReady()
        {
            if (Blocks != null)
                return true;

            if (!isBlockDataGenerationScheduled || !pendingBlockDataHandle.IsCompleted)
                return false;

            BlockDataCompletionMarker.Begin();
            try
            {
                pendingBlockDataHandle.Complete();
                Blocks = new VoxelBuffer<byte>(CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE, pendingGeneratedBlocks.ToArray());
                generatedFluidFrontierMasks = null;
                generatedFluidFrontierMasksValid = true;
                for (int i = 0; i < pendingGeneratedFluidFrontierMasks.Length; i++)
                {
                    if (pendingGeneratedFluidFrontierMasks[i] == 0u)
                        continue;

                    generatedFluidFrontierMasks = pendingGeneratedFluidFrontierMasks.ToArray();
                    break;
                }

                blockTints = null;
                blockTintsNeedLazyRebuild = false;
                LowestGroundLevel = short.MaxValue;
                HighestGroundLevel = short.MinValue;
                IsAirOnly = true;
                nonAirBlockCount = 0;
                bool hasBiomeTintedBlocks = false;
                bool hasCustomGeneratedFluid = false;

                for (int i = 0; i < CHUNK_SIZE * CHUNK_SIZE; i++)
                {
                    int gl = pendingHeightMap[i];
                    if (gl < LowestGroundLevel) LowestGroundLevel = (short)gl;
                    if (gl > HighestGroundLevel) HighestGroundLevel = (short)gl;
                }

                byte[] blockData = Blocks.Data;
                for (int i = 0; i < blockData.Length; i++)
                {
                    if (blockData[i] != BLOCK_AIR)
                        nonAirBlockCount++;

                    if (!hasBiomeTintedBlocks &&
                        (blockData[i] == BLOCK_GRASS || IsTintedLeavesBlock(blockData[i])))
                    {
                        hasBiomeTintedBlocks = true;
                    }

                    if (blockData[i] != BLOCK_WATER &&
                        blockData[i] != BLOCK_LAVA &&
                        FluidSimulator.IsFluidBlock(blockData[i]))
                    {
                        hasCustomGeneratedFluid = true;
                    }
                }

                if (hasCustomGeneratedFluid)
                {
                    generatedFluidFrontierMasks = null;
                    generatedFluidFrontierMasksValid = false;
                }

                if (hasBiomeTintedBlocks)
                {
                    blockTints = new VoxelBuffer<Color32>(CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE);
                    ApplyGeneratedBiomeTints(pendingBiomeMap, pendingSurfaceBiomeMap, pendingBiomeBlendMap, pendingRiverMap);
                }
                IsAirOnly = nonAirBlockCount == 0;

                if (!IsAirOnly && IsTop)
                    AddGeneratedSurfaceFeatures();

                RecordSkylightColumnInfo();
            }
            finally
            {
                DisposePendingBlockDataGeneration(completeJob: false);
                TerrainGenerator.UnregisterScheduledBlockDataChunk(this);
                BlockDataCompletionMarker.End();
            }

            return true;
        }

        private void AddGeneratedSurfaceFeatures()
        {
            float chunkWorldY = Position.y;
            int generatedWaterLevel = NoiseSettings.Instance.GroundOffset + NoiseSettings.Instance.WaterLevel;

            for (int x = 0; x < CHUNK_SIZE; x++)
            {
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    int heightMapIndex = x + z * CHUNK_SIZE;
                    int groundLevel = pendingHeightMap[heightMapIndex];
                    byte biome = pendingBiomeMap[heightMapIndex];
                    byte surfaceBiome = pendingSurfaceBiomeMap[heightMapIndex];
                    float transitionStrength = pendingBiomeBlendMap[heightMapIndex] / 255f;
                    float riverStrength = pendingRiverMap[heightMapIndex] / 255f;
                    int riverSurfaceLevel = pendingRiverSurfaceMap[heightMapIndex];
                    int localGroundY = groundLevel - (int)chunkWorldY;

                    if (localGroundY < 0 || localGroundY >= CHUNK_HEIGHT)
                        continue;

                    // Height-map ground may have been removed by a surface cave
                    // connector. Do not grow a tree or decoration across its mouth.
                    if (Blocks[x, localGroundY, z] == BLOCK_AIR)
                        continue;

                    if (x > 3 && z > 3 && x < CHUNK_SIZE - 3 && z < CHUNK_SIZE - 3)
                    {
                        int worldX = Coordinate.x * CHUNK_SIZE + x;
                        int worldZ = Coordinate.z * CHUNK_SIZE + z;
                        bool placedTree = false;

                        if (TerrainGenerator.Instance.AddTrees)
                        {
                            int treeHeight = GetGeneratedTreeHeight(biome, worldX, worldZ);
                            int treeHeadRadius = GetGeneratedTreeHeadRadius(biome, worldX, worldZ);
                            int requiredHeadroom = treeHeight + treeHeadRadius + 2;
                            bool fitsCurrentChunk =
                                localGroundY + requiredHeadroom <= CHUNK_HEIGHT &&
                                x >= treeHeadRadius &&
                                z >= treeHeadRadius &&
                                x < CHUNK_SIZE - treeHeadRadius &&
                                z < CHUNK_SIZE - treeHeadRadius;

                            if (fitsCurrentChunk &&
                                ShouldPlaceTree(worldX, worldZ, biome, surfaceBiome, groundLevel, generatedWaterLevel, riverStrength, riverSurfaceLevel))
                            {
                                AddTree(x, localGroundY + 1, z, worldX, worldZ, biome, surfaceBiome, transitionStrength, riverStrength);
                                placedTree = true;
                            }
                        }

                        if (!placedTree)
                            TryAddSurfaceDecoration(x, localGroundY + 1, z, worldX, worldZ, biome, surfaceBiome, riverStrength, riverSurfaceLevel);
                    }
                }
            }
        }

        private void DisposePendingBlockDataGeneration(bool completeJob)
        {
            if (completeJob && isBlockDataGenerationScheduled)
                pendingBlockDataHandle.Complete();

            if (pendingTerrainColumnGeneration != null)
            {
                ReleaseSharedTerrainColumnGeneration(pendingTerrainColumnGeneration);
                pendingTerrainColumnGeneration = null;
            }
            else
            {
                if (pendingHeightMap.IsCreated) pendingHeightMap.Dispose();
                if (pendingBiomeMap.IsCreated) pendingBiomeMap.Dispose();
                if (pendingSurfaceBiomeMap.IsCreated) pendingSurfaceBiomeMap.Dispose();
                if (pendingBiomeBlendMap.IsCreated) pendingBiomeBlendMap.Dispose();
                if (pendingDesertEdgeMap.IsCreated) pendingDesertEdgeMap.Dispose();
                if (pendingRiverMap.IsCreated) pendingRiverMap.Dispose();
                if (pendingRiverSurfaceMap.IsCreated) pendingRiverSurfaceMap.Dispose();
            }

            if (pendingGeneratedBlocks.IsCreated) pendingGeneratedBlocks.Dispose();
            if (pendingGeneratedFluidFrontierMasks.IsCreated) pendingGeneratedFluidFrontierMasks.Dispose();

            pendingHeightMap = default;
            pendingBiomeMap = default;
            pendingSurfaceBiomeMap = default;
            pendingBiomeBlendMap = default;
            pendingDesertEdgeMap = default;
            pendingRiverMap = default;
            pendingRiverSurfaceMap = default;
            pendingGeneratedBlocks = default;
            pendingGeneratedFluidFrontierMasks = default;
            isBlockDataGenerationScheduled = false;
            pendingBlockDataHandle = default;
        }

        private static bool TryAcquireSharedTerrainColumnGeneration(
            TerrainColumnGenerationKey key,
            out SharedTerrainColumnGeneration generation)
        {
            lock (SharedTerrainColumnGenerations)
            {
                if (SharedTerrainColumnGenerations.TryGetValue(key, out generation))
                {
                    generation.ReferenceCount++;
                    return true;
                }
            }

            generation = null;
            return false;
        }

        private void AttachSharedTerrainColumnGeneration(SharedTerrainColumnGeneration generation)
        {
            pendingTerrainColumnGeneration = generation;
            pendingHeightMap = generation.HeightMap;
            pendingBiomeMap = generation.BiomeMap;
            pendingSurfaceBiomeMap = generation.SurfaceBiomeMap;
            pendingBiomeBlendMap = generation.BiomeBlendMap;
            pendingDesertEdgeMap = generation.DesertEdgeMap;
            pendingRiverMap = generation.RiverMap;
            pendingRiverSurfaceMap = generation.RiverSurfaceMap;
        }

        private static void ReleaseSharedTerrainColumnGeneration(SharedTerrainColumnGeneration generation)
        {
            bool shouldDispose = false;
            lock (SharedTerrainColumnGenerations)
            {
                generation.ReferenceCount--;
                if (generation.ReferenceCount <= 0)
                {
                    if (SharedTerrainColumnGenerations.TryGetValue(generation.Key, out SharedTerrainColumnGeneration cached) &&
                        ReferenceEquals(cached, generation))
                    {
                        SharedTerrainColumnGenerations.Remove(generation.Key);
                    }

                    shouldDispose = true;
                }
            }

            if (shouldDispose)
                generation.Dispose();
        }

        internal static void DisposeSharedTerrainColumnGenerationCache()
        {
            SharedTerrainColumnGeneration[] remaining;
            lock (SharedTerrainColumnGenerations)
            {
                remaining = new SharedTerrainColumnGeneration[SharedTerrainColumnGenerations.Count];
                SharedTerrainColumnGenerations.Values.CopyTo(remaining, 0);
                SharedTerrainColumnGenerations.Clear();
            }

            for (int i = 0; i < remaining.Length; i++)
                remaining[i].Dispose();
        }

        private readonly struct TerrainColumnGenerationKey : IEquatable<TerrainColumnGenerationKey>
        {
            private readonly int chunkX;
            private readonly int chunkZ;
            private readonly int seed;
            private readonly int worldEpoch;

            public TerrainColumnGenerationKey(int chunkX, int chunkZ, int seed, int worldEpoch)
            {
                this.chunkX = chunkX;
                this.chunkZ = chunkZ;
                this.seed = seed;
                this.worldEpoch = worldEpoch;
            }

            public bool Equals(TerrainColumnGenerationKey other)
            {
                return chunkX == other.chunkX &&
                       chunkZ == other.chunkZ &&
                       seed == other.seed &&
                       worldEpoch == other.worldEpoch;
            }

            public override bool Equals(object obj)
            {
                return obj is TerrainColumnGenerationKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = chunkX;
                    hash = (hash * 397) ^ chunkZ;
                    hash = (hash * 397) ^ seed;
                    return (hash * 397) ^ worldEpoch;
                }
            }
        }

        private sealed class SharedTerrainColumnGeneration
        {
            public readonly TerrainColumnGenerationKey Key;
            public NativeArray<int> HeightMap;
            public NativeArray<byte> BiomeMap;
            public NativeArray<byte> SurfaceBiomeMap;
            public NativeArray<byte> BiomeBlendMap;
            public NativeArray<byte> DesertEdgeMap;
            public NativeArray<byte> RiverMap;
            public NativeArray<int> RiverSurfaceMap;
            public readonly JobHandle HeightHandle;
            private JobHandle dependentBlockHandles;
            private int isDisposed;
            public int ReferenceCount;

            public SharedTerrainColumnGeneration(
                TerrainColumnGenerationKey key,
                NativeArray<int> heightMap,
                NativeArray<byte> biomeMap,
                NativeArray<byte> surfaceBiomeMap,
                NativeArray<byte> biomeBlendMap,
                NativeArray<byte> desertEdgeMap,
                NativeArray<byte> riverMap,
                NativeArray<int> riverSurfaceMap,
                JobHandle heightHandle)
            {
                Key = key;
                HeightMap = heightMap;
                BiomeMap = biomeMap;
                SurfaceBiomeMap = surfaceBiomeMap;
                BiomeBlendMap = biomeBlendMap;
                DesertEdgeMap = desertEdgeMap;
                RiverMap = riverMap;
                RiverSurfaceMap = riverSurfaceMap;
                HeightHandle = heightHandle;
                ReferenceCount = 1;
            }

            public void AddDependentBlockHandle(JobHandle blockHandle)
            {
                dependentBlockHandles = JobHandle.CombineDependencies(dependentBlockHandles, blockHandle);
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref isDisposed, 1) != 0)
                    return;

                dependentBlockHandles.Complete();
                HeightHandle.Complete();
                if (HeightMap.IsCreated) HeightMap.Dispose();
                if (BiomeMap.IsCreated) BiomeMap.Dispose();
                if (SurfaceBiomeMap.IsCreated) SurfaceBiomeMap.Dispose();
                if (BiomeBlendMap.IsCreated) BiomeBlendMap.Dispose();
                if (DesertEdgeMap.IsCreated) DesertEdgeMap.Dispose();
                if (RiverMap.IsCreated) RiverMap.Dispose();
                if (RiverSurfaceMap.IsCreated) RiverSurfaceMap.Dispose();

                HeightMap = default;
                BiomeMap = default;
                SurfaceBiomeMap = default;
                BiomeBlendMap = default;
                DesertEdgeMap = default;
                RiverMap = default;
                RiverSurfaceMap = default;
            }
        }

        internal void DisposeGenerationResources()
        {
            DisposePendingBlockDataGeneration(completeJob: true);
            TerrainGenerator.UnregisterScheduledBlockDataChunk(this);
        }

        private static bool ShouldPlaceTree(
            int worldX,
            int worldZ,
            byte biome,
            byte surfaceBiome,
            int groundLevel,
            int waterLevel,
            float riverStrength,
            int riverSurfaceLevel)
        {
            if (!IsGeneratedTreeColumnDry(groundLevel, waterLevel, riverStrength, riverSurfaceLevel))
                return false;

            if (IsGeneratedStructureColumn(worldX, worldZ, biome))
                return false;

            if (TerrainNoiseUtility.IsDryDesertBiome(biome))
            {
                if (!TryGetManagedOasisSample(worldX, worldZ, out TreeOasisSample oasis) ||
                    oasis.IsWater ||
                    oasis.Influence < 0.24f)
                {
                    return false;
                }

                float oasisDensity = SampleTreeDensity01(worldX, worldZ);
                float oasisChance = Mathf.Lerp(0.035f, 0.16f, oasisDensity) * oasis.Influence;
                return Hash01(Hash(worldX, worldZ, 0x2D3D5)) < oasisChance;
            }

            float cherryGroveStrength = GetCherryGroveStrength(worldX, worldZ, biome, surfaceBiome);
            if (cherryGroveStrength > 0.12f)
            {
                float groveDensity = SampleTreeDensity01(worldX, worldZ);
                float cherryChance = Mathf.Lerp(0.018f, 0.105f, groveDensity) * Mathf.Clamp01(cherryGroveStrength * 1.18f);
                return Hash01(Hash(worldX, worldZ, 0x2D3D5)) < cherryChance;
            }

            if (cherryGroveStrength > 0.02f)
                return false;

            float density = SampleTreeDensity01(worldX, worldZ);
            if (biome == (byte)BiomeId.Jungle)
                return ShouldPlaceJungleTree(worldX, worldZ, density);

            float chance = biome switch
            {
                (byte)BiomeId.Forest => Mathf.Lerp(0.018f, 0.125f, density),
                (byte)BiomeId.Plains => Mathf.Lerp(0.0015f, 0.030f, density),
                (byte)BiomeId.Snow => Mathf.Lerp(0.0005f, 0.010f, density),
                _ => 0f,
            };

            return chance > 0f && Hash01(Hash(worldX, worldZ, 0x2D3D5)) < chance;
        }

        private static bool ShouldPlaceJungleTree(int worldX, int worldZ, float density)
        {
            const int cellSize = 5;
            int cellX = FastFloorToInt(worldX / (float)cellSize);
            int cellZ = FastFloorToInt(worldZ / (float)cellSize);
            int candidateX = cellX * cellSize + 1 + (int)(Hash(cellX, cellZ, 0xA721) % (cellSize - 2));
            int candidateZ = cellZ * cellSize + 1 + (int)(Hash(cellX, cellZ, 0xB319) % (cellSize - 2));

            if (worldX != candidateX || worldZ != candidateZ)
                return false;

            float chance = Mathf.Lerp(0.72f, 0.98f, density);
            return Hash01(Hash(cellX, cellZ, 0xD85B)) < chance;
        }

        private static float GetCherryGroveStrength(int worldX, int worldZ, byte biome, byte surfaceBiome)
        {
            if (biome != (byte)BiomeId.Plains || surfaceBiome != (byte)BiomeId.Plains)
                return 0f;

            const int cellSize = 128;
            int cellX = FastFloorToInt(worldX / (float)cellSize);
            int cellZ = FastFloorToInt(worldZ / (float)cellSize);
            float bestStrength = 0f;

            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                {
                    int candidateCellX = cellX + offsetX;
                    int candidateCellZ = cellZ + offsetZ;

                    if (Hash01(Hash(candidateCellX, candidateCellZ, 0xC837)) > 0.22f)
                        continue;

                    float margin = cellSize * 0.22f;
                    float centerX = candidateCellX * cellSize + Mathf.Lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x4C4B)));
                    float centerZ = candidateCellZ * cellSize + Mathf.Lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x91B3)));
                    float radius = Mathf.Lerp(28f, 50f, Hash01(Hash(candidateCellX, candidateCellZ, 0x5F17)));

                    float dx = worldX - centerX;
                    float dz = worldZ - centerZ;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);
                    if (distance > radius + 14f)
                        continue;

                    float coreStrength = 1f - Mathf.Clamp01(distance / radius);
                    float skirtStrength = 1f - Mathf.Clamp01((distance - radius) / 14f);
                    bestStrength = Mathf.Max(bestStrength, Mathf.Max(coreStrength, skirtStrength * 0.14f));
                }
            }

            return bestStrength;
        }

        private static bool IsGeneratedTreeColumnDry(int groundLevel, int waterLevel, float riverStrength, int riverSurfaceLevel)
        {
            if (groundLevel <= waterLevel)
                return false;

            if (riverSurfaceLevel != int.MinValue)
            {
                if (groundLevel <= riverSurfaceLevel + 2)
                    return false;

                if (riverStrength > 0.72f && groundLevel <= riverSurfaceLevel + 4)
                    return false;
            }

            return true;
        }

        private void AddTree(
            int x,
            int y,
            int z,
            int worldX,
            int worldZ,
            byte biome,
            byte surfaceBiome,
            float transitionStrength,
            float riverStrength)
        {
            AddRootedDirtBelowTree(x, y - 1, z);
            byte woodBlockId = GetTreeWoodBlock(biome, surfaceBiome, worldX, worldZ);
            byte leavesBlockId = GetTreeLeavesBlock(woodBlockId);
            int height = GetGeneratedTreeHeight(biome, worldX, worldZ);

            for (int i = 0; i < height; i++)
            {
                if (ChunkUtility.IsInsideChunk(new Vector3Int(x, y + i, z)))
                    SetGeneratedBlock(x, y + i, z, woodBlockId);
            }

            int treeHeadRadius = GetGeneratedTreeHeadRadius(biome, worldX, worldZ);
            Vector3 center = new Vector3(x, y + height + treeHeadRadius / 8.0f, z);
            float radiusSq = treeHeadRadius * treeHeadRadius;
            Color32 leavesTint = leavesBlockId == BLOCK_CHERRY_LEAVES
                ? WhiteBlockTint
                : GetLeavesTint(biome, surfaceBiome, transitionStrength, riverStrength);

            for (int relativeX = -treeHeadRadius; relativeX < treeHeadRadius + 1; relativeX++)
            {
                for (int relativeY = 0; relativeY < treeHeadRadius + 1; relativeY++)
                {
                    for (int relativeZ = -treeHeadRadius; relativeZ < treeHeadRadius + 1; relativeZ++)
                    {
                        Vector3Int blockPos = new Vector3Int(x + relativeX, y + relativeY + height, z + relativeZ);

                        if (((Vector3)blockPos - center).sqrMagnitude < radiusSq)
                        {
                            if (ChunkUtility.IsInsideChunk(blockPos) &&
                                !IsGeneratedTreeLog(Blocks[blockPos.x, blockPos.y, blockPos.z]))
                            {
                                SetGeneratedBlock(blockPos.x, blockPos.y, blockPos.z, leavesBlockId, leavesTint);
                            }
                        }
                    }
                }
            }
        }

        private static int GetGeneratedTreeHeight(byte biome, int worldX, int worldZ)
        {
            if (TerrainNoiseUtility.IsDryDesertBiome(biome))
                return 4 + (int)(Hash(worldX, worldZ, 0x51A7) % 2);

            return biome switch
            {
                (byte)BiomeId.Jungle => 7 + (int)(Hash(worldX, worldZ, 0x51A7) % 4),
                (byte)BiomeId.Forest => 5 + (int)(Hash(worldX, worldZ, 0x51A7) % 4),
                _ => 4 + (int)(Hash(worldX, worldZ, 0x51A7) % 3),
            };
        }

        private static int GetGeneratedTreeHeadRadius(byte biome, int worldX, int worldZ)
        {
            if (TerrainNoiseUtility.IsDryDesertBiome(biome))
                return 3;

            return biome switch
            {
                (byte)BiomeId.Jungle => 4 + (int)(Hash(worldX, worldZ, 0x7F21) % 2),
                (byte)BiomeId.Forest => 4 + (int)(Hash(worldX, worldZ, 0x7F21) % 2),
                _ => 3 + (int)(Hash(worldX, worldZ, 0x7F21) % 2),
            };
        }

        private static bool IsGeneratedTreeLog(byte blockId)
        {
            return blockId == BLOCK_WOOD ||
                   blockId == BLOCK_JUNGLE_LOG ||
                   blockId == BLOCK_CHERRY_LOG ||
                   blockId == BLOCK_SPRUCE_LOG;
        }

        private static byte GetTreeWoodBlock(byte biome, byte surfaceBiome, int worldX, int worldZ)
        {
            float treeVariant = Hash01(Hash(worldX, worldZ, 0x67A1));

            if (TerrainNoiseUtility.IsDryDesertBiome(biome))
                return BLOCK_JUNGLE_LOG;

            if (biome == (byte)BiomeId.Jungle)
                return BLOCK_JUNGLE_LOG;

            if (biome == (byte)BiomeId.Snow)
                return BLOCK_SPRUCE_LOG;

            if (GetCherryGroveStrength(worldX, worldZ, biome, surfaceBiome) > 0.12f)
                return BLOCK_CHERRY_LOG;

            if (biome == (byte)BiomeId.Forest)
            {
                if (treeVariant < 0.28f)
                    return BLOCK_JUNGLE_LOG;
            }

            return BLOCK_WOOD;
        }

        private static byte GetTreeLeavesBlock(byte woodBlockId)
        {
            return woodBlockId switch
            {
                BLOCK_JUNGLE_LOG => (byte)BLOCK_JUNGLE_LEAVES,
                BLOCK_CHERRY_LOG => (byte)BLOCK_CHERRY_LEAVES,
                _ => (byte)BLOCK_LEAVES,
            };
        }

        private void TryAddSurfaceDecoration(
            int x,
            int y,
            int z,
            int worldX,
            int worldZ,
            byte biome,
            byte surfaceBiome,
            float riverStrength,
            int riverSurfaceLevel)
        {
            if (!ChunkUtility.IsInsideChunk(new Vector3Int(x, y, z)) || Blocks[x, y, z] != BLOCK_AIR)
                return;

            int groundY = y - 1;
            if (!ChunkUtility.IsInsideChunk(new Vector3Int(x, groundY, z)))
                return;

            byte surfaceBlock = Blocks[x, groundY, z];
            if (TerrainNoiseUtility.IsDryDesertBiome(biome))
            {
                TryAddCactus(x, y, z, worldX, worldZ, surfaceBlock, riverStrength, riverSurfaceLevel);
                return;
            }

            if ((biome == (byte)BiomeId.Jungle ||
                 biome == (byte)BiomeId.Forest ||
                 biome == (byte)BiomeId.Plains ||
                 surfaceBiome == (byte)BiomeId.Jungle ||
                 surfaceBiome == (byte)BiomeId.Forest) &&
                surfaceBlock == BLOCK_GRASS)
            {
                TryAddMelon(x, y, z, worldX, worldZ, riverStrength);
            }
        }

        private void TryAddCactus(int x, int y, int z, int worldX, int worldZ, byte surfaceBlock, float riverStrength, int riverSurfaceLevel)
        {
            if (surfaceBlock != BLOCK_SAND && surfaceBlock != BLOCK_RED_SAND)
                return;

            if (riverStrength > 0.18f || riverSurfaceLevel != int.MinValue)
                return;

            if (Hash01(Hash(worldX, worldZ, 0xCA77)) > 0.012f)
                return;

            int height = 2 + (int)(Hash(worldX, worldZ, 0xCA78) % 3);
            for (int i = 0; i < height; i++)
            {
                int cactusY = y + i;
                if (!ChunkUtility.IsInsideChunk(new Vector3Int(x, cactusY, z)) || Blocks[x, cactusY, z] != BLOCK_AIR)
                    return;
            }

            for (int i = 0; i < height; i++)
                SetGeneratedBlock(x, y + i, z, BLOCK_CACTUS);
        }

        private void TryAddMelon(int x, int y, int z, int worldX, int worldZ, float riverStrength)
        {
            float chance = riverStrength > 0.28f ? 0.010f : 0.0025f;
            if (Hash01(Hash(worldX, worldZ, 0x4E10)) > chance)
                return;

            SetGeneratedBlock(x, y, z, BLOCK_MELON);
        }

        private void AddRootedDirtBelowTree(int x, int groundY, int z)
        {
            int rootY = groundY - 1;
            if (!ChunkUtility.IsInsideChunk(new Vector3Int(x, groundY, z)))
                return;

            if (!ChunkUtility.IsInsideChunk(new Vector3Int(x, rootY, z)))
                return;

            byte surfaceBlock = Blocks[x, groundY, z];
            if (surfaceBlock != BLOCK_GRASS && surfaceBlock != BLOCK_SNOW_GRASS)
                return;

            byte rootTargetBlock = Blocks[x, rootY, z];
            if (rootTargetBlock == BLOCK_AIR ||
                rootTargetBlock == BLOCK_WATER ||
                rootTargetBlock == BLOCK_LAVA ||
                rootTargetBlock == BLOCK_BEDROCK)
            {
                return;
            }

            SetGeneratedBlock(x, rootY, z, BLOCK_ROOTED_DIRT);
        }

        private void SetGeneratedBlock(int x, int y, int z, byte blockId)
        {
            byte previousBlockId = Blocks[x, y, z];
            if (previousBlockId == blockId)
                return;

            Blocks[x, y, z] = blockId;
            SetBlockTint(new Vector3Int(x, y, z), blockId);
            UpdateNonAirBlockCount(previousBlockId, blockId);
        }

        private void SetGeneratedBlock(int x, int y, int z, byte blockId, Color32 tint)
        {
            byte previousBlockId = Blocks[x, y, z];
            if (previousBlockId == blockId)
                return;

            Blocks[x, y, z] = blockId;
            SetBlockTint(new Vector3Int(x, y, z), blockId, tint);
            UpdateNonAirBlockCount(previousBlockId, blockId);
        }

        private void ApplyGeneratedBiomeTints(
            NativeArray<byte> biomeMap,
            NativeArray<byte> surfaceBiomeMap,
            NativeArray<byte> biomeBlendMap,
            NativeArray<byte> riverMap)
        {
            EnsureBlockTintArray();
            byte[] blockData = Blocks.Data;
            Color32[] tintData = blockTints.Data;
            int sliceStride = Blocks.SliceStride;

            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                int sliceStart = z * sliceStride;
                for (int x = 0; x < CHUNK_SIZE; x++)
                {
                    int mapIndex = x + z * CHUNK_SIZE;
                    Color32 tint = GetGrassTint(
                        biomeMap[mapIndex],
                        surfaceBiomeMap[mapIndex],
                        biomeBlendMap[mapIndex] / 255f,
                        riverMap[mapIndex] / 255f);
                    int surfaceY = pendingHeightMap[mapIndex];

                    for (int y = 0; y < CHUNK_HEIGHT; y++)
                    {
                        int blockIndex = sliceStart + y * CHUNK_SIZE + x;
                        byte blockId = blockData[blockIndex];
                        int worldY = Coordinate.y * CHUNK_HEIGHT + y;
                        bool isUndergroundFoliage = surfaceY - worldY > 8;

                        if (blockId == BLOCK_GRASS)
                            tintData[blockIndex] = isUndergroundFoliage ? LushCaveGrassTint : tint;
                        else if (IsTintedLeavesBlock(blockId))
                            tintData[blockIndex] = isUndergroundFoliage ? LushCaveLeavesTint : DefaultLeavesTint;
                    }
                }
            }
        }

        private void SetBlockTint(Vector3Int localPosition, byte blockId)
        {
            if (!ChunkUtility.IsInsideChunk(localPosition))
                return;

            if (TryGetDefaultBlockTint(localPosition, blockId, out Color32 tint))
            {
                EnsureBlockTintArray();
                blockTints[localPosition.x, localPosition.y, localPosition.z] = tint;
                return;
            }

            if (blockTints != null)
                blockTints[localPosition.x, localPosition.y, localPosition.z] = WhiteBlockTint;
        }

        private void SetBlockTint(Vector3Int localPosition, byte blockId, Color32 tint)
        {
            if (!ChunkUtility.IsInsideChunk(localPosition))
                return;

            if (tint.a == 0)
            {
                SetBlockTint(localPosition, blockId);
                return;
            }

            EnsureBlockTintArray();
            blockTints[localPosition.x, localPosition.y, localPosition.z] = tint;
        }

        private void EnsureBlockTintArray()
        {
            if (blockTints != null)
            {
                blockTintsNeedLazyRebuild = false;
                return;
            }

            blockTintsNeedLazyRebuild = false;
            blockTints = new VoxelBuffer<Color32>(CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE);

            if (Blocks == null)
                return;

            byte[] blockData = Blocks.Data;
            Color32[] tintData = blockTints.Data;
            int sliceStride = Blocks.SliceStride;

            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                int sliceStart = z * sliceStride;
                for (int x = 0; x < CHUNK_SIZE; x++)
                {
                    Color32 grassTint = DefaultGrassTint;
                    Color32 leavesTint = DefaultLeavesTint;
                    bool hasBiomeTints = false;

                    for (int y = 0; y < CHUNK_HEIGHT; y++)
                    {
                        int blockIndex = sliceStart + y * CHUNK_SIZE + x;
                        byte blockId = blockData[blockIndex];
                        bool tintedLeaves = blockId != BLOCK_AIR && IsTintedLeavesBlock(blockId);
                        if (blockId == BLOCK_GRASS || tintedLeaves)
                        {
                            Vector3Int localPosition = new Vector3Int(x, y, z);
                            if (IsLushCaveFoliagePosition(localPosition))
                            {
                                tintData[blockIndex] = tintedLeaves ? LushCaveLeavesTint : LushCaveGrassTint;
                                continue;
                            }

                            if (!hasBiomeTints)
                            {
                                int worldX = Coordinate.x * CHUNK_SIZE + x;
                                int worldZ = Coordinate.z * CHUNK_SIZE + z;
                                TryGetBiomeTintsAtWorldColumn(worldX, worldZ, out grassTint, out leavesTint);
                                hasBiomeTints = true;
                            }

                            tintData[blockIndex] = tintedLeaves ? leavesTint : grassTint;
                        }
                        else if (blockId != BLOCK_AIR && TryGetDefaultBlockTint(new Vector3Int(x, y, z), blockId, out Color32 tint))
                        {
                            tintData[blockIndex] = tint;
                        }
                    }
                }
            }
        }

        private Color32 GetBlockTint(int x, int y, int z)
        {
            if (blockTints == null)
                return WhiteBlockTint;

            Color32 tint = blockTints[x, y, z];
            return tint.a == 0 ? WhiteBlockTint : tint;
        }

        private static Color32 GetGrassTint(byte biome, byte surfaceBiome, float transitionStrength, float riverStrength)
        {
            Color32 tint = DefaultGrassTint;

            if (biome == (byte)BiomeId.Jungle || surfaceBiome == (byte)BiomeId.Jungle)
                tint = JungleGrassTint;
            else if (biome == (byte)BiomeId.Forest || surfaceBiome == (byte)BiomeId.Forest)
                tint = ForestGrassTint;
            else if (biome == (byte)BiomeId.Snow || surfaceBiome == (byte)BiomeId.Snow)
                tint = SnowGrassTint;

            if (TerrainNoiseUtility.IsDryDesertBiome(biome) || TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
            {
                float desertAmount = TerrainNoiseUtility.IsDryDesertBiome(biome)
                    ? 1f
                    : Mathf.Clamp01(0.35f + transitionStrength * 0.65f);
                tint = LerpColor(tint, DesertEdgeGrassTint, desertAmount);
            }
            else if (riverStrength > 0.45f)
            {
                float wetAmount = Mathf.Clamp01((riverStrength - 0.45f) / 0.55f);
                tint = LerpColor(tint, RiverGrassTint, wetAmount * 0.65f);
            }

            return tint;
        }

        private static Color32 GetLeavesTint(byte biome, byte surfaceBiome, float transitionStrength, float riverStrength)
        {
            Color32 tint = DefaultLeavesTint;

            if (biome == (byte)BiomeId.Jungle || surfaceBiome == (byte)BiomeId.Jungle)
                tint = JungleLeavesTint;
            else if (biome == (byte)BiomeId.Forest || surfaceBiome == (byte)BiomeId.Forest)
                tint = ForestLeavesTint;
            else if (biome == (byte)BiomeId.Snow || surfaceBiome == (byte)BiomeId.Snow)
                tint = SnowLeavesTint;

            if (TerrainNoiseUtility.IsDryDesertBiome(biome) || TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
            {
                float desertAmount = TerrainNoiseUtility.IsDryDesertBiome(biome)
                    ? 1f
                    : Mathf.Clamp01(0.35f + transitionStrength * 0.65f);
                tint = LerpColor(tint, DesertEdgeLeavesTint, desertAmount);
            }
            else if (riverStrength > 0.45f)
            {
                float wetAmount = Mathf.Clamp01((riverStrength - 0.45f) / 0.55f);
                tint = LerpColor(tint, RiverLeavesTint, wetAmount * 0.65f);
            }

            return tint;
        }

        private bool TryGetDefaultBlockTint(Vector3Int localPosition, byte blockId, out Color32 tint)
        {
            if (blockId == BLOCK_GRASS)
            {
                if (IsLushCaveFoliagePosition(localPosition))
                {
                    tint = LushCaveGrassTint;
                    return true;
                }

                TryGetGrassTintAtLocalPosition(localPosition, out tint);
                return true;
            }

            if (IsTintedLeavesBlock(blockId))
            {
                if (IsLushCaveFoliagePosition(localPosition))
                {
                    tint = LushCaveLeavesTint;
                    return true;
                }

                TryGetLeavesTintAtLocalPosition(localPosition, out tint);
                return true;
            }

            tint = WhiteBlockTint;
            return false;
        }

        private bool IsLushCaveFoliagePosition(Vector3Int localPosition)
        {
            NoiseSettings settings = NoiseSettings.Instance;
            if (settings == null || !settings.LushCaveBiome.Enable)
                return false;

            int worldY = Coordinate.y * CHUNK_HEIGHT + localPosition.y;
            int waterSurfaceY = settings.GroundOffset + settings.WaterLevel;
            return worldY < waterSurfaceY - 24;
        }

        private bool TryGetGrassTintAtLocalPosition(Vector3Int localPosition, out Color32 tint)
        {
            int worldX = Coordinate.x * CHUNK_SIZE + localPosition.x;
            int worldZ = Coordinate.z * CHUNK_SIZE + localPosition.z;
            return TryGetGrassTintAtWorldColumn(worldX, worldZ, out tint);
        }

        private bool TryGetLeavesTintAtLocalPosition(Vector3Int localPosition, out Color32 tint)
        {
            int worldX = Coordinate.x * CHUNK_SIZE + localPosition.x;
            int worldZ = Coordinate.z * CHUNK_SIZE + localPosition.z;
            return TryGetBiomeTintsAtWorldColumn(worldX, worldZ, out _, out tint);
        }

        private static bool TryGetGrassTintAtWorldColumn(int worldX, int worldZ, out Color32 tint)
        {
            return TryGetBiomeTintsAtWorldColumn(worldX, worldZ, out tint, out _);
        }

        private static bool TryGetBiomeTintsAtWorldColumn(
            int worldX,
            int worldZ,
            out Color32 grassTint,
            out Color32 leavesTint)
        {
            grassTint = DefaultGrassTint;
            leavesTint = DefaultLeavesTint;

            NoiseSettings settings = NoiseSettings.Instance;
            if (settings == null)
                return false;

            settings.GetNoiseLayers(out NoiseLayer continentLayer, out _, out NoiseLayer detailLayer, out _);
            settings.GetBiomeLayers(out NoiseLayer temperatureLayer, out NoiseLayer moistureLayer, out NoiseLayer erosionLayer);
            settings.GetRedDesertLayer(out NoiseLayer redDesertLayer);
            settings.GetHydrologyLayers(out NoiseLayer riverLayer);

            float2 worldPosition = new float2(worldX, worldZ);
            float continentalness = TerrainNoiseUtility.Fbm01(worldPosition, continentLayer, 4, 2.0f, 0.5f);
            continentalness = TerrainNoiseUtility.Redistribute01(continentalness, continentLayer.Redistribution);
            continentalness = math.saturate(continentalness + settings.LandBias);

            int biomeOctaves = Mathf.Clamp(settings.BiomeNoiseOctaves > 0 ? settings.BiomeNoiseOctaves : 3, 1, 4);
            float biomeContrast = Mathf.Clamp(settings.BiomeContrast > 0f ? settings.BiomeContrast : 1.15f, 0.5f, 2.5f);
            float2 biomePosition = TerrainNoiseUtility.WarpBiomePosition(worldPosition, erosionLayer, detailLayer);

            float temperature = TerrainNoiseUtility.Fbm01(biomePosition, temperatureLayer, biomeOctaves, 2.0f, 0.45f);
            temperature = TerrainNoiseUtility.Redistribute01(temperature, temperatureLayer.Redistribution);
            temperature = TerrainNoiseUtility.Contrast01(temperature, biomeContrast);

            float moisture = TerrainNoiseUtility.Fbm01(biomePosition, moistureLayer, biomeOctaves, 2.0f, 0.45f);
            moisture = TerrainNoiseUtility.Redistribute01(moisture, moistureLayer.Redistribution);
            moisture = TerrainNoiseUtility.Contrast01(moisture, biomeContrast);

            float redDesertRegion = TerrainNoiseUtility.Fbm01(
                biomePosition + new float2(573.7f, -811.4f),
                redDesertLayer,
                Math.Max(1, biomeOctaves - 1),
                1.85f,
                0.52f);
            redDesertRegion = TerrainNoiseUtility.Redistribute01(redDesertRegion, redDesertLayer.Redistribution);
            redDesertRegion = TerrainNoiseUtility.Contrast01(redDesertRegion, math.lerp(0.88f, 1.24f, math.saturate(biomeContrast - 0.5f)));

            float mountainMask = math.saturate((continentalness - settings.MountainBlendStart) * settings.MountainBlendSharpness);
            mountainMask = TerrainNoiseUtility.Smooth01(mountainMask);

            float oceanThreshold = math.min(settings.OceanThreshold, settings.BeachThreshold - 0.001f);
            float beachThreshold = math.max(settings.BeachThreshold, oceanThreshold + 0.001f);
            float broadCoastWarp = TerrainNoiseUtility.Fbm01(
                worldPosition + new float2(-1186.3f, 421.7f),
                erosionLayer,
                2,
                1.90f,
                0.55f);
            float fineCoastWarp = TerrainNoiseUtility.Fbm01(
                worldPosition + new float2(706.9f, -982.4f),
                detailLayer,
                3,
                2.15f,
                0.52f);
            float coastlineOffset = (broadCoastWarp - 0.5f) * 0.085f +
                                    (fineCoastWarp - 0.5f) * 0.050f;
            float coastContinentalness = math.saturate(continentalness + coastlineOffset);

            byte biome = TerrainNoiseUtility.SelectBiome(
                coastContinentalness,
                temperature,
                moisture,
                mountainMask,
                redDesertRegion,
                oceanThreshold,
                beachThreshold);
            byte landSurfaceBiome = TerrainNoiseUtility.SelectLandBiome(temperature, moisture, mountainMask, redDesertRegion);
            bool coldSurface = !TerrainNoiseUtility.IsDryDesertBiome(landSurfaceBiome) &&
                               temperature < TerrainNoiseUtility.SnowTemperatureThreshold + 0.055f;
            if (coldSurface)
                landSurfaceBiome = (byte)BiomeId.Snow;

            byte surfaceBiome = biome == (byte)BiomeId.Ocean || biome == (byte)BiomeId.Beach
                ? landSurfaceBiome
                : (coldSurface && !TerrainNoiseUtility.IsDryDesertBiome(biome) ? (byte)BiomeId.Snow : biome);
            float transitionStrength = TerrainNoiseUtility.GetBiomeTransitionStrength(
                temperature,
                moisture,
                mountainMask,
                settings.BiomeTransitionWidth > 0f ? settings.BiomeTransitionWidth : 0.08f);
            float riverStrength = GetRiverStrengthAtWorldColumn(
                worldPosition,
                coastContinentalness,
                mountainMask,
                beachThreshold,
                settings,
                riverLayer);

            grassTint = GetGrassTint(biome, surfaceBiome, transitionStrength, riverStrength);
            leavesTint = GetLeavesTint(biome, surfaceBiome, transitionStrength, riverStrength);
            return true;
        }

        private static float GetRiverStrengthAtWorldColumn(
            float2 worldPosition,
            float continentalness,
            float mountainMask,
            float beachThreshold,
            NoiseSettings settings,
            NoiseLayer riverLayer)
        {
            if (!settings.EnableRivers)
                return 0f;

            float riverWidth = math.max(0.001f, settings.RiverWidth > 0f ? settings.RiverWidth : 0.045f);
            float riverBankWidth = math.max(riverWidth + 0.001f, settings.RiverBankWidth > 0f ? settings.RiverBankWidth : 0.13f);
            float minLandDistance = settings.RiverMinLandDistance > 0f ? settings.RiverMinLandDistance : 0.075f;
            float maxMountainMask = settings.RiverMaxMountainMask > 0f ? settings.RiverMaxMountainMask : 0.78f;

            float landGate = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(
                beachThreshold + minLandDistance,
                beachThreshold + minLandDistance + 0.16f,
                continentalness)));
            float mountainGate = 1f - TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(
                maxMountainMask - 0.16f,
                maxMountainMask,
                mountainMask)));

            if (landGate <= 0f || mountainGate <= 0f)
                return 0f;

            float widthNoise = TerrainNoiseUtility.Fbm01(worldPosition + new float2(611.3f, -274.9f), riverLayer, 2, 2.0f, 0.45f);
            widthNoise = TerrainNoiseUtility.Smooth01(widthNoise);
            riverWidth *= math.lerp(0.55f, 1.85f, widthNoise);
            riverBankWidth *= math.lerp(0.75f, 2.15f, widthNoise);

            float2 riverSample = (worldPosition + riverLayer.Offset) * riverLayer.Frequency;
            float riverLine = math.abs(noise.snoise(riverSample));
            float meander = TerrainNoiseUtility.Fbm01(worldPosition + new float2(-918.2f, 431.7f), riverLayer, 2, 2.4f, 0.45f);
            riverLine = math.abs(riverLine + (meander - 0.5f) * 0.11f);

            float bankStrength = 1f - math.saturate(riverLine / riverBankWidth);
            float waterStrength = 1f - math.saturate(riverLine / riverWidth);

            bankStrength = TerrainNoiseUtility.Smooth01(bankStrength) * landGate * mountainGate;
            waterStrength = TerrainNoiseUtility.Smooth01(waterStrength) * landGate * mountainGate;

            return math.max(bankStrength, waterStrength);
        }

        private static bool IsTintedLeavesBlock(byte blockId)
        {
            if (blockId == BLOCK_LEAVES)
                return true;

            AssetsContainer assets = AssetsContainer.Instance;
            BlockData[] definitions = assets != null ? assets.Blocks : null;
            if (!ReferenceEquals(cachedTintedLeavesDefinitions, definitions) || cachedTintedLeavesFlags == null)
                CacheTintedLeavesFlags(definitions);

            return cachedTintedLeavesFlags != null &&
                   blockId < cachedTintedLeavesFlags.Length &&
                   cachedTintedLeavesFlags[blockId];
        }

        private static void CacheTintedLeavesFlags(BlockData[] definitions)
        {
            cachedTintedLeavesDefinitions = definitions;
            if (definitions == null)
            {
                cachedTintedLeavesFlags = null;
                return;
            }

            var flags = new bool[definitions.Length];
            for (int blockId = 1; blockId < definitions.Length; blockId++)
            {
                BlockData block = definitions[blockId];
                flags[blockId] = block != null && IsTintedLeavesBlockName(block.name);
            }

            if (BLOCK_LEAVES < flags.Length)
                flags[BLOCK_LEAVES] = true;

            cachedTintedLeavesFlags = flags;
        }

        private bool ContainsTintedBlocks()
        {
            if (Blocks == null)
                return false;

            byte[] blockData = Blocks.Data;
            for (int i = 0; i < blockData.Length; i++)
            {
                byte blockId = blockData[i];
                if (blockId == BLOCK_GRASS || IsTintedLeavesBlock(blockId))
                    return true;
            }

            return false;
        }

        internal static bool IsTintedLeavesBlockName(string blockName)
        {
            if (string.IsNullOrEmpty(blockName) ||
                blockName.Equals("cherry_leaves", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return blockName.Equals("leaves", StringComparison.OrdinalIgnoreCase) ||
                   blockName.EndsWith("_leaves", StringComparison.OrdinalIgnoreCase);
        }

        private static Color32 LerpColor(Color32 from, Color32 to, float amount)
        {
            amount = Mathf.Clamp01(amount);
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(from.r, to.r, amount)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(from.g, to.g, amount)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(from.b, to.b, amount)),
                255);
        }

        private struct TreeOasisSample
        {
            public float Influence;
            public bool IsWater;
        }

        private static bool TryGetManagedOasisSample(int worldX, int worldZ, out TreeOasisSample sample)
        {
            sample = default;

            NoiseSettings settings = NoiseSettings.Instance;
            if (settings == null || !settings.EnableOases)
                return false;

            float chance = Mathf.Clamp01(settings.OasisChance);
            if (chance <= 0f)
                return false;

            int cellSize = Mathf.Max(32, settings.OasisCellSize);
            int cellX = FastFloorToInt(worldX / (float)cellSize);
            int cellZ = FastFloorToInt(worldZ / (float)cellSize);
            bool found = false;
            float bestDistance = 0f;

            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                {
                    int candidateCellX = cellX + offsetX;
                    int candidateCellZ = cellZ + offsetZ;

                    if (Hash01(Hash(candidateCellX, candidateCellZ, 0x0A51)) > chance)
                        continue;

                    float baseRadius = Mathf.Max(4f, settings.OasisRadius);
                    float margin = Mathf.Min(cellSize * 0.35f, Mathf.Max(8f, baseRadius + 4f));
                    float centerX = candidateCellX * cellSize + Mathf.Lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x7113)));
                    float centerZ = candidateCellZ * cellSize + Mathf.Lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x4AF9)));
                    float radius = baseRadius * Mathf.Lerp(0.78f, 1.35f, Hash01(Hash(candidateCellX, candidateCellZ, 0x583B)));

                    float distance = math.length(new float2(worldX - centerX, worldZ - centerZ));
                    if (distance > radius)
                        continue;

                    if (found && distance >= bestDistance)
                        continue;

                    float waterRadius = Mathf.Max(2f, settings.OasisWaterRadius);
                    waterRadius *= Mathf.Lerp(0.85f, 1.25f, Hash01(Hash(candidateCellX, candidateCellZ, 0x27C1)));
                    waterRadius = Mathf.Min(waterRadius, radius - 3f);

                    bestDistance = distance;
                    sample = new TreeOasisSample
                    {
                        Influence = 1f - Mathf.Clamp01(distance / Mathf.Max(0.0001f, radius)),
                        IsWater = distance <= waterRadius,
                    };
                    found = true;
                }
            }

            return found;
        }

        private static float SampleTreeDensity01(int worldX, int worldZ)
        {
            NoiseSettings settings = NoiseSettings.Instance;
            if (settings == null)
                return 0.5f;

            settings.GetTerrainVarietyLayers(out _, out _, out NoiseLayer treeDensityLayer);
            float density = TerrainNoiseUtility.Fbm01(new float2(worldX, worldZ), treeDensityLayer, 3, 2.0f, 0.50f);
            density = TerrainNoiseUtility.Redistribute01(density, treeDensityLayer.Redistribution);
            density = TerrainNoiseUtility.Contrast01(density, Mathf.Max(0.5f, settings.TreeDensityContrast));

            float groveNoise = TerrainNoiseUtility.Fbm01(
                new float2(worldX + 183.7f, worldZ - 91.4f),
                treeDensityLayer,
                2,
                2.3f,
                0.45f);

            density = Mathf.Clamp01(density * 0.82f + groveNoise * 0.18f);
            return density;
        }

        private static bool IsGeneratedStructureColumn(int worldX, int worldZ, byte biome)
        {
            if (biome != (byte)BiomeId.Plains &&
                !TerrainNoiseUtility.IsDryDesertBiome(biome) &&
                biome != (byte)BiomeId.Jungle &&
                biome != (byte)BiomeId.Forest &&
                biome != (byte)BiomeId.Snow)
            {
                return false;
            }

            NoiseSettings settings = NoiseSettings.Instance;
            if (settings == null || !settings.EnableStructures)
                return false;

            float chance = Mathf.Clamp01(settings.StructureChance);
            if (biome == (byte)BiomeId.Snow)
                chance = Mathf.Max(chance, 0.13f);
            if (chance <= 0f)
                return false;

            float ruinChance = Mathf.Clamp01(settings.RuinStructureChance);
            int cellSize = Mathf.Max(32, settings.StructureCellSize);
            int cellX = FastFloorToInt(worldX / (float)cellSize);
            int cellZ = FastFloorToInt(worldZ / (float)cellSize);

            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                {
                    int candidateCellX = cellX + offsetX;
                    int candidateCellZ = cellZ + offsetZ;

                    if (Hash01(Hash(candidateCellX, candidateCellZ, 0x57A5)) > chance)
                        continue;

                    float margin = Mathf.Min(cellSize * 0.35f, 12f);
                    int centerX = candidateCellX * cellSize + Mathf.FloorToInt(Mathf.Lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0xB11D))));
                    int centerZ = candidateCellZ * cellSize + Mathf.FloorToInt(Mathf.Lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0xD45F))));
                    if (biome == (byte)BiomeId.Snow)
                    {
                        int dx = worldX - centerX;
                        int dz = worldZ - centerZ;
                        bool snowLodge = Hash01(Hash(candidateCellX, candidateCellZ, 0x5A0B)) < 0.44f;
                        if (snowLodge)
                        {
                            if (Mathf.Abs(dx) <= 9 && Mathf.Abs(dz) <= 11)
                                return true;

                            continue;
                        }

                        bool domeFootprint = dx * dx + dz * dz <= 36;
                        bool tunnelFootprint = Mathf.Abs(dx) <= 2 && dz >= -8 && dz <= -3;
                        if (domeFootprint || tunnelFootprint)
                            return true;

                        continue;
                    }

                    float cellRuinChance = ruinChance;
                    if (biome == (byte)BiomeId.Forest)
                        cellRuinChance = Mathf.Max(cellRuinChance, 0.48f);
                    else if (biome == (byte)BiomeId.Jungle)
                        cellRuinChance = Mathf.Max(cellRuinChance, 0.62f);

                    bool ruinStructure = Hash01(Hash(candidateCellX, candidateCellZ, 0xA17D)) < cellRuinChance;
                    bool largeRuin = ruinStructure && Hash01(Hash(candidateCellX, candidateCellZ, 0x71E3)) < 0.38f;
                    bool largeBuilding = !ruinStructure &&
                                         Hash01(Hash(candidateCellX, candidateCellZ, 0x4A6D)) <
                                         (TerrainNoiseUtility.IsDryDesertBiome(biome) ? 0.52f : 0.42f);
                    int halfWidth;
                    int halfDepth;
                    if (largeRuin)
                    {
                        halfWidth = 9 + (int)(Hash(candidateCellX, candidateCellZ, 0x2C71) % 2u);
                        halfDepth = 8 + (int)(Hash(candidateCellX, candidateCellZ, 0x69EF) % 2u);
                    }
                    else if (ruinStructure)
                    {
                        halfWidth = 5 + (int)(Hash(candidateCellX, candidateCellZ, 0x2C71) % 3u);
                        halfDepth = 4 + (int)(Hash(candidateCellX, candidateCellZ, 0x69EF) % 3u);
                    }
                    else if (largeBuilding && TerrainNoiseUtility.IsDryDesertBiome(biome))
                    {
                        halfWidth = 9;
                        halfDepth = 9;
                    }
                    else if (largeBuilding)
                    {
                        halfWidth = 8;
                        halfDepth = 6;
                    }
                    else
                    {
                        halfWidth = 3 + (int)(Hash(candidateCellX, candidateCellZ, 0x2C71) % 2u);
                        halfDepth = 3 + (int)(Hash(candidateCellX, candidateCellZ, 0x69EF) % 2u);
                    }

                    int padding = ruinStructure ? 4 : 5;

                    if (Mathf.Abs(worldX - centerX) <= halfWidth + padding &&
                        Mathf.Abs(worldZ - centerZ) <= halfDepth + padding)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static uint Hash(int x, int z, int salt)
        {
            unchecked
            {
                uint h = (uint)(NoiseSettings.Instance != null ? NoiseSettings.Instance.Seed : 0);
                h ^= (uint)x * 0x9E3779B9u;
                h ^= (uint)z * 0x85EBCA6Bu;
                h ^= (uint)salt * 0xC2B2AE35u;
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return h;
            }
        }

        private static float Hash01(uint hash)
        {
            return (hash & 0x00FFFFFFu) / 16777215f;
        }

        private static int FastFloorToInt(float value)
        {
            int integer = (int)value;
            return value < integer ? integer - 1 : integer;
        }

        private void RequestMeshData(MeshRequestPriority priority)
        {
            meshRebuildPending = true;
            if (priority > pendingMeshPriority)
                pendingMeshPriority = priority;

            if (priority == MeshRequestPriority.Interactive)
                interactiveMeshUpdatePending = true;

            int requestVersion = ++meshRequestVersion;
            MeshRequestPriority effectivePriority = interactiveMeshUpdatePending
                ? MeshRequestPriority.Interactive
                : pendingMeshPriority;

            if (effectivePriority == MeshRequestPriority.Interactive)
            {
                if (interactiveMeshRequestInFlight)
                    return;
            }
            else if (meshRequestsInFlight > 0)
            {
                return;
            }

            StartMeshDataRequest(requestVersion, effectivePriority);
        }

        private void StartMeshDataRequest(int requestVersion, MeshRequestPriority priority)
        {
            if (Blocks == null)
            {
                meshRebuildPending = false;
                pendingMeshPriority = MeshRequestPriority.Background;
                return;
            }

            // Admission happens before building the halo and lighting snapshots. A
            // rejected request remains coalesced on this chunk and is retried through
            // TerrainGenerator's frame-budgeted dirty queue.
            if (!ChunkMeshGenerator.CanAcceptMeshRequest(priority))
            {
                DeferMeshDataRequest(priority);
                return;
            }

            meshRebuildPending = false;
            pendingMeshPriority = MeshRequestPriority.Background;
            meshRequestsInFlight++;
            if (priority == MeshRequestPriority.Interactive)
                interactiveMeshRequestInFlight = true;

            VoxelBuffer<byte> haloBlocks = null;
            VoxelBuffer<Color32> haloTints = null;
            VoxelBuffer<byte> skylightBlocks = null;
            VoxelBuffer<byte> skyOpenMap = null;
            VoxelBuffer<byte> blockLightBlocks = null;
            bool meshGeneratorOwnsSnapshots = false;

            MeshSnapshotMarker.Begin();
            try
            {
                haloBlocks = BuildHaloBlockArray(usePool: true);
                haloTints = BuildHaloTintArray(usePool: true);
                skylightBlocks = BuildSkylightBlockArray(usePool: true);
                skyOpenMap = BuildSkyOpenMap(usePool: true);
                blockLightBlocks = BuildBlockLightBlockArray(usePool: true);
                // RequestMeshData takes ownership on every return path, including
                // exceptions. Track that handoff so pooled buffers are never returned
                // once here and a second time inside ChunkMeshGenerator.
                meshGeneratorOwnsSnapshots = true;
                bool accepted = ChunkMeshGenerator.RequestMeshData(
                    haloBlocks,
                    haloTints,
                    skylightBlocks,
                    skyOpenMap,
                    blockLightBlocks,
                    meshData => OnMeshDataReceived(meshData, requestVersion, priority),
                    _ => OnMeshDataFailed(requestVersion, priority),
                    priority);

                haloBlocks = null;
                haloTints = null;
                skylightBlocks = null;
                skyOpenMap = null;
                blockLightBlocks = null;
                if (!accepted)
                {
                    ReleaseMeshRequestSlot(priority);
                    DeferMeshDataRequest(priority);
                }
            }
            catch
            {
                if (!meshGeneratorOwnsSnapshots)
                {
                    haloBlocks?.ReturnToPool();
                    haloTints?.ReturnToPool();
                    skylightBlocks?.ReturnToPool();
                    skyOpenMap?.ReturnToPool();
                    blockLightBlocks?.ReturnToPool();
                }
                ReleaseMeshRequestSlot(priority);
                throw;
            }
            finally
            {
                MeshSnapshotMarker.End();
            }
        }

        public VoxelBuffer<byte> BuildHaloBlockArray()
        {
            return BuildHaloBlockArray(usePool: false);
        }

        private VoxelBuffer<byte> BuildHaloBlockArray(bool usePool)
        {
            const int SX = CHUNK_SIZE, SY = CHUNK_HEIGHT, SZ = CHUNK_SIZE;
            var halo = usePool
                ? VoxelBuffer<byte>.Rent(SX + 2, SY + 2, SZ + 2)
                : new VoxelBuffer<byte>(SX + 2, SY + 2, SZ + 2);
            if (usePool)
                halo.Clear();
            byte[] sourceData = Blocks.Data;
            byte[] haloData = halo.Data;
            int sourceSliceStride = Blocks.SliceStride;
            int haloWidth = halo.Width;
            int haloSliceStride = halo.SliceStride;

            for (int z = 0; z < SZ; z++)
            {
                int sourceSlice = z * sourceSliceStride;
                int haloSlice = (z + 1) * haloSliceStride;
                for (int y = 0; y < SY; y++)
                {
                    Array.Copy(
                        sourceData,
                        sourceSlice + y * SX,
                        haloData,
                        haloSlice + (y + 1) * haloWidth + 1,
                        SX);
                }
            }

            Chunk negX = ChunkUtility.GetChunkAtCoordinate(new Vector3Int(Coordinate.x - 1, Coordinate.y, Coordinate.z));
            Chunk posX = ChunkUtility.GetChunkAtCoordinate(new Vector3Int(Coordinate.x + 1, Coordinate.y, Coordinate.z));
            Chunk negY = ChunkUtility.GetChunkAtCoordinate(new Vector3Int(Coordinate.x, Coordinate.y - 1, Coordinate.z));
            Chunk posY = ChunkUtility.GetChunkAtCoordinate(new Vector3Int(Coordinate.x, Coordinate.y + 1, Coordinate.z));
            Chunk negZ = ChunkUtility.GetChunkAtCoordinate(new Vector3Int(Coordinate.x, Coordinate.y, Coordinate.z - 1));
            Chunk posZ = ChunkUtility.GetChunkAtCoordinate(new Vector3Int(Coordinate.x, Coordinate.y, Coordinate.z + 1));

            VoxelBuffer<byte> negXBlocks = negX?.Blocks;
            VoxelBuffer<byte> posXBlocks = posX?.Blocks;
            if (negXBlocks != null || posXBlocks != null)
            {
                for (int z = 0; z < SZ; z++)
                {
                    int haloSlice = (z + 1) * haloSliceStride;
                    int neighborSlice = z * sourceSliceStride;
                    for (int y = 0; y < SY; y++)
                    {
                        int haloRow = haloSlice + (y + 1) * haloWidth;
                        int neighborRow = neighborSlice + y * SX;

                        if (negXBlocks != null)
                            haloData[haloRow] = negXBlocks.Data[neighborRow + SX - 1];

                        if (posXBlocks != null)
                            haloData[haloRow + SX + 1] = posXBlocks.Data[neighborRow];
                    }
                }
            }

            VoxelBuffer<byte> negYBlocks = negY?.Blocks;
            VoxelBuffer<byte> posYBlocks = posY?.Blocks;
            if (negYBlocks != null || posYBlocks != null)
            {
                for (int z = 0; z < SZ; z++)
                {
                    int neighborSlice = z * sourceSliceStride;
                    int haloSlice = (z + 1) * haloSliceStride;

                    if (negYBlocks != null)
                        Array.Copy(negYBlocks.Data, neighborSlice + (SY - 1) * SX, haloData, haloSlice + 1, SX);

                    if (posYBlocks != null)
                        Array.Copy(posYBlocks.Data, neighborSlice, haloData, haloSlice + (SY + 1) * haloWidth + 1, SX);
                }
            }

            VoxelBuffer<byte> negZBlocks = negZ?.Blocks;
            VoxelBuffer<byte> posZBlocks = posZ?.Blocks;
            if (negZBlocks != null || posZBlocks != null)
            {
                for (int y = 0; y < SY; y++)
                {
                    int haloRow = (y + 1) * haloWidth + 1;

                    if (negZBlocks != null)
                        Array.Copy(negZBlocks.Data, (SZ - 1) * sourceSliceStride + y * SX, haloData, haloRow, SX);

                    if (posZBlocks != null)
                        Array.Copy(posZBlocks.Data, y * SX, haloData, (SZ + 1) * haloSliceStride + haloRow, SX);
                }
            }

            return halo;
        }

        public VoxelBuffer<byte> BuildBlockLightBlockArray()
        {
            return BuildBlockLightBlockArray(usePool: false);
        }

        private VoxelBuffer<byte> BuildSkylightBlockArray(bool usePool)
        {
            const int Padding = ChunkMeshGenerator.SkylightPadding;
            const int Width = CHUNK_SIZE + 2 + Padding * 2;
            const int Height = CHUNK_HEIGHT + 2;
            const int Depth = CHUNK_SIZE + 2 + Padding * 2;

            var snapshot = usePool
                ? VoxelBuffer<byte>.Rent(Width, Height, Depth)
                : new VoxelBuffer<byte>(Width, Height, Depth);

            try
            {
                // Unknown streaming space is opaque so a partially loaded world cannot
                // create temporary shafts of sunlight. Loaded neighbors within the full
                // 15-step horizontal attenuation radius overwrite this sentinel value.
                byte[] snapshotData = snapshot.Data;
                for (int i = 0; i < snapshotData.Length; i++)
                    snapshotData[i] = byte.MaxValue;

                int minWorldX = Coordinate.x * CHUNK_SIZE - 1 - Padding;
                int minWorldY = Coordinate.y * CHUNK_HEIGHT - 1;
                int minWorldZ = Coordinate.z * CHUNK_SIZE - 1 - Padding;
                int maxWorldX = minWorldX + Width;
                int maxWorldY = minWorldY + Height;
                int maxWorldZ = minWorldZ + Depth;

                // X/Z padding reaches at most the immediate 3x3 neighborhood. Y keeps
                // the original one-cell mesh halo so the existing sky-above test remains
                // anchored immediately above this chunk rather than above a padded volume.
                for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                {
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            var coordinate = new Vector3Int(
                                Coordinate.x + offsetX,
                                Coordinate.y + offsetY,
                                Coordinate.z + offsetZ);
                            if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk sourceChunk) ||
                                sourceChunk?.Blocks == null)
                            {
                                continue;
                            }

                            int chunkMinX = coordinate.x * CHUNK_SIZE;
                            int chunkMinY = coordinate.y * CHUNK_HEIGHT;
                            int chunkMinZ = coordinate.z * CHUNK_SIZE;
                            int copyMinX = Math.Max(minWorldX, chunkMinX);
                            int copyMinY = Math.Max(minWorldY, chunkMinY);
                            int copyMinZ = Math.Max(minWorldZ, chunkMinZ);
                            int copyMaxX = Math.Min(maxWorldX, chunkMinX + CHUNK_SIZE);
                            int copyMaxY = Math.Min(maxWorldY, chunkMinY + CHUNK_HEIGHT);
                            int copyMaxZ = Math.Min(maxWorldZ, chunkMinZ + CHUNK_SIZE);
                            int copyWidth = copyMaxX - copyMinX;
                            if (copyWidth <= 0 || copyMaxY <= copyMinY || copyMaxZ <= copyMinZ)
                                continue;

                            byte[] sourceData = sourceChunk.Blocks.Data;
                            int sourceSliceStride = sourceChunk.Blocks.SliceStride;
                            for (int worldZ = copyMinZ; worldZ < copyMaxZ; worldZ++)
                            {
                                int sourceZ = worldZ - chunkMinZ;
                                int targetZ = worldZ - minWorldZ;
                                for (int worldY = copyMinY; worldY < copyMaxY; worldY++)
                                {
                                    int sourceY = worldY - chunkMinY;
                                    int targetY = worldY - minWorldY;
                                    Array.Copy(
                                        sourceData,
                                        (copyMinX - chunkMinX) + sourceY * CHUNK_SIZE + sourceZ * sourceSliceStride,
                                        snapshotData,
                                        (copyMinX - minWorldX) + targetY * Width + targetZ * snapshot.SliceStride,
                                        copyWidth);
                                }
                            }
                        }
                    }
                }

                return snapshot;
            }
            catch
            {
                snapshot.ReturnToPool();
                throw;
            }
        }

        private VoxelBuffer<byte> BuildBlockLightBlockArray(bool usePool)
        {
            const int Padding = ChunkMeshGenerator.BlockLightPadding;
            const int MeshHaloWidth = CHUNK_SIZE + 2;
            const int MeshHaloHeight = CHUNK_HEIGHT + 2;
            const int MeshHaloDepth = CHUNK_SIZE + 2;
            const int Width = MeshHaloWidth + Padding * 2;
            const int Height = MeshHaloHeight + Padding * 2;
            const int Depth = MeshHaloDepth + Padding * 2;

            var snapshot = usePool
                ? VoxelBuffer<byte>.Rent(Width, Height, Depth)
                : new VoxelBuffer<byte>(Width, Height, Depth);

            try
            {

            // Missing chunks are deliberately opaque. Otherwise a source in one
            // loaded island could leak through an unloaded streaming gap.
            byte[] snapshotData = snapshot.Data;
            for (int i = 0; i < snapshotData.Length; i++)
                snapshotData[i] = byte.MaxValue;

            int minWorldX = Coordinate.x * CHUNK_SIZE - 1 - Padding;
            int minWorldY = Coordinate.y * CHUNK_HEIGHT - 1 - Padding;
            int minWorldZ = Coordinate.z * CHUNK_SIZE - 1 - Padding;
            int maxWorldX = minWorldX + Width;
            int maxWorldY = minWorldY + Height;
            int maxWorldZ = minWorldZ + Depth;

            // With a 15-block pad and 32-block chunks this volume overlaps at
            // most the immediate 3x3x3 neighborhood. Copy one row at a time so
            // dictionary lookups stay per chunk, never per voxel.
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        var coordinate = new Vector3Int(
                            Coordinate.x + offsetX,
                            Coordinate.y + offsetY,
                            Coordinate.z + offsetZ);
                        if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk sourceChunk) ||
                            sourceChunk?.Blocks == null)
                        {
                            continue;
                        }

                        int chunkMinX = coordinate.x * CHUNK_SIZE;
                        int chunkMinY = coordinate.y * CHUNK_HEIGHT;
                        int chunkMinZ = coordinate.z * CHUNK_SIZE;
                        int copyMinX = Math.Max(minWorldX, chunkMinX);
                        int copyMinY = Math.Max(minWorldY, chunkMinY);
                        int copyMinZ = Math.Max(minWorldZ, chunkMinZ);
                        int copyMaxX = Math.Min(maxWorldX, chunkMinX + CHUNK_SIZE);
                        int copyMaxY = Math.Min(maxWorldY, chunkMinY + CHUNK_HEIGHT);
                        int copyMaxZ = Math.Min(maxWorldZ, chunkMinZ + CHUNK_SIZE);
                        int copyWidth = copyMaxX - copyMinX;
                        if (copyWidth <= 0 || copyMaxY <= copyMinY || copyMaxZ <= copyMinZ)
                            continue;

                        byte[] sourceData = sourceChunk.Blocks.Data;
                        int sourceSliceStride = sourceChunk.Blocks.SliceStride;
                        for (int worldZ = copyMinZ; worldZ < copyMaxZ; worldZ++)
                        {
                            int sourceZ = worldZ - chunkMinZ;
                            int targetZ = worldZ - minWorldZ;
                            for (int worldY = copyMinY; worldY < copyMaxY; worldY++)
                            {
                                int sourceY = worldY - chunkMinY;
                                int targetY = worldY - minWorldY;
                                Array.Copy(
                                    sourceData,
                                    (copyMinX - chunkMinX) + sourceY * CHUNK_SIZE + sourceZ * sourceSliceStride,
                                    snapshotData,
                                    (copyMinX - minWorldX) + targetY * Width + targetZ * snapshot.SliceStride,
                                    copyWidth);
                            }
                        }
                    }
                }
            }

            return snapshot;
            }
            catch
            {
                snapshot.ReturnToPool();
                throw;
            }
        }

        public VoxelBuffer<Color32> BuildHaloTintArray()
        {
            return BuildHaloTintArray(usePool: false);
        }

        private VoxelBuffer<byte> BuildSkyOpenMap(bool usePool)
        {
            const int Padding = ChunkMeshGenerator.SkylightPadding;
            const int HaloWidth = CHUNK_SIZE + 2 + Padding * 2;
            const int HaloDepth = CHUNK_SIZE + 2 + Padding * 2;

            var skyOpenMap = usePool
                ? VoxelBuffer<byte>.Rent(HaloWidth, 1, HaloDepth)
                : new VoxelBuffer<byte>(HaloWidth, 1, HaloDepth);
            try
            {
                if (usePool)
                    skyOpenMap.Clear();

                bool[] occlusionFlags = GetSkylightOcclusionFlags();
                var columns = new Dictionary<Vector2Int, SkylightColumnInfo>(9);
                int minWorldX = Coordinate.x * CHUNK_SIZE - 1 - Padding;
                int minWorldZ = Coordinate.z * CHUNK_SIZE - 1 - Padding;

                EnsureSkylightColumnCacheEpoch();

            for (int z = 0; z < HaloDepth; z++)
            {
                for (int x = 0; x < HaloWidth; x++)
                {
                    Vector3Int sampleChunk = ChunkUtility.GetChunkCoordinateFromPosition(
                        new Vector3Int(minWorldX + x, 0, minWorldZ + z));
                    var columnKey = new Vector2Int(sampleChunk.x, sampleChunk.z);
                    if (!columns.ContainsKey(columnKey))
                    {
                        columns.Add(
                            columnKey,
                            SkylightColumnCache.TryGetValue(columnKey, out SkylightColumnInfo cachedColumn)
                                ? cachedColumn
                                : SkylightColumnInfo.Unknown);
                    }
                }
            }

            int topHaloWorldY = (Coordinate.y + 1) * CHUNK_HEIGHT;
            byte[] mapData = skyOpenMap.Data;
            for (int z = 0; z < HaloDepth; z++)
            {
                for (int x = 0; x < HaloWidth; x++)
                {
                    bool xEdge = x == 0 || x == HaloWidth - 1;
                    bool zEdge = z == 0 || z == HaloDepth - 1;
                    if (xEdge && zEdge)
                        continue;

                    int worldX = minWorldX + x;
                    int worldZ = minWorldZ + z;
                    Vector3Int sampleChunk = ChunkUtility.GetChunkCoordinateFromPosition(
                        new Vector3Int(worldX, 0, worldZ));
                    var columnKey = new Vector2Int(sampleChunk.x, sampleChunk.z);
                    if (!columns.TryGetValue(columnKey, out SkylightColumnInfo column) ||
                        !column.HasTerrainTop)
                    {
                        // Missing terrain-column evidence is deliberately dark. In
                        // deep streaming gaps, treating an unloaded chunk as air
                        // would make sealed caves flash bright.
                        continue;
                    }

                    if (HasUnoccludedSkyAbove(
                        worldX,
                        worldZ,
                        topHaloWorldY,
                        sampleChunk.x,
                        sampleChunk.z,
                        column,
                        occlusionFlags))
                    {
                        mapData[x + z * skyOpenMap.SliceStride] = 1;
                    }
                }
            }

                return skyOpenMap;
            }
            catch
            {
                skyOpenMap.ReturnToPool();
                throw;
            }
        }

        private void RecordSkylightColumnInfo()
        {
            if (Blocks == null && HighestGroundLevel == short.MinValue)
                return;

            EnsureSkylightColumnCacheEpoch();

            var columnKey = new Vector2Int(Coordinate.x, Coordinate.z);
            if (!SkylightColumnCache.TryGetValue(columnKey, out SkylightColumnInfo column))
                column = SkylightColumnInfo.Unknown;

            if (HighestGroundLevel != short.MinValue)
            {
                column.HasTerrainTop = true;
                column.TerrainTopWorldY = Math.Max(column.TerrainTopWorldY, HighestGroundLevel);
            }

            if (Blocks != null)
                column.HighestLoadedChunkY = Math.Max(column.HighestLoadedChunkY, Coordinate.y);

            SkylightColumnCache[columnKey] = column;
        }

        private static void EnsureSkylightColumnCacheEpoch()
        {
            int worldEpoch = TerrainGenerator.CurrentWorldEpoch;
            if (skylightColumnCacheWorldEpoch == worldEpoch)
                return;

            SkylightColumnCache.Clear();
            skylightColumnCacheWorldEpoch = worldEpoch;
        }

        private bool HasUnoccludedSkyAbove(
            int worldX,
            int worldZ,
            int topHaloWorldY,
            int chunkX,
            int chunkZ,
            SkylightColumnInfo column,
            bool[] occlusionFlags)
        {
            int terrainTopChunkY = Mathf.FloorToInt(column.TerrainTopWorldY / (float)CHUNK_HEIGHT);
            int firstChunkY = Coordinate.y + 1;
            int lastChunkY = Math.Max(terrainTopChunkY, column.HighestLoadedChunkY);

            // Being above a known terrain top is positive evidence of open sky,
            // even when there is no higher chunk to scan.
            if (lastChunkY < firstChunkY)
                return topHaloWorldY > column.TerrainTopWorldY;

            int localX = worldX - chunkX * CHUNK_SIZE;
            int localZ = worldZ - chunkZ * CHUNK_SIZE;
            for (int chunkY = firstChunkY; chunkY <= lastChunkY; chunkY++)
            {
                var coordinate = new Vector3Int(chunkX, chunkY, chunkZ);
                if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk) ||
                    chunk?.Blocks == null)
                {
                    // A gap below either the known surface or another loaded upper
                    // chunk cannot safely be classified as sky.
                    return false;
                }

                // Include local y=0 of the chunk above. The block halo contains
                // that plane for face neighbors, but not its edge intersections;
                // including it here prevents light leaking through those omitted
                // two-axis halo cells.
                int firstLocalY = 0;
                byte[] blocks = chunk.Blocks.Data;
                int sliceStart = localZ * chunk.Blocks.SliceStride;
                for (int localY = firstLocalY; localY < CHUNK_HEIGHT; localY++)
                {
                    byte blockId = blocks[sliceStart + localY * CHUNK_SIZE + localX];
                    if (blockId != BLOCK_AIR &&
                        ((uint)blockId >= (uint)occlusionFlags.Length || occlusionFlags[blockId]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool[] GetSkylightOcclusionFlags()
        {
            BlockData[] definitions = AssetsContainer.Instance.Blocks;
            if (ReferenceEquals(definitions, cachedSkylightDefinitions) &&
                cachedSkylightOcclusionFlags != null &&
                cachedSkylightOcclusionFlags.Length == definitions.Length)
            {
                return cachedSkylightOcclusionFlags;
            }

            var flags = new bool[definitions.Length];
            for (int blockId = 0; blockId < definitions.Length; blockId++)
            {
                BlockData definition = definitions[blockId];
                flags[blockId] = blockId != BLOCK_AIR &&
                                 (definition == null || definition.OccludesNeighborFaces);
            }

            cachedSkylightDefinitions = definitions;
            cachedSkylightOcclusionFlags = flags;
            return flags;
        }

        private static byte[] GetBlockLightEmissionLevels()
        {
            BlockData[] definitions = AssetsContainer.Instance.Blocks;
            if (ReferenceEquals(definitions, cachedBlockLightDefinitions) &&
                cachedBlockLightEmissionLevels != null &&
                cachedBlockLightEmissionLevels.Length == definitions.Length)
            {
                return cachedBlockLightEmissionLevels;
            }

            var levels = new byte[definitions.Length];
            for (int blockId = 0; blockId < definitions.Length; blockId++)
            {
                BlockData definition = definitions[blockId];
                if (definition != null)
                    levels[blockId] = (byte)Mathf.Clamp(definition.LightEmission, 0, ChunkMeshGenerator.MaximumBlockLight);
            }

            cachedBlockLightDefinitions = definitions;
            cachedBlockLightEmissionLevels = levels;
            return levels;
        }

        private struct SkylightColumnInfo
        {
            public bool HasTerrainTop;
            public int TerrainTopWorldY;
            public int HighestLoadedChunkY;

            public static SkylightColumnInfo Unknown => new SkylightColumnInfo
            {
                TerrainTopWorldY = int.MinValue,
                HighestLoadedChunkY = int.MinValue
            };
        }

        private VoxelBuffer<Color32> BuildHaloTintArray(bool usePool)
        {
            const int SX = CHUNK_SIZE, SY = CHUNK_HEIGHT, SZ = CHUNK_SIZE;

            if (blockTints == null && blockTintsNeedLazyRebuild)
            {
                blockTintsNeedLazyRebuild = false;
                if (ContainsTintedBlocks())
                    EnsureBlockTintArray();
            }

            if (blockTints == null)
                return null;

            var halo = usePool
                ? VoxelBuffer<Color32>.Rent(SX + 2, SY + 2, SZ + 2)
                : new VoxelBuffer<Color32>(SX + 2, SY + 2, SZ + 2);
            if (usePool)
                halo.Clear();

            Color32[] sourceData = blockTints.Data;
            Color32[] haloData = halo.Data;
            int sourceSliceStride = blockTints.SliceStride;
            int haloWidth = halo.Width;
            int haloSliceStride = halo.SliceStride;

            for (int z = 0; z < SZ; z++)
            {
                int sourceSlice = z * sourceSliceStride;
                int haloSlice = (z + 1) * haloSliceStride;
                for (int y = 0; y < SY; y++)
                {
                    Array.Copy(
                        sourceData,
                        sourceSlice + y * SX,
                        haloData,
                        haloSlice + (y + 1) * haloWidth + 1,
                        SX);
                }
            }

            return halo;
        }

        private void OnMeshDataReceived(
            [ReadOnly] MeshData meshData,
            int requestVersion,
            MeshRequestPriority priority)
        {
            bool appliedLatestMesh = false;
            bool retryLatestMesh = false;
            MeshApplicationMarker.Begin();
            try
            {
                if (requestVersion != meshRequestVersion)
                    return;

                voxelLighting = meshData.VoxelLighting;
                if (MeshFilter == null)
                {
                    appliedLatestMesh = true;
                    return;
                }

                MeshSection solidMeshData = meshData.SolidMesh;
                MeshSection fluidMeshData = meshData.FluidMesh;
                MeshSection lavaFluidMeshData = meshData.LavaFluidMesh;
                MeshSection transparentMeshData = meshData.TransparentMesh;

                if (lavaFluidMeshData.Vertices.Length > 0 && LavaFluidFilter == null)
                    PrepareLavaFluidRenderer();

                Mesh solidMesh = ApplyMeshSection(MeshFilter, ref this.solidMesh, solidMeshData, "SolidMesh");
                ApplyMeshSection(FluidFilter, ref this.fluidMesh, fluidMeshData, "FluidMesh");
                ApplyMeshSection(LavaFluidFilter, ref this.lavaFluidMesh, lavaFluidMeshData, "LavaFluidMesh");
                Mesh transparentMesh = ApplyMeshSection(TransparentFilter, ref this.transparentMesh, transparentMeshData, "TransparentMesh");


                if (MeshCollider != null && MeshCollider.enabled)
                {
                    if (solidMeshData.Vertices.Length == 0 || solidMeshData.Triangles.Length == 0)
                        MeshCollider.sharedMesh = null;
                    else
                    {
                        MeshCollider.sharedMesh = null;
                        MeshCollider.sharedMesh = solidMesh;
                    }

                }

                if (TransparentMeshCollider != null && TransparentMeshCollider.enabled)
                {
                    if (transparentMeshData.Vertices.Length == 0 || transparentMeshData.Triangles.Length == 0)
                        TransparentMeshCollider.sharedMesh = null;
                    else
                    {
                        TransparentMeshCollider.sharedMesh = null;
                        TransparentMeshCollider.sharedMesh = transparentMesh;
                    }
                }

                appliedLatestMesh = true;
            }
            catch (Exception exception)
            {
                if (requestVersion == meshRequestVersion)
                {
                    meshRebuildPending = false;
                    interactiveMeshUpdatePending = false;
                    pendingMeshPriority = MeshRequestPriority.Background;
                    retryLatestMesh = true;
                }

                Debug.LogException(exception);
            }
            finally
            {
                if (appliedLatestMesh)
                    interactiveMeshUpdatePending = false;

                CompleteMeshDataRequest(priority);

                if (retryLatestMesh)
                    QueueMeshRetry(priority == MeshRequestPriority.Interactive);

                MeshApplicationMarker.End();
            }
        }

        private void OnMeshDataFailed(int requestVersion, MeshRequestPriority priority)
        {
            bool retryLatestMesh = requestVersion == meshRequestVersion;
            if (requestVersion == meshRequestVersion)
            {
                meshRebuildPending = false;
                interactiveMeshUpdatePending = false;
                pendingMeshPriority = MeshRequestPriority.Background;
            }

            CompleteMeshDataRequest(priority);

            if (retryLatestMesh)
                QueueMeshRetry(priority == MeshRequestPriority.Interactive);
        }

        private void QueueMeshRetry(bool prioritizeForInteraction = false)
        {
            // IsGenerated means the block-data phase has completed; it must not make a
            // failed mesh request terminal. The generator throttles dirty rebuilds, so
            // retrying through it also avoids a tight failure loop on the main thread.
            if (Blocks != null && IsGenerated)
                TerrainGenerator.QueueChunkMeshRetry(this, prioritizeForInteraction);
        }

        private void DeferMeshDataRequest(MeshRequestPriority priority)
        {
            meshRebuildPending = true;
            if (priority > pendingMeshPriority)
                pendingMeshPriority = priority;
            if (priority == MeshRequestPriority.Interactive)
                interactiveMeshUpdatePending = true;

            QueueMeshRetry(priority == MeshRequestPriority.Interactive);
        }

        private void CompleteMeshDataRequest(MeshRequestPriority priority)
        {
            ReleaseMeshRequestSlot(priority);

            if (!meshRebuildPending || Blocks == null)
                return;

            MeshRequestPriority nextPriority = interactiveMeshUpdatePending
                ? MeshRequestPriority.Interactive
                : pendingMeshPriority;

            bool canStart = nextPriority == MeshRequestPriority.Interactive
                ? !interactiveMeshRequestInFlight
                : meshRequestsInFlight == 0;
            if (!canStart)
                return;

            // This callback is already inside the measured mesh-application lane.
            // Queue the coalesced follow-up so snapshot construction is admitted as a
            // separate operation on a later pass instead of being charged twice (and
            // extending one indivisible callback spike).
            DeferMeshDataRequest(nextPriority);
        }

        private void ReleaseMeshRequestSlot(MeshRequestPriority priority)
        {
            meshRequestsInFlight = Mathf.Max(0, meshRequestsInFlight - 1);
            if (priority == MeshRequestPriority.Interactive)
                interactiveMeshRequestInFlight = false;
        }

        private Mesh ApplyMeshSection(MeshFilter filter, ref Mesh mesh, MeshSection meshData, string meshName)
        {
            if (filter == null)
                return null;

            if (mesh == null)
            {
                mesh = new Mesh
                {
                    name = $"Chunk_{Coordinate.x}_{Coordinate.y}_{Coordinate.z}_{meshName}"
                };
                mesh.MarkDynamic();
            }

            mesh.Clear(false);
            bool needs32 = meshData.Vertices.Length > ushort.MaxValue ||
                           meshData.Triangles.Length > ushort.MaxValue;
            mesh.indexFormat = needs32
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            if (meshData.Vertices.Length > 0)
            {
                mesh.vertices = meshData.Vertices;
                mesh.triangles = meshData.Triangles;
                mesh.normals = meshData.Normals;
                mesh.uv = meshData.Uvs;
                mesh.uv2 = meshData.TextureLayers;
                mesh.uv3 = meshData.Lighting;
                mesh.uv4 = meshData.AmbientOcclusion;
                mesh.colors32 = meshData.Colors;
                mesh.bounds = ChunkMeshBounds;
            }

            if (filter.sharedMesh != mesh)
                filter.sharedMesh = mesh;

            return mesh;
        }

        private void ClearMeshes()
        {
            meshRequestVersion++;
            meshRebuildPending = false;

            if (MeshFilter != null)
                MeshFilter.sharedMesh = null;
            if (solidMesh != null)
                solidMesh.Clear(false);

            if (FluidFilter != null)
                FluidFilter.sharedMesh = null;
            if (fluidMesh != null)
                fluidMesh.Clear(false);

            if (LavaFluidFilter != null)
                LavaFluidFilter.sharedMesh = null;
            if (lavaFluidMesh != null)
                lavaFluidMesh.Clear(false);

            if (TransparentFilter != null)
                TransparentFilter.sharedMesh = null;
            if (transparentMesh != null)
                transparentMesh.Clear(false);

            if (MeshCollider != null)
                MeshCollider.sharedMesh = null;

            if (TransparentMeshCollider != null)
                TransparentMeshCollider.sharedMesh = null;
        }

        public void SetActive(bool enabled)
        {
            if (!enabled)
                SetMeshCollidersEnabled(false);

            if (GameObject != null && GameObject.activeSelf != enabled)
                GameObject.SetActive(enabled);
        }

        [BurstCompile]
        public struct FindGeneratedFluidFrontierJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Blocks;
            [WriteOnly] public NativeArray<uint> FrontierMasks;
            [ReadOnly] public int ChunkSize;
            [ReadOnly] public int ChunkHeight;

            public void Execute(int index)
            {
                int x = index % ChunkSize;
                int z = index / ChunkSize;
                int sliceStride = ChunkSize * ChunkHeight;
                int columnStart = z * sliceStride + x;
                uint mask = 0u;

                for (int y = 0; y < ChunkHeight; y++)
                {
                    int blockIndex = columnStart + y * ChunkSize;
                    byte fluidBlockId = Blocks[blockIndex];
                    if (fluidBlockId != BLOCK_WATER && fluidBlockId != BLOCK_LAVA)
                        continue;

                    bool isFrontier = y == 0 ||
                                      x == 0 ||
                                      x == ChunkSize - 1 ||
                                      z == 0 ||
                                      z == ChunkSize - 1;

                    if (!isFrontier)
                    {
                        isFrontier = IsFluidTarget(fluidBlockId, Blocks[blockIndex - ChunkSize]) ||
                                     IsFluidTarget(fluidBlockId, Blocks[blockIndex - 1]) ||
                                     IsFluidTarget(fluidBlockId, Blocks[blockIndex + 1]) ||
                                     IsFluidTarget(fluidBlockId, Blocks[blockIndex - sliceStride]) ||
                                     IsFluidTarget(fluidBlockId, Blocks[blockIndex + sliceStride]);
                    }

                    if (isFrontier)
                        mask |= 1u << y;
                }

                FrontierMasks[index] = mask;
            }

            private static bool IsFluidTarget(byte fluidBlockId, byte targetBlockId)
            {
                return targetBlockId == BLOCK_AIR ||
                       fluidBlockId == BLOCK_WATER && targetBlockId == BLOCK_LAVA ||
                       fluidBlockId == BLOCK_LAVA && targetBlockId == BLOCK_WATER;
            }
        }

        [BurstCompile]
        public struct GenerateBlocksJob : IJobParallelFor
        {
            [WriteOnly, NativeDisableParallelForRestriction]
            public NativeArray<byte> Blocks;
            [ReadOnly] public NativeArray<int> HeightMap;
            [ReadOnly] public NativeArray<byte> BiomeMap;
            [ReadOnly] public NativeArray<byte> SurfaceBiomeMap;
            [ReadOnly] public NativeArray<byte> BiomeBlendMap;
            [ReadOnly] public NativeArray<byte> DesertEdgeMap;
            [ReadOnly] public NativeArray<byte> RiverMap;
            [ReadOnly] public NativeArray<int> RiverSurfaceMap;
            [ReadOnly] public int ChunkSize;
            [ReadOnly] public int ChunkHeight;
            [ReadOnly] public int GroundOffset;
            [ReadOnly] public int3 ChunkCoordinate;
            [ReadOnly] public bool EnableCaves;
            [ReadOnly] public NoiseSettings.CaveNoiseSettings CaveNoise;
            [ReadOnly] public NoiseSettings.LushCaveBiomeSettings LushCaveBiome;
            [ReadOnly] public bool GenerateLushCaveTrees;
            [ReadOnly] public float3 CaveNoiseRuntimeOffset;
            [ReadOnly] public float2 NoiseOffset;
            [ReadOnly] public int WaterLevel;
            [ReadOnly] public int Seed;
            [ReadOnly] public int BedrockLevel;
            [ReadOnly] public int BedrockThickness;
            [ReadOnly] public bool EnableOases;
            [ReadOnly] public int OasisCellSize;
            [ReadOnly] public float OasisChance;
            [ReadOnly] public int OasisRadius;
            [ReadOnly] public int OasisWaterRadius;
            [ReadOnly] public bool EnableStructures;
            [ReadOnly] public int StructureCellSize;
            [ReadOnly] public float StructureChance;
            [ReadOnly] public float RuinStructureChance;

            [ReadOnly] public float CaveHorizontalFrequency;
            [ReadOnly] public float CaveVerticalFrequency;
            [ReadOnly] public float TunnelHorizontalFrequency;
            [ReadOnly] public float TunnelVerticalFrequency;
            [ReadOnly] public float RoomFrequency;

            public void Execute(int index)
            {
                int x = index % ChunkSize;
                int z = index / ChunkSize;

                int heightMapIndex = z * ChunkSize + x;
                int groundLevel = HeightMap[heightMapIndex];
                byte biome = BiomeMap[heightMapIndex];
                byte surfaceBiome = SurfaceBiomeMap[heightMapIndex];
                float biomeTransition = BiomeBlendMap[heightMapIndex] / 255f;
                float desertEdgeStrength = DesertEdgeMap[heightMapIndex] / 255f;
                float riverStrength = RiverMap[heightMapIndex] / 255f;
                int riverSurfaceLevel = RiverSurfaceMap[heightMapIndex];

                int worldX = ChunkCoordinate.x * ChunkSize + x;
                int worldZ = ChunkCoordinate.z * ChunkSize + z;
                int worldYOrigin = ChunkCoordinate.y * ChunkHeight;
                int waterLevel = GroundOffset + this.WaterLevel;
                OasisSample oasis = default;
                bool hasOasis = TerrainNoiseUtility.IsDryDesertBiome(biome) && TryGetOasisSample(worldX, worldZ, out oasis);
                bool allowSurfaceConnections = IsSurfaceCaveConnectionAllowed(
                    biome,
                    groundLevel,
                    waterLevel,
                    riverStrength,
                    riverSurfaceLevel,
                    hasOasis);
                // A vertical water-cave shaft needs a wider dry buffer than a normal
                // cave mouth. RiverMap includes river banks and lake shores, so a
                // zero-strength column keeps generated source water away from it.
                bool allowWaterCaveEntrance =
                    allowSurfaceConnections &&
                    riverStrength <= 0f &&
                    riverSurfaceLevel == int.MinValue;
                int maximumSurfaceConnectionDepth = math.max(
                    96,
                    math.max(12, CaveNoise.SurfaceBreakthroughProbeDepth) + 2);
                bool surfaceConnectionIntersectsChunk =
                    allowSurfaceConnections &&
                    worldYOrigin <= groundLevel + 1 &&
                    worldYOrigin + ChunkHeight - 1 >= groundLevel - maximumSurfaceConnectionDepth;
                int surfaceBreakthroughTargetDepth = surfaceConnectionIntersectsChunk
                    ? GetSurfaceCaveBreakthroughTargetDepth(worldX, worldZ, groundLevel)
                    : -1;
                WaterCaveColumnSample waterCave = default;
                bool hasWaterCave =
                    CanWaterCaveIntersectChunk(
                        worldYOrigin,
                        groundLevel,
                        waterLevel,
                        allowWaterCaveEntrance) &&
                    TryGetWaterCaveColumnSample(
                        worldX,
                        worldZ,
                        groundLevel,
                        waterLevel,
                        allowWaterCaveEntrance,
                        out waterCave);
                LushCaveColumnSample lushCave = default;
                bool hasLushCave =
                    CanLushCaveIntersectChunk(worldYOrigin, waterLevel) &&
                    TryGetLushCaveColumnSample(worldX, worldZ, groundLevel, waterLevel, out lushCave);
                int blockColumnStart = z * ChunkSize * ChunkHeight + x;

                for (int y = 0; y < ChunkHeight; y++)
                {
                    int blockIndex = blockColumnStart + y * ChunkSize;
                    int worldY = worldYOrigin + y;
                    int depthBelowSurface = groundLevel - worldY;
                    float3 worldPosition = new float3(worldX, worldY, worldZ);
                    byte blockId;
                    bool caveCarvingHandled = false;
                    bool outsideLushCaveRange =
                        !hasLushCave ||
                        worldY < lushCave.LowestEditedY ||
                        worldY > lushCave.CeilingY;

                    if (ShouldPlaceBedrock(worldX, worldY, worldZ))
                    {
                        blockId = BLOCK_BEDROCK;
                    }
                    else if (hasWaterCave && TryGetWaterCaveBlock(waterCave, worldPosition, out blockId))
                    {
                    }
                    else if (surfaceConnectionIntersectsChunk &&
                             ShouldCarveSurfaceCaveConnection(
                                 worldPosition,
                                 groundLevel,
                                 depthBelowSurface,
                                 surfaceBreakthroughTargetDepth) &&
                             (!hasLushCave ||
                              worldY > math.max(lushCave.FloorY, lushCave.RiverSurfaceY)))
                    {
                        // Surface connectors stay dry and continuous. Regular cave fill
                        // decoration (lava, webs, frost) is intentionally not applied.
                        blockId = BLOCK_AIR;
                    }
                    else if (!hasOasis && TryGetStructureBlock(biome, worldX, worldY, worldZ, groundLevel, waterLevel, riverStrength, riverSurfaceLevel, out blockId))
                    {
                    }
                    else if (TryGetSnowCoverBlock(
                                 surfaceBiome,
                                 worldX,
                                 worldY,
                                 worldZ,
                                 groundLevel,
                                 waterLevel,
                                 riverStrength,
                                 riverSurfaceLevel,
                                 out blockId))
                    {
                    }
                    else if (worldY > groundLevel)
                    {
                        if (riverSurfaceLevel != int.MinValue && worldY <= riverSurfaceLevel)
                        {
                            blockId = GetGeneratedWaterBlock(surfaceBiome, worldY, riverSurfaceLevel);
                        }
                        else if (worldY <= waterLevel)
                        {
                            blockId = GetGeneratedWaterBlock(surfaceBiome, worldY, waterLevel);
                        }
                        else
                            blockId = BLOCK_AIR;
                    }
                    else
                    {
                        if (hasLushCave && TryGetLushCaveBiomeBlock(lushCave, worldPosition, out blockId))
                        {
                        }
                        else if (hasOasis && TryGetOasisBlock(oasis, worldY, groundLevel, depthBelowSurface, out blockId))
                        {
                        }
                        else if (worldY == groundLevel)
                        {
                            blockId = GetSurfaceBlock(biome, surfaceBiome, groundLevel, waterLevel, worldPosition, biomeTransition, desertEdgeStrength, riverStrength, riverSurfaceLevel);
                        }
                        else if (depthBelowSurface < GetSoilDepth(biome, surfaceBiome))
                        {
                            blockId = GetSubsurfaceBlock(biome, surfaceBiome, worldPosition, depthBelowSurface, biomeTransition, desertEdgeStrength, riverStrength, waterLevel);
                        }
                        else
                        {
                            // Underground material selection samples ores, magma and
                            // several decorative noise fields. A carved cave discards
                            // that result, so determine carving first and only sample
                            // the solid material when this voxel will keep it.
                            caveCarvingHandled = true;
                            blockId = outsideLushCaveRange && ShouldCarveCave(worldPosition, groundLevel)
                                ? GetCaveFillBlock(biome, worldPosition, groundLevel, depthBelowSurface)
                                : GetUndergroundBlock(biome, worldPosition, groundLevel, waterLevel, depthBelowSurface);
                        }

                        if (!caveCarvingHandled && outsideLushCaveRange)
                        {
                            if (blockId != BLOCK_AIR && blockId != BLOCK_BEDROCK && blockId != BLOCK_WATER &&
                                ShouldCarveCave(worldPosition, groundLevel))
                            {
                                blockId = GetCaveFillBlock(biome, worldPosition, groundLevel, depthBelowSurface);
                            }
                        }
                    }

                    Blocks[blockIndex] = blockId;
                }
            }

            private bool IsSurfaceCaveConnectionAllowed(
                byte biome,
                int groundLevel,
                int waterLevel,
                float riverStrength,
                int riverSurfaceLevel,
                bool hasOasis)
            {
                if (!EnableCaves ||
                    !CaveNoise.EnableSurfaceConnections ||
                    hasOasis ||
                    biome == (byte)BiomeId.Ocean ||
                    biome == (byte)BiomeId.Beach)
                {
                    return false;
                }

                int minimumGroundClearance = math.max(
                    3,
                    CaveNoise.SurfaceConnectionMinGroundAboveWater > 0
                        ? CaveNoise.SurfaceConnectionMinGroundAboveWater
                        : 6);
                if (groundLevel <= waterLevel + minimumGroundClearance ||
                    riverStrength > 0.12f ||
                    (riverSurfaceLevel != int.MinValue && groundLevel <= riverSurfaceLevel + 4))
                {
                    return false;
                }

                return true;
            }

            private bool ShouldCarveSurfaceCaveConnection(
                float3 worldPosition,
                int groundLevel,
                int depthBelowSurface,
                int breakthroughTargetDepth)
            {
                return ShouldCarveSurfaceCaveBreakthrough(depthBelowSurface, breakthroughTargetDepth) ||
                       ShouldCarveSurfaceCaveEntrance(worldPosition, groundLevel, depthBelowSurface);
            }

            private struct WaterCaveColumnSample
            {
                public int WaterSurfaceY;
                public int BedY;
                public int ChamberCeilingY;
                public int ChamberLowestEditedY;
                public int EntranceCeilingY;
                public float Influence;
                public float SelectionPriority;
                public bool IsChamberColumn;
                public bool IsWaterColumn;
                public bool IsEntrance;
            }

            private bool CanWaterCaveIntersectChunk(
                int chunkWorldY,
                int groundLevel,
                int waterLevel,
                bool allowSurfaceShaft)
            {
                float chance = math.saturate(CaveNoise.WaterCaveChance);
                if (!EnableCaves || chance <= 0f)
                    return false;

                int minDepth = math.max(4, GetSetting(CaveNoise.WaterCaveMinDepthBelowWater, 12));
                int maxDepth = math.max(minDepth, GetSetting(CaveNoise.WaterCaveMaxDepthBelowWater, 72));
                int maxPoolDepth = math.clamp(GetSetting(CaveNoise.WaterCaveMaxPoolDepth, 8), 2, 16);
                int minimumSurfaceY = math.max(
                    waterLevel - maxDepth,
                    BedrockLevel + math.max(1, BedrockThickness) + maxPoolDepth + 4);
                int maximumSurfaceY = math.max(waterLevel - minDepth, minimumSurfaceY);
                int possibleMinimumY = minimumSurfaceY - maxPoolDepth - 2;
                int possibleMaximumY = allowSurfaceShaft
                    ? math.max(maximumSurfaceY + 14, groundLevel + 3)
                    : maximumSurfaceY + 14;
                int chunkMaximumY = chunkWorldY + ChunkHeight - 1;
                return chunkMaximumY >= possibleMinimumY && chunkWorldY <= possibleMaximumY;
            }

            private bool TryGetWaterCaveColumnSample(
                int worldX,
                int worldZ,
                int groundLevel,
                int waterLevel,
                bool allowSurfaceShaft,
                out WaterCaveColumnSample sample)
            {
                sample = default;
                float chance = math.saturate(CaveNoise.WaterCaveChance);
                if (!EnableCaves || chance <= 0f)
                    return false;

                int cellSize = math.max(64, GetSetting(CaveNoise.WaterCaveRegionSize, 136));
                int cellX = FastFloorToInt(worldX / (float)cellSize);
                int cellZ = FastFloorToInt(worldZ / (float)cellSize);
                bool found = false;
                float bestPriority = 0f;

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                    {
                        if (!TryGetWaterCaveColumnSampleFromRegion(
                                worldX,
                                worldZ,
                                groundLevel,
                                waterLevel,
                                cellX + offsetX,
                                cellZ + offsetZ,
                                cellSize,
                                chance,
                                allowSurfaceShaft,
                                out WaterCaveColumnSample candidate))
                        {
                            continue;
                        }

                        if (found && candidate.SelectionPriority <= bestPriority)
                            continue;

                        sample = candidate;
                        bestPriority = candidate.SelectionPriority;
                        found = true;
                    }
                }

                return found;
            }

            private bool TryGetWaterCaveColumnSampleFromRegion(
                int worldX,
                int worldZ,
                int groundLevel,
                int waterLevel,
                int regionCellX,
                int regionCellZ,
                int cellSize,
                float chance,
                bool allowSurfaceShaft,
                out WaterCaveColumnSample sample)
            {
                sample = default;
                if (Hash01(Hash(regionCellX, regionCellZ, 0x4A71, Seed)) > chance)
                    return false;

                float centerX = (regionCellX + math.lerp(
                    0.38f,
                    0.62f,
                    Hash01(Hash(regionCellX, regionCellZ, 0x7C23, Seed)))) * cellSize;
                float centerZ = (regionCellZ + math.lerp(
                    0.38f,
                    0.62f,
                    Hash01(Hash(regionCellX, regionCellZ, 0xD159, Seed)))) * cellSize;
                float radiusAlong = cellSize * math.lerp(
                    0.22f,
                    0.30f,
                    Hash01(Hash(regionCellX, regionCellZ, 0x25B7, Seed)));
                float radiusAcross = radiusAlong * math.lerp(
                    0.70f,
                    0.90f,
                    Hash01(Hash(regionCellX, regionCellZ, 0x9E35, Seed)));
                float angle = Hash01(Hash(regionCellX, regionCellZ, 0x61D3, Seed)) * math.PI * 2f;
                float2 direction = new float2(math.cos(angle), math.sin(angle));
                float2 delta = new float2(worldX - centerX, worldZ - centerZ);
                float localAlong = math.dot(delta, direction);
                float localAcross = delta.x * direction.y - delta.y * direction.x;
                float normalizedDistance = math.sqrt(
                    localAlong * localAlong / math.max(1f, radiusAlong * radiusAlong) +
                    localAcross * localAcross / math.max(1f, radiusAcross * radiusAcross));
                bool insideChamber = normalizedDistance <= 1f;

                float shaftOffset = radiusAlong * math.lerp(
                    0.12f,
                    0.28f,
                    Hash01(Hash(regionCellX, regionCellZ, 0xB843, Seed)));
                float2 shaftCenter = new float2(centerX, centerZ) + direction * shaftOffset;
                float shaftRadius = math.lerp(
                    2.6f,
                    3.6f,
                    Hash01(Hash(regionCellX, regionCellZ, 0x36E9, Seed)));
                float2 shaftDelta = new float2(worldX, worldZ) - shaftCenter;
                bool isEntrance = math.lengthsq(shaftDelta) <= shaftRadius * shaftRadius;
                if (!insideChamber && !isEntrance)
                    return false;

                if (WaterCaveRegionOverlapsLushCave(
                        centerX,
                        centerZ,
                        radiusAlong + 6f))
                {
                    return false;
                }

                int minDepth = math.max(4, GetSetting(CaveNoise.WaterCaveMinDepthBelowWater, 12));
                int maxDepth = math.max(minDepth, GetSetting(CaveNoise.WaterCaveMaxDepthBelowWater, 72));
                int maxPoolDepth = math.clamp(GetSetting(CaveNoise.WaterCaveMaxPoolDepth, 8), 2, 16);
                int depthBelowWater = (int)math.round(math.lerp(
                    minDepth,
                    maxDepth,
                    Hash01(Hash(regionCellX, regionCellZ, 0xF217, Seed))));
                int waterSurfaceY = waterLevel - depthBelowWater;
                waterSurfaceY = math.max(
                    waterSurfaceY,
                    BedrockLevel + math.max(1, BedrockThickness) + maxPoolDepth + 4);

                int clearance = math.max(7, CaveNoise.SurfaceClearance + 5);
                int maximumCeilingY = groundLevel - clearance;
                bool hasChamberClearance = maximumCeilingY >= waterSurfaceY + 3;
                bool canCarveEntrance =
                    allowSurfaceShaft &&
                    isEntrance &&
                    hasChamberClearance;
                if (!insideChamber && !canCarveEntrance)
                    return false;

                float influence = insideChamber
                    ? 1f - math.saturate(normalizedDistance)
                    : 0f;
                float minimumRadius = math.max(1f, math.min(radiusAlong, radiusAcross));
                float waterBankInfluence = math.clamp(3f / minimumRadius, 0.18f, 0.48f);
                bool isWaterColumn =
                    insideChamber &&
                    hasChamberClearance &&
                    influence > waterBankInfluence;
                float waterColumnStrength = TerrainNoiseUtility.Smooth01(math.saturate(
                    (influence - waterBankInfluence) / (1f - waterBankInfluence)));
                int bedDepth = isWaterColumn
                    ? (int)math.round(math.lerp(2f, maxPoolDepth, waterColumnStrength))
                    : 0;
                int bedY = waterSurfaceY - bedDepth;

                float chamberStrength = TerrainNoiseUtility.Smooth01(influence);
                int chamberHeight = insideChamber && hasChamberClearance
                    ? (int)math.round(math.lerp(2f, 13f, chamberStrength))
                    : 0;
                int chamberCeilingY = hasChamberClearance
                    ? math.min(waterSurfaceY + chamberHeight, maximumCeilingY)
                    : math.min(waterSurfaceY + 2, groundLevel);
                int chamberLowestEditedY = bedY - 2;
                if (insideChamber && chamberCeilingY < chamberLowestEditedY && !canCarveEntrance)
                    return false;

                int entranceCeilingY = canCarveEntrance ? groundLevel + 2 : 0;
                sample = new WaterCaveColumnSample
                {
                    WaterSurfaceY = waterSurfaceY,
                    BedY = bedY,
                    ChamberCeilingY = chamberCeilingY,
                    ChamberLowestEditedY = chamberLowestEditedY,
                    EntranceCeilingY = entranceCeilingY,
                    Influence = influence,
                    SelectionPriority = canCarveEntrance
                        ? 3f + influence
                        : (insideChamber ? 1f + influence : 0.10f),
                    IsChamberColumn = insideChamber,
                    IsWaterColumn = isWaterColumn,
                    IsEntrance = canCarveEntrance,
                };
                return true;
            }

            private bool WaterCaveRegionOverlapsLushCave(
                float waterCaveCenterX,
                float waterCaveCenterZ,
                float waterCaveReach)
            {
                if (!LushCaveBiome.Enable)
                    return false;

                int lushCellSize = math.max(192, LushCaveBiome.RegionCellSize > 0
                    ? LushCaveBiome.RegionCellSize
                    : 360);
                float minLushRadius = math.max(32f, LushCaveBiome.MinHorizontalRadius > 0f
                    ? LushCaveBiome.MinHorizontalRadius
                    : 86f);
                minLushRadius = math.min(minLushRadius, lushCellSize * 0.90f);
                float maxLushRadius = math.max(minLushRadius, LushCaveBiome.MaxHorizontalRadius > 0f
                    ? LushCaveBiome.MaxHorizontalRadius
                    : 132f);
                maxLushRadius = math.min(maxLushRadius, lushCellSize * 0.90f);
                int centerCellX = FastFloorToInt(waterCaveCenterX / lushCellSize);
                int centerCellZ = FastFloorToInt(waterCaveCenterZ / lushCellSize);
                int searchRadius = math.max(
                    1,
                    (int)math.ceil((math.max(0f, waterCaveReach) + maxLushRadius) / lushCellSize) + 1);
                float lushChance = math.saturate(LushCaveBiome.RegionChance);

                for (int offsetX = -searchRadius; offsetX <= searchRadius; offsetX++)
                {
                    for (int offsetZ = -searchRadius; offsetZ <= searchRadius; offsetZ++)
                    {
                        int lushCellX = centerCellX + offsetX;
                        int lushCellZ = centerCellZ + offsetZ;
                        bool guaranteedOriginCave =
                            LushCaveBiome.GuaranteeAtWorldOrigin &&
                            lushCellX == 0 &&
                            lushCellZ == 0;
                        if (!guaranteedOriginCave &&
                            (lushChance <= 0f || Hash01(Hash(lushCellX, lushCellZ, 0x1C45, Seed)) > lushChance))
                        {
                            continue;
                        }

                        float lushRadius = math.lerp(
                            minLushRadius,
                            maxLushRadius,
                            Hash01(Hash(lushCellX, lushCellZ, 0xA871, Seed)));
                        float lushCenterX = guaranteedOriginCave
                            ? 0f
                            : (lushCellX + math.lerp(
                                0.26f,
                                0.74f,
                                Hash01(Hash(lushCellX, lushCellZ, 0x73B1, Seed)))) * lushCellSize;
                        float lushCenterZ = guaranteedOriginCave
                            ? 0f
                            : (lushCellZ + math.lerp(
                                0.26f,
                                0.74f,
                                Hash01(Hash(lushCellX, lushCellZ, 0xC927, Seed)))) * lushCellSize;
                        float2 centerDelta = new float2(
                            waterCaveCenterX - lushCenterX,
                            waterCaveCenterZ - lushCenterZ);
                        if (math.length(centerDelta) <= lushRadius + math.max(0f, waterCaveReach))
                            return true;
                    }
                }

                return false;
            }

            private static bool TryGetWaterCaveBlock(
                WaterCaveColumnSample sample,
                float3 worldPosition,
                out byte blockId)
            {
                blockId = BLOCK_AIR;
                int worldY = (int)math.round(worldPosition.y);

                if (sample.IsEntrance &&
                    worldY > sample.WaterSurfaceY &&
                    worldY <= sample.EntranceCeilingY)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                if (!sample.IsChamberColumn ||
                    worldY < sample.ChamberLowestEditedY ||
                    worldY > sample.ChamberCeilingY)
                {
                    return false;
                }

                if (worldY < sample.BedY)
                {
                    blockId = BLOCK_STONE;
                    return true;
                }

                if (worldY == sample.BedY)
                {
                    float bedNoise = SampleMaterialNoise01(
                        worldPosition,
                        11f,
                        new float3(96.4f, -31.7f, 184.2f));
                    blockId = bedNoise > 0.80f ? (byte)BLOCK_MOSSY_BRICK_STONE : (byte)BLOCK_GRAVEL;
                    return true;
                }

                if (worldY <= sample.WaterSurfaceY)
                {
                    blockId = sample.IsWaterColumn ? (byte)BLOCK_WATER : (byte)BLOCK_STONE;
                    return true;
                }

                if (worldY == sample.ChamberCeilingY)
                {
                    float ceilingNoise = SampleMaterialNoise01(
                        worldPosition,
                        17f,
                        new float3(-132.8f, 73.5f, 41.6f));
                    blockId = ceilingNoise > 0.94f ? (byte)BLOCK_MOSSY_BRICK_STONE : (byte)BLOCK_STONE;
                    return true;
                }

                if (sample.IsWaterColumn)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                blockId = BLOCK_STONE;
                return true;
            }

            private struct LushCaveColumnSample
            {
                public int RegionCellX;
                public int RegionCellZ;
                public int RegionCellSize;
                public int BaseFloorY;
                public int FloorY;
                public int RiverSurfaceY;
                public int CeilingY;
                public int LowestEditedY;
                public float RadiusAlong;
                public float RadiusAcross;
                public float LocalAlong;
                public float LocalAcross;
                public float Influence;
                public float RiverDistance;
            }

            private bool CanLushCaveIntersectChunk(int chunkWorldY, int waterLevel)
            {
                if (!LushCaveBiome.Enable)
                    return false;

                int minDepth = math.max(24, LushCaveBiome.MinDepthBelowWater > 0
                    ? LushCaveBiome.MinDepthBelowWater
                    : 58);
                int maxDepth = math.max(minDepth, LushCaveBiome.MaxDepthBelowWater > 0
                    ? LushCaveBiome.MaxDepthBelowWater
                    : 72);
                int riverDepth = math.max(1, LushCaveBiome.RiverDepth > 0
                    ? LushCaveBiome.RiverDepth
                    : 4);
                float minHeight = math.max(16f, LushCaveBiome.MinHeight > 0f
                    ? LushCaveBiome.MinHeight
                    : 38f);
                float maxHeight = math.max(minHeight, LushCaveBiome.MaxHeight > 0f
                    ? LushCaveBiome.MaxHeight
                    : 52f);
                int minimumBaseFloor = math.max(
                    waterLevel - maxDepth,
                    BedrockLevel + math.max(1, BedrockThickness) + 7);
                int maximumBaseFloor = math.max(
                    waterLevel - minDepth,
                    BedrockLevel + math.max(1, BedrockThickness) + 7);
                int possibleMinimumY = minimumBaseFloor - riverDepth - 3;
                int possibleMaximumY = maximumBaseFloor + (int)math.ceil(maxHeight) + 2;
                int chunkMaximumY = chunkWorldY + ChunkHeight - 1;
                return chunkMaximumY >= possibleMinimumY && chunkWorldY <= possibleMaximumY;
            }

            private bool TryGetLushCaveColumnSample(
                int worldX,
                int worldZ,
                int groundLevel,
                int waterLevel,
                out LushCaveColumnSample sample)
            {
                sample = default;
                if (!LushCaveBiome.Enable ||
                    !TryGetRawLushCaveColumnSample(worldX, worldZ, waterLevel, out sample))
                {
                    return false;
                }

                int clearance = math.max(4, LushCaveBiome.SurfaceClearance > 0
                    ? LushCaveBiome.SurfaceClearance
                    : 10);
                sample.CeilingY = math.min(sample.CeilingY, groundLevel - clearance);
                if (sample.CeilingY - sample.FloorY < 9)
                    return false;

                int riverDepth = math.max(1, LushCaveBiome.RiverDepth > 0
                    ? LushCaveBiome.RiverDepth
                    : 4);
                sample.LowestEditedY = math.min(sample.FloorY - 3, sample.RiverSurfaceY - riverDepth - 3);
                sample.LowestEditedY = math.max(
                    sample.LowestEditedY,
                    BedrockLevel + math.max(1, BedrockThickness) + 1);
                return sample.CeilingY > sample.LowestEditedY;
            }

            private bool TryGetRawLushCaveColumnSample(
                int worldX,
                int worldZ,
                int waterLevel,
                out LushCaveColumnSample sample)
            {
                sample = default;
                if (!LushCaveBiome.Enable)
                    return false;

                int cellSize = math.max(192, LushCaveBiome.RegionCellSize > 0
                    ? LushCaveBiome.RegionCellSize
                    : 360);
                int cellX = FastFloorToInt(worldX / (float)cellSize);
                int cellZ = FastFloorToInt(worldZ / (float)cellSize);
                bool found = false;
                float strongestInfluence = 0f;

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                    {
                        if (!TryGetLushCaveColumnSampleFromRegion(
                                worldX,
                                worldZ,
                                waterLevel,
                                cellX + offsetX,
                                cellZ + offsetZ,
                                cellSize,
                                out LushCaveColumnSample candidate))
                        {
                            continue;
                        }

                        if (found && candidate.Influence <= strongestInfluence)
                            continue;

                        sample = candidate;
                        strongestInfluence = candidate.Influence;
                        found = true;
                    }
                }

                return found;
            }

            private bool TryGetLushCaveColumnSampleFromRegion(
                int worldX,
                int worldZ,
                int waterLevel,
                int regionCellX,
                int regionCellZ,
                int cellSize,
                out LushCaveColumnSample sample)
            {
                sample = default;
                bool guaranteedOriginCave =
                    LushCaveBiome.GuaranteeAtWorldOrigin &&
                    regionCellX == 0 &&
                    regionCellZ == 0;
                float chance = math.saturate(LushCaveBiome.RegionChance);
                if (!guaranteedOriginCave &&
                    (chance <= 0f || Hash01(Hash(regionCellX, regionCellZ, 0x1C45, Seed)) > chance))
                {
                    return false;
                }

                float minRadius = math.max(32f, LushCaveBiome.MinHorizontalRadius > 0f
                    ? LushCaveBiome.MinHorizontalRadius
                    : 86f);
                float maximumSearchableRadius = cellSize * 0.90f;
                minRadius = math.min(minRadius, maximumSearchableRadius);
                float maxRadius = math.max(minRadius, LushCaveBiome.MaxHorizontalRadius > 0f
                    ? LushCaveBiome.MaxHorizontalRadius
                    : 132f);
                maxRadius = math.min(maxRadius, maximumSearchableRadius);
                float radiusAlong = math.lerp(
                    minRadius,
                    maxRadius,
                    Hash01(Hash(regionCellX, regionCellZ, 0xA871, Seed)));
                float radiusAcross = radiusAlong * math.lerp(
                    0.68f,
                    0.88f,
                    Hash01(Hash(regionCellX, regionCellZ, 0x52D9, Seed)));

                float centerX;
                float centerZ;
                if (guaranteedOriginCave)
                {
                    centerX = 0f;
                    centerZ = 0f;
                }
                else
                {
                    centerX = (regionCellX + math.lerp(
                        0.26f,
                        0.74f,
                        Hash01(Hash(regionCellX, regionCellZ, 0x73B1, Seed)))) * cellSize;
                    centerZ = (regionCellZ + math.lerp(
                        0.26f,
                        0.74f,
                        Hash01(Hash(regionCellX, regionCellZ, 0xC927, Seed)))) * cellSize;
                }

                float angle = Hash01(Hash(regionCellX, regionCellZ, 0x4F63, Seed)) * math.PI * 2f;
                float2 direction = new float2(math.cos(angle), math.sin(angle));
                float2 delta = new float2(worldX - centerX, worldZ - centerZ);
                float localAlong = math.dot(delta, direction);
                float localAcross = delta.x * direction.y - delta.y * direction.x;
                float normalizedDistance = math.sqrt(
                    (localAlong * localAlong) / math.max(1f, radiusAlong * radiusAlong) +
                    (localAcross * localAcross) / math.max(1f, radiusAcross * radiusAcross));
                if (normalizedDistance > 1f)
                    return false;

                float influence = 1f - math.saturate(normalizedDistance);
                float shapedInfluence = TerrainNoiseUtility.Smooth01(influence);
                int minDepth = math.max(24, LushCaveBiome.MinDepthBelowWater > 0
                    ? LushCaveBiome.MinDepthBelowWater
                    : 58);
                int maxDepth = math.max(minDepth, LushCaveBiome.MaxDepthBelowWater > 0
                    ? LushCaveBiome.MaxDepthBelowWater
                    : 72);
                float depth = math.lerp(
                    minDepth,
                    maxDepth,
                    Hash01(Hash(regionCellX, regionCellZ, 0xD315, Seed)));
                int baseFloorY = waterLevel - (int)math.round(depth);
                baseFloorY = math.max(
                    baseFloorY,
                    BedrockLevel + math.max(1, BedrockThickness) + 7);

                float floorNoise = SampleMaterialNoise01(
                    new float3(worldX, 0f, worldZ),
                    24f,
                    new float3(137.2f, 0f, -84.6f));
                int floorY = baseFloorY + (int)math.round(floorNoise * 3f);
                float minHeight = math.max(16f, LushCaveBiome.MinHeight > 0f
                    ? LushCaveBiome.MinHeight
                    : 38f);
                float maxHeight = math.max(minHeight, LushCaveBiome.MaxHeight > 0f
                    ? LushCaveBiome.MaxHeight
                    : 52f);
                float centerHeight = math.lerp(
                    minHeight,
                    maxHeight,
                    Hash01(Hash(regionCellX, regionCellZ, 0x9A57, Seed)));
                float ceilingNoise = SampleMaterialNoise01(
                    new float3(worldX, 0f, worldZ),
                    31f,
                    new float3(-63.7f, 0f, 201.4f));
                int ceilingY = baseFloorY + (int)math.round(
                    math.lerp(10f, centerHeight, shapedInfluence) +
                    (ceilingNoise - 0.5f) * 4f);

                float riverPhase = Hash01(Hash(regionCellX, regionCellZ, 0x681D, Seed)) * math.PI * 2f;
                float broadMeander = math.sin(
                    localAlong / math.max(1f, radiusAlong) * math.PI * 2.6f + riverPhase) *
                    radiusAcross * 0.10f;
                float detailMeander = (SampleMaterialNoise01(
                    new float3(worldX, 0f, worldZ),
                    46f,
                    new float3(311.8f, 0f, 47.3f)) - 0.5f) * radiusAcross * 0.12f;
                float riverDistance = math.abs(localAcross - broadMeander - detailMeander);

                int riverDepth = math.max(1, LushCaveBiome.RiverDepth > 0
                    ? LushCaveBiome.RiverDepth
                    : 4);
                sample = new LushCaveColumnSample
                {
                    RegionCellX = regionCellX,
                    RegionCellZ = regionCellZ,
                    RegionCellSize = cellSize,
                    BaseFloorY = baseFloorY,
                    FloorY = floorY,
                    RiverSurfaceY = baseFloorY,
                    CeilingY = ceilingY,
                    LowestEditedY = math.min(floorY - 3, baseFloorY - riverDepth - 3),
                    RadiusAlong = radiusAlong,
                    RadiusAcross = radiusAcross,
                    LocalAlong = localAlong,
                    LocalAcross = localAcross,
                    Influence = influence,
                    RiverDistance = riverDistance,
                };
                return true;
            }

            private bool TryGetLushCaveBiomeBlock(
                LushCaveColumnSample sample,
                float3 worldPosition,
                out byte blockId)
            {
                blockId = BLOCK_AIR;
                int worldY = (int)math.round(worldPosition.y);
                if (worldY < sample.LowestEditedY || worldY > sample.CeilingY)
                    return false;

                if (worldY == sample.CeilingY)
                {
                    float ceilingAccent = SampleMaterialNoise01(
                        worldPosition,
                        13f,
                        new float3(-174.3f, 89.2f, 26.7f));
                    blockId = ceilingAccent > 0.965f
                        ? (byte)BLOCK_GLOWSTONE
                        : (ceilingAccent < 0.20f ? (byte)BLOCK_MOSSY_BRICK_STONE : (byte)BLOCK_STONE);
                    return true;
                }

                float riverHalfWidth = math.max(1.5f, LushCaveBiome.RiverHalfWidth > 0f
                    ? LushCaveBiome.RiverHalfWidth
                    : 4.5f);
                // Stop the river inside the cavern so its generated water always has
                // a solid grass-and-dirt end bank before ordinary cave carving resumes.
                bool isRiverColumn =
                    sample.Influence > 0.10f &&
                    sample.RiverDistance <= riverHalfWidth;
                if (isRiverColumn)
                {
                    float channelStrength = TerrainNoiseUtility.Smooth01(
                        1f - math.saturate(sample.RiverDistance / riverHalfWidth));
                    int maxRiverDepth = math.max(1, LushCaveBiome.RiverDepth > 0
                        ? LushCaveBiome.RiverDepth
                        : 4);
                    int riverBedY = sample.RiverSurfaceY -
                        (int)math.round(math.lerp(1f, maxRiverDepth, channelStrength));

                    if (worldY < riverBedY - 2)
                        return false;

                    if (worldY < riverBedY)
                    {
                        blockId = worldY == riverBedY - 1 ? (byte)BLOCK_COARSE_DIRT : (byte)BLOCK_STONE;
                        return true;
                    }

                    if (worldY == riverBedY)
                    {
                        float bedNoise = SampleMaterialNoise01(
                            worldPosition,
                            9f,
                            new float3(42.6f, -17.1f, 118.9f));
                        blockId = bedNoise > 0.82f ? (byte)BLOCK_MOSSY_BRICK_STONE : (byte)BLOCK_GRAVEL;
                        return true;
                    }

                    if (worldY <= sample.RiverSurfaceY)
                    {
                        blockId = BLOCK_WATER;
                        return true;
                    }

                    return true;
                }

                if (worldY < sample.FloorY - 2)
                    return false;

                if (worldY < sample.FloorY)
                {
                    blockId = worldY == sample.FloorY - 1 ? (byte)BLOCK_DIRT : (byte)BLOCK_ROOTED_DIRT;
                    return true;
                }

                if (GenerateLushCaveTrees &&
                    worldY <= sample.FloorY + 15 &&
                    TryGetLushCaveTreeBlock(sample, worldPosition, out blockId))
                {
                    return true;
                }

                if (worldY == sample.FloorY)
                {
                    blockId = BLOCK_GRASS;
                    return true;
                }

                return true;
            }

            private bool TryGetLushCaveTreeBlock(
                LushCaveColumnSample currentSample,
                float3 worldPosition,
                out byte blockId)
            {
                blockId = BLOCK_AIR;
                float treeChance = math.saturate(LushCaveBiome.TreeChance);
                if (treeChance <= 0f || currentSample.Influence < 0.16f)
                    return false;

                int spacing = math.max(8, LushCaveBiome.TreeSpacing > 0
                    ? LushCaveBiome.TreeSpacing
                    : 11);
                int treeCellX = FastFloorToInt(worldPosition.x / spacing);
                int treeCellZ = FastFloorToInt(worldPosition.z / spacing);
                int worldX = (int)math.round(worldPosition.x);
                int worldY = (int)math.round(worldPosition.y);
                int worldZ = (int)math.round(worldPosition.z);
                int jitterRange = math.max(1, spacing - 4);
                int waterLevel = GroundOffset + WaterLevel;
                float riverHalfWidth = math.max(1.5f, LushCaveBiome.RiverHalfWidth > 0f
                    ? LushCaveBiome.RiverHalfWidth
                    : 4.5f);

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                    {
                        int candidateCellX = treeCellX + offsetX;
                        int candidateCellZ = treeCellZ + offsetZ;
                        uint treeHash = Hash(candidateCellX, candidateCellZ, 0x7A31, Seed);
                        if (Hash01(treeHash) > treeChance)
                            continue;

                        int rootX = candidateCellX * spacing + 2 +
                            (int)(Hash(candidateCellX, candidateCellZ, 0x26B7, Seed) % (uint)jitterRange);
                        int rootZ = candidateCellZ * spacing + 2 +
                            (int)(Hash(candidateCellX, candidateCellZ, 0xD491, Seed) % (uint)jitterRange);
                        int deltaX = worldX - rootX;
                        int deltaZ = worldZ - rootZ;
                        if (math.abs(deltaX) > 4 || math.abs(deltaZ) > 4)
                            continue;

                        if (!TryGetRawLushCaveColumnSample(
                                rootX,
                                rootZ,
                                waterLevel,
                                out LushCaveColumnSample rootSample) ||
                            rootSample.RegionCellX != currentSample.RegionCellX ||
                            rootSample.RegionCellZ != currentSample.RegionCellZ ||
                            rootSample.Influence < 0.25f ||
                            rootSample.RiverDistance <= riverHalfWidth + 4f)
                        {
                            continue;
                        }

                        int treeHeight = 5 + (int)(Hash(candidateCellX, candidateCellZ, 0x51A7, Seed) % 4u);
                        int canopyRadius = Hash01(Hash(candidateCellX, candidateCellZ, 0x8E2D, Seed)) > 0.78f ? 4 : 3;
                        if (rootSample.CeilingY - rootSample.FloorY < treeHeight + canopyRadius + 3)
                            continue;

                        if (deltaX == 0 && deltaZ == 0)
                        {
                            if (worldY == rootSample.FloorY)
                            {
                                blockId = BLOCK_ROOTED_DIRT;
                                return true;
                            }

                            if (worldY > rootSample.FloorY && worldY <= rootSample.FloorY + treeHeight)
                            {
                                bool jungleVariant = Hash01(Hash(candidateCellX, candidateCellZ, 0xB85F, Seed)) > 0.72f;
                                blockId = jungleVariant ? (byte)BLOCK_JUNGLE_LOG : (byte)BLOCK_WOOD;
                                return true;
                            }
                        }

                        int canopyCenterY = rootSample.FloorY + treeHeight;
                        int deltaY = worldY - canopyCenterY;
                        if (deltaY < -2 || deltaY > canopyRadius)
                            continue;

                        float verticalInset = math.max(0f, math.abs(deltaY) - 0.5f) * 0.62f;
                        float horizontalRadius = math.max(1.2f, canopyRadius - verticalInset);
                        if (deltaX * deltaX + deltaZ * deltaZ > horizontalRadius * horizontalRadius)
                            continue;

                        float leafCutout = Hash01(Hash(worldX, worldY, worldZ, 0x3E91, Seed));
                        if (leafCutout < 0.10f && math.abs(deltaX) + math.abs(deltaZ) > 1)
                            continue;

                        bool jungleLeaves = Hash01(Hash(candidateCellX, candidateCellZ, 0xB85F, Seed)) > 0.72f;
                        blockId = jungleLeaves ? (byte)BLOCK_JUNGLE_LEAVES : (byte)BLOCK_LEAVES;
                        return true;
                    }
                }

                return false;
            }

            private struct OasisSample
            {
                public float Distance;
                public float Radius;
                public float WaterRadius;
                public float Influence;
                public bool IsWater;
            }

            private bool TryGetOasisSample(int worldX, int worldZ, out OasisSample sample)
            {
                sample = default;

                float chance = math.saturate(OasisChance);
                if (!EnableOases || chance <= 0f)
                    return false;

                int cellSize = math.max(32, OasisCellSize);
                int cellX = FastFloorToInt(worldX / (float)cellSize);
                int cellZ = FastFloorToInt(worldZ / (float)cellSize);
                bool found = false;
                float bestDistance = 0f;

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                    {
                        int candidateCellX = cellX + offsetX;
                        int candidateCellZ = cellZ + offsetZ;

                        if (Hash01(Hash(candidateCellX, candidateCellZ, 0x0A51, Seed)) > chance)
                            continue;

                        float baseRadius = math.max(4f, OasisRadius);
                        float margin = math.min(cellSize * 0.35f, math.max(8f, baseRadius + 4f));
                        float centerX = candidateCellX * cellSize + math.lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x7113, Seed)));
                        float centerZ = candidateCellZ * cellSize + math.lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x4AF9, Seed)));
                        float radius = baseRadius * math.lerp(0.78f, 1.35f, Hash01(Hash(candidateCellX, candidateCellZ, 0x583B, Seed)));

                        float2 delta = new float2(worldX - centerX, worldZ - centerZ);
                        float distance = math.length(delta);
                        if (distance > radius)
                            continue;

                        if (found && distance >= bestDistance)
                            continue;

                        float waterRadius = math.max(2f, OasisWaterRadius);
                        waterRadius *= math.lerp(0.85f, 1.25f, Hash01(Hash(candidateCellX, candidateCellZ, 0x27C1, Seed)));
                        waterRadius = math.min(waterRadius, radius - 3f);

                        bestDistance = distance;
                        sample = new OasisSample
                        {
                            Distance = distance,
                            Radius = radius,
                            WaterRadius = waterRadius,
                            Influence = 1f - math.saturate(distance / math.max(0.0001f, radius)),
                            IsWater = distance <= waterRadius,
                        };
                        found = true;
                    }
                }

                return found;
            }

            private static bool TryGetOasisBlock(OasisSample oasis, int worldY, int groundLevel, int depthBelowSurface, out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (oasis.IsWater)
                {
                    if (depthBelowSurface <= 2)
                    {
                        blockId = BLOCK_WATER;
                        return true;
                    }

                    if (depthBelowSurface <= 4)
                    {
                        blockId = BLOCK_SANDSTONE;
                        return true;
                    }
                }

                if (oasis.Influence > 0.18f)
                {
                    if (worldY == groundLevel)
                    {
                        blockId = BLOCK_GRASS;
                        return true;
                    }

                    if (depthBelowSurface <= 4)
                    {
                        blockId = BLOCK_DIRT;
                        return true;
                    }
                }

                return false;
            }

            private bool TryGetStructureBlock(byte biome, int worldX, int worldY, int worldZ, int groundLevel, int waterLevel, float riverStrength, int riverSurfaceLevel, out byte blockId)
            {
                blockId = BLOCK_AIR;

                // Every generated structure is contained within this vertical
                // envelope (large lodges reach the upper bound). Reject the vast
                // majority of a chunk before hashing and scanning the 3x3 cells.
                int localY = worldY - groundLevel;
                if (localY < -2 || localY > 14)
                    return false;

                float chance = math.saturate(StructureChance);
                if (biome == (byte)BiomeId.Snow)
                    chance = math.max(chance, 0.13f);
                if (!EnableStructures ||
                    chance <= 0f ||
                    !IsStructureBiome(biome) ||
                    groundLevel <= waterLevel + 2 ||
                    riverStrength > 0.18f ||
                    (riverSurfaceLevel != int.MinValue && groundLevel <= riverSurfaceLevel + 2))
                {
                    return false;
                }

                int cellSize = math.max(32, StructureCellSize);
                int cellX = FastFloorToInt(worldX / (float)cellSize);
                int cellZ = FastFloorToInt(worldZ / (float)cellSize);

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                    {
                        if (TryGetStructureBlockFromCell(
                            biome,
                            worldX,
                            worldY,
                            worldZ,
                            groundLevel,
                            cellX + offsetX,
                            cellZ + offsetZ,
                            cellSize,
                            chance,
                            out blockId))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private bool TryGetStructureBlockFromCell(
                byte biome,
                int worldX,
                int worldY,
                int worldZ,
                int groundLevel,
                int cellX,
                int cellZ,
                int cellSize,
                float chance,
                out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (Hash01(Hash(cellX, cellZ, 0x57A5, Seed)) > chance)
                    return false;

                float margin = math.min(cellSize * 0.35f, 12f);
                int centerX = cellX * cellSize + (int)math.floor(math.lerp(margin, cellSize - margin, Hash01(Hash(cellX, cellZ, 0xB11D, Seed))));
                int centerZ = cellZ * cellSize + (int)math.floor(math.lerp(margin, cellSize - margin, Hash01(Hash(cellX, cellZ, 0xD45F, Seed))));

                bool ruinStructure = IsRuinStructureCell(biome, cellX, cellZ);
                bool largeRuin = ruinStructure && Hash01(Hash(cellX, cellZ, 0x71E3, Seed)) < 0.38f;
                bool largeBuilding = !ruinStructure &&
                                     biome != (byte)BiomeId.Snow &&
                                     Hash01(Hash(cellX, cellZ, 0x4A6D, Seed)) <
                                     (TerrainNoiseUtility.IsDryDesertBiome(biome) ? 0.52f : 0.42f);
                bool snowLodge = biome == (byte)BiomeId.Snow &&
                                 Hash01(Hash(cellX, cellZ, 0x5A0B, Seed)) < 0.44f;

                int halfWidth;
                int halfDepth;
                if (snowLodge)
                {
                    halfWidth = 6;
                    halfDepth = 7;
                }
                else if (biome == (byte)BiomeId.Snow)
                {
                    halfWidth = 4;
                    halfDepth = 7;
                }
                else if (largeRuin)
                {
                    halfWidth = 9 + (int)(Hash(cellX, cellZ, 0x2C71, Seed) % 2u);
                    halfDepth = 8 + (int)(Hash(cellX, cellZ, 0x69EF, Seed) % 2u);
                }
                else if (ruinStructure)
                {
                    halfWidth = 5 + (int)(Hash(cellX, cellZ, 0x2C71, Seed) % 3u);
                    halfDepth = 4 + (int)(Hash(cellX, cellZ, 0x69EF, Seed) % 3u);
                }
                else if (largeBuilding && TerrainNoiseUtility.IsDryDesertBiome(biome))
                {
                    halfWidth = 9;
                    halfDepth = 9;
                }
                else if (largeBuilding)
                {
                    halfWidth = 8;
                    halfDepth = 6;
                }
                else
                {
                    halfWidth = 3 + (int)(Hash(cellX, cellZ, 0x2C71, Seed) % 2u);
                    halfDepth = 3 + (int)(Hash(cellX, cellZ, 0x69EF, Seed) % 2u);
                }

                int dx = worldX - centerX;
                int dz = worldZ - centerZ;
                int absDx = math.abs(dx);
                int absDz = math.abs(dz);
                int localY = worldY - groundLevel;

                if (TerrainNoiseUtility.IsDryDesertBiome(biome) && StructureTouchesOasis(centerX, centerZ, halfWidth + 3, halfDepth + 3))
                    return false;

                if (biome == (byte)BiomeId.Snow)
                {
                    if (snowLodge)
                        return TryGetSnowLodgeStructureBlock(dx, dz, absDx, absDz, halfWidth, halfDepth, localY, out blockId);

                    return TryGetIglooStructureBlock(dx, dz, absDx, absDz, localY, out blockId);
                }

                if (ruinStructure)
                    return TryGetRuinStructureBlock(
                        biome,
                        worldX,
                        worldY,
                        worldZ,
                        cellX,
                        cellZ,
                        dx,
                        dz,
                        absDx,
                        absDz,
                        halfWidth,
                        halfDepth,
                        largeRuin,
                        localY,
                        out blockId);

                if (largeBuilding && TerrainNoiseUtility.IsDryDesertBiome(biome))
                    return TryGetLargeDesertTempleStructureBlock(biome, dx, dz, absDx, absDz, halfWidth, halfDepth, localY, out blockId);

                if (largeBuilding)
                    return TryGetLargeLodgeStructureBlock(dx, dz, absDx, absDz, halfWidth, halfDepth, localY, out blockId);

                return TryGetCottageStructureBlock(biome, dx, dz, absDx, absDz, halfWidth, halfDepth, localY, out blockId);
            }

            private static bool TryGetCottageStructureBlock(
                byte biome,
                int dx,
                int dz,
                int absDx,
                int absDz,
                int halfWidth,
                int halfDepth,
                int localY,
                out byte blockId)
            {
                blockId = BLOCK_AIR;

                bool desertBuilding = TerrainNoiseUtility.IsDryDesertBiome(biome);
                int roofTop = desertBuilding ? 5 : 5 + (halfWidth + 1) / 2;
                if (localY < -1 || localY > roofTop || absDx > halfWidth + 1 || absDz > halfDepth + 1)
                    return false;

                byte foundationBlock = desertBuilding
                    ? (biome == (byte)BiomeId.RedDesert ? (byte)BLOCK_RED_SANDSTONE : (byte)BLOCK_SANDSTONE)
                    : (byte)BLOCK_COBBLESTONE;
                bool insideWalls = absDx <= halfWidth && absDz <= halfDepth;

                if (localY <= 0 && insideWalls)
                {
                    blockId = foundationBlock;
                    return true;
                }

                if (desertBuilding)
                {
                    if (localY == 4 && absDx <= halfWidth + 1 && absDz <= halfDepth + 1)
                    {
                        blockId = foundationBlock;
                        return true;
                    }

                    bool parapet = localY == 5 && insideWalls &&
                                    (absDx == halfWidth || absDz == halfDepth) &&
                                    ((dx + dz) & 1) == 0;
                    if (parapet)
                    {
                        blockId = foundationBlock;
                        return true;
                    }
                }
                else
                {
                    int roofHalfWidth = halfWidth + 1;
                    if (absDx <= roofHalfWidth && absDz <= halfDepth + 1)
                    {
                        int roofY = 4 + (roofHalfWidth - absDx + 1) / 2;
                        if (localY == roofY)
                        {
                            blockId = BLOCK_WOOD_BLANKET;
                            return true;
                        }

                        bool gable = absDz == halfDepth && localY >= 4 && localY < roofY;
                        if (gable)
                        {
                            blockId = dx == 0 ? (byte)BLOCK_WOOD : (byte)BLOCK_WOOD_BLANKET;
                            return true;
                        }
                    }
                }

                if (!insideWalls)
                    return false;

                bool perimeter = absDx == halfWidth || absDz == halfDepth;
                if (localY >= 1 && localY <= 3 && perimeter)
                {
                    bool doorway = dz == -halfDepth && absDx <= 1 && localY <= (desertBuilding ? 3 : 2);
                    if (doorway)
                    {
                        blockId = BLOCK_AIR;
                        return true;
                    }

                    bool corner = absDx == halfWidth && absDz == halfDepth;
                    bool window = localY == 2 && !corner &&
                                  ((absDx == halfWidth && absDz <= 1) ||
                                   (absDz == halfDepth && absDx <= 1));
                    if (window)
                    {
                        blockId = desertBuilding ? (byte)BLOCK_AIR : (byte)BLOCK_GLASS;
                        return true;
                    }

                    blockId = desertBuilding
                        ? foundationBlock
                        : (corner || dx == 0 && absDz == halfDepth ? (byte)BLOCK_WOOD : (byte)BLOCK_WOOD_BLANKET);
                    return true;
                }

                if (localY == 1 && dx == halfWidth - 1 && dz == halfDepth - 1)
                {
                    blockId = BLOCK_CHEST;
                    return true;
                }

                if (localY >= 1 && localY <= roofTop)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                return false;
            }

            private static bool TryGetLargeLodgeStructureBlock(
                int dx,
                int dz,
                int absDx,
                int absDz,
                int halfWidth,
                int halfDepth,
                int localY,
                out byte blockId)
            {
                blockId = BLOCK_AIR;
                int roofHalfWidth = halfWidth + 1;
                int roofTop = 8 + (roofHalfWidth + 1) / 2;
                bool insideWalls = absDx <= halfWidth && absDz <= halfDepth;
                bool porch = absDx <= 3 && dz >= -halfDepth - 3 && dz < -halfDepth;

                if (localY < -2 || localY > roofTop + 1 ||
                    absDx > halfWidth + 2 || absDz > halfDepth + 3)
                {
                    return false;
                }

                if (localY <= 0 && insideWalls)
                {
                    blockId = localY == 0 ? (byte)BLOCK_COBBLESTONE : (byte)BLOCK_STONE;
                    return true;
                }

                if (porch)
                {
                    if (localY == 0)
                    {
                        blockId = BLOCK_WOOD_BLANKET;
                        return true;
                    }

                    bool porchPost = absDx == 3 && dz == -halfDepth - 2 && localY >= 1 && localY <= 4;
                    if (porchPost)
                    {
                        blockId = BLOCK_WOOD;
                        return true;
                    }

                    if (localY == 4)
                    {
                        blockId = BLOCK_WOOD_BLANKET;
                        return true;
                    }
                }

                if (absDx <= roofHalfWidth && absDz <= halfDepth + 1)
                {
                    int roofY = 8 + (roofHalfWidth - absDx + 1) / 2;
                    if (localY == roofY)
                    {
                        blockId = BLOCK_WOOD_BLANKET;
                        return true;
                    }

                    bool gable = absDz == halfDepth && localY >= 8 && localY < roofY;
                    if (gable)
                    {
                        blockId = dx == 0 || absDx == halfWidth
                            ? (byte)BLOCK_WOOD
                            : (byte)BLOCK_WOOD_BLANKET;
                        return true;
                    }
                }

                bool chimney = dx == -halfWidth + 2 && dz == halfDepth - 2 &&
                               localY >= 1 && localY <= roofTop + 1;
                if (chimney)
                {
                    blockId = BLOCK_COBBLESTONE;
                    return true;
                }

                if (!insideWalls)
                    return false;

                bool perimeter = absDx == halfWidth || absDz == halfDepth;
                if (localY >= 1 && localY <= 7 && perimeter)
                {
                    bool doorway = dz == -halfDepth && absDx <= 1 && localY <= 3;
                    if (doorway)
                    {
                        blockId = BLOCK_AIR;
                        return true;
                    }

                    bool structuralBeam = absDx == halfWidth && (absDz == halfDepth || dz == 0) ||
                                          absDz == halfDepth && (absDx == halfWidth || dx == 0);
                    bool window = !structuralBeam && (localY == 2 || localY == 6) &&
                                  ((absDx == halfWidth && absDz % 4 <= 1) ||
                                   (absDz == halfDepth && absDx % 4 <= 1));
                    if (window)
                    {
                        blockId = BLOCK_GLASS;
                        return true;
                    }

                    blockId = structuralBeam ? (byte)BLOCK_WOOD : (byte)BLOCK_WOOD_BLANKET;
                    return true;
                }

                bool upperFloorOpening = absDx <= 1 && dz <= -halfDepth + 3;
                if (localY == 4 && absDx < halfWidth && absDz < halfDepth && !upperFloorOpening)
                {
                    blockId = BLOCK_WOOD_BLANKET;
                    return true;
                }

                if (localY == 1 && dx == halfWidth - 2 && dz == halfDepth - 2)
                {
                    blockId = BLOCK_CHEST;
                    return true;
                }

                if (localY >= 1 && localY <= roofTop)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                return false;
            }

            private static bool TryGetLargeDesertTempleStructureBlock(
                byte biome,
                int dx,
                int dz,
                int absDx,
                int absDz,
                int halfWidth,
                int halfDepth,
                int localY,
                out byte blockId)
            {
                blockId = BLOCK_AIR;
                if (localY < -2 || localY > 10 ||
                    absDx > halfWidth + 1 || absDz > halfDepth + 3)
                {
                    return false;
                }

                byte sandstone = biome == (byte)BiomeId.RedDesert
                    ? (byte)BLOCK_RED_SANDSTONE
                    : (byte)BLOCK_SANDSTONE;
                bool insideWalls = absDx <= halfWidth && absDz <= halfDepth;
                bool entrancePlatform = absDx <= 3 && dz >= -halfDepth - 3 && dz < -halfDepth;

                if (localY <= 0 && (insideWalls || entrancePlatform))
                {
                    blockId = sandstone;
                    return true;
                }

                bool entrancePillar = absDx == 3 && dz == -halfDepth - 2 && localY >= 1 && localY <= 5;
                if (entrancePillar)
                {
                    blockId = sandstone;
                    return true;
                }

                if (entrancePlatform && localY == 5)
                {
                    blockId = sandstone;
                    return true;
                }

                if (!insideWalls)
                    return false;

                bool cornerTower = absDx >= halfWidth - 2 && absDz >= halfDepth - 2;
                if (cornerTower && localY >= 1 && localY <= 8)
                {
                    bool towerWindow = localY == 4 &&
                                       (absDx == halfWidth && absDz == halfDepth - 1 ||
                                        absDz == halfDepth && absDx == halfWidth - 1);
                    blockId = towerWindow ? (byte)BLOCK_AIR : sandstone;
                    return true;
                }

                bool towerCrenellation = cornerTower && localY == 9 && ((dx + dz) & 1) == 0;
                if (towerCrenellation)
                {
                    blockId = sandstone;
                    return true;
                }

                bool perimeter = absDx == halfWidth || absDz == halfDepth;
                if (perimeter && localY >= 1 && localY <= 5)
                {
                    bool doorway = dz == -halfDepth && absDx <= 1 && localY <= 3;
                    if (doorway)
                    {
                        blockId = BLOCK_AIR;
                        return true;
                    }

                    bool window = localY == 3 &&
                                  ((absDx == halfWidth && absDz <= 2) ||
                                   (absDz == halfDepth && absDx >= 4 && absDx <= 6));
                    blockId = window ? (byte)BLOCK_AIR : sandstone;
                    return true;
                }

                bool wallCrenellation = localY == 6 && perimeter && !cornerTower && ((dx + dz) & 1) == 0;
                if (wallCrenellation)
                {
                    blockId = sandstone;
                    return true;
                }

                bool sanctuary = absDx <= 4 && dz >= halfDepth - 5;
                if (sanctuary)
                {
                    bool sanctuaryWall = absDx == 4 || dz == halfDepth - 5 || dz == halfDepth;
                    bool sanctuaryDoor = dz == halfDepth - 5 && absDx <= 1 && localY <= 3;
                    if (localY >= 1 && localY <= 4 && sanctuaryWall)
                    {
                        blockId = sanctuaryDoor ? (byte)BLOCK_AIR : sandstone;
                        return true;
                    }

                    if (localY == 5)
                    {
                        blockId = sandstone;
                        return true;
                    }
                }

                if (localY == 1 && dx == 0 && dz == halfDepth - 2)
                {
                    blockId = BLOCK_CHEST;
                    return true;
                }

                if (localY >= 1 && localY <= 9)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                return false;
            }

            private static bool TryGetSnowLodgeStructureBlock(
                int dx,
                int dz,
                int absDx,
                int absDz,
                int halfWidth,
                int halfDepth,
                int localY,
                out byte blockId)
            {
                blockId = BLOCK_AIR;
                int roofHalfWidth = halfWidth + 1;
                int roofTop = 5 + (roofHalfWidth + 1) / 2;
                bool insideWalls = absDx <= halfWidth && absDz <= halfDepth;
                bool porch = absDx <= 2 && dz >= -halfDepth - 3 && dz < -halfDepth;

                if (localY < -1 || localY > roofTop + 1 ||
                    absDx > halfWidth + 2 || absDz > halfDepth + 3)
                {
                    return false;
                }

                if (localY <= 0 && insideWalls)
                {
                    blockId = localY == 0 ? (byte)BLOCK_COBBLESTONE : (byte)BLOCK_STONE;
                    return true;
                }

                if (porch)
                {
                    if (localY == 0 || localY == 4)
                    {
                        blockId = localY == 0 ? (byte)BLOCK_WOOD_BLANKET : (byte)BLOCK_SNOW;
                        return true;
                    }

                    if (absDx == 2 && dz == -halfDepth - 2 && localY >= 1 && localY <= 3)
                    {
                        blockId = BLOCK_SPRUCE_LOG;
                        return true;
                    }
                }

                if (absDx <= roofHalfWidth && absDz <= halfDepth + 1)
                {
                    int roofY = 5 + (roofHalfWidth - absDx + 1) / 2;
                    if (localY == roofY)
                    {
                        blockId = BLOCK_SNOW;
                        return true;
                    }

                    bool gable = absDz == halfDepth && localY >= 5 && localY < roofY;
                    if (gable)
                    {
                        blockId = dx == 0 ? (byte)BLOCK_SPRUCE_LOG : (byte)BLOCK_WOOD_BLANKET;
                        return true;
                    }
                }

                bool chimney = dx == -halfWidth + 1 && dz == halfDepth - 2 &&
                               localY >= 1 && localY <= roofTop + 1;
                if (chimney)
                {
                    blockId = BLOCK_COBBLESTONE;
                    return true;
                }

                if (!insideWalls)
                    return false;

                bool perimeter = absDx == halfWidth || absDz == halfDepth;
                if (perimeter && localY >= 1 && localY <= 4)
                {
                    bool doorway = dz == -halfDepth && absDx <= 1 && localY <= 3;
                    if (doorway)
                    {
                        blockId = BLOCK_AIR;
                        return true;
                    }

                    bool corner = absDx == halfWidth && absDz == halfDepth;
                    bool window = !corner && localY == 2 &&
                                  ((absDx == halfWidth && absDz <= 2) ||
                                   (absDz == halfDepth && absDx <= 2));
                    blockId = window
                        ? (byte)BLOCK_GLASS
                        : (corner || dx == 0 && absDz == halfDepth ? (byte)BLOCK_SPRUCE_LOG : (byte)BLOCK_WOOD_BLANKET);
                    return true;
                }

                if (localY == 1 && dx == halfWidth - 2 && dz == halfDepth - 2)
                {
                    blockId = BLOCK_CHEST;
                    return true;
                }

                if (localY == 1 && dz == halfDepth - 2 && dx >= -2 && dx <= 0)
                {
                    blockId = BLOCK_WOOD_BLANKET;
                    return true;
                }

                if (localY >= 1 && localY <= roofTop)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                return false;
            }

            private bool IsRuinStructureCell(byte biome, int cellX, int cellZ)
            {
                if (biome == (byte)BiomeId.Snow)
                    return false;

                float chance = math.saturate(RuinStructureChance);
                if (biome == (byte)BiomeId.Forest)
                    chance = math.max(chance, 0.48f);
                else if (biome == (byte)BiomeId.Jungle)
                    chance = math.max(chance, 0.62f);

                return Hash01(Hash(cellX, cellZ, 0xA17D, Seed)) < chance;
            }

            private static bool TryGetIglooStructureBlock(
                int dx,
                int dz,
                int absDx,
                int absDz,
                int localY,
                out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (localY < 0 || localY > 4)
                    return false;

                bool tunnel = absDx <= 1 && dz >= -7 && dz <= -4;
                int horizontalSq = dx * dx + dz * dz;

                if (localY == 0)
                {
                    if (horizontalSq <= 16 || tunnel)
                    {
                        blockId = BLOCK_SNOW;
                        return true;
                    }

                    return false;
                }

                if (tunnel)
                {
                    if (localY >= 1 && localY <= 2 && absDx == 0)
                    {
                        blockId = BLOCK_AIR;
                        return true;
                    }

                    if (localY >= 1 && localY <= 2 && absDx == 1)
                    {
                        blockId = BLOCK_SNOW;
                        return true;
                    }

                    if (localY == 3)
                    {
                        blockId = BLOCK_SNOW;
                        return true;
                    }

                    return false;
                }

                if (absDx > 4 || absDz > 4)
                    return false;

                bool doorway = absDx <= 1 && dz <= -3 && dz >= -4 && localY <= 2;
                if (doorway)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                if (localY == 1 && dz == 1 && dx >= 1 && dx <= 2)
                {
                    blockId = BLOCK_WOOD_BLANKET;
                    return true;
                }

                if (localY == 1 && dx == -2 && dz == 1)
                {
                    blockId = BLOCK_CHEST;
                    return true;
                }

                int outerRadius = localY switch
                {
                    1 => 4,
                    2 => 4,
                    3 => 3,
                    _ => 2,
                };

                int innerRadius = localY switch
                {
                    1 => 3,
                    2 => 2,
                    3 => 1,
                    _ => -1,
                };

                if (horizontalSq > outerRadius * outerRadius)
                    return false;

                if (innerRadius >= 0 && horizontalSq <= innerRadius * innerRadius)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                blockId = BLOCK_SNOW;
                return true;
            }

            private bool TryGetRuinStructureBlock(
                byte biome,
                int worldX,
                int worldY,
                int worldZ,
                int cellX,
                int cellZ,
                int dx,
                int dz,
                int absDx,
                int absDz,
                int halfWidth,
                int halfDepth,
                bool largeRuin,
                int localY,
                out byte blockId)
            {
                blockId = BLOCK_AIR;

                int maximumHeight = largeRuin ? 9 : 5;
                if (localY < -2 || localY > maximumHeight ||
                    absDx > halfWidth + 2 || absDz > halfDepth + 2)
                {
                    return false;
                }

                bool insideFootprint = absDx <= halfWidth && absDz <= halfDepth;
                bool insideInterior = absDx < halfWidth && absDz < halfDepth;
                bool perimeter = insideFootprint && (absDx == halfWidth || absDz == halfDepth);
                bool cornerColumn = insideFootprint && absDx >= halfWidth - 1 && absDz >= halfDepth - 1;

                float columnNoise = Hash01(Hash(worldX, worldZ, 0x5EED, Seed));
                float blockNoise = Hash01(Hash(worldX, worldY, worldZ, 0x7A91, Seed));

                if (localY <= 0 && insideFootprint)
                {
                    if (localY == 0 && insideInterior && blockNoise < (largeRuin ? 0.12f : 0.22f))
                        return false;

                    blockId = GetRuinMaterial(biome, worldX, worldY, worldZ, 0.32f);
                    return true;
                }

                if (localY == 1)
                {
                    bool chestSpot = dx == halfWidth - 2 &&
                                     dz == halfDepth - 2 &&
                                     Hash01(Hash(cellX, cellZ, 0xCE57, Seed)) < (largeRuin ? 0.82f : 0.45f);
                    if (chestSpot)
                    {
                        blockId = BLOCK_CHEST;
                        return true;
                    }

                    bool outsideRubble = !insideFootprint && blockNoise < 0.26f;
                    bool insideRubble = insideInterior && blockNoise < 0.12f;
                    if (outsideRubble || insideRubble)
                    {
                        blockId = GetRuinMaterial(biome, worldX, worldY, worldZ, 0.48f);
                        return true;
                    }
                }

                if (!insideFootprint)
                    return false;

                bool entranceGap = dz == -halfDepth && absDx <= 1 && localY <= 2;
                if (entranceGap)
                {
                    blockId = BLOCK_AIR;
                    return true;
                }

                if (perimeter || cornerColumn)
                {
                    int heightVariation = largeRuin ? 7 : 4;
                    int maxWallHeight = 1 + (int)(Hash(worldX, worldZ, 0x3B29, Seed) % (uint)heightVariation);
                    if (cornerColumn)
                        maxWallHeight += largeRuin ? 2 : 1;

                    float missingChance = localY <= 1
                        ? 0.07f
                        : (largeRuin ? 0.12f : 0.20f) + localY * (largeRuin ? 0.055f : 0.08f);
                    if (localY <= maxWallHeight && columnNoise > missingChance)
                    {
                        blockId = GetRuinMaterial(biome, worldX, worldY, worldZ, 0.58f);
                        return true;
                    }
                }

                bool innerPillar =
                    localY >= 1 &&
                    localY <= (largeRuin ? 6 : 3) &&
                    absDx == math.max(1, halfWidth - 2) &&
                    absDz == math.max(1, halfDepth - 2) &&
                    Hash01(Hash(cellX + math.sign(dx), cellZ + math.sign(dz), 0xD331, Seed)) > 0.35f;

                if (innerPillar)
                {
                    blockId = GetRuinMaterial(biome, worldX, worldY, worldZ, 0.62f);
                    return true;
                }

                if (largeRuin)
                {
                    bool centralAisle = absDx <= 2 &&
                                        dz >= -halfDepth + 3 &&
                                        dz <= halfDepth - 3;
                    bool brokenUpperFloor = localY == 4 &&
                                            insideInterior &&
                                            !centralAisle &&
                                            blockNoise > 0.38f;
                    if (brokenUpperFloor)
                    {
                        blockId = GetRuinMaterial(biome, worldX, worldY, worldZ, 0.55f);
                        return true;
                    }

                    bool rearDais = localY == 1 && dz >= halfDepth - 3 && absDx <= 3;
                    if (rearDais)
                    {
                        blockId = GetRuinMaterial(biome, worldX, worldY, worldZ, 0.48f);
                        return true;
                    }
                }

                return false;
            }

            private byte GetRuinMaterial(byte biome, int worldX, int worldY, int worldZ, float mossChance)
            {
                float materialNoise = Hash01(Hash(worldX, worldY, worldZ, 0xA53D, Seed));

                if (TerrainNoiseUtility.IsDryDesertBiome(biome))
                {
                    if (materialNoise < 0.76f)
                        return biome == (byte)BiomeId.RedDesert || materialNoise < 0.16f ? (byte)BLOCK_RED_SANDSTONE : (byte)BLOCK_SANDSTONE;

                    return materialNoise < 0.90f ? (byte)BLOCK_COBBLESTONE : (byte)BLOCK_BRICKS;
                }

                if (biome == (byte)BiomeId.Snow || biome == (byte)BiomeId.Mountains)
                {
                    if (materialNoise < mossChance * 0.45f)
                        return BLOCK_TUFF_BRICKS;

                    if (materialNoise < mossChance * 0.70f)
                        return BLOCK_CRACKED_DEEPSLATE_BRICKS;
                }

                if (materialNoise < mossChance)
                    return BLOCK_MOSSY_BRICK_STONE;

                if (materialNoise < mossChance + 0.16f)
                    return BLOCK_COBBLESTONE;

                return materialNoise < mossChance + 0.27f ? (byte)BLOCK_TUFF_BRICKS : (byte)BLOCK_BRICKS;
            }

            private bool StructureTouchesOasis(int centerX, int centerZ, int halfWidth, int halfDepth)
            {
                OasisSample sample;
                return TryGetOasisSample(centerX, centerZ, out sample) ||
                       TryGetOasisSample(centerX - halfWidth, centerZ - halfDepth, out sample) ||
                       TryGetOasisSample(centerX + halfWidth, centerZ - halfDepth, out sample) ||
                       TryGetOasisSample(centerX - halfWidth, centerZ + halfDepth, out sample) ||
                       TryGetOasisSample(centerX + halfWidth, centerZ + halfDepth, out sample);
            }

            private static bool IsStructureBiome(byte biome)
            {
                return biome == (byte)BiomeId.Plains ||
                       TerrainNoiseUtility.IsDryDesertBiome(biome) ||
                       biome == (byte)BiomeId.Jungle ||
                       biome == (byte)BiomeId.Forest ||
                       biome == (byte)BiomeId.Snow;
            }

            private static bool TryGetSnowCoverBlock(
                byte biome,
                int worldX,
                int worldY,
                int worldZ,
                int groundLevel,
                int waterLevel,
                float riverStrength,
                int riverSurfaceLevel,
                out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (worldY != groundLevel + 1)
                    return false;

                bool snowBiome = biome == (byte)BiomeId.Snow;
                bool highMountainSnow = biome == (byte)BiomeId.Mountains && groundLevel > waterLevel + 72;
                if (!snowBiome && !highMountainSnow)
                    return false;

                if (groundLevel <= waterLevel + 1 ||
                    riverStrength > 0.20f ||
                    (riverSurfaceLevel != int.MinValue && groundLevel <= riverSurfaceLevel + 2))
                {
                    return false;
                }

                float coverNoise = SampleMaterialNoise01(
                    new float3(worldX, 0f, worldZ),
                    snowBiome ? 17f : 22f,
                    new float3(-28.4f, 0f, 64.7f));
                float driftNoise = SampleMaterialNoise01(
                    new float3(worldX, 0f, worldZ),
                    43f,
                    new float3(88.1f, 0f, -19.3f));

                bool covered = snowBiome
                    ? coverNoise > 0.36f || driftNoise > 0.67f
                    : coverNoise > 0.66f && driftNoise > 0.48f;

                if (!covered)
                    return false;

                blockId = BLOCK_SNOW;
                return true;
            }

            private static byte GetGeneratedWaterBlock(byte surfaceBiome, int worldY, int waterSurfaceLevel)
            {
                if (surfaceBiome == (byte)BiomeId.Snow && worldY == waterSurfaceLevel)
                    return BLOCK_ICE;

                return BLOCK_WATER;
            }

            private static byte GetSurfaceBlock(byte biome, byte surfaceBiome, int groundLevel, int waterLevel, float3 worldPosition, float transitionStrength, float desertEdgeStrength, float riverStrength, int riverSurfaceLevel)
            {
                if (TryGetRiverSurfaceBlock(biome, surfaceBiome, groundLevel, waterLevel, worldPosition, riverStrength, riverSurfaceLevel, out byte riverBlock))
                    return riverBlock;

                if (TryGetTransitionSurfaceBlock(biome, surfaceBiome, worldPosition, transitionStrength, desertEdgeStrength, out byte transitionBlock))
                    return transitionBlock;

                if (biome == (byte)BiomeId.Ocean)
                    return GetOceanSurfaceBlock(surfaceBiome, groundLevel, waterLevel, worldPosition);

                if (biome == (byte)BiomeId.Beach)
                    return GetShoreSurfaceBlock(surfaceBiome, groundLevel, waterLevel, worldPosition);

                if (surfaceBiome == (byte)BiomeId.Snow && !TerrainNoiseUtility.IsDryDesertBiome(biome))
                    return BLOCK_SNOW_GRASS;

                if (TerrainNoiseUtility.IsDryDesertBiome(biome))
                    return GetDryDesertSurfaceBlock(biome, worldPosition, desertEdgeStrength);

                if (biome == (byte)BiomeId.Snow)
                    return BLOCK_SNOW_GRASS;

                if (biome == (byte)BiomeId.Mountains)
                    return groundLevel > waterLevel + 70 ? (byte)BLOCK_SNOW_GRASS : (byte)BLOCK_STONE;

                return BLOCK_GRASS;
            }

            private static byte GetShoreSurfaceBlock(byte surfaceBiome, int groundLevel, int waterLevel, float3 worldPosition)
            {
                bool underwater = groundLevel <= waterLevel;
                float shoreNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    23f,
                    new float3(6.4f, 0f, -71.2f));

                switch (surfaceBiome)
                {
                    case (byte)BiomeId.Desert:
                    case (byte)BiomeId.RedDesert:
                        if (shoreNoise > 0.90f && underwater)
                            return GetDryDesertSandstoneBlock(surfaceBiome, worldPosition);

                        return shoreNoise < 0.18f ? (byte)BLOCK_GRAVEL : GetDryDesertSurfaceBlock(surfaceBiome, worldPosition, 0f);

                    case (byte)BiomeId.Snow:
                        if (underwater)
                            return BLOCK_DIRT;

                        return BLOCK_SNOW_GRASS;

                    case (byte)BiomeId.Mountains:
                        if (underwater && shoreNoise < 0.42f)
                            return BLOCK_GRAVEL;

                        return shoreNoise > 0.34f ? (byte)BLOCK_STONE : (byte)BLOCK_COBBLESTONE;

                    case (byte)BiomeId.Jungle:
                    case (byte)BiomeId.Forest:
                    case (byte)BiomeId.Plains:
                        if (underwater)
                            return shoreNoise < 0.46f ? (byte)BLOCK_GRAVEL : (byte)BLOCK_DIRT;

                        return BLOCK_GRASS;

                    default:
                        return underwater && shoreNoise > 0.62f ? (byte)BLOCK_DIRT : (byte)BLOCK_GRAVEL;
                }
            }

            private static byte GetDesertSurfaceBlock(float3 worldPosition, float desertEdgeStrength)
            {
                float gravelNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    13f,
                    new float3(-19.8f, 0f, 42.7f));

                if (desertEdgeStrength > 0.55f && gravelNoise > 0.94f)
                    return BLOCK_GRAVEL;

                return BLOCK_SAND;
            }

            private static byte GetDryDesertSurfaceBlock(byte biome, float3 worldPosition, float desertEdgeStrength)
            {
                if (biome == (byte)BiomeId.RedDesert)
                    return GetRedDesertSurfaceBlock(worldPosition, desertEdgeStrength);

                return GetDesertSurfaceBlock(worldPosition, desertEdgeStrength);
            }

            private static byte GetRedDesertSurfaceBlock(float3 worldPosition, float desertEdgeStrength)
            {
                float gravelNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    14f,
                    new float3(-93.8f, 0f, 81.4f));
                float sandNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    36f,
                    new float3(214.5f, 0f, -42.3f));

                if (desertEdgeStrength > 0.48f && gravelNoise > 0.90f)
                    return BLOCK_GRAVEL;

                return sandNoise < 0.020f ? (byte)BLOCK_SAND : (byte)BLOCK_RED_SAND;
            }

            private static byte GetDesertSandstoneBlock(float3 worldPosition)
            {
                return BLOCK_SANDSTONE;
            }

            private static byte GetDryDesertSandstoneBlock(byte biome, float3 worldPosition)
            {
                return biome == (byte)BiomeId.RedDesert
                    ? (byte)BLOCK_RED_SANDSTONE
                    : GetDesertSandstoneBlock(worldPosition);
            }

            private static byte GetOceanSurfaceBlock(byte surfaceBiome, int groundLevel, int waterLevel, float3 worldPosition)
            {
                int waterDepth = waterLevel - groundLevel;
                if (waterDepth <= 14)
                    return GetShoreSurfaceBlock(surfaceBiome, groundLevel, waterLevel, worldPosition);

                float floorNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    29f,
                    new float3(-103.5f, 0f, 36.2f));
                float rockNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    47f,
                    new float3(74.1f, 0f, -119.8f));

                if (waterDepth > 58)
                {
                    float ventNoise = SampleMaterialNoise01(
                        new float3(worldPosition.x, 0f, worldPosition.z),
                        11f,
                        new float3(211.6f, 0f, 58.4f));
                    if (ventNoise > 0.985f)
                        return BLOCK_MAGMA;

                    if (rockNoise > 0.78f)
                        return BLOCK_TUFF;

                    if (floorNoise < 0.22f)
                        return BLOCK_GRAVEL;

                    return floorNoise > 0.42f ? (byte)BLOCK_STONE : (byte)BLOCK_COBBLESTONE;
                }

                if (waterDepth > 30)
                {
                    if (rockNoise > 0.86f)
                        return BLOCK_TUFF;

                    if (floorNoise < 0.28f)
                        return floorNoise < 0.12f ? (byte)BLOCK_GRAVEL : (byte)BLOCK_SAND;

                    return floorNoise < 0.62f ? (byte)BLOCK_COBBLESTONE : (byte)BLOCK_STONE;
                }

                if (TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
                    return floorNoise > 0.72f ? GetDryDesertSandstoneBlock(surfaceBiome, worldPosition) : GetDryDesertSurfaceBlock(surfaceBiome, worldPosition, 0f);

                return floorNoise < 0.26f ? (byte)BLOCK_GRAVEL : (floorNoise < 0.58f ? (byte)BLOCK_SAND : (byte)BLOCK_DIRT);
            }

            private static bool TryGetRiverSurfaceBlock(byte biome, byte surfaceBiome, int groundLevel, int waterLevel, float3 worldPosition, float riverStrength, int riverSurfaceLevel, out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (riverStrength < 0.22f || biome == (byte)BiomeId.Ocean)
                    return false;

                float riverNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    12f,
                    new float3(-13.1f, 0f, 89.5f));

                bool hasRiverSurface = riverSurfaceLevel != int.MinValue;
                bool isSubmergedRiverBed = hasRiverSurface && groundLevel < riverSurfaceLevel;
                if (isSubmergedRiverBed)
                {
                    if (TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
                        blockId = riverNoise > 0.78f ? GetDryDesertSandstoneBlock(surfaceBiome, worldPosition) : GetDryDesertSurfaceBlock(surfaceBiome, worldPosition, 0f);
                    else
                    {
                        blockId = surfaceBiome switch
                        {
                            (byte)BiomeId.Snow => riverNoise > 0.62f ? (byte)BLOCK_GRAVEL : (byte)BLOCK_DIRT,
                            (byte)BiomeId.Jungle => riverNoise > 0.76f ? (byte)BLOCK_GRAVEL : (byte)BLOCK_DIRT,
                            (byte)BiomeId.Forest => riverNoise > 0.70f ? (byte)BLOCK_GRAVEL : (byte)BLOCK_DIRT,
                            (byte)BiomeId.Plains => riverNoise > 0.70f ? (byte)BLOCK_GRAVEL : (byte)BLOCK_DIRT,
                            _ => riverNoise > 0.82f ? (byte)BLOCK_COBBLESTONE : (byte)BLOCK_GRAVEL,
                        };
                    }
                    return true;
                }

                bool isWetRiverBank = hasRiverSurface && groundLevel <= riverSurfaceLevel + 1;
                if (isWetRiverBank && riverStrength > 0.58f)
                {
                    if (TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
                        blockId = riverNoise > 0.82f ? GetDryDesertSandstoneBlock(surfaceBiome, worldPosition) : GetDryDesertSurfaceBlock(surfaceBiome, worldPosition, 0f);
                    else
                    {
                        blockId = surfaceBiome switch
                        {
                            (byte)BiomeId.Snow => (byte)BLOCK_SNOW_GRASS,
                            (byte)BiomeId.Jungle => (byte)BLOCK_GRASS,
                            (byte)BiomeId.Forest => (byte)BLOCK_GRASS,
                            (byte)BiomeId.Plains => (byte)BLOCK_GRASS,
                            _ => riverNoise > 0.82f ? (byte)BLOCK_SAND : (byte)BLOCK_GRASS,
                        };
                    }
                    return true;
                }

                return false;
            }

            private static bool TryGetTransitionSurfaceBlock(byte biome, byte surfaceBiome, float3 worldPosition, float transitionStrength, float desertEdgeStrength, out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (biome == (byte)BiomeId.Ocean || biome == (byte)BiomeId.Beach)
                    return false;

                float transition = math.saturate(math.max(transitionStrength, desertEdgeStrength));
                if (transition < 0.22f)
                    return false;

                float edgeNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    18f,
                    new float3(41.7f, 0f, -26.9f));
                float patchNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    8f,
                    new float3(-82.3f, 0f, 113.8f));
                float edgeCoverage = transition * math.lerp(0.62f, 1.38f, edgeNoise);

                bool desertSide = TerrainNoiseUtility.IsDryDesertBiome(biome) || TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome);
                byte desertBiome = TerrainNoiseUtility.IsDryDesertBiome(biome) ? biome : surfaceBiome;
                if (desertSide)
                {
                    float patch = edgeCoverage * math.lerp(0.72f, 1.24f, patchNoise);

                    if (desertEdgeStrength > 0.48f && patch > 0.84f && edgeNoise > 0.86f)
                    {
                        blockId = GetDryDesertSandstoneBlock(desertBiome, worldPosition);
                        return true;
                    }

                    return false;
                }

                byte transitionBiome = surfaceBiome == (byte)BiomeId.Snow || TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome)
                    ? surfaceBiome
                    : (biome == (byte)BiomeId.Beach || biome == (byte)BiomeId.Ocean ? surfaceBiome : biome);
                switch (transitionBiome)
                {
                    case (byte)BiomeId.Desert:
                    case (byte)BiomeId.RedDesert:
                        if (edgeCoverage > 0.52f)
                        {
                            blockId = edgeNoise > 0.86f ? GetDryDesertSandstoneBlock(transitionBiome, worldPosition) : GetDryDesertSurfaceBlock(transitionBiome, worldPosition, desertEdgeStrength);
                            return true;
                        }
                        break;

                    case (byte)BiomeId.Plains:
                    case (byte)BiomeId.Jungle:
                    case (byte)BiomeId.Forest:
                        break;

                    case (byte)BiomeId.Snow:
                        break;

                    case (byte)BiomeId.Mountains:
                        if (edgeCoverage > 0.60f)
                        {
                            blockId = edgeNoise < 0.45f ? (byte)BLOCK_GRASS : (byte)BLOCK_COBBLESTONE;
                            return true;
                        }
                        break;
                }

                return false;
            }

            private static int GetSoilDepth(byte biome, byte surfaceBiome)
            {
                if (biome == (byte)BiomeId.Ocean && !TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
                    return 3;

                if (biome == (byte)BiomeId.Beach)
                    return TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome) ? 4 : 5;

                if (TerrainNoiseUtility.IsDryDesertBiome(biome))
                    return 5;

                return biome switch
                {
                    (byte)BiomeId.Ocean => 3,
                    (byte)BiomeId.Beach => 4,
                    (byte)BiomeId.Mountains => 2,
                    (byte)BiomeId.Snow => 4,
                    _ => 5,
                };
            }

            private static byte GetSubsurfaceBlock(byte biome, byte surfaceBiome, float3 worldPosition, int depthBelowSurface, float transitionStrength, float desertEdgeStrength, float riverStrength, int waterLevel)
            {
                if (riverStrength > 0.20f && depthBelowSurface <= 4)
                {
                    float riverNoise = SampleMaterialNoise01(worldPosition, 14f, new float3(27.7f, -4.1f, 112.6f));
                    return TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome)
                        ? (riverNoise > 0.76f ? GetDryDesertSandstoneBlock(surfaceBiome, worldPosition) : GetDryDesertSurfaceBlock(surfaceBiome, worldPosition, 0f))
                        : (byte)BLOCK_DIRT;
                }

                if (transitionStrength > 0.38f && depthBelowSurface <= 3 && biome != (byte)BiomeId.Ocean && biome != (byte)BiomeId.Beach)
                {
                    float edgeNoise = SampleMaterialNoise01(worldPosition, 20f, new float3(-54.2f, 8.1f, 77.5f));
                    if (TerrainNoiseUtility.IsDryDesertBiome(biome) || TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
                    {
                        byte edgeDesertBiome = TerrainNoiseUtility.IsDryDesertBiome(biome) ? biome : surfaceBiome;
                        return depthBelowSurface >= 3 && edgeNoise > 0.74f
                            ? GetDryDesertSandstoneBlock(edgeDesertBiome, worldPosition)
                            : GetDryDesertSurfaceBlock(edgeDesertBiome, worldPosition, desertEdgeStrength);
                    }

                    if (biome != (byte)BiomeId.Ocean && edgeNoise > 0.62f)
                        return BLOCK_DIRT;
                }

                if (TerrainNoiseUtility.IsDryDesertBiome(biome) || TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
                {
                    byte dryBiome = TerrainNoiseUtility.IsDryDesertBiome(biome) ? biome : surfaceBiome;
                    return depthBelowSurface >= 3 ? GetDryDesertSandstoneBlock(dryBiome, worldPosition) : GetDryDesertSurfaceBlock(dryBiome, worldPosition, desertEdgeStrength);
                }

                if (biome == (byte)BiomeId.Ocean)
                {
                    int surfaceLevel = (int)math.round(worldPosition.y + depthBelowSurface);
                    int waterDepth = waterLevel - surfaceLevel;
                    float shoreSoilNoise = SampleMaterialNoise01(worldPosition, 21f, new float3(16.7f, 5.1f, -92.4f));

                    if (waterDepth > 58)
                    {
                        if (shoreSoilNoise > 0.82f)
                            return BLOCK_TUFF;

                        return shoreSoilNoise > 0.38f ? (byte)BLOCK_STONE : (byte)BLOCK_COBBLESTONE;
                    }

                    if (waterDepth > 30)
                        return shoreSoilNoise > 0.54f ? (byte)BLOCK_STONE : (byte)BLOCK_COBBLESTONE;

                    if (TerrainNoiseUtility.IsDryDesertBiome(surfaceBiome))
                        return depthBelowSurface >= 3 ? GetDryDesertSandstoneBlock(surfaceBiome, worldPosition) : GetDryDesertSurfaceBlock(surfaceBiome, worldPosition, 0f);

                    return shoreSoilNoise < 0.62f ? (byte)BLOCK_DIRT : (byte)BLOCK_SAND;
                }

                if (biome == (byte)BiomeId.Beach)
                {
                    float shoreSoilNoise = SampleMaterialNoise01(worldPosition, 21f, new float3(16.7f, 5.1f, -92.4f));
                    if (surfaceBiome == (byte)BiomeId.Mountains)
                        return shoreSoilNoise > 0.50f ? (byte)BLOCK_STONE : (byte)BLOCK_COBBLESTONE;

                    if (surfaceBiome == (byte)BiomeId.Snow)
                        return depthBelowSurface <= 2 && shoreSoilNoise < 0.64f ? (byte)BLOCK_DIRT : (byte)BLOCK_STONE;

                    return shoreSoilNoise < 0.72f ? (byte)BLOCK_DIRT : (byte)BLOCK_SAND;
                }

                if (biome == (byte)BiomeId.Mountains)
                {
                    float mountainStoneNoise = SampleMaterialNoise01(worldPosition, 18f, new float3(13.7f, 0f, -41.2f));
                    if (mountainStoneNoise > 0.82f)
                        return BLOCK_GRANITE;

                    return mountainStoneNoise > 0.58f ? (byte)BLOCK_TUFF : (byte)BLOCK_STONE;
                }

                return BLOCK_DIRT;
            }

            private byte GetUndergroundBlock(byte biome, float3 worldPosition, int groundLevel, int waterLevel, int depthBelowSurface)
            {
                if (ShouldPlaceMagmaNearLava(worldPosition, groundLevel, depthBelowSurface))
                    return BLOCK_MAGMA;

                if (TerrainNoiseUtility.IsDryDesertBiome(biome))
                    return GetDesertUndergroundBlock(biome, worldPosition, depthBelowSurface);

                if (TryGetOreBlock(worldPosition, waterLevel, depthBelowSurface, out byte oreBlock))
                    return oreBlock;

                float dampNoise = SampleMaterialNoise01(worldPosition, 34f, new float3(57.2f, -13.8f, 4.4f));
                bool dampBiome = biome == (byte)BiomeId.Jungle || biome == (byte)BiomeId.Forest || biome == (byte)BiomeId.Ocean || biome == (byte)BiomeId.Snow;
                if (dampBiome && depthBelowSurface > 10 && depthBelowSurface < 90 && dampNoise > 0.91f)
                    return BLOCK_MOSSY_BRICK_STONE;

                if (depthBelowSurface > 18)
                {
                    float ancientNoise = SampleMaterialNoise01(worldPosition, 52f, new float3(-93.4f, 21.9f, 15.6f));
                    if (depthBelowSurface > 70 && ancientNoise > 0.985f)
                        return BLOCK_CRACKED_DEEPSLATE_BRICKS;

                    if (ancientNoise > 0.972f)
                        return BLOCK_TUFF_BRICKS;

                    if (ancientNoise > 0.958f)
                        return BLOCK_BRICKS;
                }

                if (worldPosition.y < waterLevel - 95)
                {
                    float deepHeat = SampleMaterialNoise01(worldPosition, 30f, new float3(4.9f, 101.3f, -71.6f));
                    if (deepHeat < 0.07f)
                        return BLOCK_OBSIDIAN;

                    if (deepHeat > 0.91f)
                        return BLOCK_SMOOTH_BASALT;
                }

                float rockNoise = SampleMaterialNoise01(worldPosition, 22f, new float3(31.1f, 19.6f, -83.0f));
                if (depthBelowSurface > 55 && rockNoise > 0.66f)
                    return BLOCK_TUFF;

                if (depthBelowSurface > 24 && rockNoise > 0.50f && rockNoise < 0.58f)
                    return BLOCK_GRANITE;

                if (depthBelowSurface > 38 && rockNoise > 0.60f && rockNoise < 0.64f)
                    return BLOCK_GRAVEL;

                if (depthBelowSurface > 14 && rockNoise < 0.22f)
                    return BLOCK_COBBLESTONE;

                return BLOCK_STONE;
            }

            private static byte GetDesertUndergroundBlock(byte biome, float3 worldPosition, int depthBelowSurface)
            {
                if (depthBelowSurface < 9)
                    return GetDryDesertSandstoneBlock(biome, worldPosition);

                float sandPocketNoise = SampleMaterialNoise01(worldPosition, 26f, new float3(-71.4f, 18.3f, 95.1f));
                if (sandPocketNoise > 0.90f)
                    return GetDryDesertSurfaceBlock(biome, worldPosition, 0f);

                return sandPocketNoise > 0.18f ? GetDryDesertSandstoneBlock(biome, worldPosition) : (byte)BLOCK_GRAVEL;
            }

            private bool TryGetOreBlock(float3 worldPosition, int waterLevel, int depthBelowSurface, out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (depthBelowSurface < 14)
                    return false;

                bool deepStoneLayer = depthBelowSurface > 72 || worldPosition.y < waterLevel - 62;

                float ironNoise = SampleMaterialNoise01(worldPosition, 27f, new float3(118.2f, -42.8f, 11.3f));
                if (ironNoise > 0.875f)
                {
                    blockId = deepStoneLayer ? (byte)BLOCK_DEEPSLATE_IRON_ORE : (byte)BLOCK_IRON;
                    return true;
                }

                if (depthBelowSurface > 22)
                {
                    float goldNoise = SampleMaterialNoise01(worldPosition, 30f, new float3(33.9f, -90.2f, 144.6f));
                    if (goldNoise > 0.952f)
                    {
                        blockId = deepStoneLayer ? (byte)BLOCK_DEEPSLATE_GOLD_ORE : (byte)BLOCK_GOLD_ORE;
                        return true;
                    }
                }

                if (depthBelowSurface > 26)
                {
                    float lapisNoise = SampleMaterialNoise01(worldPosition, 25f, new float3(-151.7f, 35.2f, 52.6f));
                    if (lapisNoise > 0.957f)
                    {
                        blockId = BLOCK_LAPIS_ORE;
                        return true;
                    }
                }

                if (depthBelowSurface > 32)
                {
                    float amethystNoise = SampleMaterialNoise01(worldPosition, 38f, new float3(-67.6f, 84.1f, 23.8f));
                    if (amethystNoise > 0.93f)
                    {
                        blockId = BLOCK_AMETHYST;
                        return true;
                    }
                }

                bool deepEnoughForDiamond = depthBelowSurface > 76 || worldPosition.y < waterLevel - 70;
                if (deepEnoughForDiamond)
                {
                    float diamondNoise = SampleMaterialNoise01(worldPosition, 31f, new float3(5.4f, -131.9f, 92.7f));
                    if (diamondNoise > 0.965f)
                    {
                        blockId = deepStoneLayer ? (byte)BLOCK_DEEPSLATE_DIAMOND_ORE : (byte)BLOCK_DIAMOND;
                        return true;
                    }
                }

                if (depthBelowSurface > 44)
                {
                    float emeraldNoise = SampleMaterialNoise01(worldPosition, 29f, new float3(181.4f, -74.7f, -41.9f));
                    if (emeraldNoise > 0.972f)
                    {
                        blockId = deepStoneLayer ? (byte)BLOCK_DEEPSLATE_EMERALD_ORE : (byte)BLOCK_EMERALD_ORE;
                        return true;
                    }
                }

                return false;
            }

            private byte GetCaveFillBlock(byte biome, float3 worldPosition, int groundLevel, int depthBelowSurface)
            {
                if (ShouldFillCaveWithLava(worldPosition, groundLevel, depthBelowSurface))
                    return BLOCK_LAVA;

                if (TryGetSnowCaveBlock(biome, worldPosition, groundLevel, depthBelowSurface, out byte snowCaveBlock))
                    return snowCaveBlock;

                if (depthBelowSurface > 18 && IsCaveCeilingCell(worldPosition, groundLevel))
                {
                    float glowNoise = SampleMaterialNoise01(worldPosition, 18f, new float3(-88.6f, 214.2f, 31.5f));
                    if (glowNoise > 0.992f)
                        return BLOCK_GLOWSTONE;
                }

                if (depthBelowSurface > 8)
                {
                    float webNoise = SampleMaterialNoise01(worldPosition, 10f, new float3(51.8f, -26.3f, 149.4f));
                    if (webNoise > 0.993f)
                        return BLOCK_COBWEB;
                }

                return BLOCK_AIR;
            }

            private bool ShouldFillCaveWithLava(float3 worldPosition, int groundLevel, int depthBelowSurface)
            {
                if (TryGetLavaSeaSurfaceY(worldPosition, groundLevel, depthBelowSurface, out int lavaSurfaceY) &&
                    worldPosition.y <= lavaSurfaceY)
                {
                    return true;
                }

                return IsInLavaPatch(worldPosition, depthBelowSurface, 0f) &&
                       IsCaveFloorCell(worldPosition, groundLevel);
            }

            private bool ShouldPlaceMagmaNearLava(float3 worldPosition, int groundLevel, int depthBelowSurface)
            {
                if (math.saturate(CaveNoise.LavaChance) <= 0f ||
                    depthBelowSurface < GetSetting(CaveNoise.LavaMinDepth, 72))
                {
                    return false;
                }

                if (!IsNearLavaLake(worldPosition, groundLevel, depthBelowSurface))
                {
                    return false;
                }

                float crustNoise = SampleMaterialNoise01(worldPosition, 9f, new float3(12.7f, -58.4f, 43.9f));
                return crustNoise > 0.26f;
            }

            private bool IsNearLavaLake(float3 worldPosition, int groundLevel, int depthBelowSurface)
            {
                return IsLavaLakeCell(worldPosition + new float3(0f, 1f, 0f), groundLevel, depthBelowSurface - 1, 0f) ||
                       IsLavaLakeCell(worldPosition + new float3(1f, 0f, 0f), groundLevel, depthBelowSurface, 0.015f) ||
                       IsLavaLakeCell(worldPosition + new float3(-1f, 0f, 0f), groundLevel, depthBelowSurface, 0.015f) ||
                       IsLavaLakeCell(worldPosition + new float3(0f, 0f, 1f), groundLevel, depthBelowSurface, 0.015f) ||
                       IsLavaLakeCell(worldPosition + new float3(0f, 0f, -1f), groundLevel, depthBelowSurface, 0.015f);
            }

            private bool IsLavaLakeCell(float3 worldPosition, int groundLevel, int depthBelowSurface, float thresholdRelax)
            {
                if (depthBelowSurface < 0)
                    return false;

                if (TryGetLavaSeaSurfaceY(worldPosition, groundLevel, depthBelowSurface, out int lavaSurfaceY) &&
                    worldPosition.y <= lavaSurfaceY)
                {
                    return true;
                }

                return IsInLavaPatch(worldPosition, depthBelowSurface, thresholdRelax) &&
                       IsCaveFloorCell(worldPosition, groundLevel);
            }

            private bool IsCaveFloorCell(float3 worldPosition, int groundLevel)
            {
                if (!ShouldCarveCave(worldPosition, groundLevel))
                    return false;

                if (ShouldCarveCave(worldPosition + new float3(0f, -1f, 0f), groundLevel))
                    return false;

                return ShouldCarveCave(worldPosition + new float3(0f, 1f, 0f), groundLevel);
            }

            private bool IsCaveCeilingCell(float3 worldPosition, int groundLevel)
            {
                if (!ShouldCarveCave(worldPosition, groundLevel))
                    return false;

                if (ShouldCarveCave(worldPosition + new float3(0f, 1f, 0f), groundLevel))
                    return false;

                return ShouldCarveCave(worldPosition + new float3(0f, -1f, 0f), groundLevel);
            }

            private bool IsInLavaPatch(float3 worldPosition, int depthBelowSurface, float thresholdRelax)
            {
                float lavaChance = math.saturate(CaveNoise.LavaChance);
                if (lavaChance <= 0f)
                    return false;

                int lavaMinDepth = GetSetting(CaveNoise.LavaMinDepth, 72);
                if (depthBelowSurface < lavaMinDepth)
                    return false;

                float deepEnough01 = math.saturate((depthBelowSurface - lavaMinDepth) / 42f);
                float lavaScale = math.max(12f, CaveNoise.LavaPatchScale > 0f ? CaveNoise.LavaPatchScale * 0.62f : 64f * 0.62f);
                float lavaNoise = SampleMaterialNoise01(worldPosition, lavaScale, new float3(-24.1f, 63.5f, 119.8f));
                float deepThreshold = math.max(0.90f, 0.96f - lavaChance * 0.50f);
                float threshold = math.lerp(0.985f, deepThreshold, deepEnough01) - math.max(0f, thresholdRelax);
                threshold = math.saturate(threshold);
                if (lavaNoise <= threshold)
                    return false;

                float lavaFloorNoise = SampleMaterialNoise01(worldPosition, lavaScale * 0.55f, new float3(80.3f, -11.6f, -33.7f));
                return lavaFloorNoise > 0.50f;
            }

            private bool TryGetLavaSeaSurfaceY(float3 worldPosition, int groundLevel, int depthBelowSurface, out int surfaceY)
            {
                surfaceY = int.MinValue;

                float lavaChance = math.saturate(CaveNoise.LavaChance);
                if (lavaChance <= 0f)
                    return false;

                int lavaMinDepth = GetSetting(CaveNoise.LavaMinDepth, 72);
                if (depthBelowSurface < lavaMinDepth)
                    return false;

                float deepEnough01 = math.saturate((depthBelowSurface - lavaMinDepth) / 56f);
                float lavaScale = math.max(72f, CaveNoise.LavaPatchScale > 0f ? CaveNoise.LavaPatchScale : 64f);
                float horizontalArea = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    lavaScale * 1.55f,
                    new float3(318.7f, 0f, -206.9f));
                float shoreNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    lavaScale * 0.36f,
                    new float3(41.2f, 0f, 89.7f));

                float deepThreshold = math.max(0.90f, 0.94f - lavaChance * 0.50f);
                float threshold = math.lerp(0.975f, deepThreshold, deepEnough01);
                threshold += (shoreNoise - 0.5f) * 0.055f;
                if (horizontalArea < threshold)
                    return false;

                float basinNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, worldPosition.y * 0.22f, worldPosition.z),
                    lavaScale * 0.72f,
                    new float3(-142.8f, 74.2f, 351.4f));
                if (basinNoise < 0.62f)
                    return false;

                float surfaceDepth = math.lerp(lavaMinDepth + 5f, lavaMinDepth + 36f, basinNoise);
                surfaceY = groundLevel - (int)math.round(surfaceDepth);
                surfaceY += (int)math.round((shoreNoise - 0.5f) * 9f);
                surfaceY = math.min(surfaceY, groundLevel - lavaMinDepth + 2);
                return true;
            }

            private bool TryGetSnowCaveBlock(byte biome, float3 worldPosition, int groundLevel, int depthBelowSurface, out byte blockId)
            {
                blockId = BLOCK_AIR;

                if (biome != (byte)BiomeId.Snow || depthBelowSurface < 5)
                    return false;

                float frostNoise = SampleMaterialNoise01(worldPosition, 18f, new float3(61.2f, -37.5f, 128.8f));
                float broadFrostNoise = SampleMaterialNoise01(
                    new float3(worldPosition.x, 0f, worldPosition.z),
                    72f,
                    new float3(-178.5f, 0f, 44.1f));

                if (IsCaveFloorCell(worldPosition, groundLevel))
                {
                    if (broadFrostNoise > 0.28f && frostNoise > 0.24f)
                    {
                        blockId = frostNoise > 0.54f ? (byte)BLOCK_ICE : (byte)BLOCK_SNOW;
                        return true;
                    }
                }

                if (IsCaveCeilingCell(worldPosition, groundLevel) && broadFrostNoise > 0.62f && frostNoise > 0.70f)
                {
                    blockId = BLOCK_ICE;
                    return true;
                }

                return false;
            }

            private bool ShouldPlaceBedrock(int worldX, int worldY, int worldZ)
            {
                int thickness = math.max(1, BedrockThickness);
                if (worldY <= BedrockLevel)
                    return true;

                int transitionTop = BedrockLevel + thickness;
                if (worldY >= transitionTop)
                    return false;

                float depth01 = math.saturate((transitionTop - worldY) / (float)thickness);
                float chance = math.lerp(0.20f, 0.88f, depth01);
                return Hash01(Hash(worldX, worldY, worldZ, 0xBED, Seed)) < chance;
            }

            private static float SampleMaterialNoise01(float3 worldPosition, float scale, float3 offset)
            {
                float frequency = 1f / math.max(0.0001f, scale);
                float noiseValue = noise.snoise((worldPosition + offset) * frequency);
                return noiseValue * 0.5f + 0.5f;
            }

            internal bool ShouldCarveCave(float3 worldPosition, int groundLevel)
            {
                if (!EnableCaves)
                    return false;

                int depthBelowSurface = (int)math.floor(groundLevel - worldPosition.y);
                if (depthBelowSurface <= CaveNoise.SurfaceClearance)
                    return false;

                float fadeDistance = math.max(1f, CaveNoise.DepthFadeDistance > 0 ? CaveNoise.DepthFadeDistance : 52);
                float depth01 = math.saturate((depthBelowSurface - CaveNoise.SurfaceClearance) / fadeDistance);

                if (ShouldCarveCheeseCave(worldPosition, depth01))
                    return true;

                if (depthBelowSurface >= GetSetting(CaveNoise.TunnelMinDepth, 4) &&
                    ShouldCarveTunnelCave(worldPosition, depth01))
                    return true;

                if (depthBelowSurface >= GetSetting(CaveNoise.RoomMinDepth, 12) &&
                    ShouldCarveCaveRoom(worldPosition, depth01))
                    return true;

                return depthBelowSurface >= GetSetting(CaveNoise.RavineMinDepth, 6) &&
                       ShouldCarveRavine(worldPosition, groundLevel, depth01);
            }

            private bool ShouldCarveSurfaceCaveEntrance(float3 worldPosition, int groundLevel, int depthBelowSurface)
            {
                int waterLevel = GroundOffset + WaterLevel;
                int minimumGroundClearance = math.max(
                    3,
                    CaveNoise.SurfaceConnectionMinGroundAboveWater > 0
                        ? CaveNoise.SurfaceConnectionMinGroundAboveWater
                        : 6);
                if (!CaveNoise.EnableSurfaceConnections ||
                    groundLevel <= waterLevel + minimumGroundClearance ||
                    depthBelowSurface < -1 ||
                    depthBelowSurface > 96)
                {
                    return false;
                }

                int cellSize = math.max(
                    64,
                    CaveNoise.SurfaceConnectionCellSize > 0
                        ? CaveNoise.SurfaceConnectionCellSize
                        : 112);
                float connectionChance = math.saturate(
                    CaveNoise.SurfaceConnectionChance > 0f
                        ? CaveNoise.SurfaceConnectionChance
                        : 0.24f);
                int cellX = FastFloorToInt(worldPosition.x / cellSize);
                int cellZ = FastFloorToInt(worldPosition.z / cellSize);

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                    {
                        int candidateCellX = cellX + offsetX;
                        int candidateCellZ = cellZ + offsetZ;

                        if (Hash01(Hash(candidateCellX, candidateCellZ, 0xC4E1, Seed)) > connectionChance)
                            continue;

                        float margin = cellSize * 0.22f;
                        float centerX = candidateCellX * cellSize + math.lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x4317, Seed)));
                        float centerZ = candidateCellZ * cellSize + math.lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x924B, Seed)));
                        float angle = Hash01(Hash(candidateCellX, candidateCellZ, 0xE311, Seed)) * math.PI * 2f;
                        float2 direction = new float2(math.cos(angle), math.sin(angle));
                        float2 delta = new float2(worldPosition.x - centerX, worldPosition.z - centerZ);

                        float along = math.dot(delta, direction);
                        float length = math.lerp(56f, 88f, Hash01(Hash(candidateCellX, candidateCellZ, 0xA5C3, Seed)));
                        float mouthBacktrack = math.lerp(7f, 12f, Hash01(Hash(candidateCellX, candidateCellZ, 0x742D, Seed)));
                        float lowerChamberReach = math.lerp(24f, 38f, Hash01(Hash(candidateCellX, candidateCellZ, 0x2F6D, Seed)));
                        if (along < -mouthBacktrack || along > length + lowerChamberReach)
                            continue;

                        float extraAlong = math.max(0f, along - length);
                        float chamberTaper = 1f - math.saturate(extraAlong / math.max(1f, lowerChamberReach));
                        float across = math.abs(delta.x * direction.y - delta.y * direction.x);
                        float path01 = math.saturate(along / math.max(1f, length));
                        float mouth01 = 1f - math.saturate((along + mouthBacktrack) / (mouthBacktrack + 9f));
                        float end01 = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.68f, 1f, path01)));
                        float chamber01 = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.78f, 1f, path01)));
                        float width = math.lerp(2.8f, 5.4f, Hash01(Hash(candidateCellX, candidateCellZ, 0x39D5, Seed)));
                        width *= math.lerp(1.35f, 0.88f, path01);
                        width += mouth01 * 1.9f + end01 * 1.6f + chamber01 * 2.8f;
                        width += chamberTaper * 4.1f;
                        float roughness = SampleMaterialNoise01(worldPosition, 11f, new float3(-24.7f, 31.1f, 8.6f));
                        width += (roughness - 0.5f) * 0.55f;
                        if (across > width)
                            continue;

                        float mouthDepth = math.lerp(-0.55f, 0.35f, Hash01(Hash(candidateCellX, candidateCellZ, 0x18AF, Seed)));
                        float endDepth = math.lerp(46f, 76f, Hash01(Hash(candidateCellX, candidateCellZ, 0x6D2B, Seed)));
                        float targetDepth = math.lerp(mouthDepth, endDepth, path01);
                        float verticalRadius = math.lerp(3.0f, 5.1f, path01);
                        verticalRadius += mouth01 * 1.45f + end01 * 1.4f + chamber01 * 4.0f;

                        if (along > length)
                        {
                            targetDepth = endDepth - extraAlong * 0.18f;
                            verticalRadius += chamberTaper * 2.4f;
                        }

                        // Every authored entrance terminates in a sizeable hub. The
                        // denser noodle/room networks can intersect this naturally,
                        // while the hub still makes the entrance useful on its own.
                        float junctionAlong = (along - length) / math.max(8f, lowerChamberReach * 0.72f);
                        float junctionAcross = across / math.lerp(8f, 12f, chamberTaper);
                        float junctionVertical = (depthBelowSurface - endDepth) / math.lerp(6.5f, 9f, chamberTaper);
                        if (junctionAlong * junctionAlong +
                            junctionAcross * junctionAcross +
                            junctionVertical * junctionVertical <= 1f)
                        {
                            return true;
                        }

                        if (math.abs(depthBelowSurface - targetDepth) > verticalRadius)
                            continue;

                        return true;
                    }
                }

                return false;
            }

            private int GetSurfaceCaveBreakthroughTargetDepth(int worldX, int worldZ, int groundLevel)
            {
                int surfaceClearance = math.max(1, CaveNoise.SurfaceClearance);
                int probeLimit = math.max(
                    12,
                    CaveNoise.SurfaceBreakthroughProbeDepth > 0
                        ? CaveNoise.SurfaceBreakthroughProbeDepth
                        : 42);

                float mouthThreshold = CaveNoise.SurfaceBreakthroughThreshold > 0f
                    ? math.clamp(CaveNoise.SurfaceBreakthroughThreshold, 0.45f, 0.90f)
                    : 0.60f;
                if (!HasSurfaceCaveBreakthroughMask(worldX, worldZ, mouthThreshold))
                    return -1;

                // Require neighboring support so breakthrough openings form usable
                // clusters instead of isolated one-block fall shafts.
                int supportedNeighbors = 0;
                if (HasSurfaceCaveBreakthroughMask(worldX - 1, worldZ, mouthThreshold)) supportedNeighbors++;
                if (HasSurfaceCaveBreakthroughMask(worldX + 1, worldZ, mouthThreshold)) supportedNeighbors++;
                if (HasSurfaceCaveBreakthroughMask(worldX, worldZ - 1, mouthThreshold)) supportedNeighbors++;
                if (HasSurfaceCaveBreakthroughMask(worldX, worldZ + 1, mouthThreshold)) supportedNeighbors++;
                if (supportedNeighbors < 2)
                    return -1;

                for (int probeDepth = surfaceClearance + 4; probeDepth <= probeLimit; probeDepth += 3)
                {
                    float3 probePosition = new float3(worldX, groundLevel - probeDepth, worldZ);
                    bool touchesCave = WouldCarveRegularCave(probePosition, groundLevel, probeDepth) ||
                                        WouldCarveRegularCave(probePosition + new float3(-1f, 0f, 0f), groundLevel, probeDepth) ||
                                        WouldCarveRegularCave(probePosition + new float3(1f, 0f, 0f), groundLevel, probeDepth) ||
                                        WouldCarveRegularCave(probePosition + new float3(0f, 0f, -1f), groundLevel, probeDepth) ||
                                        WouldCarveRegularCave(probePosition + new float3(0f, 0f, 1f), groundLevel, probeDepth);
                    if (!touchesCave)
                        continue;

                    return probeDepth;
                }

                return -1;
            }

            private static bool HasSurfaceCaveBreakthroughMask(
                int worldX,
                int worldZ,
                float mouthThreshold)
            {
                float mouthNoise = SampleMaterialNoise01(
                    new float3(worldX, 0f, worldZ),
                    42f,
                    new float3(72.4f, 0f, -116.8f));
                if (mouthNoise < mouthThreshold)
                    return false;

                // X/Z-only sampling keeps membership identical at every Y level.
                float shaftMask = SampleMaterialNoise01(
                    new float3(worldX, 0f, worldZ),
                    10f,
                    new float3(-91.2f, 0f, 38.5f));
                return shaftMask >= 0.54f;
            }

            private static bool ShouldCarveSurfaceCaveBreakthrough(
                int depthBelowSurface,
                int targetDepth)
            {
                return targetDepth >= 0 &&
                       depthBelowSurface >= -1 &&
                       depthBelowSurface <= targetDepth + 1;
            }

            private bool WouldCarveRegularCave(float3 worldPosition, int groundLevel, int depthBelowSurface)
            {
                if (depthBelowSurface <= CaveNoise.SurfaceClearance)
                    return false;

                float fadeDistance = math.max(1f, CaveNoise.DepthFadeDistance > 0 ? CaveNoise.DepthFadeDistance : 52);
                float depth01 = math.saturate((depthBelowSurface - CaveNoise.SurfaceClearance) / fadeDistance);

                if (ShouldCarveCheeseCave(worldPosition, depth01))
                    return true;

                if (depthBelowSurface >= GetSetting(CaveNoise.TunnelMinDepth, 4) &&
                    ShouldCarveTunnelCave(worldPosition, depth01))
                {
                    return true;
                }

                if (depthBelowSurface >= GetSetting(CaveNoise.RoomMinDepth, 12) &&
                    ShouldCarveCaveRoom(worldPosition, depth01))
                {
                    return true;
                }

                return depthBelowSurface >= GetSetting(CaveNoise.RavineMinDepth, 6) &&
                       ShouldCarveRavine(worldPosition, groundLevel, depth01);
            }

            private bool ShouldCarveCheeseCave(float3 worldPosition, float depth01)
            {
                float noiseValue = SampleCaveNoise01(worldPosition);
                float threshold = CaveNoise.Threshold > 0f ? CaveNoise.Threshold : 0.6f;
                threshold = math.lerp(0.96f, threshold, depth01);
                threshold = math.saturate(threshold + 0.015f);
                return noiseValue > threshold;
            }

            private bool ShouldCarveTunnelCave(float3 worldPosition, float depth01)
            {
                float width = CaveNoise.TunnelWidth > 0f ? CaveNoise.TunnelWidth : 0.10f;
                width *= math.lerp(0.45f, 1.15f, depth01);
                width *= 0.90f;

                float3 sampleA = GetTunnelSample(worldPosition, new float3(0f, 0f, 0f));
                float3 sampleB = GetTunnelSample(worldPosition, new float3(37.17f, -19.41f, 53.73f));
                float3 sampleC = GetTunnelSample(worldPosition, new float3(-83.4f, 31.7f, 18.9f));
                float3 sampleD = GetTunnelSample(worldPosition, new float3(64.2f, 47.6f, -91.3f));

                float tunnelA = math.max(math.abs(noise.snoise(sampleA)), math.abs(noise.snoise(sampleB)));
                float tunnelB = math.max(math.abs(noise.snoise(sampleC)), math.abs(noise.snoise(sampleD)));
                float tunnelDistance = math.min(tunnelA, tunnelB * 1.08f);

                if (tunnelDistance > width)
                    return false;

                float3 opennessSample = tunnelA <= tunnelB * 1.08f ? sampleA : sampleC;
                float openness = noise.snoise(opennessSample * 0.47f + new float3(11.5f, 43.25f, -7.75f)) * 0.5f + 0.5f;
                return openness > math.lerp(0.52f, 0.36f, depth01);
            }

            private bool ShouldCarveCaveRoom(float3 worldPosition, float depth01)
            {
                float roomValue = SampleRoomNoise01(worldPosition);
                float threshold = CaveNoise.RoomThreshold > 0f ? CaveNoise.RoomThreshold : 0.82f;
                threshold = math.lerp(0.98f, threshold, depth01);
                threshold = math.saturate(threshold + 0.010f);
                return roomValue > threshold;
            }

            private bool ShouldCarveRavine(float3 worldPosition, int groundLevel, float depth01)
            {
                int cellSize = math.max(16, GetSetting(CaveNoise.RavineCellSize, 112));
                int cellX = FastFloorToInt(worldPosition.x / cellSize);
                int cellZ = FastFloorToInt(worldPosition.z / cellSize);

                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                    {
                        if (ShouldCarveRavineCell(worldPosition, groundLevel, depth01, cellX + offsetX, cellZ + offsetZ, cellSize))
                            return true;
                    }
                }

                return false;
            }

            private bool ShouldCarveRavineCell(float3 worldPosition, int groundLevel, float depth01, int cellX, int cellZ, int cellSize)
            {
                float chance = CaveNoise.RavineChance > 0f ? CaveNoise.RavineChance : 0.075f;
                uint baseHash = Hash(cellX, cellZ, 0x4129, Seed);
                if (Hash01(baseHash) > chance)
                    return false;

                float centerX = (cellX + Hash01(Hash(cellX, cellZ, 0x83B1, Seed))) * cellSize;
                float centerZ = (cellZ + Hash01(Hash(cellX, cellZ, 0x56D7, Seed))) * cellSize;
                float angle = Hash01(Hash(cellX, cellZ, 0xC0E5, Seed)) * math.PI * 2f;
                float2 direction = new float2(math.cos(angle), math.sin(angle));
                float2 delta = new float2(worldPosition.x - centerX, worldPosition.z - centerZ);

                float halfLength = cellSize * math.lerp(0.35f, 0.80f, Hash01(Hash(cellX, cellZ, 0x4F7B, Seed)));
                float along = math.dot(delta, direction);
                if (math.abs(along) > halfLength)
                    return false;

                float lengthTaper = 1f - math.saturate(math.abs(along) / halfLength);
                float acrossSigned = delta.x * direction.y - delta.y * direction.x;
                float meanderPhase = Hash01(Hash(cellX, cellZ, 0x91E7, Seed)) * math.PI * 2f;
                float meanderWavelength = cellSize * math.lerp(0.32f, 0.62f, Hash01(Hash(cellX, cellZ, 0x2D59, Seed)));
                float meanderAmplitude = math.max(1.5f, CaveNoise.RavineWidth * 0.55f);
                float centerOffset = math.sin(
                    along / math.max(1f, meanderWavelength) * math.PI * 2f + meanderPhase) *
                    meanderAmplitude;
                float across = math.abs(acrossSigned - centerOffset);

                float ravineDepth = GetSetting(CaveNoise.RavineMinDepth, 6) +
                    cellSize * math.lerp(0.35f, 0.90f, Hash01(Hash(cellX, cellZ, 0xA9D3, Seed)));
                float topY = groundLevel - CaveNoise.SurfaceClearance - 2f;
                float bottomY = topY - ravineDepth;

                if (worldPosition.y > topY || worldPosition.y < bottomY)
                    return false;

                float vertical01 = math.saturate((topY - worldPosition.y) / math.max(1f, ravineDepth));
                float verticalTaper = math.sin(vertical01 * math.PI);
                float baseWidth = CaveNoise.RavineWidth > 0f ? CaveNoise.RavineWidth : 4.5f;
                float width = baseWidth * math.lerp(0.75f, 1.65f, Hash01(Hash(cellX, cellZ, 0x771D, Seed)));
                width *= math.max(0.25f, lengthTaper) * math.max(0.35f, verticalTaper);
                width *= math.lerp(0.75f, 1.15f, depth01);

                return across <= width;
            }

            internal float SampleCaveNoise01(float3 worldPosition)
            {
                float sampleX = worldPosition.x + CaveNoise.Offset.x + CaveNoiseRuntimeOffset.x + NoiseOffset.x;
                float sampleY = worldPosition.y + CaveNoise.Offset.y + CaveNoiseRuntimeOffset.y;
                float sampleZ = worldPosition.z + CaveNoise.Offset.z + CaveNoiseRuntimeOffset.z + NoiseOffset.y;

                float3 sample = new float3(
                    sampleX * CaveHorizontalFrequency,
                    sampleY * CaveVerticalFrequency,
                    sampleZ * CaveHorizontalFrequency
                );

                float noiseValue = noise.snoise(sample);
                return noiseValue * 0.5f + 0.5f;
            }

            private float3 GetTunnelSample(float3 worldPosition, float3 offset)
            {
                return new float3(
                    (worldPosition.x + CaveNoise.Offset.x + CaveNoiseRuntimeOffset.x + NoiseOffset.x + offset.x) * TunnelHorizontalFrequency,
                    (worldPosition.y + CaveNoise.Offset.y + CaveNoiseRuntimeOffset.y + offset.y) * TunnelVerticalFrequency,
                    (worldPosition.z + CaveNoise.Offset.z + CaveNoiseRuntimeOffset.z + NoiseOffset.y + offset.z) * TunnelHorizontalFrequency
                );
            }

            private float SampleRoomNoise01(float3 worldPosition)
            {
                float3 sample = new float3(
                    (worldPosition.x + CaveNoise.Offset.x + CaveNoiseRuntimeOffset.x + NoiseOffset.x + 91.7f) * RoomFrequency,
                    (worldPosition.y + CaveNoise.Offset.y + CaveNoiseRuntimeOffset.y - 27.3f) * RoomFrequency,
                    (worldPosition.z + CaveNoise.Offset.z + CaveNoiseRuntimeOffset.z + NoiseOffset.y + 38.2f) * RoomFrequency
                );

                float roomValue = noise.snoise(sample);
                return roomValue * 0.5f + 0.5f;
            }

            private static int GetSetting(int value, int fallback)
            {
                return value > 0 ? value : fallback;
            }

            private static int FastFloorToInt(float value)
            {
                int integer = (int)value;
                return value < integer ? integer - 1 : integer;
            }

            private static uint Hash(int x, int z, int salt, int seed)
            {
                unchecked
                {
                    uint h = (uint)seed;
                    h ^= (uint)x * 0x9E3779B9u;
                    h ^= (uint)z * 0x85EBCA6Bu;
                    h ^= (uint)salt * 0xC2B2AE35u;
                    h ^= h >> 16;
                    h *= 0x7FEB352Du;
                    h ^= h >> 15;
                    h *= 0x846CA68Bu;
                    h ^= h >> 16;
                    return h;
                }
            }

            private static uint Hash(int x, int y, int z, int salt, int seed)
            {
                unchecked
                {
                    uint h = (uint)seed;
                    h ^= (uint)x * 0x9E3779B9u;
                    h ^= (uint)y * 0x7F4A7C15u;
                    h ^= (uint)z * 0x85EBCA6Bu;
                    h ^= (uint)salt * 0xC2B2AE35u;
                    h ^= h >> 16;
                    h *= 0x7FEB352Du;
                    h ^= h >> 15;
                    h *= 0x846CA68Bu;
                    h ^= h >> 16;
                    return h;
                }
            }

            private static float Hash01(uint hash)
            {
                return (hash & 0x00FFFFFFu) / 16777215f;
            }
        }
    }
}
