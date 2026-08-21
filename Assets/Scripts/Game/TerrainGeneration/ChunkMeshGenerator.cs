using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public enum MeshRequestPriority : byte
    {
        Background,
        Streaming,
        Interactive
    }

    public class ChunkMeshGenerator
    {
        private const float FluidSurfaceInset = 0.003f;
        internal const byte MaximumSkylight = 15;
        internal const byte MaximumBlockLight = 15;
        private const byte IndirectSkylightFalloff = 3;
        internal const int SkylightPadding = MaximumSkylight;
        internal const int BlockLightPadding = MaximumBlockLight;
        private const int MaxMeshCallbacksPerUpdate = 1;
        private const double MaxMeshCallbackMillisecondsPerUpdate = 2.0;
        private const int MaxLoadingMeshCallbacksPerUpdate = 4;
        private const double MaxLoadingMeshCallbackMillisecondsPerUpdate = 6.0;
        private const int MaxQueuedMeshCompletions = 8;
        private const int MaxInteractiveMeshCallbacksPerUpdate = 4;
        private const double MaxInteractiveMeshCallbackMillisecondsPerUpdate = 6.0;
        private const int MaxQueuedInteractiveMeshCompletions = 4;
        private const int MaxStreamingMeshCallbacksPerUpdate = 2;
        private const double MaxStreamingMeshCallbackMillisecondsPerUpdate = 2.0;
        private const int MaxQueuedStreamingMeshCompletions = 8;
        private const int InteractiveRequestsPerStreamingRequest = 3;
        private const int HigherPriorityRequestsPerBackgroundRequest = 3;
        private const int ShutdownDrainTimeoutMilliseconds = 30000;
        private static readonly int MaxConcurrentMeshWorkers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1));
        // A request owns several large voxel snapshots until its callback is processed.
        // Partition capacity so every lane retains progress even while the other two
        // are saturated, while keeping the total retained snapshot memory bounded.
        private const int MaxPerPriorityBackgroundMeshRequests = MaxQueuedMeshCompletions;
        private const int MaxPerPriorityStreamingMeshRequests = MaxQueuedStreamingMeshCompletions;
        private static readonly int MaxPerPriorityInteractiveMeshRequests =
            MaxConcurrentMeshWorkers + MaxQueuedInteractiveMeshCompletions;
        private static readonly int MaxAdmittedMeshRequests =
            MaxPerPriorityBackgroundMeshRequests +
            MaxPerPriorityStreamingMeshRequests +
            MaxPerPriorityInteractiveMeshRequests;
        private static readonly Color32 WhiteTint = new Color32(255, 255, 255, 255);
        private static readonly object WorldStateLock = new object();
        private static readonly object MeshQueueSelectionLock = new object();
        private static readonly ConcurrentQueue<MeshRequest> PendingMeshRequestQueue = new ConcurrentQueue<MeshRequest>();
        private static readonly ConcurrentQueue<MeshCompletion> MeshCompletionQueue = new ConcurrentQueue<MeshCompletion>();
        private static readonly ConcurrentQueue<MeshRequest> InteractiveMeshRequestQueue = new ConcurrentQueue<MeshRequest>();
        private static readonly ConcurrentQueue<MeshCompletion> InteractiveMeshCompletionQueue = new ConcurrentQueue<MeshCompletion>();
        private static readonly ConcurrentQueue<MeshRequest> StreamingMeshRequestQueue = new ConcurrentQueue<MeshRequest>();
        private static readonly ConcurrentQueue<MeshCompletion> StreamingMeshCompletionQueue = new ConcurrentQueue<MeshCompletion>();
        private static readonly ConcurrentBag<MeshBuilderScratch> MeshBuilderScratchPool = new ConcurrentBag<MeshBuilderScratch>();
        private static int activeMeshWorkers;
        private static int pooledMeshBuilderScratchCount;
        private static int nextWorldEpoch;
        private static int mainThreadId;
        private static int interactiveCallbackBudgetFrame = -1;
        private static int interactiveCallbacksRemaining;
        private static double interactiveCallbackMillisecondsUsed;
        private static int streamingCallbackBudgetFrame = -1;
        private static int streamingCallbacksRemaining;
        private static double streamingCallbackMillisecondsUsed;
        private static int interactiveRequestsSinceStreaming;
        private static int higherPriorityRequestsSinceBackground;
        private static MeshWorldState currentWorld;

        public static bool HasPendingMeshWork
        {
            get
            {
                MeshWorldState world = Volatile.Read(ref currentWorld);
                return world != null && Volatile.Read(ref world.PendingRequestCount) > 0;
            }
        }

        public static void Update(bool isLoading = false)
        {
            ProcessInteractiveCompletions();
            ProcessStreamingCompletions();

            int callbackLimit = isLoading ? MaxLoadingMeshCallbacksPerUpdate : MaxMeshCallbacksPerUpdate;
            double millisecondLimit = isLoading
                ? MaxLoadingMeshCallbackMillisecondsPerUpdate
                : MaxMeshCallbackMillisecondsPerUpdate;

            // Keep the original background completion allowance after the prioritized
            // callbacks so lighting and rebuild work cannot be starved indefinitely.
            // Do not allocate a Stopwatch during steady-state frames with no callback.
            if (!MeshCompletionQueue.IsEmpty)
            {
                var backgroundStopwatch = System.Diagnostics.Stopwatch.StartNew();
            ProcessCompletionQueue(
                MeshCompletionQueue,
                callbackLimit,
                millisecondLimit,
                backgroundStopwatch,
                MeshRequestPriority.Background);
            }

            TryStartMeshWorker();
        }

        public static void UpdateInteractive()
        {
            ProcessInteractiveCompletions();
            TryStartMeshWorker();
        }

        private static int ProcessInteractiveCompletions()
        {
            int frame = Time.frameCount;
            if (interactiveCallbackBudgetFrame != frame)
            {
                interactiveCallbackBudgetFrame = frame;
                interactiveCallbacksRemaining = MaxInteractiveMeshCallbacksPerUpdate;
                interactiveCallbackMillisecondsUsed = 0.0;
            }

            if (interactiveCallbacksRemaining <= 0 ||
                interactiveCallbackMillisecondsUsed >= MaxInteractiveMeshCallbackMillisecondsPerUpdate ||
                InteractiveMeshCompletionQueue.IsEmpty)
            {
                return 0;
            }

            var callbackStopwatch = System.Diagnostics.Stopwatch.StartNew();
            int processed = ProcessCompletionQueue(
                InteractiveMeshCompletionQueue,
                interactiveCallbacksRemaining,
                MaxInteractiveMeshCallbackMillisecondsPerUpdate - interactiveCallbackMillisecondsUsed,
                callbackStopwatch,
                MeshRequestPriority.Interactive);
            interactiveCallbacksRemaining -= processed;
            interactiveCallbackMillisecondsUsed += callbackStopwatch.Elapsed.TotalMilliseconds;
            return processed;
        }

        private static int ProcessStreamingCompletions()
        {
            int frame = Time.frameCount;
            if (streamingCallbackBudgetFrame != frame)
            {
                streamingCallbackBudgetFrame = frame;
                streamingCallbacksRemaining = MaxStreamingMeshCallbacksPerUpdate;
                streamingCallbackMillisecondsUsed = 0.0;
            }

            if (streamingCallbacksRemaining <= 0 ||
                streamingCallbackMillisecondsUsed >= MaxStreamingMeshCallbackMillisecondsPerUpdate ||
                StreamingMeshCompletionQueue.IsEmpty)
            {
                return 0;
            }

            var callbackStopwatch = System.Diagnostics.Stopwatch.StartNew();
            int processed = ProcessCompletionQueue(
                StreamingMeshCompletionQueue,
                streamingCallbacksRemaining,
                MaxStreamingMeshCallbackMillisecondsPerUpdate - streamingCallbackMillisecondsUsed,
                callbackStopwatch,
                MeshRequestPriority.Streaming);
            streamingCallbacksRemaining -= processed;
            streamingCallbackMillisecondsUsed += callbackStopwatch.Elapsed.TotalMilliseconds;
            return processed;
        }

        private static int ProcessCompletionQueue(
            ConcurrentQueue<MeshCompletion> completionQueue,
            int callbackLimit,
            double millisecondLimit,
            System.Diagnostics.Stopwatch callbackStopwatch,
            MeshRequestPriority priority)
        {
            ChunkRuntimeWorkKind applicationKind;
            switch (priority)
            {
                case MeshRequestPriority.Interactive:
                    applicationKind = ChunkRuntimeWorkKind.InteractiveMeshApplication;
                    break;
                case MeshRequestPriority.Streaming:
                    applicationKind = ChunkRuntimeWorkKind.StreamingMeshApplication;
                    break;
                default:
                    applicationKind = ChunkRuntimeWorkKind.BackgroundMeshApplication;
                    break;
            }

            int processed = 0;
            while (processed < callbackLimit &&
                   (processed == 0 || callbackStopwatch.Elapsed.TotalMilliseconds < millisecondLimit) &&
                   !completionQueue.IsEmpty)
            {
                if (!TerrainGenerator.TryBeginRuntimeChunkWork(
                        applicationKind,
                        urgent: priority == MeshRequestPriority.Interactive,
                        allowStarvation: true,
                        out long applicationStartedAt))
                {
                    break;
                }

                if (!completionQueue.TryDequeue(out MeshCompletion completion))
                {
                    TerrainGenerator.CancelRuntimeChunkWork(applicationKind, applicationStartedAt);
                    break;
                }

                try
                {
                    bool invokeCallback = completion.Request.CanInvokeCallback;
                    completion.Request.Settle();

                    if (invokeCallback)
                    {
                        if (completion.Exception != null)
                        {
                            try
                            {
                                Debug.LogException(completion.Exception);
                            }
                            finally
                            {
                                completion.Request.FailureCallback?.Invoke(completion.Exception);
                            }
                        }
                        else
                        {
                            completion.Request.Callback(completion.MeshData);
                        }
                    }
                }
                finally
                {
                    TerrainGenerator.CompleteRuntimeChunkWork(
                        applicationKind,
                        applicationStartedAt);
                }

                processed++;
            }

            return processed;
        }

        public static int BeginWorld()
        {
            MeshWorldState previousWorld;
            MeshWorldState nextWorld;

            lock (WorldStateLock)
            {
                mainThreadId = Thread.CurrentThread.ManagedThreadId;
                previousWorld = currentWorld;
                if (previousWorld != null)
                    previousWorld.AcceptingRequests = false;

                nextWorld = new MeshWorldState(++nextWorldEpoch);
                Volatile.Write(ref currentWorld, nextWorld);
            }

            if (previousWorld != null)
                DrainWorld(previousWorld);

            interactiveCallbackBudgetFrame = -1;
            interactiveCallbacksRemaining = 0;
            interactiveCallbackMillisecondsUsed = 0.0;
            streamingCallbackBudgetFrame = -1;
            streamingCallbacksRemaining = 0;
            streamingCallbackMillisecondsUsed = 0.0;
            lock (MeshQueueSelectionLock)
            {
                interactiveRequestsSinceStreaming = 0;
                higherPriorityRequestsSinceBackground = 0;
            }

            return nextWorld.Epoch;
        }

        public static void ShutdownWorldAndDrain(int epoch)
        {
            MeshWorldState world;
            lock (WorldStateLock)
            {
                world = currentWorld;
                if (world == null || world.Epoch != epoch)
                    return;

                world.AcceptingRequests = false;
                Volatile.Write(ref currentWorld, null);
            }

            DrainWorld(world);
        }

        public static bool CanAcceptMeshRequest(MeshRequestPriority priority)
        {
            lock (WorldStateLock)
            {
                MeshWorldState world = currentWorld;
                return world != null &&
                       world.AcceptingRequests &&
                       CanAdmitMeshRequest(world, priority);
            }
        }

        private static bool CanAdmitMeshRequest(
            MeshWorldState world,
            MeshRequestPriority priority)
        {
            if (Volatile.Read(ref world.PendingRequestCount) >= MaxAdmittedMeshRequests)
                return false;

            // Partition the overall snapshot capacity among all three lanes. The
            // limits sum to the total cap, so no combination of two priorities can
            // consume the slots reserved for the third.
            return priority switch
            {
                MeshRequestPriority.Interactive =>
                    Volatile.Read(ref world.PendingInteractiveRequestCount) < MaxPerPriorityInteractiveMeshRequests,
                MeshRequestPriority.Streaming =>
                    Volatile.Read(ref world.PendingStreamingRequestCount) < MaxPerPriorityStreamingMeshRequests,
                _ =>
                    Volatile.Read(ref world.PendingBackgroundRequestCount) < MaxPerPriorityBackgroundMeshRequests
            };
        }

        public static bool RequestMeshData(
            VoxelBuffer<byte> haloBlocks,
            VoxelBuffer<Color32> haloTints,
            Action<MeshData> callback,
            Action<Exception> failureCallback = null)
        {
            return RequestMeshData(haloBlocks, haloTints, null, null, callback, failureCallback);
        }

        public static bool RequestMeshData(
            VoxelBuffer<byte> haloBlocks,
            VoxelBuffer<Color32> haloTints,
            VoxelBuffer<byte> skyOpenMap,
            Action<MeshData> callback,
            Action<Exception> failureCallback = null)
        {
            return RequestMeshData(haloBlocks, haloTints, skyOpenMap, null, callback, failureCallback);
        }

        public static bool RequestMeshData(
            VoxelBuffer<byte> haloBlocks,
            VoxelBuffer<Color32> haloTints,
            VoxelBuffer<byte> skyOpenMap,
            VoxelBuffer<byte> blockLightBlocks,
            Action<MeshData> callback,
            Action<Exception> failureCallback = null,
            MeshRequestPriority priority = MeshRequestPriority.Background)
        {
            return RequestMeshData(
                haloBlocks,
                haloTints,
                null,
                skyOpenMap,
                blockLightBlocks,
                callback,
                failureCallback,
                priority);
        }

        public static bool RequestMeshData(
            VoxelBuffer<byte> haloBlocks,
            VoxelBuffer<Color32> haloTints,
            VoxelBuffer<byte> skylightBlocks,
            VoxelBuffer<byte> skyOpenMap,
            VoxelBuffer<byte> blockLightBlocks,
            Action<MeshData> callback,
            Action<Exception> failureCallback = null,
            MeshRequestPriority priority = MeshRequestPriority.Background)
        {
            MeshRequest request = null;
            try
            {
                if (callback == null)
                    throw new ArgumentNullException(nameof(callback));

                if (Thread.CurrentThread.ManagedThreadId != Volatile.Read(ref mainThreadId))
                    throw new InvalidOperationException("Mesh requests and BlockData snapshots must be created on the Unity main thread.");

                lock (WorldStateLock)
                {
                    MeshWorldState world = currentWorld;
                    if (world == null || !world.AcceptingRequests)
                    {
                        haloBlocks?.ReturnToPool();
                        haloTints?.ReturnToPool();
                        skylightBlocks?.ReturnToPool();
                        skyOpenMap?.ReturnToPool();
                        blockLightBlocks?.ReturnToPool();
                        return false;
                    }

                    // Keep this defensive check even though Chunk checks admission
                    // before constructing snapshots. It bounds callers using the
                    // public overloads directly as well.
                    if (!CanAdmitMeshRequest(world, priority))
                    {
                        haloBlocks?.ReturnToPool();
                        haloTints?.ReturnToPool();
                        skylightBlocks?.ReturnToPool();
                        skyOpenMap?.ReturnToPool();
                        blockLightBlocks?.ReturnToPool();
                        return false;
                    }

                    if (world.BlockSnapshot == null)
                        world.BlockSnapshot = CreateBlockMeshingSnapshot();

                    request = new MeshRequest(
                        world,
                        world.BlockSnapshot,
                        haloBlocks,
                        haloTints,
                        skylightBlocks,
                        skyOpenMap,
                        blockLightBlocks,
                        callback,
                        failureCallback,
                        priority);
                    request.Register();
                    switch (priority)
                    {
                        case MeshRequestPriority.Interactive:
                            InteractiveMeshRequestQueue.Enqueue(request);
                            break;
                        case MeshRequestPriority.Streaming:
                            StreamingMeshRequestQueue.Enqueue(request);
                            break;
                        default:
                            PendingMeshRequestQueue.Enqueue(request);
                            break;
                    }
                }
            }
            catch
            {
                if (request != null)
                {
                    request.ReturnInputs();
                    request.Settle();
                }
                else
                {
                    haloBlocks?.ReturnToPool();
                    haloTints?.ReturnToPool();
                    skylightBlocks?.ReturnToPool();
                    skyOpenMap?.ReturnToPool();
                    blockLightBlocks?.ReturnToPool();
                }

                throw;
            }

            TryStartMeshWorker();
            return true;
        }

        private static void TryStartMeshWorker()
        {
            if (!HasProcessableMeshRequest())
                return;

            while (true)
            {
                int workerCount = Volatile.Read(ref activeMeshWorkers);
                if (workerCount >= MaxConcurrentMeshWorkers)
                    return;

                if (Interlocked.CompareExchange(ref activeMeshWorkers, workerCount + 1, workerCount) != workerCount)
                    continue;

                ThreadPool.QueueUserWorkItem(ProcessMeshRequestQueue);
                return;
            }
        }

        private static void ProcessMeshRequestQueue(object _)
        {
            try
            {
                while (TryDequeueMeshRequest(out MeshRequest request))
                {
                    if (!request.CanProcess)
                    {
                        request.ReturnInputs();
                        request.Settle();
                        continue;
                    }

                    MeshData meshData = default;
                    Exception failure = null;
                    try
                    {
                        meshData = GenerateMeshData(
                            request.HaloBlocks,
                            request.HaloTints,
                            request.SkylightBlocks,
                            request.SkyOpenMap,
                            request.BlockLightBlocks,
                            request.BlockSnapshot);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        request.ReturnInputs();
                    }

                    if (request.CanInvokeCallback)
                    {
                        var completion = new MeshCompletion(request, meshData, failure);
                        switch (request.Priority)
                        {
                            case MeshRequestPriority.Interactive:
                                InteractiveMeshCompletionQueue.Enqueue(completion);
                                break;
                            case MeshRequestPriority.Streaming:
                                StreamingMeshCompletionQueue.Enqueue(completion);
                                break;
                            default:
                                MeshCompletionQueue.Enqueue(completion);
                                break;
                        }
                    }
                    else
                        request.Settle();
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeMeshWorkers);
                TryStartMeshWorker();
            }
        }

        private static bool HasProcessableMeshRequest()
        {
            return (!InteractiveMeshRequestQueue.IsEmpty &&
                    InteractiveMeshCompletionQueue.Count < MaxQueuedInteractiveMeshCompletions) ||
                   (!StreamingMeshRequestQueue.IsEmpty &&
                    StreamingMeshCompletionQueue.Count < MaxQueuedStreamingMeshCompletions) ||
                   (!PendingMeshRequestQueue.IsEmpty &&
                    MeshCompletionQueue.Count < MaxQueuedMeshCompletions);
        }

        private static bool TryDequeueMeshRequest(out MeshRequest request)
        {
            lock (MeshQueueSelectionLock)
            {
                bool canProcessInteractive =
                    InteractiveMeshCompletionQueue.Count < MaxQueuedInteractiveMeshCompletions;
                bool canProcessStreaming =
                    StreamingMeshCompletionQueue.Count < MaxQueuedStreamingMeshCompletions;
                bool canProcessBackground =
                    MeshCompletionQueue.Count < MaxQueuedMeshCompletions;

                // Give streaming a forced turn after sustained interactive work. If a
                // background request is also waiting, it follows immediately afterward.
                if (canProcessStreaming &&
                    interactiveRequestsSinceStreaming >= InteractiveRequestsPerStreamingRequest &&
                    StreamingMeshRequestQueue.TryDequeue(out request))
                {
                    interactiveRequestsSinceStreaming = 0;
                    higherPriorityRequestsSinceBackground = Math.Min(
                        higherPriorityRequestsSinceBackground + 1,
                        HigherPriorityRequestsPerBackgroundRequest);
                    return true;
                }

                if (canProcessBackground &&
                    higherPriorityRequestsSinceBackground >= HigherPriorityRequestsPerBackgroundRequest &&
                    PendingMeshRequestQueue.TryDequeue(out request))
                {
                    higherPriorityRequestsSinceBackground = 0;
                    return true;
                }

                if (canProcessInteractive &&
                    InteractiveMeshRequestQueue.TryDequeue(out request))
                {
                    interactiveRequestsSinceStreaming = Math.Min(
                        interactiveRequestsSinceStreaming + 1,
                        InteractiveRequestsPerStreamingRequest);
                    higherPriorityRequestsSinceBackground = Math.Min(
                        higherPriorityRequestsSinceBackground + 1,
                        HigherPriorityRequestsPerBackgroundRequest);
                    return true;
                }

                if (canProcessStreaming &&
                    StreamingMeshRequestQueue.TryDequeue(out request))
                {
                    interactiveRequestsSinceStreaming = 0;
                    higherPriorityRequestsSinceBackground = Math.Min(
                        higherPriorityRequestsSinceBackground + 1,
                        HigherPriorityRequestsPerBackgroundRequest);
                    return true;
                }

                if (canProcessBackground &&
                    PendingMeshRequestQueue.TryDequeue(out request))
                {
                    higherPriorityRequestsSinceBackground = 0;
                    return true;
                }

                request = null;
                return false;
            }
        }

        private static void DrainWorld(MeshWorldState world)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref world.PendingRequestCount) > 0)
            {
                DrainQueuedRequests(world);
                DrainCompletions(world);

                if (Volatile.Read(ref world.PendingRequestCount) == 0)
                    break;

                TryStartMeshWorker();
                if (stopwatch.ElapsedMilliseconds >= ShutdownDrainTimeoutMilliseconds)
                {
                    Debug.LogWarning($"Timed out draining mesh work for world epoch {world.Epoch}. Remaining workers will self-cancel without callbacks.");
                    break;
                }

                world.DrainedEvent.Wait(5);
            }

            DrainQueuedRequests(world);
            DrainCompletions(world);
        }

        private static void DrainQueuedRequests(MeshWorldState world)
        {
            DrainQueuedRequests(world, InteractiveMeshRequestQueue);
            DrainQueuedRequests(world, StreamingMeshRequestQueue);
            DrainQueuedRequests(world, PendingMeshRequestQueue);
            TryStartMeshWorker();
        }

        private static void DrainQueuedRequests(
            MeshWorldState world,
            ConcurrentQueue<MeshRequest> requestQueue)
        {
            List<MeshRequest> retainedRequests = null;
            while (requestQueue.TryDequeue(out MeshRequest request))
            {
                if (ReferenceEquals(request.World, world))
                {
                    request.ReturnInputs();
                    request.Settle();
                }
                else
                {
                    retainedRequests ??= new List<MeshRequest>();
                    retainedRequests.Add(request);
                }
            }

            if (retainedRequests == null)
                return;

            for (int i = 0; i < retainedRequests.Count; i++)
                requestQueue.Enqueue(retainedRequests[i]);
        }

        private static void DrainCompletions(MeshWorldState world)
        {
            DrainCompletions(world, InteractiveMeshCompletionQueue);
            DrainCompletions(world, StreamingMeshCompletionQueue);
            DrainCompletions(world, MeshCompletionQueue);
        }

        private static void DrainCompletions(
            MeshWorldState world,
            ConcurrentQueue<MeshCompletion> completionQueue)
        {
            List<MeshCompletion> retainedCompletions = null;
            while (completionQueue.TryDequeue(out MeshCompletion completion))
            {
                if (ReferenceEquals(completion.Request.World, world))
                {
                    completion.Request.Settle();
                }
                else
                {
                    retainedCompletions ??= new List<MeshCompletion>();
                    retainedCompletions.Add(completion);
                }
            }

            if (retainedCompletions == null)
                return;

            for (int i = 0; i < retainedCompletions.Count; i++)
                completionQueue.Enqueue(retainedCompletions[i]);
        }


        public static MeshData GenerateMeshData(VoxelBuffer<byte> haloBlocks, VoxelBuffer<Color32> haloTints)
        {
            MeshWorldState world = Volatile.Read(ref currentWorld);
            BlockMeshingInfo[] snapshot = world?.BlockSnapshot;
            if (snapshot == null)
                throw new InvalidOperationException("No immutable block meshing snapshot is available for the active world.");

            return GenerateMeshData(haloBlocks, haloTints, null, null, null, snapshot);
        }

        private static MeshData GenerateMeshData(
            VoxelBuffer<byte> haloBlocks,
            VoxelBuffer<Color32> haloTints,
            VoxelBuffer<byte> skylightBlocks,
            VoxelBuffer<byte> skyOpenMap,
            VoxelBuffer<byte> blockLightBlocks,
            BlockMeshingInfo[] blockDefinitions)
        {
            MeshBuilderScratch scratch = RentMeshBuilderScratch();

            try
            {
                scratch.Reset();
                if (skylightBlocks != null)
                {
                    BuildSkylight(
                        skylightBlocks,
                        skyOpenMap,
                        blockDefinitions,
                        scratch.SkylightWork,
                        scratch.LightingQueue,
                        scratch.LightingQueued);
                    CopySkylightToMeshHalo(
                        skylightBlocks,
                        scratch.SkylightWork,
                        scratch.Skylight);
                }
                else
                {
                    // Keep the isolated synchronous/tooling overload useful. Those callers
                    // only provide the traditional one-cell mesh halo and no sky-open map.
                    BuildSkylight(
                        haloBlocks,
                        skyOpenMap,
                        blockDefinitions,
                        scratch.Skylight,
                        scratch.LightingQueue,
                        scratch.LightingQueued);
                }
                BuildBlockLight(
                    blockLightBlocks ?? haloBlocks,
                    haloBlocks,
                    blockDefinitions,
                    scratch.BlockLightWork,
                    scratch.BlockLightSources,
                    scratch.BlockLightHalo,
                    scratch.LightingQueue,
                    scratch.LightingQueued);

                for (int face = 0; face < 6; face++)
                {
                    BuildGreedyFacesForDirection(
                        haloBlocks,
                        haloTints,
                        scratch.Skylight,
                        scratch.BlockLightHalo,
                        blockDefinitions,
                        face,
                        scratch.Mask,
                        scratch.Solid,
                        scratch.Fluid,
                        scratch.LavaFluid,
                        scratch.Transparent);
                }

                return new MeshData(
                   scratch.Solid.ToMeshSection(),
                   scratch.Fluid.ToMeshSection(),
                   scratch.LavaFluid.ToMeshSection(),
                   scratch.Transparent.ToMeshSection(),
                   CreateVoxelLightingSnapshot(scratch.Skylight, scratch.BlockLightHalo));
            }
            finally
            {
                ReturnMeshBuilderScratch(scratch);
            }
        }

        private static MeshBuilderScratch RentMeshBuilderScratch()
        {
            if (MeshBuilderScratchPool.TryTake(out MeshBuilderScratch scratch))
            {
                Interlocked.Decrement(ref pooledMeshBuilderScratchCount);
                return scratch;
            }

            return new MeshBuilderScratch();
        }

        private static byte[] CreateVoxelLightingSnapshot(byte[] skylight, byte[] blockLight)
        {
            const int HaloWidth = Chunk.CHUNK_SIZE + 2;
            const int HaloSliceStride = HaloWidth * (Chunk.CHUNK_HEIGHT + 2);
            var snapshot = new byte[Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT * Chunk.CHUNK_SIZE];

            for (int z = 0; z < Chunk.CHUNK_SIZE; z++)
            {
                int haloSlice = (z + 1) * HaloSliceStride;
                int targetSlice = z * Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;
                for (int y = 0; y < Chunk.CHUNK_HEIGHT; y++)
                {
                    int haloRow = haloSlice + (y + 1) * HaloWidth + 1;
                    int targetRow = targetSlice + y * Chunk.CHUNK_SIZE;
                    for (int x = 0; x < Chunk.CHUNK_SIZE; x++)
                    {
                        int haloIndex = haloRow + x;
                        snapshot[targetRow + x] = (byte)(
                            (skylight[haloIndex] & 0x0F) |
                            ((blockLight[haloIndex] & 0x0F) << 4));
                    }
                }
            }

            return snapshot;
        }

        private static void CopySkylightToMeshHalo(
            VoxelBuffer<byte> sourceBlocks,
            byte[] sourceSkylight,
            byte[] targetSkylight)
        {
            const int TargetWidth = Chunk.CHUNK_SIZE + 2;
            const int TargetHeight = Chunk.CHUNK_HEIGHT + 2;
            const int TargetDepth = Chunk.CHUNK_SIZE + 2;

            int xOffset = (sourceBlocks.Width - TargetWidth) / 2;
            int yOffset = (sourceBlocks.Height - TargetHeight) / 2;
            int zOffset = (sourceBlocks.Depth - TargetDepth) / 2;
            if (xOffset < 0 || yOffset < 0 || zOffset < 0 ||
                sourceBlocks.Width - TargetWidth != xOffset * 2 ||
                sourceBlocks.Height - TargetHeight != yOffset * 2 ||
                sourceBlocks.Depth - TargetDepth != zOffset * 2)
            {
                throw new ArgumentException("The skylight snapshot must contain a centered mesh halo.", nameof(sourceBlocks));
            }

            int targetSliceStride = TargetWidth * TargetHeight;
            for (int z = 0; z < TargetDepth; z++)
            {
                int sourceSlice = (z + zOffset) * sourceBlocks.SliceStride;
                int targetSlice = z * targetSliceStride;
                for (int y = 0; y < TargetHeight; y++)
                {
                    Array.Copy(
                        sourceSkylight,
                        sourceSlice + (y + yOffset) * sourceBlocks.Width + xOffset,
                        targetSkylight,
                        targetSlice + y * TargetWidth,
                        TargetWidth);
                }
            }
        }

        private static void ReturnMeshBuilderScratch(MeshBuilderScratch scratch)
        {
            int poolCount = Interlocked.Increment(ref pooledMeshBuilderScratchCount);
            if (poolCount <= MaxConcurrentMeshWorkers)
            {
                MeshBuilderScratchPool.Add(scratch);
                return;
            }

            Interlocked.Decrement(ref pooledMeshBuilderScratchCount);
        }

        private static void BuildSkylight(
            VoxelBuffer<byte> haloBlocks,
            VoxelBuffer<byte> skyOpenMap,
            BlockMeshingInfo[] blockMeshingInfo,
            byte[] skylight,
            int[] queue,
            bool[] queued)
        {
            Array.Clear(skylight, 0, skylight.Length);
            Array.Clear(queued, 0, queued.Length);

            byte[] blockData = haloBlocks.Data;
            int width = haloBlocks.Width;
            int height = haloBlocks.Height;
            int depth = haloBlocks.Depth;
            int sliceStride = haloBlocks.SliceStride;
            int queueHead = 0;
            int queueTail = 0;
            int queueCount = 0;

            // Seed every cell with a direct, unobstructed path to the sky. Open air
            // keeps full vertical skylight, while transparent blocks such as water,
            // glass, and leaves attenuate it instead of transmitting sunlight forever.
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (IsHorizontalHaloCorner(x, z, width, depth) ||
                        !IsSkyOpenAbove(skyOpenMap, x, z, width, depth))
                    {
                        continue;
                    }

                    byte directLevel = MaximumSkylight;
                    for (int y = height - 1; y >= 0; y--)
                    {
                        if (IsUnusedHaloCell(x, y, z, width, height, depth))
                            continue;

                        int index = x + y * width + z * sliceStride;
                        byte blockId = blockData[index];
                        if (IsSkylightOccluder(blockId, blockMeshingInfo))
                            break;

                        if (blockId != Chunk.BLOCK_AIR)
                        {
                            if (directLevel <= 1)
                                break;

                            directLevel--;
                        }

                        skylight[index] = directLevel;
                        EnqueueSkylight(index, queue, queued, ref queueTail, ref queueCount);
                    }
                }
            }

            while (queueCount > 0)
            {
                int index = queue[queueHead];
                queueHead = (queueHead + 1) % queue.Length;
                queueCount--;
                queued[index] = false;

                byte level = skylight[index];
                if (level <= IndirectSkylightFalloff)
                    continue;

                int z = index / sliceStride;
                int withinSlice = index - z * sliceStride;
                int y = withinSlice / width;
                int x = withinSlice - y * width;
                // Direct sky columns remain at full strength. Once skylight has to
                // travel sideways or around an opaque voxel it fades more quickly,
                // so a roof/wall creates a readable penumbra instead of transmitting
                // almost-full daylight for fourteen blocks.
                byte spreadLevel = (byte)(level - IndirectSkylightFalloff);

                if (x > 0)
                    SpreadSkylight(index - 1, x - 1, y, z, spreadLevel, haloBlocks, blockMeshingInfo, skylight, queue, queued, ref queueTail, ref queueCount);
                if (x + 1 < width)
                    SpreadSkylight(index + 1, x + 1, y, z, spreadLevel, haloBlocks, blockMeshingInfo, skylight, queue, queued, ref queueTail, ref queueCount);
                if (y > 0)
                    SpreadSkylight(index - width, x, y - 1, z, spreadLevel, haloBlocks, blockMeshingInfo, skylight, queue, queued, ref queueTail, ref queueCount);
                if (y + 1 < height)
                    SpreadSkylight(index + width, x, y + 1, z, spreadLevel, haloBlocks, blockMeshingInfo, skylight, queue, queued, ref queueTail, ref queueCount);
                if (z > 0)
                    SpreadSkylight(index - sliceStride, x, y, z - 1, spreadLevel, haloBlocks, blockMeshingInfo, skylight, queue, queued, ref queueTail, ref queueCount);
                if (z + 1 < depth)
                    SpreadSkylight(index + sliceStride, x, y, z + 1, spreadLevel, haloBlocks, blockMeshingInfo, skylight, queue, queued, ref queueTail, ref queueCount);
            }
        }

        private static void BuildBlockLight(
            VoxelBuffer<byte> lightBlocks,
            VoxelBuffer<byte> meshHaloBlocks,
            BlockMeshingInfo[] blockMeshingInfo,
            byte[] blockLight,
            int[] blockLightSources,
            byte[] meshHaloBlockLight,
            int[] queue,
            bool[] queued)
        {
            int sourceLength = lightBlocks.Length;
            if (sourceLength > blockLight.Length ||
                sourceLength > blockLightSources.Length ||
                sourceLength > queue.Length ||
                sourceLength > queued.Length)
            {
                throw new InvalidOperationException("The block-light snapshot exceeds the lighting scratch capacity.");
            }

            Array.Clear(blockLight, 0, blockLight.Length);
            Array.Clear(meshHaloBlockLight, 0, meshHaloBlockLight.Length);
            Array.Clear(queued, 0, queued.Length);
            Array.Fill(blockLightSources, -1);

            byte[] blocks = lightBlocks.Data;
            int width = lightBlocks.Width;
            int height = lightBlocks.Height;
            int depth = lightBlocks.Depth;
            int sliceStride = lightBlocks.SliceStride;
            int queueHead = 0;
            int queueTail = 0;
            int queueCount = 0;

            // Emissive blocks are valid light sources even when their own voxel is
            // opaque. Propagation only enters air or other non-occluding voxels.
            for (int index = 0; index < sourceLength; index++)
            {
                byte blockId = blocks[index];
                if ((uint)blockId >= (uint)blockMeshingInfo.Length)
                    continue;

                ref readonly BlockMeshingInfo block = ref blockMeshingInfo[blockId];
                if (!block.IsValid || block.LightEmission == 0)
                    continue;

                blockLight[index] = block.LightEmission;
                blockLightSources[index] = index;
                EnqueueSkylight(index, queue, queued, ref queueTail, ref queueCount);
            }

            while (queueCount > 0)
            {
                int index = queue[queueHead];
                queueHead = (queueHead + 1) % queue.Length;
                queueCount--;
                queued[index] = false;

                byte level = blockLight[index];
                if (level <= 1)
                    continue;

                int z = index / sliceStride;
                int withinSlice = index - z * sliceStride;
                int y = withinSlice / width;
                int x = withinSlice - y * width;
                byte spreadLevel = (byte)(level - 1);
                int sourceIndex = blockLightSources[index];
                if (sourceIndex < 0)
                    continue;

                if (x > 0)
                    SpreadBlockLight(index - 1, spreadLevel, sourceIndex, width, sliceStride, blocks, blockMeshingInfo, blockLight, blockLightSources, queue, queued, ref queueTail, ref queueCount);
                if (x + 1 < width)
                    SpreadBlockLight(index + 1, spreadLevel, sourceIndex, width, sliceStride, blocks, blockMeshingInfo, blockLight, blockLightSources, queue, queued, ref queueTail, ref queueCount);
                if (y > 0)
                    SpreadBlockLight(index - width, spreadLevel, sourceIndex, width, sliceStride, blocks, blockMeshingInfo, blockLight, blockLightSources, queue, queued, ref queueTail, ref queueCount);
                if (y + 1 < height)
                    SpreadBlockLight(index + width, spreadLevel, sourceIndex, width, sliceStride, blocks, blockMeshingInfo, blockLight, blockLightSources, queue, queued, ref queueTail, ref queueCount);
                if (z > 0)
                    SpreadBlockLight(index - sliceStride, spreadLevel, sourceIndex, width, sliceStride, blocks, blockMeshingInfo, blockLight, blockLightSources, queue, queued, ref queueTail, ref queueCount);
                if (z + 1 < depth)
                    SpreadBlockLight(index + sliceStride, spreadLevel, sourceIndex, width, sliceStride, blocks, blockMeshingInfo, blockLight, blockLightSources, queue, queued, ref queueTail, ref queueCount);
            }

            int offsetX = (width - meshHaloBlocks.Width) / 2;
            int offsetY = (height - meshHaloBlocks.Height) / 2;
            int offsetZ = (depth - meshHaloBlocks.Depth) / 2;
            if (offsetX < 0 || offsetY < 0 || offsetZ < 0 ||
                width - meshHaloBlocks.Width != offsetX * 2 ||
                height - meshHaloBlocks.Height != offsetY * 2 ||
                depth - meshHaloBlocks.Depth != offsetZ * 2)
            {
                throw new InvalidOperationException("The block-light snapshot must be symmetrically padded around the mesh halo.");
            }

            int haloWidth = meshHaloBlocks.Width;
            int haloHeight = meshHaloBlocks.Height;
            int haloDepth = meshHaloBlocks.Depth;
            int haloSliceStride = meshHaloBlocks.SliceStride;
            for (int z = 0; z < haloDepth; z++)
            {
                int sourceSlice = (z + offsetZ) * sliceStride;
                int targetSlice = z * haloSliceStride;
                for (int y = 0; y < haloHeight; y++)
                {
                    Array.Copy(
                        blockLight,
                        sourceSlice + (y + offsetY) * width + offsetX,
                        meshHaloBlockLight,
                        targetSlice + y * haloWidth,
                        haloWidth);
                }
            }
        }

        private static void SpreadBlockLight(
            int index,
            byte level,
            int sourceIndex,
            int width,
            int sliceStride,
            byte[] blocks,
            BlockMeshingInfo[] blockMeshingInfo,
            byte[] blockLight,
            int[] blockLightSources,
            int[] queue,
            bool[] queued,
            ref int queueTail,
            ref int queueCount)
        {
            if (level <= blockLight[index] ||
                IsBlockLightOccluder(blocks[index], blockMeshingInfo) ||
                !HasBlockLightLineOfSight(sourceIndex, index, width, sliceStride, blocks, blockMeshingInfo))
            {
                return;
            }

            blockLight[index] = level;
            blockLightSources[index] = sourceIndex;
            EnqueueSkylight(index, queue, queued, ref queueTail, ref queueCount);
        }

        private static bool HasBlockLightLineOfSight(
            int sourceIndex,
            int targetIndex,
            int width,
            int sliceStride,
            byte[] blocks,
            BlockMeshingInfo[] blockMeshingInfo)
        {
            if (sourceIndex == targetIndex)
                return true;

            DecodeVoxelIndex(sourceIndex, width, sliceStride, out int sourceX, out int sourceY, out int sourceZ);
            DecodeVoxelIndex(targetIndex, width, sliceStride, out int targetX, out int targetY, out int targetZ);

            int x = sourceX;
            int y = sourceY;
            int z = sourceZ;
            int stepX = Math.Sign(targetX - sourceX);
            int stepY = Math.Sign(targetY - sourceY);
            int stepZ = Math.Sign(targetZ - sourceZ);
            float deltaX = stepX == 0 ? float.PositiveInfinity : 1f / Math.Abs(targetX - sourceX);
            float deltaY = stepY == 0 ? float.PositiveInfinity : 1f / Math.Abs(targetY - sourceY);
            float deltaZ = stepZ == 0 ? float.PositiveInfinity : 1f / Math.Abs(targetZ - sourceZ);
            float maxX = deltaX * 0.5f;
            float maxY = deltaY * 0.5f;
            float maxZ = deltaZ * 0.5f;

            // Conservative supercover traversal. When a ray touches a voxel edge or
            // corner, every touched neighbor must be transparent; this prevents light
            // slipping through diagonal cracks between otherwise opaque blocks.
            while (x != targetX || y != targetY || z != targetZ)
            {
                float nextBoundary = Math.Min(maxX, Math.Min(maxY, maxZ));
                bool moveX = maxX <= nextBoundary + 0.000001f;
                bool moveY = maxY <= nextBoundary + 0.000001f;
                bool moveZ = maxZ <= nextBoundary + 0.000001f;

                if (moveX && IsBlockLightRayCellOccluded(x + stepX, y, z, targetIndex, width, sliceStride, blocks, blockMeshingInfo))
                    return false;
                if (moveY && IsBlockLightRayCellOccluded(x, y + stepY, z, targetIndex, width, sliceStride, blocks, blockMeshingInfo))
                    return false;
                if (moveZ && IsBlockLightRayCellOccluded(x, y, z + stepZ, targetIndex, width, sliceStride, blocks, blockMeshingInfo))
                    return false;
                if (moveX && moveY && IsBlockLightRayCellOccluded(x + stepX, y + stepY, z, targetIndex, width, sliceStride, blocks, blockMeshingInfo))
                    return false;
                if (moveX && moveZ && IsBlockLightRayCellOccluded(x + stepX, y, z + stepZ, targetIndex, width, sliceStride, blocks, blockMeshingInfo))
                    return false;
                if (moveY && moveZ && IsBlockLightRayCellOccluded(x, y + stepY, z + stepZ, targetIndex, width, sliceStride, blocks, blockMeshingInfo))
                    return false;
                if (moveX && moveY && moveZ && IsBlockLightRayCellOccluded(x + stepX, y + stepY, z + stepZ, targetIndex, width, sliceStride, blocks, blockMeshingInfo))
                    return false;

                if (moveX)
                {
                    x += stepX;
                    maxX += deltaX;
                }
                if (moveY)
                {
                    y += stepY;
                    maxY += deltaY;
                }
                if (moveZ)
                {
                    z += stepZ;
                    maxZ += deltaZ;
                }
            }

            return true;
        }

        private static bool IsBlockLightRayCellOccluded(
            int x,
            int y,
            int z,
            int targetIndex,
            int width,
            int sliceStride,
            byte[] blocks,
            BlockMeshingInfo[] blockMeshingInfo)
        {
            int index = x + y * width + z * sliceStride;
            return index != targetIndex && IsBlockLightOccluder(blocks[index], blockMeshingInfo);
        }

        private static void DecodeVoxelIndex(
            int index,
            int width,
            int sliceStride,
            out int x,
            out int y,
            out int z)
        {
            z = index / sliceStride;
            int withinSlice = index - z * sliceStride;
            y = withinSlice / width;
            x = withinSlice - y * width;
        }

        private static bool IsBlockLightOccluder(byte blockId, BlockMeshingInfo[] blockMeshingInfo)
        {
            if (blockId == Chunk.BLOCK_AIR)
                return false;

            return (uint)blockId >= (uint)blockMeshingInfo.Length ||
                   !blockMeshingInfo[blockId].IsValid ||
                   blockMeshingInfo[blockId].OccludesNeighborFaces;
        }

        private static bool IsSkyOpenAbove(
            VoxelBuffer<byte> skyOpenMap,
            int x,
            int z,
            int expectedWidth,
            int expectedDepth)
        {
            // The null case keeps the synchronous GenerateMeshData overload useful
            // for isolated tools/tests. Runtime chunk requests always supply a map.
            if (skyOpenMap == null)
                return true;

            if (skyOpenMap.Width != expectedWidth ||
                skyOpenMap.Height != 1 ||
                skyOpenMap.Depth != expectedDepth)
            {
                return false;
            }

            return skyOpenMap.Data[x + z * skyOpenMap.SliceStride] != 0;
        }

        private static void SpreadSkylight(
            int index,
            int x,
            int y,
            int z,
            byte level,
            VoxelBuffer<byte> haloBlocks,
            BlockMeshingInfo[] blockMeshingInfo,
            byte[] skylight,
            int[] queue,
            bool[] queued,
            ref int queueTail,
            ref int queueCount)
        {
            if (level <= skylight[index] ||
                IsUnusedHaloCell(x, y, z, haloBlocks.Width, haloBlocks.Height, haloBlocks.Depth) ||
                IsSkylightOccluder(haloBlocks.Data[index], blockMeshingInfo))
            {
                return;
            }

            skylight[index] = level;
            EnqueueSkylight(index, queue, queued, ref queueTail, ref queueCount);
        }

        private static void EnqueueSkylight(
            int index,
            int[] queue,
            bool[] queued,
            ref int queueTail,
            ref int queueCount)
        {
            if (queued[index])
                return;

            queued[index] = true;
            queue[queueTail] = index;
            queueTail = (queueTail + 1) % queue.Length;
            queueCount++;
        }

        private static bool IsSkylightOccluder(byte blockId, BlockMeshingInfo[] blockMeshingInfo)
        {
            if (blockId == Chunk.BLOCK_AIR)
                return false;

            return (uint)blockId >= (uint)blockMeshingInfo.Length ||
                   !blockMeshingInfo[blockId].IsValid ||
                   blockMeshingInfo[blockId].OccludesNeighborFaces;
        }

        private static bool IsHorizontalHaloCorner(int x, int z, int width, int depth)
        {
            bool xEdge = x == 0 || x == width - 1;
            bool zEdge = z == 0 || z == depth - 1;
            return xEdge && zEdge;
        }

        private static bool IsUnusedHaloCell(int x, int y, int z, int width, int height, int depth)
        {
            int edgeCount = 0;
            if (x == 0 || x == width - 1)
                edgeCount++;
            if (y == 0 || y == height - 1)
                edgeCount++;
            if (z == 0 || z == depth - 1)
                edgeCount++;
            return edgeCount >= 2;
        }

        private static void BuildGreedyFacesForDirection(
                   VoxelBuffer<byte> haloBlocks,
                   VoxelBuffer<Color32> haloTints,
                   byte[] skylight,
                   byte[] blockLight,
                   BlockMeshingInfo[] blockMeshingInfo,
                   int face,
                   GreedyCell[] mask,
                   MeshSectionBuilder solid,
                   MeshSectionBuilder fluid,
                   MeshSectionBuilder lavaFluid,
                   MeshSectionBuilder transparent)
        {
            GetMaskSizeForFace(face, out int width, out int height, out int slices);

            byte[] blockData = haloBlocks.Data;
            Color32[] tintData = haloTints?.Data;
            int haloWidth = haloBlocks.Width;
            int haloSliceStride = haloBlocks.SliceStride;
            int neighborStride = face switch
            {
                0 => -haloSliceStride,
                1 => haloSliceStride,
                2 => haloWidth,
                3 => -haloWidth,
                4 => -1,
                5 => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(face), face, null),
            };

            for (int slice = 0; slice < slices; slice++)
            {
                GetFaceTraversal(face, slice, haloWidth, haloSliceStride, out int sliceStart, out int uStride, out int vStride);

                for (int v = 0; v < height; v++)
                {
                    int blockIndex = sliceStart + v * vStride;
                    int maskIndex = v * width;

                    for (int u = 0; u < width; u++)
                    {
                        int currentMaskIndex = maskIndex + u;
                        mask[currentMaskIndex] = default;

                        int currentBlockIndex = blockIndex + u * uStride;
                        int blockId = blockData[currentBlockIndex];

                        if (blockId == Chunk.BLOCK_AIR)
                        {
                            continue;
                        }

                        if ((uint)blockId >= (uint)blockMeshingInfo.Length)
                        {
                            continue;
                        }

                        ref readonly BlockMeshingInfo block = ref blockMeshingInfo[blockId];
                        if (!block.RendersCube)
                            continue;

                        int neighborBlockId = blockData[currentBlockIndex + neighborStride];
                        if ((uint)neighborBlockId >= (uint)blockMeshingInfo.Length)
                        {
                            continue;
                        }

                        ref readonly BlockMeshingInfo neighbourBlock = ref blockMeshingInfo[neighborBlockId];
                        if (!neighbourBlock.IsValid)
                            continue;

                        bool neighbourIsOpaque = neighbourBlock.OccludesNeighborFaces;
                        bool hidesFaceBecauseSameFluid = block.IsFluid && neighbourBlock.DefinitionId == block.DefinitionId;
                        bool hidesFluidFaceAgainstFullBlock =
                            block.IsFluid &&
                            neighborBlockId != Chunk.BLOCK_AIR &&
                            !neighbourBlock.IsFluid &&
                            (neighbourBlock.IsFullBlock || neighbourBlock.OccludesNeighborFaces);
                        bool hidesFaceBecauseSameTransparentFullBlock =
                            blockId == Chunk.BLOCK_SNOW &&
                            neighborBlockId == blockId &&
                            block.IsTransparent &&
                            !block.IsFluid &&
                            block.IsFullBlock &&
                            neighbourBlock.IsFullBlock;

                        bool hidesFace = block.IsTransparent
                            ? (block.IsFluid
                                ? (hidesFaceBecauseSameFluid || hidesFluidFaceAgainstFullBlock)
                                : (hidesFaceBecauseSameTransparentFullBlock || neighbourIsOpaque))
                            : neighbourIsOpaque;

                        if (hidesFace)
                        {
                            continue;
                        }

                        Color32 tint = WhiteTint;
                        if (tintData != null)
                        {
                            tint = tintData[currentBlockIndex];
                            if (tint.a == 0)
                                tint = WhiteTint;
                        }

                        TextureLayerPair texture = block.GetTexture(face);
                        byte faceSkylight = skylight[currentBlockIndex + neighborStride];
                        byte faceBlockLight = Math.Max(
                            blockLight[currentBlockIndex],
                            blockLight[currentBlockIndex + neighborStride]);
                        ComputeFaceAmbientOcclusion(
                            blockData,
                            blockMeshingInfo,
                            currentBlockIndex,
                            neighborStride,
                            uStride,
                            vStride,
                            face,
                            out byte ao0,
                            out byte ao1,
                            out byte ao2,
                            out byte ao3);
                        mask[currentMaskIndex] = new GreedyCell
                        {
                            Valid = true,
                            BlockId = blockId,
                            TextureLayer = texture.TextureLayer,
                            OverlayTextureLayer = texture.OverlayTextureLayer,
                            Tint = tint,
                            Skylight = faceSkylight,
                            BlockLight = faceBlockLight,
                            AmbientOcclusion0 = ao0,
                            AmbientOcclusion1 = ao1,
                            AmbientOcclusion2 = ao2,
                            AmbientOcclusion3 = ao3,
                            IsFluid = block.IsFluid,
                            IsTransparent = block.IsTransparent,
                        };
                    }
                }

                for (int v = 0; v < height; v++)
                {
                    for (int u = 0; u < width; u++)
                    {
                        int startIdx = u + v * width;
                        GreedyCell cell = mask[startIdx];

                        if (!cell.Valid)
                        {
                            continue;
                        }

                        int quadWidth = 1;
                        while (u + quadWidth < width)
                        {
                            int idx = u + quadWidth + v * width;
                            if (!mask[idx].Matches(cell))
                            {
                                break;
                            }

                            quadWidth++;
                        }

                        int quadHeight = 1;
                        bool canGrow = true;
                        while (v + quadHeight < height && canGrow)
                        {
                            for (int checkU = 0; checkU < quadWidth; checkU++)
                            {
                                int idx = (u + checkU) + (v + quadHeight) * width;
                                if (!mask[idx].Matches(cell))
                                {
                                    canGrow = false;
                                    break;
                                }
                            }

                            if (canGrow)
                            {
                                quadHeight++;
                            }
                        }

                        for (int markV = 0; markV < quadHeight; markV++)
                        {
                            for (int markU = 0; markU < quadWidth; markU++)
                            {
                                mask[(u + markU) + (v + markV) * width].Valid = false;
                            }
                        }

                        GetQuadForFace(face, u, v, slice, quadWidth, quadHeight, out Vector3 origin, out Vector3 du, out Vector3 dv);

                        if (cell.IsFluid)
                        {
                            if (cell.BlockId == Chunk.BLOCK_LAVA)
                                AddFluidQuad(origin, du, dv, face, cell.TextureLayer, cell.OverlayTextureLayer, cell.Tint, cell.Skylight, cell.BlockLight, cell, lavaFluid);
                            else
                                AddFluidQuad(origin, du, dv, face, cell.TextureLayer, cell.OverlayTextureLayer, cell.Tint, cell.Skylight, cell.BlockLight, cell, fluid);
                        }
                        else if (cell.IsTransparent)
                        {
                            AddQuad(origin, du, dv, face, cell.TextureLayer, cell.OverlayTextureLayer, cell.Tint, cell.Skylight, cell.BlockLight, cell, transparent);
                        }
                        else
                        {
                            AddQuad(origin, du, dv, face, cell.TextureLayer, cell.OverlayTextureLayer, cell.Tint, cell.Skylight, cell.BlockLight, cell, solid);
                        }
                    }
                }
            }
        }

        private static void AddQuad(
                  Vector3 origin,
                  Vector3 du,
                  Vector3 dv,
                  int face,
                  int textureLayer,
                  int overlayTextureLayer,
                  Color32 tint,
                  byte skylight,
                  byte blockLight,
                  in GreedyCell cell,
                  MeshSectionBuilder target)
        {
            int vertexIndex = target.Vertices.Count;

            target.Vertices.Add(origin);
            target.Vertices.Add(origin + du);
            target.Vertices.Add(origin + dv);
            target.Vertices.Add(origin + du + dv);

            Vector3 normal = CubeNormals[face];
            target.Normals.Add(normal);
            target.Normals.Add(normal);
            target.Normals.Add(normal);
            target.Normals.Add(normal);

            AddTexture(textureLayer, overlayTextureLayer, target.Uvs, target.TextureLayers);
            AddTint(tint, target.Colors);
            AddLighting(skylight, blockLight, target.Lighting);
            AddAmbientOcclusion(cell, target.AmbientOcclusion);

            // Choose the diagonal that best preserves the corner-light gradient. Without
            // this flip, asymmetric AO produces a visible bright/dark triangle seam.
            if (cell.AmbientOcclusion0 + cell.AmbientOcclusion3 >
                cell.AmbientOcclusion1 + cell.AmbientOcclusion2)
            {
                target.Triangles.Add(vertexIndex);
                target.Triangles.Add(vertexIndex + 1);
                target.Triangles.Add(vertexIndex + 3);
                target.Triangles.Add(vertexIndex);
                target.Triangles.Add(vertexIndex + 3);
                target.Triangles.Add(vertexIndex + 2);
            }
            else
            {
                target.Triangles.Add(vertexIndex);
                target.Triangles.Add(vertexIndex + 1);
                target.Triangles.Add(vertexIndex + 2);
                target.Triangles.Add(vertexIndex + 2);
                target.Triangles.Add(vertexIndex + 1);
                target.Triangles.Add(vertexIndex + 3);
            }
        }

        private static void AddFluidQuad(
                  Vector3 origin,
                  Vector3 du,
                  Vector3 dv,
                  int face,
                  int textureLayer,
                  int overlayTextureLayer,
                  Color32 tint,
                  byte skylight,
                  byte blockLight,
                  in GreedyCell cell,
                  MeshSectionBuilder target)
        {
            Vector3 normal = (Vector3)CubeNormals[face];
            AddQuad(
                origin - normal * FluidSurfaceInset,
                du,
                dv,
                face,
                textureLayer,
                overlayTextureLayer,
                tint,
                skylight,
                blockLight,
                cell,
                target);
        }

        private static void ComputeFaceAmbientOcclusion(
            byte[] blocks,
            BlockMeshingInfo[] blockMeshingInfo,
            int currentIndex,
            int normalStride,
            int uStride,
            int vStride,
            int face,
            out byte ao0,
            out byte ao1,
            out byte ao2,
            out byte ao3)
        {
            // Sample the four corners in the empty plane immediately outside the face.
            // This is the classic block-world three-neighbour AO rule: two edge cells
            // plus their diagonal corner determine how much sky can reach each vertex.
            int outside = currentIndex + normalStride;
            bool reverseU = face == 1 || face == 3 || face == 4;
            int negativeU = reverseU ? uStride : -uStride;
            int positiveU = -negativeU;

            ao0 = EvaluateCornerAmbientOcclusion(blocks, blockMeshingInfo, outside, negativeU, -vStride);
            ao1 = EvaluateCornerAmbientOcclusion(blocks, blockMeshingInfo, outside, negativeU, vStride);
            ao2 = EvaluateCornerAmbientOcclusion(blocks, blockMeshingInfo, outside, positiveU, -vStride);
            ao3 = EvaluateCornerAmbientOcclusion(blocks, blockMeshingInfo, outside, positiveU, vStride);
        }

        private static byte EvaluateCornerAmbientOcclusion(
            byte[] blocks,
            BlockMeshingInfo[] blockMeshingInfo,
            int outside,
            int uOffset,
            int vOffset)
        {
            bool sideU = IsAmbientOcclusionOccluder(blocks[outside + uOffset], blockMeshingInfo);
            bool sideV = IsAmbientOcclusionOccluder(blocks[outside + vOffset], blockMeshingInfo);
            bool corner = IsAmbientOcclusionOccluder(blocks[outside + uOffset + vOffset], blockMeshingInfo);

            if (sideU && sideV)
                return 0;

            return (byte)(3 - (sideU ? 1 : 0) - (sideV ? 1 : 0) - (corner ? 1 : 0));
        }

        private static bool IsAmbientOcclusionOccluder(
            byte blockId,
            BlockMeshingInfo[] blockMeshingInfo)
        {
            return blockId != Chunk.BLOCK_AIR &&
                   ((uint)blockId >= (uint)blockMeshingInfo.Length ||
                    !blockMeshingInfo[blockId].IsValid ||
                    blockMeshingInfo[blockId].OccludesNeighborFaces);
        }

        private static void GetFaceTraversal(
            int face,
            int slice,
            int haloWidth,
            int haloSliceStride,
            out int sliceStart,
            out int uStride,
            out int vStride)
        {
            switch (face)
            {
                case 0:
                case 1:
                    sliceStart = 1 + haloWidth + (slice + 1) * haloSliceStride;
                    uStride = 1;
                    vStride = haloWidth;
                    break;
                case 2:
                case 3:
                    sliceStart = 1 + (slice + 1) * haloWidth + haloSliceStride;
                    uStride = 1;
                    vStride = haloSliceStride;
                    break;
                case 4:
                case 5:
                    sliceStart = slice + 1 + haloWidth + haloSliceStride;
                    uStride = haloSliceStride;
                    vStride = haloWidth;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face), face, null);
            }
        }

        private static void GetMaskSizeForFace(int face, out int width, out int height, out int slices)
        {
            switch (face)
            {
                case 0:
                case 1:
                    width = Chunk.CHUNK_SIZE;
                    height = Chunk.CHUNK_HEIGHT;
                    slices = Chunk.CHUNK_SIZE;
                    break;
                case 2:
                case 3:
                    width = Chunk.CHUNK_SIZE;
                    height = Chunk.CHUNK_SIZE;
                    slices = Chunk.CHUNK_HEIGHT;
                    break;
                case 4:
                case 5:
                    width = Chunk.CHUNK_SIZE;
                    height = Chunk.CHUNK_HEIGHT;
                    slices = Chunk.CHUNK_SIZE;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face), face, null);
            }
        }

        private static void GetQuadForFace(int face, int u, int v, int slice, int quadWidth, int quadHeight, out Vector3 origin, out Vector3 du, out Vector3 dv)
        {
            switch (face)
            {
                case 0: // back
                    origin = new Vector3(u, v, slice);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(quadWidth, 0, 0);
                    break;
                case 1: // front
                    origin = new Vector3(u + quadWidth, v, slice + 1);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(-quadWidth, 0, 0);
                    break;
                case 2: // top
                    origin = new Vector3(u, slice + 1, v);
                    du = new Vector3(0, 0, quadHeight);
                    dv = new Vector3(quadWidth, 0, 0);
                    break;
                case 3: // bottom
                    origin = new Vector3(u + quadWidth, slice, v);
                    du = new Vector3(0, 0, quadHeight);
                    dv = new Vector3(-quadWidth, 0, 0);
                    break;
                case 4: // left
                    origin = new Vector3(slice, v, u + quadWidth);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(0, 0, -quadWidth);
                    break;
                case 5: // right
                    origin = new Vector3(slice + 1, v, u);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(0, 0, quadWidth);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face), face, null);
            }
        }

        private sealed class MeshWorldState
        {
            public readonly int Epoch;
            public readonly ManualResetEventSlim DrainedEvent = new ManualResetEventSlim(initialState: true);
            public volatile bool AcceptingRequests = true;
            public int PendingRequestCount;
            public int PendingInteractiveRequestCount;
            public int PendingStreamingRequestCount;
            public int PendingBackgroundRequestCount;
            public BlockMeshingInfo[] BlockSnapshot;

            public MeshWorldState(int epoch)
            {
                Epoch = epoch;
            }
        }

        private sealed class MeshRequest
        {
            public readonly MeshWorldState World;
            public readonly BlockMeshingInfo[] BlockSnapshot;
            public readonly VoxelBuffer<byte> HaloBlocks;
            public readonly VoxelBuffer<Color32> HaloTints;
            public readonly VoxelBuffer<byte> SkylightBlocks;
            public readonly VoxelBuffer<byte> SkyOpenMap;
            public readonly VoxelBuffer<byte> BlockLightBlocks;
            public readonly Action<MeshData> Callback;
            public readonly Action<Exception> FailureCallback;
            public readonly MeshRequestPriority Priority;
            private int inputsReturned;
            private int settled;

            public MeshRequest(
                MeshWorldState world,
                BlockMeshingInfo[] blockSnapshot,
                VoxelBuffer<byte> haloBlocks,
                VoxelBuffer<Color32> haloTints,
                VoxelBuffer<byte> skylightBlocks,
                VoxelBuffer<byte> skyOpenMap,
                VoxelBuffer<byte> blockLightBlocks,
                Action<MeshData> callback,
                Action<Exception> failureCallback,
                MeshRequestPriority priority)
            {
                World = world;
                BlockSnapshot = blockSnapshot;
                HaloBlocks = haloBlocks;
                HaloTints = haloTints;
                SkylightBlocks = skylightBlocks;
                SkyOpenMap = skyOpenMap;
                BlockLightBlocks = blockLightBlocks;
                Callback = callback;
                FailureCallback = failureCallback;
                Priority = priority;
            }

            public bool CanProcess => CanInvokeCallback;

            public bool CanInvokeCallback =>
                World.AcceptingRequests && ReferenceEquals(Volatile.Read(ref currentWorld), World);

            public void Register()
            {
                switch (Priority)
                {
                    case MeshRequestPriority.Interactive:
                        Interlocked.Increment(ref World.PendingInteractiveRequestCount);
                        break;
                    case MeshRequestPriority.Streaming:
                        Interlocked.Increment(ref World.PendingStreamingRequestCount);
                        break;
                    default:
                        Interlocked.Increment(ref World.PendingBackgroundRequestCount);
                        break;
                }

                if (Interlocked.Increment(ref World.PendingRequestCount) == 1)
                    World.DrainedEvent.Reset();
            }

            public void ReturnInputs()
            {
                if (Interlocked.Exchange(ref inputsReturned, 1) != 0)
                    return;

                HaloBlocks?.ReturnToPool();
                HaloTints?.ReturnToPool();
                SkylightBlocks?.ReturnToPool();
                SkyOpenMap?.ReturnToPool();
                BlockLightBlocks?.ReturnToPool();
            }

            public void Settle()
            {
                if (Interlocked.Exchange(ref settled, 1) != 0)
                    return;

                switch (Priority)
                {
                    case MeshRequestPriority.Interactive:
                        Interlocked.Decrement(ref World.PendingInteractiveRequestCount);
                        break;
                    case MeshRequestPriority.Streaming:
                        Interlocked.Decrement(ref World.PendingStreamingRequestCount);
                        break;
                    default:
                        Interlocked.Decrement(ref World.PendingBackgroundRequestCount);
                        break;
                }

                if (Interlocked.Decrement(ref World.PendingRequestCount) == 0)
                    World.DrainedEvent.Set();
            }
        }

        private readonly struct MeshCompletion
        {
            public readonly MeshRequest Request;
            public readonly MeshData MeshData;
            public readonly Exception Exception;

            public MeshCompletion(MeshRequest request, MeshData meshData, Exception exception)
            {
                Request = request;
                MeshData = meshData;
                Exception = exception;
            }
        }

        private sealed class MeshBuilderScratch
        {
            private const int MeshHaloVolume =
                (Chunk.CHUNK_SIZE + 2) * (Chunk.CHUNK_HEIGHT + 2) * (Chunk.CHUNK_SIZE + 2);
            private const int SkylightSnapshotWidth = Chunk.CHUNK_SIZE + 2 + SkylightPadding * 2;
            private const int SkylightSnapshotHeight = Chunk.CHUNK_HEIGHT + 2;
            private const int SkylightSnapshotDepth = Chunk.CHUNK_SIZE + 2 + SkylightPadding * 2;
            private const int SkylightSnapshotVolume =
                SkylightSnapshotWidth * SkylightSnapshotHeight * SkylightSnapshotDepth;
            private const int BlockLightSnapshotWidth = Chunk.CHUNK_SIZE + 2 + BlockLightPadding * 2;
            private const int BlockLightSnapshotHeight = Chunk.CHUNK_HEIGHT + 2 + BlockLightPadding * 2;
            private const int BlockLightSnapshotDepth = Chunk.CHUNK_SIZE + 2 + BlockLightPadding * 2;
            private const int BlockLightSnapshotVolume =
                BlockLightSnapshotWidth * BlockLightSnapshotHeight * BlockLightSnapshotDepth;

            public readonly GreedyCell[] Mask = new GreedyCell[
                Math.Max(Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT, Chunk.CHUNK_SIZE * Chunk.CHUNK_SIZE)];
            public readonly byte[] Skylight = new byte[MeshHaloVolume];
            public readonly byte[] SkylightWork = new byte[SkylightSnapshotVolume];
            public readonly byte[] BlockLightHalo = new byte[MeshHaloVolume];
            public readonly byte[] BlockLightWork = new byte[BlockLightSnapshotVolume];
            public readonly int[] BlockLightSources = new int[BlockLightSnapshotVolume];
            public readonly int[] LightingQueue = new int[BlockLightSnapshotVolume];
            public readonly bool[] LightingQueued = new bool[BlockLightSnapshotVolume];
            public readonly MeshSectionBuilder Solid = new MeshSectionBuilder(2048, 3072);
            public readonly MeshSectionBuilder Fluid = new MeshSectionBuilder(256, 384);
            public readonly MeshSectionBuilder LavaFluid = new MeshSectionBuilder(128, 192);
            public readonly MeshSectionBuilder Transparent = new MeshSectionBuilder(512, 768);

            public void Reset()
            {
                Solid.Clear();
                Fluid.Clear();
                LavaFluid.Clear();
                Transparent.Clear();
            }
        }

        private sealed class MeshSectionBuilder
        {
            public readonly List<int> Triangles;
            public readonly List<Vector3> Vertices;
            public readonly List<Vector3> Normals;
            public readonly List<Vector2> Uvs;
            public readonly List<Vector2> TextureLayers;
            public readonly List<Vector2> Lighting;
            public readonly List<Vector2> AmbientOcclusion;
            public readonly List<Color32> Colors;

            public MeshSectionBuilder(int vertexCapacity, int indexCapacity)
            {
                Triangles = new List<int>(indexCapacity);
                Vertices = new List<Vector3>(vertexCapacity);
                Normals = new List<Vector3>(vertexCapacity);
                Uvs = new List<Vector2>(vertexCapacity);
                TextureLayers = new List<Vector2>(vertexCapacity);
                Lighting = new List<Vector2>(vertexCapacity);
                AmbientOcclusion = new List<Vector2>(vertexCapacity);
                Colors = new List<Color32>(vertexCapacity);
            }

            public void Clear()
            {
                Triangles.Clear();
                Vertices.Clear();
                Normals.Clear();
                Uvs.Clear();
                TextureLayers.Clear();
                Lighting.Clear();
                AmbientOcclusion.Clear();
                Colors.Clear();
            }

            public MeshSection ToMeshSection()
            {
                return new MeshSection(Triangles, Vertices, Normals, Uvs, TextureLayers, Lighting, AmbientOcclusion, Colors);
            }
        }

        private static BlockMeshingInfo[] CreateBlockMeshingSnapshot()
        {
            if (Thread.CurrentThread.ManagedThreadId != Volatile.Read(ref mainThreadId))
                throw new InvalidOperationException("BlockData may only be snapshotted on the Unity main thread.");

            BlockData[] blockDefinitions = AssetsContainer.Instance.Blocks;
            var snapshot = new BlockMeshingInfo[blockDefinitions.Length];

            for (int blockId = 0; blockId < blockDefinitions.Length; blockId++)
            {
                BlockData block = blockDefinitions[blockId];
                if (block == null)
                    continue;

                snapshot[blockId] = new BlockMeshingInfo(
                    rendersCube: !block.UsesCustomModel,
                    isFluid: block.IsFluid,
                    isTransparent: block.IsTransparent,
                    isFullBlock: block.IsFullBlock,
                    occludesNeighborFaces: block.OccludesNeighborFaces,
                    lightEmission: (byte)Math.Max(0, Math.Min(MaximumBlockLight, block.LightEmission)),
                    definitionId: block.id,
                    back: CreateTextureLayerPair(block.GetTexture(0)),
                    front: CreateTextureLayerPair(block.GetTexture(1)),
                    top: CreateTextureLayerPair(block.GetTexture(2)),
                    bottom: CreateTextureLayerPair(block.GetTexture(3)),
                    left: CreateTextureLayerPair(block.GetTexture(4)),
                    right: CreateTextureLayerPair(block.GetTexture(5)));
            }

            return snapshot;
        }

        private static TextureLayerPair CreateTextureLayerPair(BlockData.FaceTextureData texture)
        {
            return new TextureLayerPair(
                Math.Max(0, texture.TextureLayer),
                Math.Max(0, texture.OverlayTextureLayer));
        }

        private readonly struct TextureLayerPair
        {
            public readonly int TextureLayer;
            public readonly int OverlayTextureLayer;

            public TextureLayerPair(int textureLayer, int overlayTextureLayer)
            {
                TextureLayer = textureLayer;
                OverlayTextureLayer = overlayTextureLayer;
            }
        }

        private readonly struct BlockMeshingInfo
        {
            public readonly bool IsValid;
            public readonly bool RendersCube;
            public readonly bool IsFluid;
            public readonly bool IsTransparent;
            public readonly bool IsFullBlock;
            public readonly bool OccludesNeighborFaces;
            public readonly byte LightEmission;
            public readonly ushort DefinitionId;
            private readonly TextureLayerPair back;
            private readonly TextureLayerPair front;
            private readonly TextureLayerPair top;
            private readonly TextureLayerPair bottom;
            private readonly TextureLayerPair left;
            private readonly TextureLayerPair right;

            public BlockMeshingInfo(
                bool rendersCube,
                bool isFluid,
                bool isTransparent,
                bool isFullBlock,
                bool occludesNeighborFaces,
                byte lightEmission,
                ushort definitionId,
                TextureLayerPair back,
                TextureLayerPair front,
                TextureLayerPair top,
                TextureLayerPair bottom,
                TextureLayerPair left,
                TextureLayerPair right)
            {
                IsValid = true;
                RendersCube = rendersCube;
                IsFluid = isFluid;
                IsTransparent = isTransparent;
                IsFullBlock = isFullBlock;
                OccludesNeighborFaces = occludesNeighborFaces;
                LightEmission = lightEmission;
                DefinitionId = definitionId;
                this.back = back;
                this.front = front;
                this.top = top;
                this.bottom = bottom;
                this.left = left;
                this.right = right;
            }

            public TextureLayerPair GetTexture(int face)
            {
                return face switch
                {
                    0 => back,
                    1 => front,
                    2 => top,
                    3 => bottom,
                    4 => left,
                    5 => right,
                    _ => back,
                };
            }
        }

        internal struct GreedyCell
        {
            public bool Valid;
            public int BlockId;
            public int TextureLayer;
            public int OverlayTextureLayer;
            public Color32 Tint;
            public byte Skylight;
            public byte BlockLight;
            public byte AmbientOcclusion0;
            public byte AmbientOcclusion1;
            public byte AmbientOcclusion2;
            public byte AmbientOcclusion3;
            public bool IsFluid;
            public bool IsTransparent;

            public bool Matches(in GreedyCell other)
            {
                return Valid
                    && other.Valid
                    && BlockId == other.BlockId
                    && TextureLayer == other.TextureLayer
                    && OverlayTextureLayer == other.OverlayTextureLayer
                    && Tint.r == other.Tint.r
                    && Tint.g == other.Tint.g
                    && Tint.b == other.Tint.b
                    && Tint.a == other.Tint.a
                    && Skylight == other.Skylight
                    && BlockLight == other.BlockLight
                    && AmbientOcclusion0 == other.AmbientOcclusion0
                    && AmbientOcclusion1 == other.AmbientOcclusion1
                    && AmbientOcclusion2 == other.AmbientOcclusion2
                    && AmbientOcclusion3 == other.AmbientOcclusion3
                    && IsFluid == other.IsFluid
                    && IsTransparent == other.IsTransparent;
            }
        }

        private static void AddTexture(int textureLayerIndex, int overlayTextureLayerIndex, List<Vector2> uvs, List<Vector2> textureLayers)
        {
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(0f, 1f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));

            Vector2 textureLayer = new Vector2(textureLayerIndex, overlayTextureLayerIndex);
            textureLayers.Add(textureLayer);
            textureLayers.Add(textureLayer);
            textureLayers.Add(textureLayer);
            textureLayers.Add(textureLayer);
        }

        private static void AddTint(Color32 tint, List<Color32> colors)
        {
            if (tint.a == 0)
                tint = WhiteTint;

            colors.Add(tint);
            colors.Add(tint);
            colors.Add(tint);
            colors.Add(tint);
        }

        private static void AddLighting(byte skylight, byte blockLight, List<Vector2> lighting)
        {
            Vector2 value = new Vector2(
                skylight / (float)MaximumSkylight,
                blockLight / (float)MaximumBlockLight);
            lighting.Add(value);
            lighting.Add(value);
            lighting.Add(value);
            lighting.Add(value);
        }

        private static void AddAmbientOcclusion(in GreedyCell cell, List<Vector2> ambientOcclusion)
        {
            const float InverseMaximumAo = 1f / 3f;
            ambientOcclusion.Add(new Vector2(cell.AmbientOcclusion0 * InverseMaximumAo, 0f));
            ambientOcclusion.Add(new Vector2(cell.AmbientOcclusion1 * InverseMaximumAo, 0f));
            ambientOcclusion.Add(new Vector2(cell.AmbientOcclusion2 * InverseMaximumAo, 0f));
            ambientOcclusion.Add(new Vector2(cell.AmbientOcclusion3 * InverseMaximumAo, 0f));
        }


        public static readonly Vector3[] CubeVertices = new Vector3[8] {
        new Vector3(0.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 1.0f, 1.0f),
        new Vector3(0.0f, 1.0f, 1.0f),
    };

        public static readonly Vector3Int[] CubeNormals = new Vector3Int[6] {
        new Vector3Int(0, 0, -1),
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 1, 0),
        new Vector3Int(0, -1, 0),
        new Vector3Int(-1, 0, 0),
        new Vector3Int(1, 0, 0)
    };

        public static readonly int[,] CubeTriangles = new int[6, 4] {
        // Back, Front, Top, Bottom, Left, Right

		// 0 1 2 2 1 3
		{0, 3, 1, 2}, // Back Face
		{5, 6, 4, 7}, // Front Face
		{3, 7, 2, 6}, // Top Face
		{1, 5, 0, 4}, // Bottom Face
		{4, 7, 0, 3}, // Left Face
		{1, 2, 5, 6} // Right Face
	};

        public static readonly Vector2[] CubeUVs = new Vector2[4] {
        new Vector2 (0.0f, 0.0f),
        new Vector2 (0.0f, 1.0f),
        new Vector2 (1.0f, 0.0f),
        new Vector2 (1.0f, 1.0f)
    };
    }

    public readonly struct MeshData
    {
        public readonly MeshSection SolidMesh;
        public readonly MeshSection FluidMesh;
        public readonly MeshSection LavaFluidMesh;
        public readonly MeshSection TransparentMesh;
        // One packed byte per voxel: low nibble skylight, high nibble block light.
        public readonly byte[] VoxelLighting;

        public MeshData(
            MeshSection solidMesh,
            MeshSection fluidMesh,
            MeshSection lavaFluidMesh,
            MeshSection transparentMesh,
            byte[] voxelLighting)
        {
            this.SolidMesh = solidMesh;
            this.FluidMesh = fluidMesh;
            this.LavaFluidMesh = lavaFluidMesh;
            this.TransparentMesh = transparentMesh;
            VoxelLighting = voxelLighting;
        }
    }

    public readonly struct MeshSection
    {
        public readonly int[] Triangles;
        public readonly Vector3[] Vertices;
        public readonly Vector3[] Normals;
        public readonly Vector2[] Uvs;
        public readonly Vector2[] TextureLayers;
        public readonly Vector2[] Lighting;
        public readonly Vector2[] AmbientOcclusion;
        public readonly Color32[] Colors;

        public MeshSection(
            List<int> triangles,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Vector2> textureLayers,
            List<Vector2> lighting,
            List<Vector2> ambientOcclusion,
            List<Color32> colors)
        {
            this.Triangles = triangles.ToArray();
            this.Vertices = vertices.ToArray();
            this.Normals = normals.ToArray();
            this.Uvs = uvs.ToArray();
            this.TextureLayers = textureLayers.ToArray();
            this.Lighting = lighting.ToArray();
            this.AmbientOcclusion = ambientOcclusion.ToArray();
            this.Colors = colors.ToArray();
        }
    }
}
