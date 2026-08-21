using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public static class FluidSimulator
    {
        private const int MinecraftWaterMaxDepth = 7;
        private const int MinecraftLavaOverworldMaxDepth = 3;
        private const int MinecraftWaterSlopeFindDistance = 4;
        private const int MinecraftLavaOverworldSlopeFindDistance = 2;
        private const int MaxFluidOperationsPerTickLimit = 4096;
        private const float LavaOverworldSpeedMultiplier = 6f;
        private const float LavaFullerStateDelayMultiplier = 4f;
        private const uint InitialFluidRandomState = 0x6D2B79F5u;
        private const int FluidSimulationBatchSize = 16;
        private const int FluidJobScheduleThreshold = 8;
        private const int MaxChunkDiscoveryScansPerUpdate = 1;
        private const int MaxChunkDiscoveryEntriesInspectedPerUpdate = 16;
        private const int FluidSampleRadius = 1;
        private const int FluidSamplesPerNode =
            (FluidSampleRadius * 2 + 1) * (FluidSampleRadius * 2 + 1) * 3 + 5;
        private const int ChunkCoordinateShift = 5;
        private const int FluidJobActionNone = 0;
        private const int FluidJobActionRemoveRuntime = 1;
        private const int FluidJobActionRetry = 2;
        private const int FluidJobActionFallback = 3;
        private const int FluidJobActionClearSelf = 4;
        private const int FluidJobActionSolidifySelf = 5;
        private const int FluidJobActionSolidifyTarget = 6;
        private const int FluidJobActionFlowDown = 7;
        private const int FluidJobActionFlowHorizontal = 8;
        private const int FluidBoundaryBlockId = -1;

        private static readonly Vector3Int[] HorizontalDirections =
        {
            Vector3Int.forward,
            Vector3Int.back,
            Vector3Int.left,
            Vector3Int.right
        };

        private static readonly Vector3Int[] NeighborDirections =
        {
            Vector3Int.up,
            Vector3Int.down,
            Vector3Int.forward,
            Vector3Int.back,
            Vector3Int.left,
            Vector3Int.right
        };

        // Java Edition checks water above and beside lava, but not water below it.
        private static readonly Vector3Int[] LavaSolidifyingWaterDirections =
        {
            Vector3Int.up,
            Vector3Int.forward,
            Vector3Int.back,
            Vector3Int.left,
            Vector3Int.right
        };

        // The inverse lookup used when a water node wakes: lava may be below or
        // beside the water, but lava directly above water does not solidify.
        private static readonly Vector3Int[] WaterSolidifyingLavaDirections =
        {
            Vector3Int.down,
            Vector3Int.forward,
            Vector3Int.back,
            Vector3Int.left,
            Vector3Int.right
        };

        private static readonly FluidNodeHeap priorityFluids = new(128);
        private static readonly FluidNodeHeap pendingFluids = new(128);
        private static readonly Dictionary<Vector3Int, PendingFluidState> pendingFluidStates = new();
        private static readonly Dictionary<Vector3Int, FluidRuntimeState> fluidRuntimeStates = new();
        private static readonly Queue<Chunk> pendingChunkDiscovery = new();
        private static readonly HashSet<Chunk> pendingChunkDiscoverySet = new();
        private static readonly Dictionary<Chunk, int> discoveredChunkRevisions = new();
        private static readonly HashSet<Chunk> dirtyChunks = new();
        private static readonly List<Vector3Int> spreadDirections = new(4);
        private static readonly HashSet<Vector3Int> dropSearchVisited = new();
        private static readonly Queue<DropSearchNode> dropSearchQueue = new();
        private static readonly Dictionary<Vector3Int, CachedBlockState> blockStateCache = new();
        private static readonly HashSet<Vector3Int> fluidJobMutatedPositions = new();
        private static HashSet<Vector3Int> activeSimulationChunks = new();
        private static HashSet<Vector3Int> nextSimulationChunks = new();
        private static readonly List<Vector3Int> staleFluidPositions = new();
        private static readonly List<FluidNode> readyFluidNodes = new(MaxFluidOperationsPerTickLimit);
        private static NativeArray<FluidJobNode> fluidJobNodes;
        private static NativeArray<FluidJobResult> fluidJobResults;
        private static NativeParallelHashMap<long, FluidJobBlockSample> fluidJobBlockSamples;

        private static bool isEnabled = true;
        private static float waterSpreadInterval = 0.25f;
        private static float tickTimer;
        private static float simulationTime;
        private static int maxFluidOperationsPerTick = 128;
        private static int maxWaterDepth = MinecraftWaterMaxDepth;
        private static bool isProcessingTick;
        private static bool isApplyingFluidJobResults;
        private static bool simulationAreaConfigured;
        private static Vector3Int simulationCenterChunk;
        private static int simulationChunkRange = 5;
        private static int simulationChunkRangeSq = 25;
        private static int simulationVerticalChunkRange = 2;
        private static long fluidNodeSequence;
        private static uint fluidRandomState = InitialFluidRandomState;
        private static BlockData[] blockDefinitions;
        private static bool[] fluidBlockFlags;

        public static void Configure(
            bool simulateFluids,
            float waterTickInterval,
            int waterBlocksPerTick,
            int waterSpreadDistance,
            Vector3Int centerChunk,
            int chunkSimulationRange,
            int verticalChunkSimulationRange)
        {
            AssetsContainer assets = AssetsContainer.Instance;
            BlockData[] configuredBlocks = assets != null ? assets.Blocks : null;
            if (!ReferenceEquals(blockDefinitions, configuredBlocks) || fluidBlockFlags == null)
                CacheBlockDefinitions(configuredBlocks);

            int sanitizedRange = Mathf.Max(0, chunkSimulationRange);
            int sanitizedVerticalRange = Mathf.Max(0, verticalChunkSimulationRange);
            float sanitizedSpreadInterval = Mathf.Max(0.05f, waterTickInterval);
            int sanitizedOperations = Mathf.Clamp(waterBlocksPerTick, 1, MaxFluidOperationsPerTickLimit);
            int sanitizedWaterDepth = waterSpreadDistance <= 0
                ? MinecraftWaterMaxDepth
                : Mathf.Clamp(waterSpreadDistance, 1, MinecraftWaterMaxDepth);

            if (simulationAreaConfigured &&
                isEnabled == simulateFluids &&
                waterSpreadInterval == sanitizedSpreadInterval &&
                maxFluidOperationsPerTick == sanitizedOperations &&
                maxWaterDepth == sanitizedWaterDepth &&
                simulationCenterChunk == centerChunk &&
                simulationChunkRange == sanitizedRange &&
                simulationVerticalChunkRange == sanitizedVerticalRange)
            {
                return;
            }

            bool wasEnabled = isEnabled;
            bool areaChanged =
                !simulationAreaConfigured ||
                simulationCenterChunk != centerChunk ||
                simulationChunkRange != sanitizedRange ||
                simulationVerticalChunkRange != sanitizedVerticalRange;

            isEnabled = simulateFluids;
            waterSpreadInterval = sanitizedSpreadInterval;
            maxFluidOperationsPerTick = sanitizedOperations;
            maxWaterDepth = sanitizedWaterDepth;

            simulationAreaConfigured = true;
            simulationCenterChunk = centerChunk;
            simulationChunkRange = sanitizedRange;
            simulationChunkRangeSq = simulationChunkRange * simulationChunkRange;
            simulationVerticalChunkRange = sanitizedVerticalRange;

            if (!isEnabled)
            {
                activeSimulationChunks.Clear();
                nextSimulationChunks.Clear();
                pendingChunkDiscovery.Clear();
                pendingChunkDiscoverySet.Clear();
                discoveredChunkRevisions.Clear();
                return;
            }

            if (areaChanged || !wasEnabled)
            {
                RefreshSimulationArea();
                PruneFluidWorkOutsideSimulationRange();
            }
        }

        public static void Clear()
        {
            priorityFluids.Clear();
            pendingFluids.Clear();
            pendingFluidStates.Clear();
            fluidRuntimeStates.Clear();
            pendingChunkDiscovery.Clear();
            pendingChunkDiscoverySet.Clear();
            discoveredChunkRevisions.Clear();
            dirtyChunks.Clear();
            dropSearchVisited.Clear();
            dropSearchQueue.Clear();
            blockStateCache.Clear();
            fluidJobMutatedPositions.Clear();
            activeSimulationChunks.Clear();
            nextSimulationChunks.Clear();
            staleFluidPositions.Clear();
            DisposeFluidJobBuffers();
            tickTimer = 0f;
            simulationTime = 0f;
            isProcessingTick = false;
            isApplyingFluidJobResults = false;
            simulationAreaConfigured = false;
            fluidNodeSequence = 0L;
            fluidRandomState = InitialFluidRandomState;
            blockDefinitions = null;
            fluidBlockFlags = null;
        }

        /// <summary>
        /// Drops simulator state owned by a chunk after its persistent state has been
        /// staged by SaveController. This keeps the static fluid caches bounded by the
        /// terrain residency window rather than by total explored world size.
        /// </summary>
        public static void ReleaseChunk(Chunk chunk)
        {
            if (chunk == null)
                return;

            Vector3Int coordinate = chunk.Coordinate;
            pendingChunkDiscoverySet.Remove(chunk);
            discoveredChunkRevisions.Remove(chunk);
            dirtyChunks.Remove(chunk);
            activeSimulationChunks.Remove(coordinate);
            nextSimulationChunks.Remove(coordinate);

            RemoveChunkPositions(fluidRuntimeStates, coordinate);
            RemoveChunkPositions(pendingFluidStates, coordinate);
            RemoveChunkPositions(blockStateCache, coordinate);

            staleFluidPositions.Clear();
            foreach (Vector3Int position in fluidJobMutatedPositions)
            {
                if (GetChunkCoordinateFromBlockPosition(position) == coordinate)
                    staleFluidPositions.Add(position);
            }

            for (int i = 0; i < staleFluidPositions.Count; i++)
                fluidJobMutatedPositions.Remove(staleFluidPositions[i]);

            staleFluidPositions.Clear();
        }

        private static void RemoveChunkPositions<TValue>(
            Dictionary<Vector3Int, TValue> states,
            Vector3Int chunkCoordinate)
        {
            if (states.Count == 0)
                return;

            staleFluidPositions.Clear();
            foreach (KeyValuePair<Vector3Int, TValue> entry in states)
            {
                if (GetChunkCoordinateFromBlockPosition(entry.Key) == chunkCoordinate)
                    staleFluidPositions.Add(entry.Key);
            }

            for (int i = 0; i < staleFluidPositions.Count; i++)
                states.Remove(staleFluidPositions[i]);

            staleFluidPositions.Clear();
        }

        public static void Update(float deltaTime)
        {
            if (!isEnabled || !simulationAreaConfigured)
                return;

            ProcessPendingChunkDiscovery();
            simulationTime += deltaTime;
            tickTimer += deltaTime;
            if (tickTimer < waterSpreadInterval)
                return;

            // Process at most one batch per frame, but keep residual time so a slow
            // frame does not permanently stretch every later fluid tick.
            tickTimer -= waterSpreadInterval;
            ProcessFluids();
            GenerateDirtyChunks();
        }

        public static void NotifyBlockChanged(Vector3Int worldPosition, int oldBlockId, int newBlockId)
        {
            if (oldBlockId == newBlockId)
                return;

            if (!isEnabled)
            {
                if (IsSupportedFluid(oldBlockId))
                {
                    fluidRuntimeStates.Remove(worldPosition);
                    pendingFluidStates.Remove(worldPosition);
                }

                return;
            }

            if (!IsWorldPositionInSimulationRange(worldPosition))
            {
                if (IsSupportedFluid(oldBlockId))
                {
                    fluidRuntimeStates.Remove(worldPosition);
                    pendingFluidStates.Remove(worldPosition);
                }

                return;
            }

            if (IsSupportedFluid(newBlockId))
            {
                QueueFluidSource(worldPosition, newBlockId, priority: true);
                QueueNeighborFluids(worldPosition, priority: true);
                return;
            }

            if (IsSupportedFluid(oldBlockId))
            {
                fluidRuntimeStates.Remove(worldPosition);
                pendingFluidStates.Remove(worldPosition);
                QueueNeighborFluids(worldPosition, priority: true);
                return;
            }

            QueueNeighborFluids(worldPosition, priority: true);
        }

        public static void QueueWaterSource(Vector3Int worldPosition)
        {
            if (!isEnabled)
                return;

            if (!IsWorldPositionInSimulationRange(worldPosition))
                return;

            QueueFluidSource(worldPosition, Chunk.BLOCK_WATER, priority: true);
        }

        public static void QueueLavaSource(Vector3Int worldPosition)
        {
            if (!isEnabled)
                return;

            if (!IsWorldPositionInSimulationRange(worldPosition))
                return;

            QueueFluidSource(worldPosition, Chunk.BLOCK_LAVA, priority: true);
        }

        public static void QueueChunkFluids(Chunk chunk)
        {
            if (!isEnabled)
                return;

            if (chunk?.Blocks == null || !chunk.IsGenerated)
                return;

            if (!IsChunkInSimulationRange(chunk.Coordinate))
                return;

            if (discoveredChunkRevisions.TryGetValue(chunk, out int discoveredRevision) &&
                discoveredRevision == chunk.BlockRevision)
            {
                return;
            }

            if (pendingChunkDiscoverySet.Add(chunk))
                pendingChunkDiscovery.Enqueue(chunk);
        }

        private static void ProcessPendingChunkDiscovery()
        {
            int processed = 0;
            int inspected = 0;
            while (processed < MaxChunkDiscoveryScansPerUpdate &&
                   inspected < MaxChunkDiscoveryEntriesInspectedPerUpdate &&
                   pendingChunkDiscovery.Count > 0)
            {
                Chunk chunk = pendingChunkDiscovery.Dequeue();
                inspected++;
                pendingChunkDiscoverySet.Remove(chunk);

                if (chunk?.Blocks == null ||
                    !chunk.IsGenerated ||
                    !IsChunkInSimulationRange(chunk.Coordinate))
                {
                    continue;
                }

                if (discoveredChunkRevisions.TryGetValue(chunk, out int discoveredRevision) &&
                    discoveredRevision == chunk.BlockRevision)
                {
                    continue;
                }

                if (!chunk.IsAirOnly)
                    QueueChunkFluidFrontier(chunk);
                QueueNeighborChunkBorderFluidFrontier(chunk);
                discoveredChunkRevisions[chunk] = chunk.BlockRevision;
                processed++;
            }
        }

        public static bool TryGetFluidStateData(
            Vector3Int worldPosition,
            int blockId,
            out int depth,
            out bool isSource,
            out bool isFalling)
        {
            depth = 0;
            isSource = true;
            isFalling = false;

            if (!IsSupportedFluid(blockId))
                return false;

            if (fluidRuntimeStates.TryGetValue(worldPosition, out FluidRuntimeState state) &&
                state.BlockId == blockId)
            {
                depth = state.Depth;
                isSource = state.IsSource;
                isFalling = state.IsFalling;
                return true;
            }

            return false;
        }

        public static void RestoreFluidStateData(
            Vector3Int worldPosition,
            int blockId,
            int depth,
            bool isSource,
            bool isFalling)
        {
            if (!IsSupportedFluid(blockId))
                return;

            FluidRuntimeState state = new FluidRuntimeState(
                blockId,
                Mathf.Clamp(depth, 0, GetMaxDepth(blockId)),
                isSource,
                isFalling && !isSource);

            SetFluidRuntimeState(worldPosition, state);
            if (isEnabled && IsWorldPositionInSimulationRange(worldPosition))
                TryEnqueueFluidInRange(worldPosition, blockId, GetFluidSpreadInterval(blockId), priority: true);
        }

        private static void QueueFluidSource(Vector3Int worldPosition, int fluidBlockId)
        {
            QueueFluidSource(worldPosition, fluidBlockId, priority: false);
        }

        private static void QueueFluidSource(Vector3Int worldPosition, int fluidBlockId, bool priority)
        {
            if (!IsSupportedFluid(fluidBlockId))
                return;

            if (!IsWorldPositionInSimulationRange(worldPosition))
                return;

            SetFluidRuntimeState(worldPosition, FluidRuntimeState.Source(fluidBlockId));

            if (priority && TryGetBlockState(worldPosition, out _, out Chunk chunk, out Vector3Int localPosition))
                MarkDirty(chunk, localPosition);

            TryEnqueueFluidInRange(worldPosition, fluidBlockId, GetFluidSpreadInterval(fluidBlockId), priority);
        }

        private static void QueueNeighborFluids(Vector3Int worldPosition, bool priority = false)
        {
            Vector3Int sourceChunkCoordinate = GetChunkCoordinateFromBlockPosition(worldPosition);
            Vector3Int sourceLocalPosition = GetLocalPosition(worldPosition, sourceChunkCoordinate);

            for (int i = 0; i < NeighborDirections.Length; i++)
            {
                Vector3Int direction = NeighborDirections[i];
                if (CrossesChunkBoundary(sourceLocalPosition, direction) &&
                    !IsChunkInSimulationRange(sourceChunkCoordinate + direction))
                {
                    continue;
                }

                Vector3Int neighborPosition = worldPosition + direction;
                if (!TryGetBlockId(neighborPosition, out int blockId) ||
                    !IsSupportedFluid(blockId))
                {
                    continue;
                }

                TryEnqueueFluidInRange(neighborPosition, blockId, GetFluidSpreadInterval(blockId), priority);
            }
        }

        private static bool CrossesChunkBoundary(Vector3Int localPosition, Vector3Int direction)
        {
            return direction.x < 0 && localPosition.x == 0 ||
                   direction.x > 0 && localPosition.x == Chunk.CHUNK_SIZE - 1 ||
                   direction.y < 0 && localPosition.y == 0 ||
                   direction.y > 0 && localPosition.y == Chunk.CHUNK_HEIGHT - 1 ||
                   direction.z < 0 && localPosition.z == 0 ||
                   direction.z > 0 && localPosition.z == Chunk.CHUNK_SIZE - 1;
        }

        private static void QueueChunkFluidFrontier(Chunk chunk)
        {
            if (chunk.TryGetGeneratedFluidFrontierMasks(out uint[] generatedMasks))
            {
                QueueGeneratedFluidFrontier(chunk, generatedMasks);
                return;
            }

            Vector3Int origin = new Vector3Int(
                chunk.Coordinate.x * Chunk.CHUNK_SIZE,
                chunk.Coordinate.y * Chunk.CHUNK_HEIGHT,
                chunk.Coordinate.z * Chunk.CHUNK_SIZE);
            byte[] blocks = chunk.Blocks.Data;
            int sliceStride = Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;

            for (int x = 0; x < Chunk.CHUNK_SIZE; x++)
            {
                for (int y = 0; y < Chunk.CHUNK_HEIGHT; y++)
                {
                    int blockIndex = x + y * Chunk.CHUNK_SIZE;
                    for (int z = 0; z < Chunk.CHUNK_SIZE; z++)
                    {
                        int blockId = blocks[blockIndex];
                        blockIndex += sliceStride;
                        if (!IsSupportedFluid(blockId))
                            continue;

                        Vector3Int worldPosition = new Vector3Int(
                            origin.x + x,
                            origin.y + y,
                            origin.z + z);
                        QueueFluidIfCanMove(chunk, x, y, z, worldPosition, blockId);
                    }
                }
            }
        }

        private static void QueueGeneratedFluidFrontier(Chunk chunk, uint[] frontierMasks)
        {
            if (frontierMasks == null)
                return;

            Vector3Int origin = new Vector3Int(
                chunk.Coordinate.x * Chunk.CHUNK_SIZE,
                chunk.Coordinate.y * Chunk.CHUNK_HEIGHT,
                chunk.Coordinate.z * Chunk.CHUNK_SIZE);
            byte[] blocks = chunk.Blocks.Data;
            int sliceStride = Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;

            for (int columnIndex = 0; columnIndex < frontierMasks.Length; columnIndex++)
            {
                uint mask = frontierMasks[columnIndex];
                if (mask == 0u)
                    continue;

                int x = columnIndex % Chunk.CHUNK_SIZE;
                int z = columnIndex / Chunk.CHUNK_SIZE;
                int columnStart = z * sliceStride + x;

                for (int y = 0; y < Chunk.CHUNK_HEIGHT; y++)
                {
                    if ((mask & (1u << y)) == 0u)
                        continue;

                    int blockId = blocks[columnStart + y * Chunk.CHUNK_SIZE];
                    if (!IsSupportedFluid(blockId))
                        continue;

                    Vector3Int worldPosition = new Vector3Int(
                        origin.x + x,
                        origin.y + y,
                        origin.z + z);
                    QueueFluidIfCanMove(chunk, x, y, z, worldPosition, blockId);
                }
            }
        }

        private static void QueueNeighborChunkBorderFluidFrontier(Chunk chunk)
        {
            QueueChunkBorderFluidFrontier(
                ChunkUtility.GetChunkAtCoordinate(chunk.Coordinate + Vector3Int.left),
                Vector3Int.right);
            QueueChunkBorderFluidFrontier(
                ChunkUtility.GetChunkAtCoordinate(chunk.Coordinate + Vector3Int.right),
                Vector3Int.left);
            QueueChunkBorderFluidFrontier(
                ChunkUtility.GetChunkAtCoordinate(chunk.Coordinate + Vector3Int.down),
                Vector3Int.up);
            QueueChunkBorderFluidFrontier(
                ChunkUtility.GetChunkAtCoordinate(chunk.Coordinate + Vector3Int.up),
                Vector3Int.down);
            QueueChunkBorderFluidFrontier(
                ChunkUtility.GetChunkAtCoordinate(chunk.Coordinate + Vector3Int.back),
                Vector3Int.forward);
            QueueChunkBorderFluidFrontier(
                ChunkUtility.GetChunkAtCoordinate(chunk.Coordinate + Vector3Int.forward),
                Vector3Int.back);
        }

        private static void QueueChunkBorderFluidFrontier(Chunk chunk, Vector3Int faceDirection)
        {
            if (chunk?.Blocks == null || !chunk.IsGenerated)
                return;

            if (!IsChunkInSimulationRange(chunk.Coordinate))
                return;

            Vector3Int origin = new Vector3Int(
                chunk.Coordinate.x * Chunk.CHUNK_SIZE,
                chunk.Coordinate.y * Chunk.CHUNK_HEIGHT,
                chunk.Coordinate.z * Chunk.CHUNK_SIZE);
            byte[] blocks = chunk.Blocks.Data;
            int sliceStride = Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;

            if (faceDirection.x != 0)
            {
                int x = faceDirection.x > 0 ? Chunk.CHUNK_SIZE - 1 : 0;
                for (int y = 0; y < Chunk.CHUNK_HEIGHT; y++)
                {
                    int blockIndex = x + y * Chunk.CHUNK_SIZE;
                    for (int z = 0; z < Chunk.CHUNK_SIZE; z++)
                    {
                        int blockId = blocks[blockIndex];
                        blockIndex += sliceStride;
                        if (IsSupportedFluid(blockId))
                        {
                            QueueFluidIfCanMove(
                                chunk,
                                x,
                                y,
                                z,
                                new Vector3Int(origin.x + x, origin.y + y, origin.z + z),
                                blockId);
                        }
                    }
                }

                return;
            }

            if (faceDirection.y != 0)
            {
                int y = faceDirection.y > 0 ? Chunk.CHUNK_HEIGHT - 1 : 0;
                for (int x = 0; x < Chunk.CHUNK_SIZE; x++)
                {
                    int blockIndex = x + y * Chunk.CHUNK_SIZE;
                    for (int z = 0; z < Chunk.CHUNK_SIZE; z++)
                    {
                        int blockId = blocks[blockIndex];
                        blockIndex += sliceStride;
                        if (IsSupportedFluid(blockId))
                        {
                            QueueFluidIfCanMove(
                                chunk,
                                x,
                                y,
                                z,
                                new Vector3Int(origin.x + x, origin.y + y, origin.z + z),
                                blockId);
                        }
                    }
                }

                return;
            }

            int faceZ = faceDirection.z > 0 ? Chunk.CHUNK_SIZE - 1 : 0;
            for (int x = 0; x < Chunk.CHUNK_SIZE; x++)
            {
                int blockIndex = x + faceZ * sliceStride;
                for (int y = 0; y < Chunk.CHUNK_HEIGHT; y++)
                {
                    int blockId = blocks[blockIndex];
                    blockIndex += Chunk.CHUNK_SIZE;
                    if (IsSupportedFluid(blockId))
                    {
                        QueueFluidIfCanMove(
                            chunk,
                            x,
                            y,
                            faceZ,
                            new Vector3Int(origin.x + x, origin.y + y, origin.z + faceZ),
                            blockId);
                    }
                }
            }
        }

        private static void QueueFluidIfCanMove(
            Chunk chunk,
            int localX,
            int localY,
            int localZ,
            Vector3Int worldPosition,
            int fluidBlockId)
        {
            if (!CanLoadedFluidMoveFrom(
                    chunk,
                    localX,
                    localY,
                    localZ,
                    worldPosition,
                    fluidBlockId))
            {
                return;
            }

            TryEnqueueFluidInRange(worldPosition, fluidBlockId, GetFluidSpreadInterval(fluidBlockId));
        }

        private static bool CanLoadedFluidMoveFrom(
            Chunk chunk,
            int localX,
            int localY,
            int localZ,
            Vector3Int worldPosition,
            int fluidBlockId)
        {
            if (fluidRuntimeStates.TryGetValue(worldPosition, out FluidRuntimeState state) &&
                state.BlockId == fluidBlockId &&
                !state.IsSource)
            {
                return CanFluidMoveFrom(worldPosition, fluidBlockId);
            }

            byte[] blocks = chunk.Blocks.Data;
            int sliceStride = Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;
            int blockIndex = localX +
                             localY * Chunk.CHUNK_SIZE +
                             localZ * sliceStride;

            if (localY > 0)
            {
                if (IsDirectFluidTarget(fluidBlockId, blocks[blockIndex - Chunk.CHUNK_SIZE]))
                    return true;
            }
            else if (IsExternalFluidTarget(worldPosition, Vector3Int.down, fluidBlockId))
            {
                return true;
            }

            if (localX > 0 && IsDirectFluidTarget(fluidBlockId, blocks[blockIndex - 1]))
                return true;
            if (localX < Chunk.CHUNK_SIZE - 1 && IsDirectFluidTarget(fluidBlockId, blocks[blockIndex + 1]))
                return true;
            if (localZ > 0 && IsDirectFluidTarget(fluidBlockId, blocks[blockIndex - sliceStride]))
                return true;
            if (localZ < Chunk.CHUNK_SIZE - 1 && IsDirectFluidTarget(fluidBlockId, blocks[blockIndex + sliceStride]))
                return true;

            if (localX == 0 && IsExternalFluidTarget(worldPosition, Vector3Int.left, fluidBlockId))
                return true;
            if (localX == Chunk.CHUNK_SIZE - 1 && IsExternalFluidTarget(worldPosition, Vector3Int.right, fluidBlockId))
                return true;
            if (localZ == 0 && IsExternalFluidTarget(worldPosition, Vector3Int.back, fluidBlockId))
                return true;

            return localZ == Chunk.CHUNK_SIZE - 1 &&
                   IsExternalFluidTarget(worldPosition, Vector3Int.forward, fluidBlockId);
        }

        private static bool IsExternalFluidTarget(
            Vector3Int worldPosition,
            Vector3Int direction,
            int fluidBlockId)
        {
            return TryGetSimulationBlockId(worldPosition + direction, out int targetBlockId) &&
                   IsDirectFluidTarget(fluidBlockId, targetBlockId);
        }

        private static bool IsDirectFluidTarget(int fluidBlockId, int targetBlockId)
        {
            return targetBlockId == Chunk.BLOCK_AIR ||
                   CanSolidifyFluidCollision(fluidBlockId, targetBlockId);
        }

        private static bool CanFluidMoveFrom(Vector3Int worldPosition, int fluidBlockId)
        {
            FluidRuntimeState state = EnsureFluidState(worldPosition, fluidBlockId);

            if (!state.IsSource && !CanFlowingBlockRemain(worldPosition, fluidBlockId, state, out _))
                return true;

            if (!state.IsSource && ShouldBecomeSource(worldPosition, fluidBlockId))
                return true;

            Vector3Int belowPosition = worldPosition + Vector3Int.down;
            if (CanFlowInto(belowPosition, fluidBlockId, 0, false, true, out _))
                return true;

            int nextDepth = state.Depth + 1;
            if (nextDepth > GetMaxDepth(fluidBlockId))
                return false;

            for (int i = 0; i < HorizontalDirections.Length; i++)
            {
                Vector3Int targetPosition = worldPosition + HorizontalDirections[i];
                if (CanFlowInto(
                        targetPosition,
                        fluidBlockId,
                        nextDepth,
                        false,
                        false,
                        out _))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ProcessFluids()
        {
            isProcessingTick = true;

            try
            {
                int operations = 0;
                int evaluatedNodes = 0;

                operations = ProcessFluidQueue(
                    priorityFluids,
                    operations,
                    ref evaluatedNodes,
                    maxFluidOperationsPerTick);

                if (operations >= maxFluidOperationsPerTick ||
                    evaluatedNodes >= maxFluidOperationsPerTick)
                    return;

                ProcessFluidQueue(
                    pendingFluids,
                    operations,
                    ref evaluatedNodes,
                    maxFluidOperationsPerTick);
            }
            finally
            {
                isProcessingTick = false;
                blockStateCache.Clear();
            }
        }

        private static int ProcessFluidQueue(
            FluidNodeHeap fluidQueue,
            int operations,
            ref int evaluatedNodes,
            int operationBudget)
        {
            readyFluidNodes.Clear();
            int inspectedEntries = 0;
            int inspectionBudget = Mathf.Max(64, operationBudget * 4);

            while (fluidQueue.Count > 0 &&
                   operations + readyFluidNodes.Count < operationBudget &&
                   evaluatedNodes + readyFluidNodes.Count < operationBudget &&
                   inspectedEntries < inspectionBudget)
            {
                FluidNode nextNode = fluidQueue.Peek();
                if (nextNode.ReadyTime > simulationTime)
                    break;

                FluidNode node = fluidQueue.Dequeue();
                inspectedEntries++;
                if (!pendingFluidStates.TryGetValue(node.Position, out PendingFluidState pendingState) ||
                    pendingState.BlockId != node.BlockId ||
                    !Mathf.Approximately(pendingState.ReadyTime, node.ReadyTime))
                {
                    continue;
                }

                if (!IsWorldPositionInSimulationRange(node.Position))
                {
                    pendingFluidStates.Remove(node.Position);
                    continue;
                }

                pendingFluidStates.Remove(node.Position);
                readyFluidNodes.Add(node);
            }

            if (readyFluidNodes.Count == 0)
                return operations;

            evaluatedNodes += readyFluidNodes.Count;
            return operations + ProcessReadyFluidNodesJobified(readyFluidNodes, operationBudget - operations);
        }

        private static int ProcessReadyFluidNodesJobified(List<FluidNode> nodes, int operationBudget)
        {
            if (nodes.Count == 0 || operationBudget <= 0)
                return 0;

            int count = Mathf.Min(nodes.Count, operationBudget);
            if (fluidJobBlockSamples.IsCreated)
                fluidJobBlockSamples.Clear();

            EnsureFluidJobBuffers(count);

            bool hasBuiltinFluid = false;
            for (int i = 0; i < count; i++)
            {
                FluidNode node = nodes[i];
                fluidJobNodes[i] = new FluidJobNode
                {
                    Position = ToInt3(node.Position),
                    BlockId = node.BlockId
                };

                if (IsBuiltinFluid(node.BlockId))
                {
                    hasBuiltinFluid = true;
                    AddFluidJobSamples(node.Position, fluidJobBlockSamples);
                }
            }

            if (hasBuiltinFluid)
            {
                EvaluateFluidNodesJob evaluationJob = new EvaluateFluidNodesJob
                {
                    Nodes = fluidJobNodes,
                    BlockSamples = fluidJobBlockSamples,
                    MaxWaterDepth = maxWaterDepth,
                    SimulationCenterChunk = ToInt3(simulationCenterChunk),
                    SimulationChunkRangeSq = simulationChunkRangeSq,
                    SimulationVerticalChunkRange = simulationVerticalChunkRange,
                    Results = fluidJobResults
                };

                if (count < FluidJobScheduleThreshold)
                {
                    // Keep the exact snapshot evaluator for small batches, but avoid the
                    // worker scheduling/fence overhead that dominates a handful of nodes.
                    for (int i = 0; i < count; i++)
                        evaluationJob.Execute(i);
                }
                else
                {
                    JobHandle handle = evaluationJob.Schedule(count, FluidSimulationBatchSize);
                    handle.Complete();
                }
            }

            int operations = 0;
            int appliedCount = 0;
            fluidJobMutatedPositions.Clear();
            isApplyingFluidJobResults = true;
            try
            {
                for (int i = 0; i < count && operations < operationBudget; i++)
                {
                    if (!IsBuiltinFluid(nodes[i].BlockId))
                    {
                        operations += ProcessFluidNode(
                            nodes[i].Position,
                            nodes[i].BlockId,
                            operationBudget - operations);
                    }
                    else
                    {
                        operations += ApplyFluidJobResult(
                            nodes[i],
                            fluidJobResults[i],
                            operationBudget - operations);
                    }

                    appliedCount = i + 1;
                }
            }
            finally
            {
                isApplyingFluidJobResults = false;
                fluidJobMutatedPositions.Clear();
            }

            for (int i = appliedCount; i < nodes.Count; i++)
                TryEnqueueFluidInRange(nodes[i].Position, nodes[i].BlockId, 0f, priority: true);

            return operations;
        }

        private static int ApplyFluidJobResult(FluidNode node, FluidJobResult result, int remainingOperations)
        {
            if (remainingOperations <= 0)
            {
                TryEnqueueFluidInRange(node.Position, node.BlockId, 0f, priority: true);
                return 0;
            }

            Vector3Int worldPosition = node.Position;
            int operations = 0;

            if (HasFluidJobDependencyMutation(worldPosition))
                return ProcessFluidNode(worldPosition, node.BlockId, remainingOperations);

            if (result.Action == FluidJobActionFallback)
                return ProcessFluidNode(worldPosition, node.BlockId, remainingOperations);

            if (result.Action == FluidJobActionRemoveRuntime)
            {
                if (TryGetBlockId(worldPosition, out int currentBlockId) && currentBlockId == node.BlockId)
                    return ProcessFluidNode(worldPosition, node.BlockId, remainingOperations);

                RemoveFluidRuntimeState(worldPosition, node.BlockId);
                return 0;
            }

            if (result.HasExpectedState != 0 && !FluidJobSnapshotStillValid(worldPosition, node.BlockId, result))
                return ProcessFluidNode(worldPosition, node.BlockId, remainingOperations);

            FluidRuntimeState previousState = EnsureFluidState(worldPosition, node.BlockId);
            float rescheduleDelay = GetFluidSpreadInterval(node.BlockId);
            if (result.HasStateUpdate != 0)
            {
                FluidRuntimeState updatedState = new FluidRuntimeState(
                    node.BlockId,
                    Mathf.Clamp(result.StateDepth, 0, GetMaxDepth(node.BlockId)),
                    result.StateIsSource != 0,
                    result.StateIsFalling != 0 && result.StateIsSource == 0);

                SetFluidRuntimeState(worldPosition, updatedState);
                if (ShouldUseExtendedLavaDelay(previousState, updatedState))
                    rescheduleDelay *= LavaFullerStateDelayMultiplier;
            }

            switch (result.Action)
            {
                case FluidJobActionClearSelf:
                    if (TryClearFluidBlock(worldPosition, node.BlockId))
                    {
                        QueueNeighborFluids(worldPosition, priority: true);
                        operations++;
                    }
                    break;

                case FluidJobActionSolidifySelf:
                    if (TrySolidifyLavaBlock(worldPosition))
                        operations++;
                    break;

                case FluidJobActionSolidifyTarget:
                    if (TrySolidifyLavaBlock(ToVector3Int(result.TargetPosition)))
                        operations++;
                    break;

                case FluidJobActionFlowDown:
                    if (TryFlowInto(
                            worldPosition,
                            ToVector3Int(result.TargetPosition),
                            node.BlockId,
                            result.TargetDepth,
                            false,
                            true))
                    {
                        operations++;
                    }
                    else
                    {
                        result.NeedsRetry = 1;
                    }
                    break;

                case FluidJobActionFlowHorizontal:
                    operations += ApplyHorizontalFlowMask(worldPosition, node.BlockId, result.DirectionMask, result.TargetDepth, remainingOperations);
                    if (operations == 0)
                        result.NeedsRetry = 1;
                    break;

                case FluidJobActionRetry:
                    result.NeedsRetry = 1;
                    break;
            }

            if (operations == 0 && result.HasStateUpdate != 0)
                operations = 1;

            if ((operations > 0 || result.NeedsRetry != 0 || result.HasStateUpdate != 0) &&
                TryGetBlockId(worldPosition, out int finalBlockId) &&
                finalBlockId == node.BlockId)
            {
                TryEnqueueFluidInRange(worldPosition, node.BlockId, rescheduleDelay, priority: true);
            }

            return operations;
        }

        private static bool FluidJobSnapshotStillValid(
            Vector3Int worldPosition,
            int fluidBlockId,
            FluidJobResult result)
        {
            if (!TryGetBlockId(worldPosition, out int blockId) || blockId != fluidBlockId)
                return false;

            FluidRuntimeState currentState = EnsureFluidState(worldPosition, fluidBlockId);
            return currentState.Depth == result.ExpectedDepth &&
                   currentState.IsSource == (result.ExpectedIsSource != 0) &&
                   currentState.IsFalling == (result.ExpectedIsFalling != 0);
        }

        private static bool HasFluidJobDependencyMutation(Vector3Int center)
        {
            if (fluidJobMutatedPositions.Count == 0)
                return false;

            for (int dx = -FluidSampleRadius; dx <= FluidSampleRadius; dx++)
            {
                for (int dz = -FluidSampleRadius; dz <= FluidSampleRadius; dz++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (fluidJobMutatedPositions.Contains(center + new Vector3Int(dx, dy, dz)))
                            return true;
                    }
                }
            }

            for (int i = 0; i < HorizontalDirections.Length; i++)
            {
                if (fluidJobMutatedPositions.Contains(center + HorizontalDirections[i] * 2))
                    return true;
            }

            if (fluidJobMutatedPositions.Contains(center + Vector3Int.down * 2))
                return true;

            return false;
        }

        private static int ApplyHorizontalFlowMask(
            Vector3Int worldPosition,
            int fluidBlockId,
            int directionMask,
            int targetDepth,
            int remainingOperations)
        {
            int operations = 0;
            for (int i = 0; i < HorizontalDirections.Length && operations < remainingOperations; i++)
            {
                if ((directionMask & (1 << i)) == 0)
                    continue;

                Vector3Int direction = HorizontalDirections[i];
                if (TryFlowInto(
                        worldPosition,
                        worldPosition + direction,
                        fluidBlockId,
                        targetDepth,
                        false,
                        false))
                {
                    operations++;

                    if (!TryGetBlockId(worldPosition, out int currentBlockId) ||
                        currentBlockId != fluidBlockId)
                    {
                        break;
                    }
                }
            }

            return operations;
        }

        private static void EnsureFluidJobBuffers(int nodeCount)
        {
            int nodeCapacity = Mathf.NextPowerOfTwo(Mathf.Max(nodeCount, FluidSimulationBatchSize));
            if (!fluidJobNodes.IsCreated || fluidJobNodes.Length < nodeCount)
            {
                if (fluidJobNodes.IsCreated)
                    fluidJobNodes.Dispose();

                fluidJobNodes = new NativeArray<FluidJobNode>(
                    nodeCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }

            if (!fluidJobResults.IsCreated || fluidJobResults.Length < nodeCount)
            {
                if (fluidJobResults.IsCreated)
                    fluidJobResults.Dispose();

                fluidJobResults = new NativeArray<FluidJobResult>(
                    nodeCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }

            int sampleCapacity = Mathf.NextPowerOfTwo(
                Mathf.Max(FluidSamplesPerNode, nodeCount * FluidSamplesPerNode));
            if (!fluidJobBlockSamples.IsCreated)
            {
                fluidJobBlockSamples = new NativeParallelHashMap<long, FluidJobBlockSample>(
                    sampleCapacity,
                    Allocator.Persistent);
            }
            else if (fluidJobBlockSamples.Capacity < sampleCapacity)
            {
                fluidJobBlockSamples.Capacity = sampleCapacity;
            }
        }

        private static void DisposeFluidJobBuffers()
        {
            if (fluidJobBlockSamples.IsCreated)
                fluidJobBlockSamples.Dispose();

            if (fluidJobResults.IsCreated)
                fluidJobResults.Dispose();

            if (fluidJobNodes.IsCreated)
                fluidJobNodes.Dispose();
        }

        private static void AddFluidJobSamples(
            Vector3Int center,
            NativeParallelHashMap<long, FluidJobBlockSample> blockSamples)
        {
            for (int dx = -FluidSampleRadius; dx <= FluidSampleRadius; dx++)
            {
                for (int dz = -FluidSampleRadius; dz <= FluidSampleRadius; dz++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        AddFluidJobSample(center + new Vector3Int(dx, dy, dz), blockSamples);
                    }
                }
            }

            // Evaluating an adjacent target's source state also reads the cell on
            // the far side of that target. The other three neighbors are already
            // covered by the 3x3x3 sample cube.
            for (int i = 0; i < HorizontalDirections.Length; i++)
                AddFluidJobSample(center + HorizontalDirections[i] * 2, blockSamples);

            // A downward target can test source conversion, which also reads the
            // support cell directly below that target.
            AddFluidJobSample(center + Vector3Int.down * 2, blockSamples);
        }

        private static void AddFluidJobSample(
            Vector3Int worldPosition,
            NativeParallelHashMap<long, FluidJobBlockSample> blockSamples)
        {
            long key = PackFluidPosition(worldPosition.x, worldPosition.y, worldPosition.z);
            if (blockSamples.ContainsKey(key))
                return;

            FluidJobBlockSample sample = default;
            if (TryGetBlockId(worldPosition, out int blockId))
            {
                sample.Exists = 1;
                sample.BlockId = blockId;
                sample.IsFluid = IsSupportedFluid(blockId) ? (byte)1 : (byte)0;
                sample.IsSolidSourceSupport = IsSolidSourceSupportBlock(blockId) ? (byte)1 : (byte)0;

                if (IsBuiltinFluid(blockId))
                {
                    sample.RuntimeBlockId = blockId;
                    if (fluidRuntimeStates.TryGetValue(worldPosition, out FluidRuntimeState state) &&
                        state.BlockId == blockId)
                    {
                        sample.Depth = (byte)state.Depth;
                        sample.IsSource = state.IsSource ? (byte)1 : (byte)0;
                        sample.IsFalling = state.IsFalling ? (byte)1 : (byte)0;
                    }
                    else
                    {
                        sample.Depth = 0;
                        sample.IsSource = 1;
                        sample.IsFalling = 0;
                    }
                }
            }

            blockSamples.TryAdd(key, sample);
        }

        private static bool IsBuiltinFluid(int blockId)
        {
            return blockId == Chunk.BLOCK_WATER || blockId == Chunk.BLOCK_LAVA;
        }

        private static int3 ToInt3(Vector3Int value)
        {
            return new int3(value.x, value.y, value.z);
        }

        private static Vector3Int ToVector3Int(int3 value)
        {
            return new Vector3Int(value.x, value.y, value.z);
        }

        private const int FluidPositionPackOffset = 1 << 20;
        private const long FluidPositionPackMask = (1L << 21) - 1L;

        private static long PackFluidPosition(int x, int y, int z)
        {
            unchecked
            {
                long px = ((long)x + FluidPositionPackOffset) & FluidPositionPackMask;
                long py = ((long)y + FluidPositionPackOffset) & FluidPositionPackMask;
                long pz = ((long)z + FluidPositionPackOffset) & FluidPositionPackMask;
                return (px << 42) | (py << 21) | pz;
            }
        }

        private static int ProcessFluidNode(
            Vector3Int worldPosition,
            int fluidBlockId,
            int remainingOperations)
        {
            if (remainingOperations <= 0)
                return 0;

            if (!IsWorldPositionInSimulationRange(worldPosition))
            {
                pendingFluidStates.Remove(worldPosition);
                return 0;
            }

            if (!TryGetBlockId(worldPosition, out int blockId))
                return 0;

            if (blockId != fluidBlockId)
            {
                RemoveFluidRuntimeState(worldPosition, fluidBlockId);
                return 0;
            }

            FluidRuntimeState state = EnsureFluidState(worldPosition, fluidBlockId);
            FluidRuntimeState stateBeforeUpdate = state;
            float rescheduleDelay = GetFluidSpreadInterval(fluidBlockId);
            int operations = 0;
            bool stateChanged = false;
            bool needsRetry = false;

            operations += SolidifyLavaWaterContacts(
                worldPosition,
                fluidBlockId,
                remainingOperations,
                out bool solidifiedSelf);

            if (solidifiedSelf)
                return Mathf.Max(operations, 1);

            if (operations > 0)
            {
                TryEnqueueFluidInRange(worldPosition, fluidBlockId, GetFluidSpreadInterval(fluidBlockId), priority: true);
                return operations;
            }

            if (!state.IsSource && ShouldBecomeSource(worldPosition, fluidBlockId))
            {
                state = FluidRuntimeState.Source(fluidBlockId);
                SetFluidRuntimeState(worldPosition, state);
                stateChanged = true;
            }

            if (!state.IsSource)
            {
                if (!CanFlowingBlockRemain(worldPosition, fluidBlockId, state, out FluidRuntimeState updatedState))
                {
                    if (TryClearFluidBlock(worldPosition, fluidBlockId))
                    {
                        QueueNeighborFluids(worldPosition, priority: true);
                        return 1;
                    }

                    return 0;
                }

                if (!state.Matches(updatedState))
                {
                    state = updatedState;
                    SetFluidRuntimeState(worldPosition, state);
                    stateChanged = true;

                    if (ShouldUseExtendedLavaDelay(stateBeforeUpdate, state))
                        rescheduleDelay *= LavaFullerStateDelayMultiplier;
                }
            }

            Vector3Int belowPosition = worldPosition + Vector3Int.down;
            bool canFlowDown = CanFlowInto(
                belowPosition,
                fluidBlockId,
                0,
                false,
                true,
                out bool missingDownTarget);

            if (missingDownTarget)
            {
                needsRetry = true;
            }
            else if (canFlowDown && TryFlowInto(
                         worldPosition,
                         belowPosition,
                         fluidBlockId,
                         0,
                         false,
                         true))
            {
                operations++;
            }

            if (operations < remainingOperations &&
                ShouldSpreadHorizontally(
                    worldPosition,
                    fluidBlockId,
                    state,
                    canFlowDown,
                    missingDownTarget))
            {
                int horizontalDepth = state.Depth + 1;
                if (horizontalDepth <= GetMaxDepth(fluidBlockId))
                {
                    GetSpreadDirections(worldPosition, fluidBlockId, horizontalDepth, spreadDirections, ref needsRetry);

                    for (int i = 0; i < spreadDirections.Count && operations < remainingOperations; i++)
                    {
                        Vector3Int direction = spreadDirections[i];
                        Vector3Int targetPosition = worldPosition + direction;

                        if (TryFlowInto(
                                worldPosition,
                                targetPosition,
                                fluidBlockId,
                                horizontalDepth,
                                false,
                                false))
                        {
                            operations++;
                        }
                    }
                }
            }

            if (stateChanged)
                operations = Mathf.Max(operations, 1);

            if (operations > 0 || needsRetry || stateChanged)
                TryEnqueueFluidInRange(worldPosition, fluidBlockId, rescheduleDelay, priority: true);

            return operations;
        }

        private static bool ShouldSpreadHorizontally(
            Vector3Int worldPosition,
            int fluidBlockId,
            FluidRuntimeState state,
            bool canFlowDown,
            bool missingDownTarget)
        {
            if (missingDownTarget)
                return false;

            if (canFlowDown)
                return HasAtLeastHorizontalSourceNeighbors(worldPosition, fluidBlockId, 3);

            if (state.IsSource)
                return true;

            return HasSolidSupportBelow(worldPosition);
        }

        private static bool HasSolidSupportBelow(Vector3Int worldPosition)
        {
            if (!TryGetSimulationBlockId(worldPosition + Vector3Int.down, out int belowBlockId))
                return false;

            return belowBlockId != Chunk.BLOCK_AIR && !IsSupportedFluid(belowBlockId);
        }

        private static void GetSpreadDirections(
            Vector3Int worldPosition,
            int fluidBlockId,
            int nextDepth,
            List<Vector3Int> results,
            ref bool needsRetry)
        {
            results.Clear();

            int bestDropDistance = int.MaxValue;
            for (int i = 0; i < HorizontalDirections.Length; i++)
            {
                Vector3Int direction = HorizontalDirections[i];
                Vector3Int targetPosition = worldPosition + direction;

                if (!CanFlowToward(
                        worldPosition,
                        targetPosition,
                        fluidBlockId,
                        nextDepth,
                        false,
                        false,
                        direction,
                        out bool missingTarget))
                {
                    needsRetry |= missingTarget;
                    continue;
                }

                int dropDistance = GetDistanceToDrop(
                    targetPosition,
                    fluidBlockId,
                    0,
                    direction,
                    ref needsRetry);

                if (dropDistance < bestDropDistance)
                {
                    bestDropDistance = dropDistance;
                    results.Clear();
                }

                if (dropDistance == bestDropDistance)
                    results.Add(direction);
            }

            if (bestDropDistance != int.MaxValue &&
                bestDropDistance <= GetSlopeFindDistance(fluidBlockId))
                return;

            results.Clear();
            for (int i = 0; i < HorizontalDirections.Length; i++)
            {
                Vector3Int direction = HorizontalDirections[i];
                Vector3Int targetPosition = worldPosition + direction;
                if (CanFlowInto(
                        targetPosition,
                        fluidBlockId,
                        nextDepth,
                        false,
                        false,
                        out bool missingTarget))
                {
                    results.Add(direction);
                }

                needsRetry |= missingTarget;
            }
        }

        private static int GetDistanceToDrop(
            Vector3Int worldPosition,
            int fluidBlockId,
            int depth,
            Vector3Int incomingDirection,
            ref bool needsRetry)
        {
            int slopeFindDistance = GetSlopeFindDistance(fluidBlockId);
            if (depth > slopeFindDistance)
                return int.MaxValue;

            dropSearchVisited.Clear();
            dropSearchQueue.Clear();
            dropSearchVisited.Add(worldPosition);
            dropSearchQueue.Enqueue(new DropSearchNode(worldPosition, depth, incomingDirection));

            while (dropSearchQueue.Count > 0)
            {
                DropSearchNode node = dropSearchQueue.Dequeue();
                Vector3Int belowPosition = node.Position + Vector3Int.down;
                if (CanFlowToward(
                        node.Position,
                        belowPosition,
                        fluidBlockId,
                        0,
                        false,
                        true,
                        Vector3Int.down,
                        out bool missingBelowTarget))
                {
                    return node.Distance;
                }

                needsRetry |= missingBelowTarget;

                if (node.Distance >= slopeFindDistance)
                    continue;

                for (int i = 0; i < HorizontalDirections.Length; i++)
                {
                    Vector3Int direction = HorizontalDirections[i];
                    if (direction == -node.IncomingDirection)
                        continue;

                    Vector3Int targetPosition = node.Position + direction;
                    int targetDistance = node.Distance + 1;
                    if (!CanFlowToward(
                            node.Position,
                            targetPosition,
                            fluidBlockId,
                            Mathf.Min(targetDistance, GetMaxDepth(fluidBlockId)),
                            false,
                            false,
                            direction,
                            out bool missingHorizontalTarget) ||
                        !dropSearchVisited.Add(targetPosition))
                    {
                        needsRetry |= missingHorizontalTarget;
                        continue;
                    }

                    dropSearchQueue.Enqueue(new DropSearchNode(
                        targetPosition,
                        targetDistance,
                        direction));
                }
            }

            return int.MaxValue;
        }

        private static bool TryFlowInto(
            Vector3Int fromPosition,
            Vector3Int targetPosition,
            int fluidBlockId,
            int depth,
            bool isSource,
            bool isFalling)
        {
            if (!IsWorldPositionInSimulationRange(targetPosition))
                return false;

            if (!TryGetBlockState(targetPosition, out int targetBlockId, out Chunk chunk, out Vector3Int localPosition))
                return false;

            if (targetBlockId == Chunk.BLOCK_AIR)
            {
                FluidRuntimeState newState = GetIncomingFluidState(targetPosition, fluidBlockId, depth, isSource, isFalling);
                SetBlockRaw(chunk, localPosition, fluidBlockId);
                SetFluidRuntimeState(targetPosition, newState);
                MarkDirty(chunk, localPosition);

                if (SolidifyContactsAfterFluidPlacement(targetPosition, fluidBlockId))
                    return true;

                TryEnqueueFluidInRange(targetPosition, fluidBlockId, GetFluidSpreadInterval(fluidBlockId), priority: true);
                return true;
            }

            if (targetBlockId == fluidBlockId)
            {
                FluidRuntimeState currentState = EnsureFluidState(targetPosition, fluidBlockId);
                FluidRuntimeState newState = GetIncomingFluidState(targetPosition, fluidBlockId, depth, isSource, isFalling);
                if (!CanReplaceFluidState(currentState, newState))
                    return false;

                SetFluidRuntimeState(targetPosition, newState);
                TryEnqueueFluidInRange(targetPosition, fluidBlockId, GetFluidSpreadInterval(fluidBlockId), priority: true);
                QueueNeighborFluids(targetPosition, priority: true);
                return true;
            }

            if (TrySolidifyFluidCollision(fromPosition, targetPosition, fluidBlockId, targetBlockId))
                return true;

            return false;
        }

        private static bool CanFlowInto(
            Vector3Int targetPosition,
            int fluidBlockId,
            int depth,
            bool isSource,
            bool isFalling,
            out bool missingTarget)
        {
            missingTarget = false;

            if (!IsWorldPositionInSimulationRange(targetPosition))
                return false;

            if (!TryGetBlockId(targetPosition, out int targetBlockId))
            {
                missingTarget = true;
                return false;
            }

            if (targetBlockId == Chunk.BLOCK_AIR)
                return true;

            if (targetBlockId == fluidBlockId)
            {
                FluidRuntimeState currentState = EnsureFluidState(targetPosition, fluidBlockId);
                FluidRuntimeState newState = GetIncomingFluidState(targetPosition, fluidBlockId, depth, isSource, isFalling);
                return CanReplaceFluidState(currentState, newState);
            }

            return CanSolidifyFluidCollision(fluidBlockId, targetBlockId);
        }

        private static int SolidifyLavaWaterContacts(
            Vector3Int worldPosition,
            int fluidBlockId,
            int remainingOperations,
            out bool solidifiedSelf)
        {
            solidifiedSelf = false;

            if (remainingOperations <= 0)
                return 0;

            if (fluidBlockId == Chunk.BLOCK_LAVA)
            {
                if (!IsTouchingWaterThatSolidifiesLava(worldPosition))
                    return 0;

                if (!TrySolidifyLavaBlock(worldPosition))
                    return 0;

                solidifiedSelf = true;
                return 1;
            }

            if (fluidBlockId != Chunk.BLOCK_WATER)
                return 0;

            for (int i = 0; i < WaterSolidifyingLavaDirections.Length; i++)
            {
                Vector3Int neighborPosition = worldPosition + WaterSolidifyingLavaDirections[i];
                if (!TryGetSimulationBlockId(neighborPosition, out int neighborBlockId) ||
                    neighborBlockId != Chunk.BLOCK_LAVA)
                {
                    continue;
                }

                if (TrySolidifyLavaBlock(neighborPosition))
                    return 1;
            }

            return 0;
        }

        private static bool SolidifyContactsAfterFluidPlacement(Vector3Int worldPosition, int fluidBlockId)
        {
            if (fluidBlockId == Chunk.BLOCK_LAVA)
                return IsTouchingWaterThatSolidifiesLava(worldPosition) && TrySolidifyLavaBlock(worldPosition);

            if (fluidBlockId != Chunk.BLOCK_WATER)
                return false;

            // Defer neighboring lava conversions into the normal operation budget.
            // This keeps a single water placement from synchronously mutating and
            // remeshing several surrounding lava cells at once.
            for (int i = 0; i < WaterSolidifyingLavaDirections.Length; i++)
            {
                Vector3Int neighborPosition = worldPosition + WaterSolidifyingLavaDirections[i];
                if (TryGetSimulationBlockId(neighborPosition, out int neighborBlockId) &&
                    neighborBlockId == Chunk.BLOCK_LAVA)
                {
                    TryEnqueueFluidInRange(neighborPosition, Chunk.BLOCK_LAVA, 0f, priority: true);
                }
            }

            return false;
        }

        private static bool CanSolidifyFluidCollision(int flowingFluidBlockId, int targetFluidBlockId)
        {
            return (flowingFluidBlockId == Chunk.BLOCK_WATER && targetFluidBlockId == Chunk.BLOCK_LAVA) ||
                   (flowingFluidBlockId == Chunk.BLOCK_LAVA && targetFluidBlockId == Chunk.BLOCK_WATER);
        }

        private static bool TrySolidifyFluidCollision(
            Vector3Int fromPosition,
            Vector3Int targetPosition,
            int flowingFluidBlockId,
            int targetFluidBlockId)
        {
            if (flowingFluidBlockId == Chunk.BLOCK_WATER && targetFluidBlockId == Chunk.BLOCK_LAVA)
                return TrySolidifyLavaBlock(targetPosition);

            if (flowingFluidBlockId == Chunk.BLOCK_LAVA && targetFluidBlockId == Chunk.BLOCK_WATER)
            {
                return targetPosition.y < fromPosition.y
                    ? TryConvertWaterToStone(targetPosition)
                    : TrySolidifyLavaBlock(fromPosition);
            }

            return false;
        }

        private static bool TrySolidifyLavaBlock(Vector3Int lavaPosition)
        {
            if (!IsWorldPositionInSimulationRange(lavaPosition))
                return false;

            if (!TryGetBlockState(lavaPosition, out int blockId, out Chunk chunk, out Vector3Int localPosition) ||
                blockId != Chunk.BLOCK_LAVA)
            {
                return false;
            }

            FluidRuntimeState lavaState = EnsureFluidState(lavaPosition, Chunk.BLOCK_LAVA);
            int solidBlockId = lavaState.IsSource ? Chunk.BLOCK_OBSIDIAN : Chunk.BLOCK_COBBLESTONE;

            RemoveFluidRuntimeState(lavaPosition, Chunk.BLOCK_LAVA);
            pendingFluidStates.Remove(lavaPosition);
            SetBlockRaw(chunk, localPosition, solidBlockId);
            MarkDirty(chunk, localPosition);
            QueueNeighborFluids(lavaPosition, priority: true);
            return true;
        }

        private static bool TryConvertWaterToStone(Vector3Int waterPosition)
        {
            if (!IsWorldPositionInSimulationRange(waterPosition))
                return false;

            if (!TryGetBlockState(waterPosition, out int blockId, out Chunk chunk, out Vector3Int localPosition) ||
                blockId != Chunk.BLOCK_WATER)
            {
                return false;
            }

            RemoveFluidRuntimeState(waterPosition, Chunk.BLOCK_WATER);
            pendingFluidStates.Remove(waterPosition);
            SetBlockRaw(chunk, localPosition, Chunk.BLOCK_STONE);
            MarkDirty(chunk, localPosition);
            QueueNeighborFluids(waterPosition, priority: true);
            return true;
        }

        private static bool IsTouchingWaterThatSolidifiesLava(Vector3Int worldPosition)
        {
            for (int i = 0; i < LavaSolidifyingWaterDirections.Length; i++)
            {
                Vector3Int neighborPosition = worldPosition + LavaSolidifyingWaterDirections[i];
                if (TryGetSimulationBlockId(neighborPosition, out int neighborBlockId) &&
                    neighborBlockId == Chunk.BLOCK_WATER)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanFlowToward(
            Vector3Int fromPosition,
            Vector3Int targetPosition,
            int fluidBlockId,
            int depth,
            bool isSource,
            bool isFalling,
            Vector3Int flowDirection,
            out bool missingTarget)
        {
            missingTarget = false;
            if (!IsWorldPositionInSimulationRange(targetPosition))
                return false;

            if (CanFlowInto(
                    targetPosition,
                    fluidBlockId,
                    depth,
                    isSource,
                    isFalling,
                    out missingTarget))
            {
                return true;
            }

            if (missingTarget)
                return false;

            if (!TryGetBlockId(targetPosition, out int targetBlockId) ||
                targetBlockId != fluidBlockId)
            {
                return false;
            }

            FluidRuntimeState targetState = EnsureFluidState(targetPosition, fluidBlockId);

            if (flowDirection == Vector3Int.down)
                return targetState.IsFalling || !HasSolidSupportBelow(targetPosition);

            if (targetState.IsSource)
                return false;

            return targetState.IsFalling || targetState.Depth >= depth;
        }

        private static FluidRuntimeState GetIncomingFluidState(
            Vector3Int targetPosition,
            int fluidBlockId,
            int depth,
            bool isSource,
            bool isFalling)
        {
            if ((isSource || ShouldBecomeSource(targetPosition, fluidBlockId)) && fluidBlockId == Chunk.BLOCK_WATER)
                return FluidRuntimeState.Source(fluidBlockId);

            return new FluidRuntimeState(
                fluidBlockId,
                Mathf.Clamp(depth, 0, GetMaxDepth(fluidBlockId)),
                isSource,
                isFalling && !isSource);
        }

        private static bool CanReplaceFluidState(FluidRuntimeState currentState, FluidRuntimeState newState)
        {
            if (currentState.BlockId != newState.BlockId)
                return true;

            if (currentState.IsSource)
                return false;

            if (newState.IsSource)
                return true;

            if (newState.Depth < currentState.Depth)
                return true;

            return newState.IsFalling && !currentState.IsFalling && newState.Depth <= currentState.Depth;
        }

        private static bool TryClearFluidBlock(Vector3Int worldPosition, int fluidBlockId)
        {
            if (!IsWorldPositionInSimulationRange(worldPosition))
                return false;

            if (!TryGetBlockState(worldPosition, out int blockId, out Chunk chunk, out Vector3Int localPosition) ||
                blockId != fluidBlockId)
            {
                return false;
            }

            RemoveFluidRuntimeState(worldPosition, fluidBlockId);
            SetBlockRaw(chunk, localPosition, Chunk.BLOCK_AIR);
            MarkDirty(chunk, localPosition);
            return true;
        }

        private static bool CanFlowingBlockRemain(
            Vector3Int worldPosition,
            int fluidBlockId,
            FluidRuntimeState state,
            out FluidRuntimeState updatedState)
        {
            updatedState = state;

            Vector3Int abovePosition = worldPosition + Vector3Int.up;
            if (TryGetSimulationBlockId(abovePosition, out int aboveBlockId) &&
                aboveBlockId == fluidBlockId)
            {
                updatedState = new FluidRuntimeState(fluidBlockId, 0, false, true);
                return true;
            }

            int bestParentDepth = int.MaxValue;
            for (int i = 0; i < HorizontalDirections.Length; i++)
            {
                Vector3Int neighborPosition = worldPosition + HorizontalDirections[i];
                if (!TryGetSimulationBlockId(neighborPosition, out int neighborBlockId) ||
                    neighborBlockId != fluidBlockId)
                {
                    continue;
                }

                FluidRuntimeState neighborState = EnsureFluidState(neighborPosition, fluidBlockId);
                if (neighborState.IsSource)
                {
                    bestParentDepth = 0;
                    break;
                }

                if (neighborState.IsFalling)
                {
                    if (CanFallingFluidParentFeedHorizontalFlow(neighborPosition, fluidBlockId))
                        bestParentDepth = Mathf.Min(bestParentDepth, 0);

                    continue;
                }

                if (!neighborState.IsFalling && neighborState.Depth < state.Depth)
                    bestParentDepth = Mathf.Min(bestParentDepth, neighborState.Depth);
            }

            if (bestParentDepth == int.MaxValue)
                return false;

            int expectedDepth = bestParentDepth + 1;
            if (expectedDepth > GetMaxDepth(fluidBlockId))
                return false;

            updatedState = new FluidRuntimeState(fluidBlockId, expectedDepth, false, false);
            return true;
        }

        private static bool CanFallingFluidParentFeedHorizontalFlow(Vector3Int parentPosition, int fluidBlockId)
        {
            return !CanFlowInto(
                       parentPosition + Vector3Int.down,
                       fluidBlockId,
                       0,
                       false,
                       true,
                       out _) &&
                   HasSolidSupportBelow(parentPosition);
        }

        private static bool ShouldBecomeSource(Vector3Int worldPosition, int fluidBlockId)
        {
            if (fluidBlockId != Chunk.BLOCK_WATER)
                return false;

            if (!TryGetSimulationBlockId(worldPosition + Vector3Int.down, out int belowBlockId))
                return false;

            bool hasSourceSupport = IsSolidSourceSupportBlock(belowBlockId);
            if (!hasSourceSupport && belowBlockId == fluidBlockId)
            {
                FluidRuntimeState belowState = EnsureFluidState(
                    worldPosition + Vector3Int.down,
                    fluidBlockId);
                hasSourceSupport = belowState.IsSource;
            }

            if (!hasSourceSupport)
                return false;

            return HasAtLeastHorizontalSourceNeighbors(worldPosition, fluidBlockId, 2);
        }

        private static bool HasAtLeastHorizontalSourceNeighbors(
            Vector3Int worldPosition,
            int fluidBlockId,
            int requiredSources)
        {
            int sourceCount = 0;
            for (int i = 0; i < HorizontalDirections.Length; i++)
            {
                Vector3Int neighborPosition = worldPosition + HorizontalDirections[i];
                if (!TryGetSimulationBlockId(neighborPosition, out int neighborBlockId) ||
                    neighborBlockId != fluidBlockId)
                {
                    continue;
                }

                FluidRuntimeState neighborState = EnsureFluidState(neighborPosition, fluidBlockId);
                if (neighborState.IsSource)
                    sourceCount++;

                if (sourceCount >= requiredSources)
                    return true;
            }

            return false;
        }

        private static bool IsSolidSourceSupportBlock(int blockId)
        {
            if (blockId <= Chunk.BLOCK_AIR || IsSupportedFluid(blockId))
                return false;

            BlockData[] blocks = blockDefinitions;
            if (blocks == null)
            {
                AssetsContainer assets = AssetsContainer.Instance;
                CacheBlockDefinitions(assets != null ? assets.Blocks : null);
                blocks = blockDefinitions;
            }

            if (blocks == null || blockId >= blocks.Length)
                return true;

            BlockData block = blocks[blockId];
            return block != null && block.IsFullBlock;
        }

        private static FluidRuntimeState EnsureFluidState(Vector3Int worldPosition, int fluidBlockId)
        {
            if (fluidRuntimeStates.TryGetValue(worldPosition, out FluidRuntimeState state) &&
                state.BlockId == fluidBlockId)
            {
                return state;
            }

            // Generated/static source blocks are the overwhelmingly common state
            // (especially oceans). Keep that default implicit so discovery does not
            // allocate a dictionary entry for every settled fluid voxel.
            return FluidRuntimeState.Source(fluidBlockId);
        }

        private static void SetFluidRuntimeState(Vector3Int worldPosition, FluidRuntimeState state)
        {
            RecordFluidJobMutation(worldPosition);

            if (state.IsSource && state.Depth == 0 && !state.IsFalling)
            {
                if (fluidRuntimeStates.Remove(worldPosition))
                    MarkFluidStatePersistenceDirty(worldPosition);
                return;
            }

            if (fluidRuntimeStates.TryGetValue(worldPosition, out FluidRuntimeState currentState) &&
                currentState.Matches(state))
            {
                return;
            }

            fluidRuntimeStates[worldPosition] = state;
            MarkFluidStatePersistenceDirty(worldPosition);
        }

        private static bool RemoveFluidRuntimeState(Vector3Int worldPosition, int expectedBlockId)
        {
            if (!fluidRuntimeStates.TryGetValue(worldPosition, out FluidRuntimeState state) ||
                state.BlockId != expectedBlockId)
            {
                return false;
            }

            fluidRuntimeStates.Remove(worldPosition);
            RecordFluidJobMutation(worldPosition);
            MarkFluidStatePersistenceDirty(worldPosition);
            return true;
        }

        private static void MarkFluidStatePersistenceDirty(Vector3Int worldPosition)
        {
            Vector3Int coordinate = GetChunkCoordinateFromBlockPosition(worldPosition);
            if (TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk) && chunk != null)
                chunk.HasChanged = true;
        }

        private static bool TryEnqueueFluidInRange(
            Vector3Int worldPosition,
            int fluidBlockId,
            float delay,
            bool priority = false)
        {
            float readyTime = simulationTime + Mathf.Max(0f, delay);

            if (pendingFluidStates.TryGetValue(worldPosition, out PendingFluidState pendingState) &&
                pendingState.BlockId == fluidBlockId &&
                pendingState.ReadyTime <= readyTime)
            {
                return false;
            }

            pendingFluidStates[worldPosition] = new PendingFluidState(fluidBlockId, readyTime);
            FluidNode node = new FluidNode(
                worldPosition,
                fluidBlockId,
                readyTime,
                fluidNodeSequence++);

            if (priority)
                priorityFluids.Enqueue(node);
            else
                pendingFluids.Enqueue(node);

            return true;
        }

        private static void SetBlockRaw(Chunk chunk, Vector3Int localPosition, int blockId)
        {
            chunk.SetBlockRaw(localPosition, blockId);

            if (isApplyingFluidJobResults)
                fluidJobMutatedPositions.Add(GetWorldPosition(chunk, localPosition));

            if (isProcessingTick)
            {
                Vector3Int worldPosition = GetWorldPosition(chunk, localPosition);
                blockStateCache[worldPosition] = new CachedBlockState(blockId, chunk);
            }
        }

        private static void RecordFluidJobMutation(Vector3Int worldPosition)
        {
            if (isApplyingFluidJobResults)
                fluidJobMutatedPositions.Add(worldPosition);
        }

        private static int GetMaxDepth(int blockId)
        {
            return blockId == Chunk.BLOCK_LAVA
                ? MinecraftLavaOverworldMaxDepth
                : maxWaterDepth;
        }

        private static int GetSlopeFindDistance(int blockId)
        {
            return blockId == Chunk.BLOCK_LAVA
                ? MinecraftLavaOverworldSlopeFindDistance
                : MinecraftWaterSlopeFindDistance;
        }

        private static bool ShouldUseExtendedLavaDelay(
            FluidRuntimeState previousState,
            FluidRuntimeState updatedState)
        {
            if (previousState.BlockId != Chunk.BLOCK_LAVA ||
                updatedState.BlockId != Chunk.BLOCK_LAVA ||
                previousState.IsFalling ||
                updatedState.IsFalling ||
                updatedState.Depth >= previousState.Depth)
            {
                return false;
            }

            // Java Edition applies the 4x delay on three of four random outcomes.
            fluidRandomState ^= fluidRandomState << 13;
            fluidRandomState ^= fluidRandomState >> 17;
            fluidRandomState ^= fluidRandomState << 5;
            return (fluidRandomState & 3u) != 0u;
        }

        private static void RefreshSimulationArea()
        {
            nextSimulationChunks.Clear();

            if (!isEnabled || !simulationAreaConfigured)
            {
                activeSimulationChunks.Clear();
                return;
            }

            int minY = simulationCenterChunk.y - simulationVerticalChunkRange;
            int maxY = simulationCenterChunk.y + simulationVerticalChunkRange;

            for (int x = -simulationChunkRange; x <= simulationChunkRange; x++)
            {
                int xSq = x * x;
                for (int z = -simulationChunkRange; z <= simulationChunkRange; z++)
                {
                    if (xSq + z * z > simulationChunkRangeSq)
                        continue;

                    for (int y = minY; y <= maxY; y++)
                    {
                        Vector3Int coordinate = new Vector3Int(
                            simulationCenterChunk.x + x,
                            y,
                            simulationCenterChunk.z + z);

                        nextSimulationChunks.Add(coordinate);

                        if (activeSimulationChunks.Contains(coordinate))
                            continue;

                        if (TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk))
                            QueueChunkFluids(chunk);
                    }
                }
            }

            // Pending frontier nodes are pruned when a chunk leaves the active
            // area. Invalidate its discovery stamp so returning later rebuilds
            // that compact frontier instead of silently losing the work.
            foreach (Vector3Int previousCoordinate in activeSimulationChunks)
            {
                if (!nextSimulationChunks.Contains(previousCoordinate) &&
                    TerrainGenerator.Chunks.TryGetValue(previousCoordinate, out Chunk previousChunk))
                {
                    discoveredChunkRevisions.Remove(previousChunk);
                }
            }

            activeSimulationChunks.Clear();
            HashSet<Vector3Int> previousSimulationChunks = activeSimulationChunks;
            activeSimulationChunks = nextSimulationChunks;
            nextSimulationChunks = previousSimulationChunks;
        }

        private static void PruneFluidWorkOutsideSimulationRange()
        {
            PruneFluidQueue(priorityFluids);
            PruneFluidQueue(pendingFluids);

            staleFluidPositions.Clear();
            foreach (KeyValuePair<Vector3Int, PendingFluidState> entry in pendingFluidStates)
            {
                if (!IsWorldPositionInSimulationRange(entry.Key))
                    staleFluidPositions.Add(entry.Key);
            }

            for (int i = 0; i < staleFluidPositions.Count; i++)
                pendingFluidStates.Remove(staleFluidPositions[i]);

            staleFluidPositions.Clear();
        }

        private static void PruneFluidQueue(FluidNodeHeap fluidQueue)
        {
            fluidQueue.RemoveOutsideSimulationRange();
        }

        private static bool IsWorldPositionInSimulationRange(Vector3Int worldPosition)
        {
            return IsChunkInSimulationRange(GetChunkCoordinateFromBlockPosition(worldPosition));
        }

        private static bool IsChunkInSimulationRange(Vector3Int chunkCoordinate)
        {
            if (!simulationAreaConfigured)
                return false;

            int dx = chunkCoordinate.x - simulationCenterChunk.x;
            int dz = chunkCoordinate.z - simulationCenterChunk.z;
            int dy = Mathf.Abs(chunkCoordinate.y - simulationCenterChunk.y);

            return dx * dx + dz * dz <= simulationChunkRangeSq &&
                   dy <= simulationVerticalChunkRange;
        }

        private static bool TryGetBlockState(
            Vector3Int worldPosition,
            out int blockId,
            out Chunk chunk,
            out Vector3Int localPosition)
        {
            if (isProcessingTick &&
                blockStateCache.TryGetValue(worldPosition, out CachedBlockState cachedState))
            {
                blockId = cachedState.BlockId;
                chunk = cachedState.Chunk;
                if (chunk == null)
                {
                    localPosition = default;
                    return false;
                }

                localPosition = GetLocalPosition(worldPosition, chunk.Coordinate);
                return true;
            }

            return TryGetBlockStateUncached(worldPosition, out blockId, out chunk, out localPosition);
        }

        private static bool TryGetBlockId(Vector3Int worldPosition, out int blockId)
        {
            if (isProcessingTick &&
                blockStateCache.TryGetValue(worldPosition, out CachedBlockState cachedState))
            {
                blockId = cachedState.BlockId;
                return cachedState.Chunk != null;
            }

            return TryGetBlockStateUncached(worldPosition, out blockId, out _, out _);
        }

        private static bool TryGetSimulationBlockId(Vector3Int worldPosition, out int blockId)
        {
            if (!IsWorldPositionInSimulationRange(worldPosition))
            {
                blockId = FluidBoundaryBlockId;
                return true;
            }

            return TryGetBlockId(worldPosition, out blockId);
        }

        private static bool TryGetBlockStateUncached(
            Vector3Int worldPosition,
            out int blockId,
            out Chunk chunk,
            out Vector3Int localPosition)
        {
            blockId = Chunk.BLOCK_AIR;
            chunk = null;
            localPosition = default;

            Vector3Int chunkCoordinate = GetChunkCoordinateFromBlockPosition(worldPosition);
            if (!TerrainGenerator.Chunks.TryGetValue(chunkCoordinate, out chunk) ||
                chunk.Blocks == null ||
                !chunk.IsGenerated)
            {
                CacheBlockState(worldPosition, Chunk.BLOCK_AIR, null);
                return false;
            }

            localPosition = GetLocalPosition(worldPosition, chunkCoordinate);

            if (!ChunkUtility.IsInsideChunk(localPosition))
            {
                CacheBlockState(worldPosition, Chunk.BLOCK_AIR, null);
                return false;
            }

            int blockIndex = localPosition.x +
                             localPosition.y * Chunk.CHUNK_SIZE +
                             localPosition.z * Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;
            blockId = chunk.Blocks.Data[blockIndex];
            CacheBlockState(worldPosition, blockId, chunk);
            return true;
        }

        private static void CacheBlockState(
            Vector3Int worldPosition,
            int blockId,
            Chunk chunk)
        {
            if (!isProcessingTick)
                return;

            blockStateCache[worldPosition] = new CachedBlockState(blockId, chunk);
        }

        private static Vector3Int GetLocalPosition(Vector3Int worldPosition, Vector3Int chunkCoordinate)
        {
            return new Vector3Int(
                worldPosition.x - chunkCoordinate.x * Chunk.CHUNK_SIZE,
                worldPosition.y - chunkCoordinate.y * Chunk.CHUNK_HEIGHT,
                worldPosition.z - chunkCoordinate.z * Chunk.CHUNK_SIZE);
        }

        private static Vector3Int GetWorldPosition(Chunk chunk, Vector3Int localPosition)
        {
            return new Vector3Int(
                chunk.Coordinate.x * Chunk.CHUNK_SIZE + localPosition.x,
                chunk.Coordinate.y * Chunk.CHUNK_HEIGHT + localPosition.y,
                chunk.Coordinate.z * Chunk.CHUNK_SIZE + localPosition.z);
        }

        private static Vector3Int GetChunkCoordinateFromBlockPosition(Vector3Int worldPosition)
        {
            return new Vector3Int(
                Chunk.CHUNK_SIZE == (1 << ChunkCoordinateShift)
                    ? worldPosition.x >> ChunkCoordinateShift
                    : FloorDiv(worldPosition.x, Chunk.CHUNK_SIZE),
                Chunk.CHUNK_HEIGHT == (1 << ChunkCoordinateShift)
                    ? worldPosition.y >> ChunkCoordinateShift
                    : FloorDiv(worldPosition.y, Chunk.CHUNK_HEIGHT),
                Chunk.CHUNK_SIZE == (1 << ChunkCoordinateShift)
                    ? worldPosition.z >> ChunkCoordinateShift
                    : FloorDiv(worldPosition.z, Chunk.CHUNK_SIZE));
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }

        private static bool IsSupportedFluid(int blockId)
        {
            if (blockId == Chunk.BLOCK_WATER || blockId == Chunk.BLOCK_LAVA)
                return true;

            bool[] flags = fluidBlockFlags;
            if (flags == null)
            {
                AssetsContainer assets = AssetsContainer.Instance;
                CacheBlockDefinitions(assets != null ? assets.Blocks : null);
                flags = fluidBlockFlags;
            }

            return blockId > Chunk.BLOCK_AIR &&
                   flags != null &&
                   blockId < flags.Length &&
                   flags[blockId];
        }

        internal static bool IsFluidBlock(int blockId)
        {
            return IsSupportedFluid(blockId);
        }

        private static void CacheBlockDefinitions(BlockData[] blocks)
        {
            blockDefinitions = blocks;
            if (blocks == null)
            {
                fluidBlockFlags = null;
                return;
            }

            var flags = new bool[blocks.Length];
            for (int blockId = 1; blockId < blocks.Length; blockId++)
            {
                BlockData block = blocks[blockId];
                flags[blockId] = block != null && block.IsFluid;
            }

            if (Chunk.BLOCK_WATER < flags.Length)
                flags[Chunk.BLOCK_WATER] = true;
            if (Chunk.BLOCK_LAVA < flags.Length)
                flags[Chunk.BLOCK_LAVA] = true;

            fluidBlockFlags = flags;
        }

        private static float GetFluidSpreadInterval(int blockId)
        {
            return blockId == Chunk.BLOCK_LAVA
                ? waterSpreadInterval * LavaOverworldSpeedMultiplier
                : waterSpreadInterval;
        }

        private static void MarkDirty(Chunk chunk, Vector3Int localPosition)
        {
            MarkDirty(chunk);

            if (localPosition.x == 0)
                MarkDirty(chunk.Coordinate + Vector3Int.left);
            else if (localPosition.x == Chunk.CHUNK_SIZE - 1)
                MarkDirty(chunk.Coordinate + Vector3Int.right);

            if (localPosition.y == 0)
                MarkDirty(chunk.Coordinate + Vector3Int.down);
            else if (localPosition.y == Chunk.CHUNK_HEIGHT - 1)
                MarkDirty(chunk.Coordinate + Vector3Int.up);

            if (localPosition.z == 0)
                MarkDirty(chunk.Coordinate + Vector3Int.back);
            else if (localPosition.z == Chunk.CHUNK_SIZE - 1)
                MarkDirty(chunk.Coordinate + Vector3Int.forward);
        }

        private static void MarkDirty(Vector3Int chunkCoordinate)
        {
            if (TerrainGenerator.Chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                MarkDirty(chunk);
        }

        private static void MarkDirty(Chunk chunk)
        {
            if (chunk != null && chunk.Blocks != null && chunk.IsGenerated)
                dirtyChunks.Add(chunk);
        }

        private static void GenerateDirtyChunks()
        {
            if (dirtyChunks.Count == 0)
                return;

            foreach (Chunk chunk in dirtyChunks)
            {
                if (chunk != null && chunk.Blocks != null && chunk.IsGenerated)
                    TerrainGenerator.MarkChunkMeshDirty(chunk);
            }

            dirtyChunks.Clear();
        }

        private struct FluidJobNode
        {
            public int3 Position;
            public int BlockId;
        }

        private struct FluidJobBlockSample
        {
            public int BlockId;
            public int RuntimeBlockId;
            public byte Exists;
            public byte Depth;
            public byte IsSource;
            public byte IsFalling;
            public byte IsFluid;
            public byte IsSolidSourceSupport;
        }

        private struct FluidJobResult
        {
            public int Action;
            public int3 TargetPosition;
            public byte TargetDepth;
            public byte DirectionMask;
            public byte NeedsRetry;
            public byte HasStateUpdate;
            public byte StateDepth;
            public byte StateIsSource;
            public byte StateIsFalling;
            public byte HasExpectedState;
            public byte ExpectedDepth;
            public byte ExpectedIsSource;
            public byte ExpectedIsFalling;
        }

        private struct FluidJobRuntimeState
        {
            public int BlockId;
            public int Depth;
            public int IsSource;
            public int IsFalling;

            public static FluidJobRuntimeState Source(int blockId)
            {
                return new FluidJobRuntimeState
                {
                    BlockId = blockId,
                    Depth = 0,
                    IsSource = 1,
                    IsFalling = 0
                };
            }

            public bool Matches(FluidJobRuntimeState other)
            {
                return BlockId == other.BlockId &&
                       Depth == other.Depth &&
                       IsSource == other.IsSource &&
                       IsFalling == other.IsFalling;
            }
        }

        [BurstCompile]
        private struct EvaluateFluidNodesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<FluidJobNode> Nodes;
            [ReadOnly] public NativeParallelHashMap<long, FluidJobBlockSample> BlockSamples;
            [ReadOnly] public int MaxWaterDepth;
            [ReadOnly] public int3 SimulationCenterChunk;
            [ReadOnly] public int SimulationChunkRangeSq;
            [ReadOnly] public int SimulationVerticalChunkRange;

            [WriteOnly] public NativeArray<FluidJobResult> Results;

            public void Execute(int index)
            {
                FluidJobNode node = Nodes[index];
                FluidJobResult result = default;

                if (!IsSupportedFluid(node.BlockId))
                {
                    Results[index] = result;
                    return;
                }

                if (!TryGetSample(node.Position, out FluidJobBlockSample currentSample))
                {
                    Results[index] = result;
                    return;
                }

                if (currentSample.Exists == 0)
                {
                    Results[index] = result;
                    return;
                }

                if (currentSample.BlockId != node.BlockId)
                {
                    result.Action = FluidJobActionRemoveRuntime;
                    Results[index] = result;
                    return;
                }

                FluidJobRuntimeState state = GetRuntimeState(currentSample, node.BlockId);
                WriteExpectedState(ref result, state);

                if (node.BlockId == Chunk.BLOCK_LAVA)
                {
                    if (IsTouchingWaterThatSolidifiesLava(node.Position))
                    {
                        result.Action = FluidJobActionSolidifySelf;
                        Results[index] = result;
                        return;
                    }
                }
                else if (node.BlockId == Chunk.BLOCK_WATER &&
                         TryFindLavaSolidifiedByWater(node.Position, out int3 lavaPosition))
                {
                    result.Action = FluidJobActionSolidifyTarget;
                    result.TargetPosition = lavaPosition;
                    Results[index] = result;
                    return;
                }

                bool stateChanged = false;

                if (state.IsSource == 0 && ShouldBecomeSource(node.Position, node.BlockId))
                {
                    state = FluidJobRuntimeState.Source(node.BlockId);
                    stateChanged = true;
                }

                if (state.IsSource == 0)
                {
                    if (!CanFlowingBlockRemain(node.Position, node.BlockId, state, out FluidJobRuntimeState updatedState))
                    {
                        result.Action = FluidJobActionClearSelf;
                        Results[index] = result;
                        return;
                    }

                    if (!state.Matches(updatedState))
                    {
                        state = updatedState;
                        stateChanged = true;
                    }
                }

                int3 belowPosition = node.Position + new int3(0, -1, 0);
                bool canFlowDown = CanFlowInto(
                    belowPosition,
                    node.BlockId,
                    0,
                    0,
                    1,
                    out bool missingDownTarget);

                if (canFlowDown)
                {
                    if (HasAtLeastHorizontalSourceNeighbors(node.Position, node.BlockId, 3))
                    {
                        // The rare vanilla exception spreads down and sideways in one
                        // tick. Reuse the exact managed path instead of duplicating its
                        // bounded slope search inside every parallel job sample.
                        result.Action = FluidJobActionFallback;
                        Results[index] = result;
                        return;
                    }

                    result.Action = FluidJobActionFlowDown;
                    result.TargetPosition = belowPosition;
                    result.TargetDepth = 0;
                    WriteStateUpdate(ref result, state, stateChanged);
                    Results[index] = result;
                    return;
                }

                if (ShouldSpreadHorizontally(node.Position, state, missingDownTarget))
                {
                    int horizontalDepth = state.Depth + 1;
                    if (horizontalDepth <= GetMaxDepth(node.BlockId))
                    {
                        int directDropMask = 0;
                        int validMask = 0;

                        for (int i = 0; i < 4; i++)
                        {
                            int3 direction = GetHorizontalDirection(i);
                            int3 targetPosition = node.Position + direction;
                            if (!CanFlowInto(
                                    targetPosition,
                                    node.BlockId,
                                    horizontalDepth,
                                    0,
                                    0,
                                    out bool missingTarget))
                            {
                                result.NeedsRetry |= missingTarget ? (byte)1 : (byte)0;
                                continue;
                            }

                            validMask |= 1 << i;
                            if (CanFlowToward(
                                    targetPosition,
                                    targetPosition + new int3(0, -1, 0),
                                    node.BlockId,
                                    0,
                                    0,
                                    1,
                                    out _))
                            {
                                directDropMask |= 1 << i;
                            }
                        }

                        if (directDropMask != 0)
                        {
                            result.Action = FluidJobActionFlowHorizontal;
                            result.DirectionMask = (byte)directDropMask;
                            result.TargetDepth = (byte)horizontalDepth;
                            WriteStateUpdate(ref result, state, stateChanged);
                            Results[index] = result;
                            return;
                        }

                        if (validMask != 0)
                        {
                            result.Action = FluidJobActionFallback;
                            Results[index] = result;
                            return;
                        }
                    }
                }

                if (missingDownTarget)
                    result.NeedsRetry = 1;

                WriteStateUpdate(ref result, state, stateChanged);

                if (result.NeedsRetry != 0)
                    result.Action = FluidJobActionRetry;

                Results[index] = result;
            }

            private void WriteStateUpdate(ref FluidJobResult result, FluidJobRuntimeState state, bool stateChanged)
            {
                if (!stateChanged)
                    return;

                result.HasStateUpdate = 1;
                result.StateDepth = (byte)state.Depth;
                result.StateIsSource = (byte)state.IsSource;
                result.StateIsFalling = (byte)state.IsFalling;
            }

            private static void WriteExpectedState(ref FluidJobResult result, FluidJobRuntimeState state)
            {
                result.HasExpectedState = 1;
                result.ExpectedDepth = (byte)state.Depth;
                result.ExpectedIsSource = (byte)state.IsSource;
                result.ExpectedIsFalling = (byte)state.IsFalling;
            }

            private bool ShouldSpreadHorizontally(
                int3 worldPosition,
                FluidJobRuntimeState state,
                bool missingDownTarget)
            {
                if (missingDownTarget)
                    return false;

                if (state.IsSource != 0)
                    return true;

                return HasSolidSupportBelow(worldPosition);
            }

            private bool HasSolidSupportBelow(int3 worldPosition)
            {
                if (!TryGetSample(worldPosition + new int3(0, -1, 0), out FluidJobBlockSample belowSample) ||
                    belowSample.Exists == 0)
                {
                    return false;
                }

                return belowSample.BlockId != Chunk.BLOCK_AIR && belowSample.IsFluid == 0;
            }

            private bool CanFlowInto(
                int3 targetPosition,
                int fluidBlockId,
                int depth,
                int isSource,
                int isFalling,
                out bool missingTarget)
            {
                missingTarget = false;

                if (!TryGetSample(targetPosition, out FluidJobBlockSample targetSample) ||
                    targetSample.Exists == 0)
                {
                    missingTarget = true;
                    return false;
                }

                if (targetSample.BlockId == Chunk.BLOCK_AIR)
                    return true;

                if (targetSample.BlockId == fluidBlockId)
                {
                    FluidJobRuntimeState currentState = GetRuntimeState(targetSample, fluidBlockId);
                    FluidJobRuntimeState newState = GetIncomingFluidState(targetPosition, fluidBlockId, depth, isSource, isFalling);
                    return CanReplaceFluidState(currentState, newState);
                }

                return CanSolidifyFluidCollision(fluidBlockId, targetSample.BlockId);
            }

            private bool CanFlowToward(
                int3 fromPosition,
                int3 targetPosition,
                int fluidBlockId,
                int depth,
                int isSource,
                int isFalling,
                out bool missingTarget)
            {
                if (CanFlowInto(
                        targetPosition,
                        fluidBlockId,
                        depth,
                        isSource,
                        isFalling,
                        out missingTarget))
                {
                    return true;
                }

                if (missingTarget)
                    return false;

                if (!TryGetSample(targetPosition, out FluidJobBlockSample targetSample) ||
                    targetSample.BlockId != fluidBlockId)
                {
                    return false;
                }

                FluidJobRuntimeState targetState = GetRuntimeState(targetSample, fluidBlockId);
                bool isDownward = targetPosition.y < fromPosition.y;
                if (isDownward)
                    return targetState.IsFalling != 0 || !HasSolidSupportBelow(targetPosition);

                if (targetState.IsSource != 0)
                    return false;

                return targetState.IsFalling != 0 || targetState.Depth >= depth;
            }

            private bool CanFlowingBlockRemain(
                int3 worldPosition,
                int fluidBlockId,
                FluidJobRuntimeState state,
                out FluidJobRuntimeState updatedState)
            {
                updatedState = state;

                int3 abovePosition = worldPosition + new int3(0, 1, 0);
                if (TryGetSample(abovePosition, out FluidJobBlockSample aboveSample) &&
                    aboveSample.BlockId == fluidBlockId)
                {
                    updatedState = new FluidJobRuntimeState
                    {
                        BlockId = fluidBlockId,
                        Depth = 0,
                        IsSource = 0,
                        IsFalling = 1
                    };
                    return true;
                }

                int bestParentDepth = int.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    int3 neighborPosition = worldPosition + GetHorizontalDirection(i);
                    if (!TryGetSample(neighborPosition, out FluidJobBlockSample neighborSample) ||
                        neighborSample.BlockId != fluidBlockId)
                    {
                        continue;
                    }

                    FluidJobRuntimeState neighborState = GetRuntimeState(neighborSample, fluidBlockId);
                    if (neighborState.IsSource != 0)
                    {
                        bestParentDepth = 0;
                        break;
                    }

                    if (neighborState.IsFalling != 0)
                    {
                        if (CanFallingFluidParentFeedHorizontalFlow(neighborPosition, fluidBlockId))
                            bestParentDepth = math.min(bestParentDepth, 0);

                        continue;
                    }

                    if (neighborState.Depth < state.Depth)
                        bestParentDepth = math.min(bestParentDepth, neighborState.Depth);
                }

                if (bestParentDepth == int.MaxValue)
                    return false;

                int expectedDepth = bestParentDepth + 1;
                if (expectedDepth > GetMaxDepth(fluidBlockId))
                    return false;

                updatedState = new FluidJobRuntimeState
                {
                    BlockId = fluidBlockId,
                    Depth = expectedDepth,
                    IsSource = 0,
                    IsFalling = 0
                };
                return true;
            }

            private bool CanFallingFluidParentFeedHorizontalFlow(int3 parentPosition, int fluidBlockId)
            {
                return !CanFlowInto(
                           parentPosition + new int3(0, -1, 0),
                           fluidBlockId,
                           0,
                           0,
                           1,
                           out _) &&
                       HasSolidSupportBelow(parentPosition);
            }

            private bool ShouldBecomeSource(int3 worldPosition, int fluidBlockId)
            {
                if (fluidBlockId != Chunk.BLOCK_WATER)
                    return false;

                if (!TryGetSample(worldPosition + new int3(0, -1, 0), out FluidJobBlockSample belowSample) ||
                    belowSample.Exists == 0)
                {
                    return false;
                }

                bool hasSourceSupport = belowSample.IsSolidSourceSupport != 0;
                if (!hasSourceSupport && belowSample.BlockId == fluidBlockId)
                {
                    FluidJobRuntimeState belowState = GetRuntimeState(belowSample, fluidBlockId);
                    hasSourceSupport = belowState.IsSource != 0;
                }

                return hasSourceSupport &&
                       HasAtLeastHorizontalSourceNeighbors(worldPosition, fluidBlockId, 2);
            }

            private bool HasAtLeastHorizontalSourceNeighbors(
                int3 worldPosition,
                int fluidBlockId,
                int requiredSources)
            {
                int sourceCount = 0;
                for (int i = 0; i < 4; i++)
                {
                    int3 neighborPosition = worldPosition + GetHorizontalDirection(i);
                    if (!TryGetSample(neighborPosition, out FluidJobBlockSample neighborSample) ||
                        neighborSample.BlockId != fluidBlockId)
                    {
                        continue;
                    }

                    FluidJobRuntimeState neighborState = GetRuntimeState(neighborSample, fluidBlockId);
                    if (neighborState.IsSource != 0)
                        sourceCount++;

                    if (sourceCount >= requiredSources)
                        return true;
                }

                return false;
            }

            private FluidJobRuntimeState GetIncomingFluidState(
                int3 targetPosition,
                int fluidBlockId,
                int depth,
                int isSource,
                int isFalling)
            {
                if ((isSource != 0 || ShouldBecomeSource(targetPosition, fluidBlockId)) &&
                    fluidBlockId == Chunk.BLOCK_WATER)
                {
                    return FluidJobRuntimeState.Source(fluidBlockId);
                }

                return new FluidJobRuntimeState
                {
                    BlockId = fluidBlockId,
                    Depth = math.clamp(depth, 0, GetMaxDepth(fluidBlockId)),
                    IsSource = isSource,
                    IsFalling = isFalling != 0 && isSource == 0 ? 1 : 0
                };
            }

            private bool CanReplaceFluidState(FluidJobRuntimeState currentState, FluidJobRuntimeState newState)
            {
                if (currentState.BlockId != newState.BlockId)
                    return true;

                if (currentState.IsSource != 0)
                    return false;

                if (newState.IsSource != 0)
                    return true;

                if (newState.Depth < currentState.Depth)
                    return true;

                return newState.IsFalling != 0 &&
                       currentState.IsFalling == 0 &&
                       newState.Depth <= currentState.Depth;
            }

            private FluidJobRuntimeState GetRuntimeState(FluidJobBlockSample sample, int fluidBlockId)
            {
                if (sample.RuntimeBlockId == fluidBlockId)
                {
                    return new FluidJobRuntimeState
                    {
                        BlockId = fluidBlockId,
                        Depth = sample.Depth,
                        IsSource = sample.IsSource,
                        IsFalling = sample.IsFalling
                    };
                }

                return FluidJobRuntimeState.Source(fluidBlockId);
            }

            private bool TryFindLavaSolidifiedByWater(int3 waterPosition, out int3 lavaPosition)
            {
                for (int i = 0; i < 5; i++)
                {
                    int3 neighborPosition = waterPosition + GetWaterSolidifyingLavaDirection(i);
                    if (TryGetSample(neighborPosition, out FluidJobBlockSample neighborSample) &&
                        neighborSample.BlockId == Chunk.BLOCK_LAVA)
                    {
                        lavaPosition = neighborPosition;
                        return true;
                    }
                }

                lavaPosition = default;
                return false;
            }

            private bool IsTouchingWaterThatSolidifiesLava(int3 lavaPosition)
            {
                for (int i = 0; i < 5; i++)
                {
                    int3 neighborPosition = lavaPosition + GetLavaSolidifyingWaterDirection(i);
                    if (TryGetSample(neighborPosition, out FluidJobBlockSample neighborSample) &&
                        neighborSample.BlockId == Chunk.BLOCK_WATER)
                    {
                        return true;
                    }
                }

                return false;
            }

            private bool TryGetSample(int3 worldPosition, out FluidJobBlockSample sample)
            {
                if (!IsWorldPositionInSimulationRange(worldPosition))
                {
                    sample = new FluidJobBlockSample
                    {
                        BlockId = FluidBoundaryBlockId,
                        Exists = 1
                    };
                    return true;
                }

                return BlockSamples.TryGetValue(
                    PackFluidPosition(worldPosition.x, worldPosition.y, worldPosition.z),
                    out sample);
            }

            private bool IsWorldPositionInSimulationRange(int3 worldPosition)
            {
                int chunkX = Chunk.CHUNK_SIZE == (1 << ChunkCoordinateShift)
                    ? worldPosition.x >> ChunkCoordinateShift
                    : FloorDiv(worldPosition.x, Chunk.CHUNK_SIZE);
                int chunkY = Chunk.CHUNK_HEIGHT == (1 << ChunkCoordinateShift)
                    ? worldPosition.y >> ChunkCoordinateShift
                    : FloorDiv(worldPosition.y, Chunk.CHUNK_HEIGHT);
                int chunkZ = Chunk.CHUNK_SIZE == (1 << ChunkCoordinateShift)
                    ? worldPosition.z >> ChunkCoordinateShift
                    : FloorDiv(worldPosition.z, Chunk.CHUNK_SIZE);

                int dx = chunkX - SimulationCenterChunk.x;
                int dz = chunkZ - SimulationCenterChunk.z;
                int dy = math.abs(chunkY - SimulationCenterChunk.y);

                return dx * dx + dz * dz <= SimulationChunkRangeSq &&
                       dy <= SimulationVerticalChunkRange;
            }

            private static int FloorDiv(int value, int divisor)
            {
                int quotient = value / divisor;
                int remainder = value % divisor;
                return remainder < 0 ? quotient - 1 : quotient;
            }

            private int GetMaxDepth(int blockId)
            {
                return blockId == Chunk.BLOCK_LAVA
                    ? MinecraftLavaOverworldMaxDepth
                    : MaxWaterDepth;
            }

            private static bool IsSupportedFluid(int blockId)
            {
                return blockId == Chunk.BLOCK_WATER || blockId == Chunk.BLOCK_LAVA;
            }

            private static bool CanSolidifyFluidCollision(int flowingFluidBlockId, int targetFluidBlockId)
            {
                return (flowingFluidBlockId == Chunk.BLOCK_WATER && targetFluidBlockId == Chunk.BLOCK_LAVA) ||
                       (flowingFluidBlockId == Chunk.BLOCK_LAVA && targetFluidBlockId == Chunk.BLOCK_WATER);
            }

            private static int3 GetHorizontalDirection(int index)
            {
                return index switch
                {
                    0 => new int3(0, 0, 1),
                    1 => new int3(0, 0, -1),
                    2 => new int3(-1, 0, 0),
                    _ => new int3(1, 0, 0),
                };
            }

            private static int3 GetLavaSolidifyingWaterDirection(int index)
            {
                return index == 0
                    ? new int3(0, 1, 0)
                    : GetHorizontalDirection(index - 1);
            }

            private static int3 GetWaterSolidifyingLavaDirection(int index)
            {
                return index == 0
                    ? new int3(0, -1, 0)
                    : GetHorizontalDirection(index - 1);
            }
        }

        private sealed class FluidNodeHeap
        {
            private readonly List<FluidNode> nodes;

            public FluidNodeHeap(int initialCapacity)
            {
                nodes = new List<FluidNode>(initialCapacity);
            }

            public int Count => nodes.Count;

            public void Clear()
            {
                nodes.Clear();
            }

            public void Enqueue(FluidNode node)
            {
                int index = nodes.Count;
                nodes.Add(node);

                while (index > 0)
                {
                    int parentIndex = (index - 1) >> 1;
                    FluidNode parent = nodes[parentIndex];
                    if (!ComesBefore(node, parent))
                        break;

                    nodes[index] = parent;
                    index = parentIndex;
                }

                nodes[index] = node;
            }

            public FluidNode Peek()
            {
                return nodes[0];
            }

            public FluidNode Dequeue()
            {
                FluidNode root = nodes[0];
                int lastIndex = nodes.Count - 1;
                FluidNode last = nodes[lastIndex];
                nodes.RemoveAt(lastIndex);

                if (lastIndex == 0)
                    return root;

                nodes[0] = last;
                SiftDown(0);
                return root;
            }

            public void RemoveOutsideSimulationRange()
            {
                int writeIndex = 0;
                for (int readIndex = 0; readIndex < nodes.Count; readIndex++)
                {
                    FluidNode node = nodes[readIndex];
                    if (!IsWorldPositionInSimulationRange(node.Position))
                        continue;

                    nodes[writeIndex++] = node;
                }

                if (writeIndex == nodes.Count)
                    return;

                nodes.RemoveRange(writeIndex, nodes.Count - writeIndex);
                for (int index = (nodes.Count >> 1) - 1; index >= 0; index--)
                    SiftDown(index);
            }

            private void SiftDown(int index)
            {
                int count = nodes.Count;
                FluidNode node = nodes[index];

                while (true)
                {
                    int leftIndex = (index << 1) + 1;
                    if (leftIndex >= count)
                        break;

                    int rightIndex = leftIndex + 1;
                    int firstIndex = rightIndex < count && ComesBefore(nodes[rightIndex], nodes[leftIndex])
                        ? rightIndex
                        : leftIndex;
                    FluidNode first = nodes[firstIndex];
                    if (!ComesBefore(first, node))
                        break;

                    nodes[index] = first;
                    index = firstIndex;
                }

                nodes[index] = node;
            }

            private static bool ComesBefore(FluidNode left, FluidNode right)
            {
                return left.ReadyTime < right.ReadyTime ||
                       (left.ReadyTime == right.ReadyTime && left.Sequence < right.Sequence);
            }
        }

        private readonly struct DropSearchNode
        {
            public readonly Vector3Int Position;
            public readonly int Distance;
            public readonly Vector3Int IncomingDirection;

            public DropSearchNode(Vector3Int position, int distance, Vector3Int incomingDirection)
            {
                Position = position;
                Distance = distance;
                IncomingDirection = incomingDirection;
            }
        }

        private readonly struct FluidNode
        {
            public readonly Vector3Int Position;
            public readonly int BlockId;
            public readonly float ReadyTime;
            public readonly long Sequence;

            public FluidNode(Vector3Int position, int blockId, float readyTime, long sequence)
            {
                Position = position;
                BlockId = blockId;
                ReadyTime = readyTime;
                Sequence = sequence;
            }
        }

        private readonly struct PendingFluidState
        {
            public readonly int BlockId;
            public readonly float ReadyTime;

            public PendingFluidState(int blockId, float readyTime)
            {
                BlockId = blockId;
                ReadyTime = readyTime;
            }
        }

        private readonly struct CachedBlockState
        {
            public readonly int BlockId;
            public readonly Chunk Chunk;

            public CachedBlockState(
                int blockId,
                Chunk chunk)
            {
                BlockId = blockId;
                Chunk = chunk;
            }
        }

        private readonly struct FluidRuntimeState
        {
            public readonly int BlockId;
            public readonly int Depth;
            public readonly bool IsSource;
            public readonly bool IsFalling;

            public FluidRuntimeState(int blockId, int depth, bool isSource, bool isFalling)
            {
                BlockId = blockId;
                Depth = depth;
                IsSource = isSource;
                IsFalling = isFalling;
            }

            public static FluidRuntimeState Source(int blockId)
            {
                return new FluidRuntimeState(blockId, 0, true, false);
            }

            public bool Matches(FluidRuntimeState other)
            {
                return BlockId == other.BlockId &&
                       Depth == other.Depth &&
                       IsSource == other.IsSource &&
                       IsFalling == other.IsFalling;
            }
        }
    }
}
