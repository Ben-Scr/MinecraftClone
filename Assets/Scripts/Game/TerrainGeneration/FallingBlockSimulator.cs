using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public static class FallingBlockSimulator
    {
        public const float MaxTntFuseSeconds = 3600f;
        public const float MaxTntDestructionRadius = 16f;
        public const int MaxTntDestroyedBlocks = 8192;
        public const float MaximumFallSpeed = 18f;

        public struct TntExplosionSettings
        {
            public float FuseSeconds;
            public float DestructionRadius;
            public int MaxDestroyedBlocks;
            public bool DestroyFluids;
            public bool DestroyIndestructibleBlocks;
            public bool DropDestroyedBlocks;
            public bool PrimeNearbyTnt;
            public float ChainedFuseSeconds;

            public readonly TntExplosionSettings Sanitized()
            {
                return new TntExplosionSettings
                {
                    FuseSeconds = Mathf.Clamp(FuseSeconds, 0.05f, MaxTntFuseSeconds),
                    DestructionRadius = Mathf.Clamp(
                        DestructionRadius,
                        0.1f,
                        MaxTntDestructionRadius),
                    MaxDestroyedBlocks = Mathf.Clamp(
                        MaxDestroyedBlocks,
                        1,
                        MaxTntDestroyedBlocks),
                    DestroyFluids = DestroyFluids,
                    DestroyIndestructibleBlocks = DestroyIndestructibleBlocks,
                    DropDestroyedBlocks = DropDestroyedBlocks,
                    PrimeNearbyTnt = PrimeNearbyTnt,
                    ChainedFuseSeconds = Mathf.Clamp(
                        ChainedFuseSeconds,
                        0.05f,
                        MaxTntFuseSeconds)
                };
            }
        }

        private static readonly Queue<Vector3Int> pendingBlocks = new(128);
        private static readonly HashSet<Vector3Int> pendingBlockSet = new();
        private static readonly HashSet<Vector3Int> deferredRecheckBlocks = new();
        private static readonly Dictionary<Vector3Int, HashSet<Vector3Int>> pendingChecksByChunk = new();
        private static readonly List<Vector3Int> deferredRecheckBuffer = new(16);
        private static readonly HashSet<Vector3Int> activeFallingBlocks = new();
        private static readonly List<FallingBlockEntity> activeFallingEntities = new(128);
        private static readonly List<FallingBlockEntity> activePrimedEntities = new(16);
        private static readonly List<FallingBlockEntity> simulationEntities = new(128);
        private static readonly HashSet<Chunk> dirtyChunks = new();
        private static readonly List<ExplosionCandidate> explosionCandidates = new(512);
        private static readonly ExplosionCandidateDistanceComparer explosionCandidateComparer = new();

        private const float Gravity = 28f;
        private const int SimulationBatchSize = 64;
        private const int ScheduledSimulationThreshold = 512;
        private const byte SweepStateUnavailable = 0;
        private const byte SweepStateClear = 1;
        private const byte SweepStateLanding = 2;

        private static bool isEnabled = true;
        private static float fallInterval = 0.1f;
        private static float tickTimer;
        private static int maxBlocksPerTick = 64;
        private static int simulationCapacity;
        private static bool simulationAreaConfigured;
        private static bool activeEntitiesNeedCompaction;
        private static bool primedEntitiesNeedCompaction;
        private static Vector3Int simulationCenterChunk;
        private static int simulationChunkRange = 3;
        private static int simulationChunkRangeSq = 9;

        private static NativeArray<float3> entityPositions;
        private static NativeArray<float> entityVelocities;
        private static NativeArray<float3> candidatePositions;
        private static NativeArray<float> candidateVelocities;
        private static NativeArray<int3> candidateCells;
        private static NativeArray<byte> sweepStates;
        private static NativeArray<byte> settleFlags;

        private readonly struct ExplosionCandidate
        {
            public readonly Vector3Int WorldPosition;
            public readonly float DistanceSquared;

            public ExplosionCandidate(Vector3Int worldPosition, float distanceSquared)
            {
                WorldPosition = worldPosition;
                DistanceSquared = distanceSquared;
            }
        }

        private sealed class ExplosionCandidateDistanceComparer : IComparer<ExplosionCandidate>
        {
            public int Compare(ExplosionCandidate first, ExplosionCandidate second)
            {
                return first.DistanceSquared.CompareTo(second.DistanceSquared);
            }
        }

        public static void Configure(
            bool simulateFallingBlocks,
            float fallingBlockTickInterval,
            int fallingBlocksPerTick,
            Vector3Int centerChunk,
            int chunkSimulationRange)
        {
            int sanitizedChunkRange = Mathf.Max(0, chunkSimulationRange);
            bool rangeChanged = !simulationAreaConfigured ||
                                simulationCenterChunk != centerChunk ||
                                simulationChunkRange != sanitizedChunkRange;

            isEnabled = simulateFallingBlocks;
            fallInterval = Mathf.Max(0.02f, fallingBlockTickInterval);
            maxBlocksPerTick = Mathf.Max(1, fallingBlocksPerTick);
            simulationAreaConfigured = true;
            simulationCenterChunk = centerChunk;
            simulationChunkRange = sanitizedChunkRange;
            simulationChunkRangeSq = simulationChunkRange * simulationChunkRange;

            if (rangeChanged)
                QueueDeferredRechecksInRange();
        }

        public static void Clear()
        {
            for (int i = activeFallingEntities.Count - 1; i >= 0; i--)
            {
                FallingBlockEntity entity = activeFallingEntities[i];
                if (entity != null)
                    entity.DestroyForSimulatorClear();
            }

            pendingBlocks.Clear();
            pendingBlockSet.Clear();
            deferredRecheckBlocks.Clear();
            pendingChecksByChunk.Clear();
            deferredRecheckBuffer.Clear();
            activeFallingBlocks.Clear();
            activeFallingEntities.Clear();
            activePrimedEntities.Clear();
            simulationEntities.Clear();
            dirtyChunks.Clear();
            explosionCandidates.Clear();
            tickTimer = 0f;
            simulationAreaConfigured = false;
            activeEntitiesNeedCompaction = false;
            primedEntitiesNeedCompaction = false;
            DisposeSimulationArrays();
        }

        public static void Update(float deltaTime)
        {
            if (isEnabled)
            {
                tickTimer += deltaTime;
                if (tickTimer >= fallInterval)
                {
                    tickTimer = 0f;
                    if (pendingBlocks.Count > 0)
                        ProcessBlocks();
                }
            }

            TickPrimedExplosives(deltaTime);
            SimulateActiveEntities(deltaTime);
            GenerateDirtyChunks();
        }

        public static bool HasPendingWorkInChunk(Vector3Int chunkCoordinate)
        {
            return pendingChecksByChunk.TryGetValue(chunkCoordinate, out HashSet<Vector3Int> checks) &&
                   checks.Count > 0;
        }

        public static void CopyPendingChecksInChunk(
            Vector3Int chunkCoordinate,
            HashSet<Vector3Int> destination)
        {
            if (destination == null ||
                !pendingChecksByChunk.TryGetValue(chunkCoordinate, out HashSet<Vector3Int> checks))
            {
                return;
            }

            destination.UnionWith(checks);
        }

        public static void CopyAllPendingChecks(HashSet<Vector3Int> destination)
        {
            if (destination == null)
                return;

            foreach (HashSet<Vector3Int> checks in pendingChecksByChunk.Values)
                destination.UnionWith(checks);
        }

        public static void RestorePendingCheck(Vector3Int worldPosition)
        {
            if (IsWorldPositionInSimulationRange(worldPosition))
                QueueBlock(worldPosition);
            else
                AddDeferredRecheck(worldPosition);
        }

        public static void ReleaseChunk(Chunk chunk)
        {
            if (chunk == null)
                return;

            dirtyChunks.Remove(chunk);
            Vector3Int chunkCoordinate = chunk.Coordinate;
            if (!pendingChecksByChunk.TryGetValue(
                    chunkCoordinate,
                    out HashSet<Vector3Int> checks))
            {
                return;
            }

            // Queue entries are cheap tombstones. ProcessBlocks discards them when
            // their set entry is absent, avoiding an O(n) Queue rebuild on eviction.
            foreach (Vector3Int worldPosition in checks)
            {
                pendingBlockSet.Remove(worldPosition);
                deferredRecheckBlocks.Remove(worldPosition);
            }

            pendingChecksByChunk.Remove(chunkCoordinate);
        }

        public static List<SaveController.FallingBlockSaveData> CaptureActiveEntities()
        {
            var result = new List<SaveController.FallingBlockSaveData>(activeFallingEntities.Count);
            for (int i = 0; i < activeFallingEntities.Count; i++)
            {
                FallingBlockEntity entity = activeFallingEntities[i];
                SaveController.FallingBlockSaveData state = entity?.CreateSaveData();
                if (state != null && state.IsValid)
                    result.Add(state);
            }

            return result;
        }

        public static void RestoreActiveEntities(
            IReadOnlyList<SaveController.FallingBlockSaveData> savedEntities)
        {
            if (savedEntities == null)
                return;

            for (int i = 0; i < savedEntities.Count; i++)
            {
                SaveController.FallingBlockSaveData state = savedEntities[i];
                if (IsSavedEntityAreaReady(state))
                    TryRestoreActiveEntity(state);
            }
        }

        public static bool IsSavedEntityAreaReady(SaveController.FallingBlockSaveData state)
        {
            if (state == null || !state.IsValid)
                return true;

            AssetsContainer assets = AssetsContainer.Instance;
            if (assets?.Blocks == null)
                return false;

            if (state.BlockId <= Chunk.BLOCK_AIR || state.BlockId >= assets.Blocks.Length)
                return true;

            BlockData block = assets.Blocks[state.BlockId];
            bool validDefinition = block != null &&
                (state.IsPrimedExplosive
                    ? state.BlockId == Chunk.BLOCK_TNT
                    : IsFallingBlock(state.BlockId));
            if (!validDefinition)
                return true;

            Vector3Int minimumBlock;
            Vector3Int maximumBlock;
            if (state.IsPrimedExplosive)
            {
                int radius = Mathf.CeilToInt(Mathf.Clamp(
                    state.TntDestructionRadius,
                    0.1f,
                    MaxTntDestructionRadius));
                Vector3Int centerBlock = ChunkUtility.SnapPosition(state.Position);
                Vector3Int extent = new Vector3Int(radius, radius, radius);
                minimumBlock = centerBlock - extent;
                maximumBlock = centerBlock + extent;
            }
            else
            {
                Vector3Int currentBlock = Vector3Int.FloorToInt(state.Position);
                minimumBlock = currentBlock + Vector3Int.down;
                maximumBlock = currentBlock;
            }

            Vector3Int minimumChunk = GetChunkCoordinateFromBlockPosition(minimumBlock);
            Vector3Int maximumChunk = GetChunkCoordinateFromBlockPosition(maximumBlock);
            for (int x = minimumChunk.x; x <= maximumChunk.x; x++)
            {
                for (int y = minimumChunk.y; y <= maximumChunk.y; y++)
                {
                    for (int z = minimumChunk.z; z <= maximumChunk.z; z++)
                    {
                        Vector3Int coordinate = new Vector3Int(x, y, z);
                        if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk) ||
                            chunk?.Blocks == null ||
                            !chunk.IsGenerated)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        public static bool TryRestoreActiveEntity(SaveController.FallingBlockSaveData state)
        {
            if (state == null || !state.IsValid)
                return false;

            Vector3Int startWorldPosition = state.StartWorldPosition;
            if (!activeFallingBlocks.Add(startWorldPosition))
                return false;

            try
            {
                if (FallingBlockEntity.Restore(state) != null)
                    return true;

                activeFallingBlocks.Remove(startWorldPosition);
                return false;
            }
            catch (System.Exception exception)
            {
                activeFallingBlocks.Remove(startWorldPosition);
                Debug.LogException(exception);
                return false;
            }
        }

        public static bool IsChunkRequiredByActiveEntity(Vector3Int chunkCoordinate)
        {
            for (int i = 0; i < activeFallingEntities.Count; i++)
            {
                FallingBlockEntity entity = activeFallingEntities[i];
                if (entity == null)
                    continue;

                Vector3Int entityChunk = GetChunkCoordinateFromBlockPosition(
                    Vector3Int.FloorToInt(entity.Position));
                Vector3Int delta = entityChunk - chunkCoordinate;
                if (Mathf.Abs(delta.x) <= 1 &&
                    Mathf.Abs(delta.y) <= 1 &&
                    Mathf.Abs(delta.z) <= 1)
                {
                    return true;
                }
            }

            return false;
        }

        public static void NotifyBlockChanged(Vector3Int worldPosition, int oldBlockId, int newBlockId)
        {
            if (IsFallingBlock(newBlockId))
                QueueBlock(worldPosition);

            if (IsFallThroughBlock(newBlockId))
                QueueBlock(worldPosition + Vector3Int.up);

            if (IsFallingBlock(oldBlockId) &&
                newBlockId != oldBlockId &&
                !IsFallingBlock(newBlockId))
            {
                RemovePendingBlock(worldPosition);
                RemoveDeferredRecheck(worldPosition);
            }
        }

        public static void OnEntityDisposed(Vector3Int startWorldPosition)
        {
            if (!activeFallingBlocks.Remove(startWorldPosition))
                return;

            if (IsWorldPositionInSimulationRange(startWorldPosition))
                QueueBlock(startWorldPosition);
            else
                AddDeferredRecheck(startWorldPosition);
        }

        public static void RegisterEntity(FallingBlockEntity entity)
        {
            if (entity == null || entity.SimulatorIndex >= 0)
                return;

            entity.SimulatorIndex = activeFallingEntities.Count;
            activeFallingEntities.Add(entity);

            if (!entity.IsPrimedExplosive)
                return;

            entity.PrimedSimulatorIndex = activePrimedEntities.Count;
            activePrimedEntities.Add(entity);
        }

        public static void UnregisterEntity(FallingBlockEntity entity)
        {
            if (entity == null)
                return;

            int index = entity.SimulatorIndex;
            if ((uint)index < (uint)activeFallingEntities.Count &&
                ReferenceEquals(activeFallingEntities[index], entity))
            {
                activeFallingEntities[index] = null;
            }

            entity.SimulatorIndex = -1;
            activeEntitiesNeedCompaction = true;

            index = entity.PrimedSimulatorIndex;
            if ((uint)index < (uint)activePrimedEntities.Count &&
                ReferenceEquals(activePrimedEntities[index], entity))
            {
                activePrimedEntities[index] = null;
                primedEntitiesNeedCompaction = true;
            }

            entity.PrimedSimulatorIndex = -1;
        }

        public static bool IsWorldPositionBlockedByFallingEntity(Vector3Int worldPosition)
        {
            for (int i = activeFallingEntities.Count - 1; i >= 0; i--)
            {
                FallingBlockEntity entity = activeFallingEntities[i];
                if (entity == null || entity.IsSettling)
                    continue;

                if (entity.OccupiesWorldPosition(worldPosition))
                    return true;
            }

            return false;
        }

        public static bool TryPrimeTntBlock(Vector3Int worldPosition, TntExplosionSettings settings)
        {
            settings = settings.Sanitized();

            if (!IsWorldPositionInSimulationRange(worldPosition) ||
                activeFallingBlocks.Contains(worldPosition))
            {
                return false;
            }

            if (!TryGetBlockState(
                    worldPosition,
                    out int blockId,
                    out Chunk sourceChunk,
                    out Vector3Int sourceLocalPosition) ||
                blockId != Chunk.BLOCK_TNT)
            {
                return false;
            }

            BlockData block = AssetsContainer.GetBlock(blockId);
            if (block == null)
                return false;

            PlacedBlockData placedBlockData =
                CapturePlacedBlockData(sourceChunk, sourceLocalPosition, blockId, block);

            ClearBlockViewState(sourceChunk, sourceLocalPosition);
            sourceChunk.SetBlockRaw(sourceLocalPosition, Chunk.BLOCK_AIR);
            MarkDirty(sourceChunk, sourceLocalPosition);

            activeFallingBlocks.Add(worldPosition);
            FluidSimulator.NotifyBlockChanged(worldPosition, blockId, Chunk.BLOCK_AIR);
            NotifyBlockChanged(worldPosition, blockId, Chunk.BLOCK_AIR);

            FallingBlockEntity.SpawnPrimedExplosive(
                worldPosition,
                blockId,
                block,
                placedBlockData,
                settings);

            return true;
        }

        public static bool TryPlaceSettledBlock(
            Vector3Int preferredWorldPosition,
            int blockId,
            out Vector3Int placedWorldPosition)
        {
            return TryPlaceSettledBlock(preferredWorldPosition, blockId, null, out placedWorldPosition);
        }

        public static bool TryPlaceSettledBlock(
            Vector3Int preferredWorldPosition,
            int blockId,
            PlacedBlockData placedBlockData,
            out Vector3Int placedWorldPosition)
        {
            placedWorldPosition = preferredWorldPosition;

            if (!IsFallingBlock(blockId))
                return false;

            Vector3Int candidate = preferredWorldPosition;
            for (int i = 0; i < 4; i++)
            {
                if (TryPlaceBlockAt(candidate, blockId, placedBlockData, out int replacedBlockId))
                {
                    placedWorldPosition = candidate;
                    FluidSimulator.NotifyBlockChanged(candidate, replacedBlockId, blockId);
                    NotifyBlockChanged(candidate, replacedBlockId, blockId);
                    return true;
                }

                candidate += Vector3Int.up;
            }

            return false;
        }

        private static void ProcessBlocks()
        {
            int processed = 0;
            int processBudget = pendingBlocks.Count;

            while (pendingBlocks.Count > 0 && processed < processBudget && processed < maxBlocksPerTick)
            {
                Vector3Int worldPosition = pendingBlocks.Dequeue();
                if (!RemovePendingBlock(worldPosition))
                {
                    processed++;
                    continue;
                }

                if (!IsWorldPositionInSimulationRange(worldPosition))
                {
                    AddDeferredRecheck(worldPosition);
                    processed++;
                    continue;
                }

                if (!TrySpawnFallingEntity(worldPosition, out bool retryWhenChunksReady) && retryWhenChunksReady)
                    QueueBlock(worldPosition);

                processed++;
            }
        }

        private static void TickPrimedExplosives(float deltaTime)
        {
            if (activePrimedEntities.Count == 0)
                return;

            CompactPrimedEntities();
            for (int i = activePrimedEntities.Count - 1; i >= 0; i--)
            {
                FallingBlockEntity entity = activePrimedEntities[i];
                if (entity == null || entity.IsSettling)
                    continue;

                entity.TickPrimedExplosive(deltaTime);
            }
        }

        private static void SimulateActiveEntities(float deltaTime)
        {
            if (activeFallingEntities.Count == 0)
                return;

            CompactActiveEntities();

            if (activeFallingEntities.Count == 0)
                return;

            simulationEntities.Clear();
            for (int i = 0; i < activeFallingEntities.Count; i++)
            {
                FallingBlockEntity entity = activeFallingEntities[i];
                if (entity != null &&
                    (isEnabled || entity.SimulatesWhenFallingBlocksDisabled))
                {
                    simulationEntities.Add(entity);
                }
            }

            int count = simulationEntities.Count;
            if (count == 0)
                return;

            EnsureSimulationCapacity(count);

            for (int i = 0; i < count; i++)
            {
                FallingBlockEntity entity = simulationEntities[i];
                entityPositions[i] = ToFloat3(entity.Position);
                entityVelocities[i] = entity.VerticalVelocity;
            }

            IntegrateFallingBlocksJob integrateJob = new IntegrateFallingBlocksJob
            {
                DeltaTime = deltaTime,
                Positions = entityPositions,
                Velocities = entityVelocities,
                CandidatePositions = candidatePositions,
                CandidateVelocities = candidateVelocities,
                CandidateCells = candidateCells
            };

            if (count >= ScheduledSimulationThreshold)
            {
                integrateJob.Schedule(count, SimulationBatchSize).Complete();
            }
            else
            {
                for (int i = 0; i < count; i++)
                    integrateJob.Execute(i);
            }

            for (int i = 0; i < count; i++)
            {
                int3 candidateCell = candidateCells[i];
                byte sweepState = ReadVerticalSweep(
                    entityPositions[i],
                    candidatePositions[i],
                    candidateCell,
                    out int landingCellY);
                sweepStates[i] = sweepState;

                if (sweepState == SweepStateLanding)
                    candidateCells[i] = new int3(candidateCell.x, landingCellY, candidateCell.z);
            }

            ResolveFallingBlocksJob resolveJob = new ResolveFallingBlocksJob
            {
                OriginalPositions = entityPositions,
                Positions = candidatePositions,
                Velocities = candidateVelocities,
                Cells = candidateCells,
                SweepStates = sweepStates,
                SettleFlags = settleFlags
            };

            if (count >= ScheduledSimulationThreshold)
            {
                resolveJob.Schedule(count, SimulationBatchSize).Complete();
            }
            else
            {
                for (int i = 0; i < count; i++)
                    resolveJob.Execute(i);
            }

            for (int i = count - 1; i >= 0; i--)
            {
                FallingBlockEntity entity = simulationEntities[i];
                if (entity == null || entity.IsSettling)
                    continue;

                bool shouldSettle = settleFlags[i] != 0;
                float velocity = shouldSettle && entity.IsPrimedExplosive
                    ? 0f
                    : candidateVelocities[i];
                entity.ApplySimulation(ToVector3(candidatePositions[i]), velocity);

                if (!shouldSettle || entity.IsPrimedExplosive)
                    continue;

                int3 targetCell = candidateCells[i];
                Vector3Int targetWorldPosition = new Vector3Int(targetCell.x, targetCell.y, targetCell.z);
                entity.SettleFromSimulation(targetWorldPosition);
            }
        }

        private static void CompactActiveEntities()
        {
            if (!activeEntitiesNeedCompaction)
                return;

            int writeIndex = 0;
            int count = activeFallingEntities.Count;
            for (int readIndex = 0; readIndex < count; readIndex++)
            {
                FallingBlockEntity entity = activeFallingEntities[readIndex];
                if (entity == null || entity.IsSettling)
                {
                    if (entity != null)
                        entity.SimulatorIndex = -1;
                    continue;
                }

                if (writeIndex != readIndex)
                    activeFallingEntities[writeIndex] = entity;

                entity.SimulatorIndex = writeIndex;
                writeIndex++;
            }

            if (writeIndex < count)
                activeFallingEntities.RemoveRange(writeIndex, count - writeIndex);

            activeEntitiesNeedCompaction = false;
        }

        private static void CompactPrimedEntities()
        {
            if (!primedEntitiesNeedCompaction)
                return;

            int writeIndex = 0;
            int count = activePrimedEntities.Count;
            for (int readIndex = 0; readIndex < count; readIndex++)
            {
                FallingBlockEntity entity = activePrimedEntities[readIndex];
                if (entity == null || entity.IsSettling || !entity.IsPrimedExplosive)
                {
                    if (entity != null)
                        entity.PrimedSimulatorIndex = -1;
                    continue;
                }

                if (writeIndex != readIndex)
                    activePrimedEntities[writeIndex] = entity;

                entity.PrimedSimulatorIndex = writeIndex;
                writeIndex++;
            }

            if (writeIndex < count)
                activePrimedEntities.RemoveRange(writeIndex, count - writeIndex);

            primedEntitiesNeedCompaction = false;
        }

        private static bool TrySpawnFallingEntity(Vector3Int worldPosition, out bool retryWhenChunksReady)
        {
            retryWhenChunksReady = false;

            if (!IsWorldPositionInSimulationRange(worldPosition))
                return false;

            if (activeFallingBlocks.Contains(worldPosition))
                return false;

            if (!TryGetBlockState(
                    worldPosition,
                    out int blockId,
                    out Chunk sourceChunk,
                    out Vector3Int sourceLocalPosition))
            {
                retryWhenChunksReady = true;
                return false;
            }

            if (!IsFallingBlock(blockId))
                return false;

            if (!TryGetBlockBelow(sourceChunk, sourceLocalPosition, out int belowBlockId))
            {
                retryWhenChunksReady = true;
                return false;
            }

            if (!IsFallThroughBlock(belowBlockId))
                return false;

            BlockData block = AssetsContainer.GetBlock(blockId);
            if (block == null)
                return false;

            PlacedBlockData placedBlockData = CapturePlacedBlockData(sourceChunk, sourceLocalPosition, blockId, block);

            ClearBlockViewState(sourceChunk, sourceLocalPosition);
            sourceChunk.SetBlockRaw(sourceLocalPosition, Chunk.BLOCK_AIR);

            MarkDirty(sourceChunk, sourceLocalPosition);
            activeFallingBlocks.Add(worldPosition);

            FluidSimulator.NotifyBlockChanged(worldPosition, blockId, Chunk.BLOCK_AIR);
            QueueBlock(worldPosition + Vector3Int.up);
            FallingBlockEntity.Spawn(worldPosition, blockId, block, placedBlockData);
            return true;
        }

        private static bool TryPlaceBlockAt(
            Vector3Int worldPosition,
            int blockId,
            PlacedBlockData placedBlockData,
            out int replacedBlockId)
        {
            replacedBlockId = Chunk.BLOCK_AIR;

            if (!TryGetBlockState(worldPosition, out int targetBlockId, out Chunk chunk, out Vector3Int localPosition) ||
                !IsFallThroughBlock(targetBlockId))
            {
                return false;
            }

            replacedBlockId = targetBlockId;
            chunk.SetBlockRaw(localPosition, blockId);

            BlockData block = AssetsContainer.GetBlock(blockId);
            if (block != null && block.UsesCustomModel)
            {
                int rotationY = placedBlockData != null ? placedBlockData.RotationY : 0;
                PlacedBlockManager.PlaceOrUpdate(chunk, localPosition, blockId, rotationY);
            }
            else
            {
                PlacedBlockManager.RemoveAt(chunk, localPosition);
            }

            MarkDirty(chunk, localPosition);
            return true;
        }

        internal static void ExplodePrimedTnt(Vector3 explosionCenter, TntExplosionSettings settings)
        {
            settings = settings.Sanitized();

            int radius = Mathf.CeilToInt(settings.DestructionRadius);
            float radiusSquared = settings.DestructionRadius * settings.DestructionRadius;
            Vector3Int centerBlock = ChunkUtility.SnapPosition(explosionCenter);
            BuildExplosionCandidates(centerBlock, explosionCenter, radius, radiusSquared);
            explosionCandidates.Sort(explosionCandidateComparer);

            int destroyedBlocks = 0;
            for (int i = 0; i < explosionCandidates.Count; i++)
            {
                if (destroyedBlocks >= settings.MaxDestroyedBlocks)
                    break;

                Vector3Int worldPosition = explosionCandidates[i].WorldPosition;
                if (!TryGetBlockState(
                        worldPosition,
                        out int blockId,
                        out Chunk chunk,
                        out Vector3Int localPosition) ||
                    blockId == Chunk.BLOCK_AIR)
                {
                    continue;
                }

                BlockData block = AssetsContainer.GetBlock(blockId);
                if (!CanDestroyBlockByExplosion(block, settings))
                    continue;

                if (blockId == Chunk.BLOCK_TNT &&
                    settings.PrimeNearbyTnt &&
                    TryPrimeChainedTnt(worldPosition, settings))
                {
                    continue;
                }

                if (DestroyBlockByExplosion(
                        worldPosition,
                        localPosition,
                        chunk,
                        blockId,
                        block,
                        settings.DropDestroyedBlocks))
                {
                    destroyedBlocks++;
                }
            }
        }

        private static void BuildExplosionCandidates(
            Vector3Int centerBlock,
            Vector3 explosionCenter,
            int radius,
            float radiusSquared)
        {
            explosionCandidates.Clear();

            for (int x = centerBlock.x - radius; x <= centerBlock.x + radius; x++)
            {
                for (int y = centerBlock.y - radius; y <= centerBlock.y + radius; y++)
                {
                    for (int z = centerBlock.z - radius; z <= centerBlock.z + radius; z++)
                    {
                        Vector3Int worldPosition = new Vector3Int(x, y, z);
                        Vector3 blockCenter = (Vector3)worldPosition + Vector3.one * 0.5f;
                        float distanceSquared = (blockCenter - explosionCenter).sqrMagnitude;
                        if (distanceSquared <= radiusSquared)
                            explosionCandidates.Add(new ExplosionCandidate(worldPosition, distanceSquared));
                    }
                }
            }
        }

        private static bool TryPrimeChainedTnt(Vector3Int worldPosition, TntExplosionSettings settings)
        {
            TntExplosionSettings chainedSettings = settings.Sanitized();
            chainedSettings.FuseSeconds = chainedSettings.ChainedFuseSeconds;
            return TryPrimeTntBlock(worldPosition, chainedSettings);
        }

        private static bool DestroyBlockByExplosion(
            Vector3Int worldPosition,
            Vector3Int localPosition,
            Chunk chunk,
            int blockId,
            BlockData block,
            bool dropDestroyedBlock)
        {
            if (chunk == null || block == null)
                return false;

            ClearBlockViewState(chunk, localPosition);
            if (!chunk.SetBlockRaw(localPosition, Chunk.BLOCK_AIR))
                return false;

            MarkDirty(chunk, localPosition);
            FluidSimulator.NotifyBlockChanged(worldPosition, blockId, Chunk.BLOCK_AIR);
            NotifyBlockChanged(worldPosition, blockId, Chunk.BLOCK_AIR);

            if (dropDestroyedBlock && block.ItemData != null)
            {
                Vector3 dropPosition = (Vector3)worldPosition + Vector3.one * 0.5f;
                DroppedItemManager.TryDropAt(
                    block.ItemData,
                    1,
                    block.ItemData.MaxDuration,
                    dropPosition);
            }

            return true;
        }

        private static bool CanDestroyBlockByExplosion(BlockData block, TntExplosionSettings settings)
        {
            if (block == null)
                return false;

            if (block.IsIndestructible && !settings.DestroyIndestructibleBlocks)
                return false;

            if (block.IsFluid && !settings.DestroyFluids)
                return false;

            return true;
        }

        private static PlacedBlockData CapturePlacedBlockData(
            Chunk chunk,
            Vector3Int localPosition,
            int blockId,
            BlockData block)
        {
            if (block == null || !block.UsesCustomModel)
                return null;

            if (PlacedBlockManager.TryGetDataAt(chunk, localPosition, out PlacedBlockData placedBlockData))
                return placedBlockData;

            return new PlacedBlockData
            {
                BlockId = blockId,
                LocalPosition = localPosition,
                RotationY = 0
            };
        }

        private static void QueueBlock(Vector3Int worldPosition)
        {
            if (!IsWorldPositionInSimulationRange(worldPosition))
            {
                AddDeferredRecheck(worldPosition);
                return;
            }

            if (AddPendingBlock(worldPosition))
                pendingBlocks.Enqueue(worldPosition);
        }

        private static bool AddPendingBlock(Vector3Int worldPosition)
        {
            if (!pendingBlockSet.Add(worldPosition))
                return false;

            IndexPendingCheck(worldPosition);
            return true;
        }

        private static bool RemovePendingBlock(Vector3Int worldPosition)
        {
            if (!pendingBlockSet.Remove(worldPosition))
                return false;

            UnindexPendingCheckIfUnused(worldPosition);
            return true;
        }

        private static void AddDeferredRecheck(Vector3Int worldPosition)
        {
            if (!deferredRecheckBlocks.Add(worldPosition))
                return;

            IndexPendingCheck(worldPosition);
        }

        private static void RemoveDeferredRecheck(Vector3Int worldPosition)
        {
            if (!deferredRecheckBlocks.Remove(worldPosition))
                return;

            UnindexPendingCheckIfUnused(worldPosition);
        }

        private static void IndexPendingCheck(Vector3Int worldPosition)
        {
            Vector3Int chunkCoordinate = GetChunkCoordinateFromBlockPosition(worldPosition);
            if (!pendingChecksByChunk.TryGetValue(
                    chunkCoordinate,
                    out HashSet<Vector3Int> checks))
            {
                checks = new HashSet<Vector3Int>();
                pendingChecksByChunk.Add(chunkCoordinate, checks);
            }

            checks.Add(worldPosition);
        }

        private static void UnindexPendingCheckIfUnused(Vector3Int worldPosition)
        {
            if (pendingBlockSet.Contains(worldPosition) ||
                deferredRecheckBlocks.Contains(worldPosition))
            {
                return;
            }

            Vector3Int chunkCoordinate = GetChunkCoordinateFromBlockPosition(worldPosition);
            if (!pendingChecksByChunk.TryGetValue(
                    chunkCoordinate,
                    out HashSet<Vector3Int> checks))
            {
                return;
            }

            checks.Remove(worldPosition);
            if (checks.Count == 0)
                pendingChecksByChunk.Remove(chunkCoordinate);
        }

        private static void QueueDeferredRechecksInRange()
        {
            if (deferredRecheckBlocks.Count == 0)
                return;

            deferredRecheckBuffer.Clear();
            foreach (Vector3Int worldPosition in deferredRecheckBlocks)
            {
                if (IsWorldPositionInSimulationRange(worldPosition))
                    deferredRecheckBuffer.Add(worldPosition);
            }

            for (int i = 0; i < deferredRecheckBuffer.Count; i++)
            {
                Vector3Int worldPosition = deferredRecheckBuffer[i];
                RemoveDeferredRecheck(worldPosition);
                QueueBlock(worldPosition);
            }

            deferredRecheckBuffer.Clear();
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
            int dy = chunkCoordinate.y - simulationCenterChunk.y;

            return dx * dx + dz * dz <= simulationChunkRangeSq &&
                   dy * dy <= simulationChunkRangeSq;
        }

        private static bool CanFallFrom(Vector3Int worldPosition)
        {
            if (!TryGetBlockState(
                    worldPosition,
                    out int blockId,
                    out Chunk chunk,
                    out Vector3Int localPosition) ||
                !IsFallingBlock(blockId))
            {
                return false;
            }

            return TryGetBlockBelow(chunk, localPosition, out int belowBlockId) &&
                   IsFallThroughBlock(belowBlockId);
        }

        public static bool IsFallingBlock(int blockId)
        {
            if (AssetsContainer.Instance == null ||
                blockId <= Chunk.BLOCK_AIR ||
                blockId >= AssetsContainer.Instance.Blocks.Length)
            {
                return false;
            }

            BlockData block = AssetsContainer.Instance.Blocks[blockId];
            return block != null && !block.IsFluid && block.FallsWhenUnsupported;
        }

        private static bool IsFallThroughBlock(int blockId)
        {
            return blockId == Chunk.BLOCK_AIR || IsFluidBlock(blockId);
        }

        private static bool IsFluidBlock(int blockId)
        {
            if (blockId == Chunk.BLOCK_WATER || blockId == Chunk.BLOCK_LAVA)
                return true;

            if (AssetsContainer.Instance == null ||
                blockId <= Chunk.BLOCK_AIR ||
                blockId >= AssetsContainer.Instance.Blocks.Length)
            {
                return false;
            }

            BlockData block = AssetsContainer.Instance.Blocks[blockId];
            return block != null && block.IsFluid;
        }

        public static bool TryGetBlockState(
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
                return false;
            }

            localPosition = new Vector3Int(
                worldPosition.x - chunkCoordinate.x * Chunk.CHUNK_SIZE,
                worldPosition.y - chunkCoordinate.y * Chunk.CHUNK_HEIGHT,
                worldPosition.z - chunkCoordinate.z * Chunk.CHUNK_SIZE);

            if (!ChunkUtility.IsInsideChunk(localPosition))
                return false;

            blockId = chunk.Blocks[localPosition.x, localPosition.y, localPosition.z];
            return true;
        }

        private static bool TryGetBlockBelow(
            Chunk sourceChunk,
            Vector3Int sourceLocalPosition,
            out int blockId)
        {
            blockId = Chunk.BLOCK_AIR;
            if (sourceChunk == null || sourceChunk.Blocks == null || !sourceChunk.IsGenerated)
                return false;

            if (sourceLocalPosition.y > 0)
            {
                blockId = sourceChunk.Blocks[
                    sourceLocalPosition.x,
                    sourceLocalPosition.y - 1,
                    sourceLocalPosition.z];
                return true;
            }

            Vector3Int belowCoordinate = sourceChunk.Coordinate + Vector3Int.down;
            if (!TerrainGenerator.Chunks.TryGetValue(belowCoordinate, out Chunk belowChunk) ||
                belowChunk.Blocks == null ||
                !belowChunk.IsGenerated)
            {
                return false;
            }

            blockId = belowChunk.Blocks[
                sourceLocalPosition.x,
                Chunk.CHUNK_HEIGHT - 1,
                sourceLocalPosition.z];
            return true;
        }

        private static byte ReadVerticalSweep(
            float3 originalPosition,
            float3 candidatePosition,
            int3 candidateCell,
            out int landingCellY)
        {
            landingCellY = candidateCell.y;

            int chunkX = FloorDiv(candidateCell.x, Chunk.CHUNK_SIZE);
            int chunkZ = FloorDiv(candidateCell.z, Chunk.CHUNK_SIZE);
            int localX = candidateCell.x - chunkX * Chunk.CHUNK_SIZE;
            int localZ = candidateCell.z - chunkZ * Chunk.CHUNK_SIZE;
            int originalCellY = (int)math.floor(originalPosition.y);
            if (originalCellY < candidateCell.y)
                originalCellY = candidateCell.y;

            int minimumCellY = candidateCell.y == int.MinValue
                ? int.MinValue
                : candidateCell.y - 1;
            int cachedChunkY = int.MinValue;
            Chunk cachedChunk = null;

            for (int worldY = originalCellY; ; worldY--)
            {
                int chunkY = FloorDiv(worldY, Chunk.CHUNK_HEIGHT);
                if (chunkY != cachedChunkY)
                {
                    cachedChunkY = chunkY;
                    Vector3Int chunkCoordinate = new Vector3Int(chunkX, chunkY, chunkZ);
                    if (!TerrainGenerator.Chunks.TryGetValue(chunkCoordinate, out cachedChunk) ||
                        cachedChunk.Blocks == null ||
                        !cachedChunk.IsGenerated)
                    {
                        return SweepStateUnavailable;
                    }
                }

                int localY = worldY - chunkY * Chunk.CHUNK_HEIGHT;
                int blockId = cachedChunk.Blocks[localX, localY, localZ];
                if (!IsFallThroughBlock(blockId))
                {
                    landingCellY = worldY + 1;
                    float landingCenterY = landingCellY + 0.5f;
                    return candidatePosition.y <= landingCenterY
                        ? SweepStateLanding
                        : SweepStateClear;
                }

                if (worldY == minimumCellY)
                    return SweepStateClear;
            }
        }

        private static Vector3Int GetChunkCoordinateFromBlockPosition(Vector3Int worldPosition)
        {
            return new Vector3Int(
                FloorDiv(worldPosition.x, Chunk.CHUNK_SIZE),
                FloorDiv(worldPosition.y, Chunk.CHUNK_HEIGHT),
                FloorDiv(worldPosition.z, Chunk.CHUNK_SIZE));
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }

        private static void ClearBlockViewState(Chunk chunk, Vector3Int localPosition)
        {
            PlacedBlockManager.RemoveAt(chunk, localPosition);

            if (chunk?.DamagedBlocks == null)
                return;

            ByteVector3 key = new ByteVector3((byte)localPosition.x, (byte)localPosition.y, (byte)localPosition.z);
            if (chunk.DamagedBlocks.TryGetValue(key, out DamagedBlock damagedBlock))
            {
                if (damagedBlock.DamageStage != null)
                    GameObject.Destroy(damagedBlock.DamageStage);

                chunk.DamagedBlocks.Remove(key);
            }
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

        private static void EnsureSimulationCapacity(int count)
        {
            if (simulationCapacity >= count && entityPositions.IsCreated)
                return;

            DisposeSimulationArrays();

            simulationCapacity = Mathf.NextPowerOfTwo(Mathf.Max(16, count));
            entityPositions = new NativeArray<float3>(
                simulationCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            entityVelocities = new NativeArray<float>(
                simulationCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            candidatePositions = new NativeArray<float3>(
                simulationCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            candidateVelocities = new NativeArray<float>(
                simulationCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            candidateCells = new NativeArray<int3>(
                simulationCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            sweepStates = new NativeArray<byte>(
                simulationCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            settleFlags = new NativeArray<byte>(
                simulationCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        private static void DisposeSimulationArrays()
        {
            if (entityPositions.IsCreated)
                entityPositions.Dispose();
            if (entityVelocities.IsCreated)
                entityVelocities.Dispose();
            if (candidatePositions.IsCreated)
                candidatePositions.Dispose();
            if (candidateVelocities.IsCreated)
                candidateVelocities.Dispose();
            if (candidateCells.IsCreated)
                candidateCells.Dispose();
            if (sweepStates.IsCreated)
                sweepStates.Dispose();
            if (settleFlags.IsCreated)
                settleFlags.Dispose();

            simulationCapacity = 0;
        }

        private static float3 ToFloat3(Vector3 value)
        {
            return new float3(value.x, value.y, value.z);
        }

        private static Vector3 ToVector3(float3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        [BurstCompile]
        private struct IntegrateFallingBlocksJob : IJobParallelFor
        {
            [ReadOnly] public float DeltaTime;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<float> Velocities;

            [WriteOnly] public NativeArray<float3> CandidatePositions;
            [WriteOnly] public NativeArray<float> CandidateVelocities;
            [WriteOnly] public NativeArray<int3> CandidateCells;

            public void Execute(int index)
            {
                float velocity = math.max(Velocities[index] - Gravity * DeltaTime, -MaximumFallSpeed);
                float3 candidatePosition = Positions[index] + new float3(0f, velocity * DeltaTime, 0f);

                CandidatePositions[index] = candidatePosition;
                CandidateVelocities[index] = velocity;
                CandidateCells[index] = new int3(
                    (int)math.floor(candidatePosition.x),
                    (int)math.floor(candidatePosition.y),
                    (int)math.floor(candidatePosition.z));
            }
        }

        [BurstCompile]
        private struct ResolveFallingBlocksJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> OriginalPositions;
            public NativeArray<float3> Positions;
            public NativeArray<float> Velocities;
            [ReadOnly] public NativeArray<int3> Cells;
            [ReadOnly] public NativeArray<byte> SweepStates;
            [WriteOnly] public NativeArray<byte> SettleFlags;

            public void Execute(int index)
            {
                byte sweepState = SweepStates[index];

                if (sweepState == SweepStateUnavailable)
                {
                    Positions[index] = OriginalPositions[index];
                    Velocities[index] = 0f;
                    SettleFlags[index] = 0;
                    return;
                }

                if (sweepState == SweepStateLanding)
                {
                    Positions[index] = GetCellCenter(Cells[index]);
                    Velocities[index] = 0f;
                    SettleFlags[index] = 1;
                    return;
                }

                SettleFlags[index] = 0;
            }

            private static float3 GetCellCenter(int3 cell)
            {
                return new float3(cell.x + 0.5f, cell.y + 0.5f, cell.z + 0.5f);
            }
        }
    }
}
