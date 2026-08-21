using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;
using Unity.Profiling;
using System.Collections;
using UnityEngine.InputSystem;
using BenScr.CubeDash;

namespace BenScr.MinecraftClone
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using TMPro;
    using Unity.Mathematics;
    using UnityEngine;
    using UnityEngine.UI;

    internal enum ChunkRuntimeWorkKind : byte
    {
        SurfaceSampling,
        TargetCommit,
        ChunkCreation,
        BlockDataScheduling,
        ForegroundBlockDataCompletion,
        HarvestBlockDataCompletion,
        InteractiveMeshSubmission,
        StreamingMeshSubmission,
        BackgroundMeshSubmission,
        InteractiveMeshApplication,
        StreamingMeshApplication,
        BackgroundMeshApplication,
        ColliderCooking,
        ChunkEviction,
        Count
    }

    public class TerrainGenerator : MonoBehaviour
    {
        [FormerlySerializedAs("chunkPrefab")]
        public GameObject ChunkPrefab;
        [FormerlySerializedAs("addColliders")]
        public bool AddColliders = false;
        [FormerlySerializedAs("addTrees")]
        public bool AddTrees = true;

        [Header("Initialization")]
        [SerializeField] private int preloadViewDistance = 5;
        [SerializeField] private int preloadViewDistanceY = 2;

        [Header("General")]

        [SerializeField] private int viewDistance = 5;
        [SerializeField] private int viewDistanceY = 2;
        [SerializeField, Tooltip("Keeps visible terrain columns connected down to the world's bedrock chunk instead of only loading a thin band around the player's height.")]
        private bool loadTerrainColumnsToBedrock = true;
        [SerializeField, Tooltip("Skips deep underground chunks that are neither near the player nor near the visible terrain or water surface.")]
        private bool skipHiddenUndergroundChunks = true;
        [SerializeField, Min(0), Tooltip("Extra generated chunk layers below the sampled surface. Keeps steep cliffs and shallow openings stable without loading whole columns.")]
        private int visibleSurfaceChunkDepth = 1;
        [SerializeField, Min(0), Tooltip("Extra generated chunk layers above the sampled surface. Keeps trees, structures, and water surfaces from popping at chunk boundaries.")]
        private int visibleSurfaceChunkHeadroom = 1;
        [SerializeField, Min(0), Tooltip("Vertical chunk range loaded around the player when hidden underground chunks are skipped.")]
        private int hiddenUndergroundPlayerRangeY = 1;
        [SerializeField, Min(0), Tooltip("Horizontal chunk radius that also keeps the player's underground vertical band loaded. Distant columns only load their sampled surface band.")]
        private int hiddenUndergroundPlayerRangeXZ = 3;
        [SerializeField] private bool shouldDisableChunks = true;

        [Header("Chunk Residency")]
        [SerializeField, Tooltip("Releases generated chunks that are safely outside the view and simulation ranges. Modified chunks are retained until their data can be staged for persistence.")]
        private bool unloadDistantChunks = true;
        [SerializeField, Min(1), Tooltip("Extra horizontal chunk rings retained beyond the largest render or simulation radius to avoid boundary churn.")]
        private int chunkRetentionMargin = 2;
        [SerializeField, Min(1), Tooltip("Maximum distant chunks released in one frame.")]
        private int maxChunkUnloadsPerFrame = 2;

        [SerializeField] private int maxChunksCreatePerFrame = 2;
        [SerializeField] private int maxChunksGeneratePerFrame = 2;
        [SerializeField, Min(1), Tooltip("Logical chunks are cheap during the loading screen because views are created lazily.")]
        private int maxChunksCreatePerLoadingFrame = 16;
        [SerializeField, Min(1), Tooltip("Maximum mesh requests started per loading-screen frame.")]
        private int maxChunksGeneratePerLoadingFrame = 6;
        [SerializeField, Min(1f), Tooltip("Soft main-thread budget used while preparing chunks behind the loading screen.")]
        private float loadingFrameBudgetMilliseconds = 8f;
        [SerializeField, Min(1), Tooltip("Maximum chunk block-data jobs allocated/scheduled per frame during loading and runtime streaming.")]
        private int maxChunkDataSchedulesPerFrame = 8;
        [SerializeField, Min(1), Tooltip("Maximum completed block-data jobs finalized on the main thread per frame.")]
        private int maxBlockDataCompletionsPerFrame = 2;
        [SerializeField, Min(1), Tooltip("Maximum completed block-data jobs finalized per loading-screen frame.")]
        private int maxBlockDataCompletionsPerLoadingFrame = 8;
        [SerializeField, Min(1)] private int maxDirtyChunkMeshesPerFrame = 4;
        [SerializeField, Min(1), Tooltip("Maximum player-edited chunk meshes submitted per frame before background rebuilds.")]
        private int maxInteractiveChunkMeshesPerFrame = 4;
        [SerializeField, Min(0f)] private float addColliderDistance = 10f;
        [SerializeField, Min(1), Tooltip("Maximum chunk collider components/cooks started per frame.")]
        private int maxColliderAddsPerFrame = 1;

        [Header("Adaptive Runtime Streaming")]
        [SerializeField, Tooltip("Adjusts optional runtime chunk work from measured frame headroom. Loading-screen generation keeps its separate fixed budget.")]
        private bool useAdaptiveRuntimeChunkBudget = true;
        [SerializeField, Min(0.05f), Tooltip("Initial main-thread time allocated to runtime chunk work each frame.")]
        private float initialRuntimeChunkBudgetMilliseconds = 0.5f;
        [SerializeField, Min(0.01f), Tooltip("Minimum runtime chunk-work allowance. Starvation protection still permits occasional progress when this is exhausted.")]
        private float minimumRuntimeChunkBudgetMilliseconds = 0.15f;
        [SerializeField, Min(0.1f), Tooltip("Absolute upper bound for runtime chunk work. The controller also limits this to a fraction of the target frame time.")]
        private float maximumRuntimeChunkBudgetMilliseconds = 2f;
        [SerializeField, Range(0.05f, 0.5f), Tooltip("Largest fraction of the target frame time that runtime chunk work may consume.")]
        private float maximumRuntimeChunkBudgetFrameFraction = 0.2f;
        [SerializeField, Range(0.5f, 1f), Tooltip("Reduce streaming when rolling P95 frame time exceeds this fraction of the target frame time.")]
        private float runtimeBudgetOverloadRatio = 0.9f;
        [SerializeField, Range(0.25f, 0.95f), Tooltip("Increase streaming only while rolling P95 frame time remains below this fraction of the target frame time.")]
        private float runtimeBudgetHealthyRatio = 0.7f;
        [SerializeField, Range(0.1f, 0.95f), Tooltip("Multiplier applied after sustained frame-time pressure.")]
        private float runtimeBudgetDecreaseMultiplier = 0.65f;
        [SerializeField, Min(0.01f), Tooltip("Milliseconds added after several seconds of sustained frame-time headroom.")]
        private float runtimeBudgetIncreaseStepMilliseconds = 0.05f;
        [SerializeField, Min(0.1f)] private float runtimeBudgetEvaluationInterval = 0.5f;
        [SerializeField, Min(1), Tooltip("Maximum uncached terrain columns sampled in one runtime frame, in addition to the adaptive time limit.")]
        private int maxSurfaceSpanSamplesPerFrame = 4;
        [SerializeField, Min(8), Tooltip("Maximum cached or uncached target columns incorporated per runtime frame.")]
        private int maxTargetColumnsProcessedPerFrame = 96;
        [SerializeField, Min(8), Tooltip("Maximum target coordinates copied or transitioned per runtime frame. Large view-distance changes are spread across multiple frames.")]
        private int maxTargetCoordinatesProcessedPerFrame = 64;
        [SerializeField] private PlayerController player;
        [SerializeField] private Image loadTerrainImage;
        [SerializeField] private TextMeshProUGUI loadTerrainTxt;

        [Header("Fluid Simulation")]
        [FormerlySerializedAs("simulateWater")]
        [SerializeField] private bool simulateFluids = true;
        [SerializeField, Min(0.05f), Tooltip("Seconds between visible water spread steps. Minecraft water uses 5 game ticks, about 0.25 seconds.")]
        private float waterTickInterval = 0.25f;
        [SerializeField, Min(1), Tooltip("Maximum fluid block updates processed per spread step.")]
        private int maxWaterBlocksPerTick = 128;
        [SerializeField, Range(1, 7), Tooltip("Maximum horizontal X/Z water depth from a source. Minecraft water uses 7; lava uses 3 in this overworld-like dimension.")]
        private int maxFluidHorizontalSpreadDistance = 7;
        [SerializeField, Min(0), Tooltip("Chunk radius around the player where water and lava are simulated. This is independent from terrain render distance.")]
        private int fluidSimulationRange = 5;
        [SerializeField, Min(0), Tooltip("Vertical chunk radius around the player where water and lava are simulated.")]
        private int fluidSimulationRangeY = 2;

        [Header("Falling Blocks")]
        [SerializeField] private bool simulateFallingBlocks = true;
        [SerializeField, Min(0.02f)] private float fallingBlockTickInterval = 0.1f;
        [SerializeField, Min(1)] private int maxFallingBlocksPerTick = 64;
        [SerializeField, Min(0), Tooltip("Chunk radius around the player where unsupported falling blocks are checked and active falling block entities are simulated.")]
        private int fallingBlockSimulationRange = 3;

        public static Action OnLoadedTerrain;
        public static TerrainGenerator Instance { get; private set; }
        public static bool IsWorldReady { get; private set; }
        internal static int CurrentWorldEpoch => Instance != null ? Instance.meshWorldEpoch : 0;

        public static readonly Dictionary<Vector3Int, Chunk> Chunks = new(2048);
        private static readonly Vector3Int[] NeighborChunkOffsets =
        {
            Vector3Int.right,
            Vector3Int.left,
            Vector3Int.forward,
            Vector3Int.back,
            Vector3Int.up,
            Vector3Int.down
        };

        private readonly HashSet<Vector3Int> lastActiveChunks = new();
        private HashSet<Vector3Int> currentActiveChunks = new();
        private List<Vector3Int> currentActiveCoordinateBuffer = new(1024);
        private List<Vector3Int> nextActiveCoordinateBuffer = new(1024);
        private HashSet<Vector3Int> pendingTargetChunks = new();
        private readonly List<Vector3Int> pendingTargetCoordinateBuffer = new(1024);
        private readonly HashSet<Vector3Int> urgentTargetChunks = new();
        private readonly HashSet<Vector3Int> urgentActivatedChunks = new();
        private readonly Vector3Int[] urgentTargetCoordinateBuffer = new Vector3Int[7];
        private readonly List<Vector3Int> urgentActivationRetentionBuffer = new(7);
        private readonly List<Vector3Int> previousTargetCoordinateBuffer = new(1024);
        private readonly HashSet<Chunk> dirtyMeshChunks = new();
        private readonly List<Chunk> dirtyMeshChunkBuffer = new(64);
        private readonly HashSet<Chunk> interactiveDirtyMeshChunks = new();
        private readonly Queue<Chunk> interactiveDirtyMeshQueue = new(8);
        private readonly HashSet<Chunk> interactiveSourceMeshChunks = new();
        private readonly HashSet<Chunk> scheduledBlockDataChunks = new();
        private readonly Queue<Chunk> scheduledBlockDataQueue = new(128);
        private readonly List<Chunk> scheduledBlockDataChunkBuffer = new(128);
        private readonly Dictionary<Vector2Int, SurfaceChunkSpan> surfaceChunkSpanCache = new();
        private readonly Queue<Vector3Int> chunksToUnload = new(256);
        private readonly HashSet<Vector3Int> queuedForUnload = new();
        private readonly Queue<Vector3Int> residentChunkScanQueue = new(512);
        private readonly HashSet<Vector3Int> residentChunkScanSet = new();
        private readonly HashSet<Vector3Int> registeredResidentChunks = new();
        private readonly Dictionary<Vector2Int, int> residentChunkCountsByColumn = new();
        private readonly HashSet<Vector3Int> nearbyColliderChunks = new();
        private readonly HashSet<Vector3Int> previousNearbyColliderChunks = new();
        private readonly List<ColliderCandidate> nearbyColliderCandidates = new(32);

        private const int TerrainHeightSampleBufferCapacity = 25;
        private NativeArray<int> terrainSampleHeightMap;
        private NativeArray<byte> terrainSampleBiomeMap;
        private NativeArray<byte> terrainSampleSurfaceBiomeMap;
        private NativeArray<byte> terrainSampleBiomeBlendMap;
        private NativeArray<byte> terrainSampleDesertEdgeMap;
        private NativeArray<byte> terrainSampleRiverMap;
        private NativeArray<int> terrainSampleRiverSurfaceMap;

        // (Ben-Scr) Missing chunks that need Prepare()
        private readonly List<Vector3Int> chunksToCreate = new(512);
        private int createIndex;
        private readonly Queue<Vector3Int> urgentChunksToCreate = new(8);
        private readonly HashSet<Vector3Int> queuedForUrgentCreate = new();

        // (Ben-Scr) Existing prepared chunks that still need Generate()
        private readonly List<Vector3Int> chunksToGenerate = new(512);
        private int generateIndex;
        private readonly Queue<Vector3Int> urgentChunksToGenerate = new(8);
        private readonly HashSet<Vector3Int> queuedForUrgentGenerate = new();
        private readonly Queue<Vector3Int> urgentChunksToActivate = new(8);
        private readonly HashSet<Vector3Int> queuedForUrgentActivation = new();

        // (Ben-Scr) Prevent duplicates in work lists
        private readonly HashSet<Vector3Int> queuedForCreate = new();
        private readonly HashSet<Vector3Int> queuedForGenerate = new();

        private Transform playerTransform;
        private Vector3Int[] horizontalOffsets;

        private int viewDistanceXZSq;
        private float addColliderDistanceSq;

        private int lastViewDistance = -1;
        private int lastViewDistanceY = -1;

        private Vector3Int lastPlayerChunk = new(int.MinValue, int.MinValue, int.MinValue);
        private bool loadedTerrain;
        private int meshWorldEpoch;
        private bool meshWorldShutdown;
        private int blockDataSchedulesRemaining;
        private int blockDataCompletionsRemaining;
        private int harvestBlockDataCompletionsRemaining;
        private int lastLoadingPercentage = -1;
        private int interactiveMeshSubmissionFrame = -1;
        private int interactiveMeshSubmissionsRemaining;
        private AdaptiveRuntimeChunkBudget adaptiveRuntimeChunkBudget;
        private bool targetRebuildPending;
        private Vector3Int targetRebuildCenter = new(int.MinValue, int.MinValue, int.MinValue);
        private int targetRebuildHorizontalIndex;
        private TargetRebuildPhase targetRebuildPhase;
        private int targetActiveSnapshotIndex;
        private int targetActiveSnapshotCount;
        private int targetTransitionCoordinateIndex;
        private int targetRemovalCoordinateIndex;
        private readonly ChunkYRange[] targetColumnRanges = new ChunkYRange[2];
        private bool targetColumnPrepared;
        private int targetColumnRangeCount;
        private int targetColumnRangeIndex;
        private int targetColumnNextY;
        private int targetColumnX;
        private int targetColumnZ;
        private const int MaxScheduledBlockJobsInspectedPerFrame = 64;
        private const int TargetTransitionBatchSize = 4;

        private static readonly ProfilerMarker SurfaceSpanSampleMarker = new("VoxelBuilder.Streaming.SurfaceSpanSample");
        private static readonly ProfilerMarker TargetCommitMarker = new("VoxelBuilder.Streaming.TargetCommit");
        private static readonly ProfilerMarker ChunkEvictionMarker = new("VoxelBuilder.Streaming.ChunkEviction");

        private struct SurfaceChunkSpan
        {
            public int MinChunkY;
            public int MaxChunkY;
        }

        private struct ChunkYRange
        {
            public int Min;
            public int Max;
        }

        private enum TargetRebuildPhase : byte
        {
            None,
            Sampling,
            SnapshotCurrent,
            ApplyNew,
            RemoveOld
        }

        private struct ColliderCandidate
        {
            public Chunk Chunk;
            public float DistanceSquared;
        }

        private sealed class AdaptiveRuntimeChunkBudget : IDisposable
        {
            private const int FrameSampleCapacity = 180;
            private const int MinimumFrameSamples = 30;
            private const int StarvationIntervalFrames = 8;
            private const float HealthyDurationSeconds = 3f;
            private readonly float[] frameSamples = new float[FrameSampleCapacity];
            private readonly float[] sortedFrameSamples = new float[FrameSampleCapacity];
            private static readonly double[] DefaultEstimatedWorkMilliseconds =
            {
                0.12d, // SurfaceSampling
                0.08d, // TargetCommit (a bounded transition batch)
                0.15d, // ChunkCreation
                0.12d, // BlockDataScheduling
                0.45d, // ForegroundBlockDataCompletion
                0.45d, // HarvestBlockDataCompletion
                0.55d, // InteractiveMeshSubmission
                0.55d, // StreamingMeshSubmission
                0.55d, // BackgroundMeshSubmission
                0.75d, // InteractiveMeshApplication
                0.75d, // StreamingMeshApplication
                0.75d, // BackgroundMeshApplication
                0.75d, // ColliderCooking
                0.45d  // ChunkEviction
            };
            private readonly double[] estimatedWorkMilliseconds =
                (double[])DefaultEstimatedWorkMilliseconds.Clone();
            private readonly int[] lastProgressFrames = new int[(int)ChunkRuntimeWorkKind.Count];

            private ProfilerRecorder mainThreadTimeRecorder;
            private bool recorderStarted;
            private bool frameActive;
            private bool usedProfilerFrameTime;
            private bool urgentBypassUsedThisFrame;
            private bool starvationBypassUsedThisFrame;
            private int frameSampleCount;
            private int nextFrameSampleIndex;
            private int overloadEvaluationCount;
            private int healthyEvaluationCount;
            private float nextEvaluationTime;
            private float smoothedFrameMilliseconds;
            private float percentile95FrameMilliseconds;
            private double currentBudgetMilliseconds;
            private double spentMilliseconds;
            private double reservedMilliseconds;

            public bool FrameActive => frameActive;

            public void Initialize(TerrainGenerator owner)
            {
                StopRecorder();
                try
                {
                    mainThreadTimeRecorder = ProfilerRecorder.StartNew(
                        ProfilerCategory.Internal,
                        "CPU Main Thread Frame Time",
                        1);
                    recorderStarted = mainThreadTimeRecorder.Valid;
                }
                catch (Exception exception)
                {
                    recorderStarted = false;
                    Debug.LogWarning($"Could not start the runtime chunk frame-time recorder: {exception.Message}");
                }

                Array.Clear(frameSamples, 0, frameSamples.Length);
                frameSampleCount = 0;
                nextFrameSampleIndex = 0;
                overloadEvaluationCount = 0;
                healthyEvaluationCount = 0;
                nextEvaluationTime = Time.unscaledTime + Mathf.Max(0.1f, owner.runtimeBudgetEvaluationInterval);
                smoothedFrameMilliseconds = 0f;
                percentile95FrameMilliseconds = 0f;
                Array.Copy(
                    DefaultEstimatedWorkMilliseconds,
                    estimatedWorkMilliseconds,
                    estimatedWorkMilliseconds.Length);
                for (int i = 0; i < lastProgressFrames.Length; i++)
                    lastProgressFrames[i] = -StarvationIntervalFrames;
                currentBudgetMilliseconds = Mathf.Max(
                    0.01f,
                    owner.initialRuntimeChunkBudgetMilliseconds);
                ResetFrameState(active: false);
            }

            public void BeginFrame(TerrainGenerator owner, bool hasBacklog)
            {
                ResetFrameState(owner.useAdaptiveRuntimeChunkBudget && owner.loadedTerrain);
                if (!frameActive)
                    return;

                // Keep the current allowance while paused, but do not learn from a
                // menu/loading frame whose timing does not represent normal play.
                if (GameController.IsFrozen)
                    return;

                float frameMilliseconds = ReadPreviousFrameMilliseconds(out usedProfilerFrameTime);
                if (frameMilliseconds > 0f && frameMilliseconds < 250f)
                    AddFrameSample(frameMilliseconds);

                int configuredTargetFps = Application.targetFrameRate;
                float targetFrameMilliseconds = 1000f / Mathf.Max(1, configuredTargetFps > 0 ? configuredTargetFps : 60);
                float minimumBudget = Mathf.Max(0.01f, owner.minimumRuntimeChunkBudgetMilliseconds);
                float maximumBudget = Mathf.Max(
                    minimumBudget,
                    Mathf.Min(
                        Mathf.Max(minimumBudget, owner.maximumRuntimeChunkBudgetMilliseconds),
                        targetFrameMilliseconds * Mathf.Clamp(owner.maximumRuntimeChunkBudgetFrameFraction, 0.05f, 0.5f)));
                currentBudgetMilliseconds = Math.Max(
                    minimumBudget,
                    Math.Min(maximumBudget, currentBudgetMilliseconds));

                if (frameSampleCount < MinimumFrameSamples || Time.unscaledTime < nextEvaluationTime)
                    return;

                float evaluationInterval = Mathf.Max(0.1f, owner.runtimeBudgetEvaluationInterval);
                nextEvaluationTime = Time.unscaledTime + evaluationInterval;
                UpdatePercentile95();

                float overloadThreshold = targetFrameMilliseconds * (usedProfilerFrameTime
                    ? Mathf.Clamp(owner.runtimeBudgetOverloadRatio, 0.5f, 1f)
                    : 1.1f);
                float healthyThreshold = targetFrameMilliseconds * (usedProfilerFrameTime
                    ? Mathf.Clamp(owner.runtimeBudgetHealthyRatio, 0.25f, 0.95f)
                    : 0.8f);
                bool severeSpike = percentile95FrameMilliseconds > targetFrameMilliseconds * 1.35f;
                bool overloaded = percentile95FrameMilliseconds > overloadThreshold ||
                                  smoothedFrameMilliseconds > overloadThreshold;
                bool healthy = hasBacklog &&
                               percentile95FrameMilliseconds < healthyThreshold &&
                               smoothedFrameMilliseconds < healthyThreshold;

                if (overloaded)
                {
                    overloadEvaluationCount++;
                    healthyEvaluationCount = 0;
                    if (severeSpike || overloadEvaluationCount >= 2)
                    {
                        currentBudgetMilliseconds = Math.Max(
                            minimumBudget,
                            currentBudgetMilliseconds * Mathf.Clamp(owner.runtimeBudgetDecreaseMultiplier, 0.1f, 0.95f));
                        overloadEvaluationCount = 0;
                    }
                }
                else if (healthy)
                {
                    overloadEvaluationCount = 0;
                    healthyEvaluationCount++;
                    int evaluationsRequired = Mathf.Max(
                        1,
                        Mathf.CeilToInt(HealthyDurationSeconds / evaluationInterval));
                    if (healthyEvaluationCount >= evaluationsRequired)
                    {
                        currentBudgetMilliseconds = Math.Min(
                            maximumBudget,
                            currentBudgetMilliseconds + Mathf.Max(0.01f, owner.runtimeBudgetIncreaseStepMilliseconds));
                        healthyEvaluationCount = 0;
                    }
                }
                else
                {
                    overloadEvaluationCount = 0;
                    healthyEvaluationCount = 0;
                }
            }

            public bool TryBegin(
                ChunkRuntimeWorkKind kind,
                bool urgent,
                bool allowStarvation,
                out long startedAt)
            {
                startedAt = 0L;
                if (!frameActive)
                    return true;

                int kindIndex = Mathf.Clamp((int)kind, 0, estimatedWorkMilliseconds.Length - 1);
                double predictedMilliseconds = estimatedWorkMilliseconds[kindIndex];
                if (spentMilliseconds + reservedMilliseconds + predictedMilliseconds <= currentBudgetMilliseconds)
                {
                    reservedMilliseconds += predictedMilliseconds;
                    startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    return true;
                }

                if (urgent && !urgentBypassUsedThisFrame)
                {
                    urgentBypassUsedThisFrame = true;
                    reservedMilliseconds += predictedMilliseconds;
                    startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    return true;
                }

                int frame = Time.frameCount;
                if (allowStarvation &&
                    !starvationBypassUsedThisFrame &&
                    frame - lastProgressFrames[kindIndex] >= StarvationIntervalFrames)
                {
                    starvationBypassUsedThisFrame = true;
                    reservedMilliseconds += predictedMilliseconds;
                    startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    return true;
                }

                return false;
            }

            public void Complete(ChunkRuntimeWorkKind kind, long startedAt)
            {
                if (!frameActive || startedAt == 0L)
                    return;

                long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
                double elapsedMilliseconds = elapsedTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMilliseconds < 0d)
                    return;

                int kindIndex = Mathf.Clamp((int)kind, 0, estimatedWorkMilliseconds.Length - 1);
                reservedMilliseconds = Math.Max(
                    0d,
                    reservedMilliseconds - estimatedWorkMilliseconds[kindIndex]);
                spentMilliseconds += elapsedMilliseconds;
                lastProgressFrames[kindIndex] = Time.frameCount;

                // Coalesced submissions and stale mesh callbacks intentionally do
                // almost no work. They still consume their measured frame time, but
                // must not teach the next real snapshot/upload that the operation is
                // nearly free. Keep estimates conservative until a representative
                // sample reaches at least a quarter of the current prediction (capped
                // at 0.1 ms so legitimately faster implementations can still adapt).
                double representativeThreshold = Math.Min(
                    0.1d,
                    estimatedWorkMilliseconds[kindIndex] * 0.25d);
                if (elapsedMilliseconds < representativeThreshold)
                    return;

                double clampedMeasurement = Math.Max(0.01d, Math.Min(50d, elapsedMilliseconds));
                estimatedWorkMilliseconds[kindIndex] +=
                    (clampedMeasurement - estimatedWorkMilliseconds[kindIndex]) * 0.2d;
            }

            public void Cancel(ChunkRuntimeWorkKind kind, long startedAt)
            {
                if (!frameActive || startedAt == 0L)
                    return;

                int kindIndex = Mathf.Clamp((int)kind, 0, estimatedWorkMilliseconds.Length - 1);
                reservedMilliseconds = Math.Max(
                    0d,
                    reservedMilliseconds - estimatedWorkMilliseconds[kindIndex]);
            }

            public void EndFrame()
            {
                frameActive = false;
            }

            public void Dispose()
            {
                StopRecorder();
                frameActive = false;
            }

            private void ResetFrameState(bool active)
            {
                frameActive = active;
                spentMilliseconds = 0d;
                reservedMilliseconds = 0d;
                urgentBypassUsedThisFrame = false;
                starvationBypassUsedThisFrame = false;
            }

            private float ReadPreviousFrameMilliseconds(out bool usedProfiler)
            {
                usedProfiler = false;
                if (recorderStarted &&
                    mainThreadTimeRecorder.Valid &&
                    mainThreadTimeRecorder.Count > 0 &&
                    mainThreadTimeRecorder.LastValue > 0)
                {
                    usedProfiler = true;
                    return (float)(mainThreadTimeRecorder.LastValue * 0.000001d);
                }

                return Time.unscaledDeltaTime * 1000f;
            }

            private void AddFrameSample(float frameMilliseconds)
            {
                frameSamples[nextFrameSampleIndex] = frameMilliseconds;
                nextFrameSampleIndex = (nextFrameSampleIndex + 1) % frameSamples.Length;
                frameSampleCount = Mathf.Min(frameSampleCount + 1, frameSamples.Length);
                smoothedFrameMilliseconds = smoothedFrameMilliseconds <= 0f
                    ? frameMilliseconds
                    : Mathf.Lerp(smoothedFrameMilliseconds, frameMilliseconds, 0.05f);
            }

            private void UpdatePercentile95()
            {
                Array.Copy(frameSamples, sortedFrameSamples, frameSampleCount);
                Array.Sort(sortedFrameSamples, 0, frameSampleCount);
                int percentileIndex = Mathf.Clamp(
                    Mathf.CeilToInt(frameSampleCount * 0.95f) - 1,
                    0,
                    frameSampleCount - 1);
                percentile95FrameMilliseconds = sortedFrameSamples[percentileIndex];
            }

            private void StopRecorder()
            {
                if (recorderStarted || mainThreadTimeRecorder.Valid)
                    mainThreadTimeRecorder.Dispose();

                recorderStarted = false;
                mainThreadTimeRecorder = default;
            }
        }

        internal static bool TryBeginRuntimeChunkWork(
            ChunkRuntimeWorkKind kind,
            bool urgent,
            bool allowStarvation,
            out long startedAt)
        {
            AdaptiveRuntimeChunkBudget budget = Instance?.adaptiveRuntimeChunkBudget;
            if (budget == null || !budget.FrameActive)
            {
                startedAt = 0L;
                return true;
            }

            return budget.TryBegin(kind, urgent, allowStarvation, out startedAt);
        }

        internal static void CompleteRuntimeChunkWork(ChunkRuntimeWorkKind kind, long startedAt)
        {
            Instance?.adaptiveRuntimeChunkBudget?.Complete(kind, startedAt);
        }

        internal static void CancelRuntimeChunkWork(ChunkRuntimeWorkKind kind, long startedAt)
        {
            Instance?.adaptiveRuntimeChunkBudget?.Cancel(kind, startedAt);
        }

        private void Awake()
        {
            Instance = this;
            meshWorldEpoch = ChunkMeshGenerator.BeginWorld();
            IsWorldReady = false;
            loadedTerrain = false;
            playerTransform = player.transform;
            surfaceChunkSpanCache.Clear();
            chunksToUnload.Clear();
            queuedForUnload.Clear();
            residentChunkScanQueue.Clear();
            residentChunkScanSet.Clear();
            registeredResidentChunks.Clear();
            residentChunkCountsByColumn.Clear();
            FluidSimulator.Clear();
            FallingBlockSimulator.Clear();
            ConfigureFluidSimulation(GetPlayerChunkCoordinate());
            ConfigureFallingBlockSimulation(GetPlayerChunkCoordinate());

            UpdateViewDistance();

        }

        private void Start()
        {
            StartCoroutine(InitializeTerrain());
        }

        private void OnEnable()
        {
            adaptiveRuntimeChunkBudget ??= new AdaptiveRuntimeChunkBudget();
            adaptiveRuntimeChunkBudget.Initialize(this);
            PersistentSceneManager.BeforeUnloadScene += OnBeforeUnloadScene;
        }

        private void OnDisable()
        {
            PersistentSceneManager.BeforeUnloadScene -= OnBeforeUnloadScene;
            CancelTargetActiveSnapshot();
            adaptiveRuntimeChunkBudget?.Dispose();
        }

        private void OnBeforeUnloadScene(SceneType scene)
        {
            if (scene != SceneType.Game)
                return;

            loadedTerrain = false;
            IsWorldReady = false;
            ShutdownMeshWorld();

            foreach (Chunk chunk in Chunks.Values)
                chunk?.ReleaseViewToPool();
        }

        private void ShutdownMeshWorld()
        {
            if (meshWorldShutdown)
                return;

            meshWorldShutdown = true;
            ChunkMeshGenerator.ShutdownWorldAndDrain(meshWorldEpoch);
        }

        private void OnDestroy()
        {
            PersistentSceneManager.BeforeUnloadScene -= OnBeforeUnloadScene;
            CancelTargetActiveSnapshot();
            adaptiveRuntimeChunkBudget?.Dispose();
            DisposeTerrainHeightSampleBuffers();
            loadedTerrain = false;
            ShutdownMeshWorld();
            DisposeTrackedBlockDataJobs();

            if (Instance != this)
                return;

            IsWorldReady = false;
            FluidSimulator.Clear();
            FallingBlockSimulator.Clear();

            foreach (Chunk chunk in Chunks.Values)
            {
                chunk?.DisposeGenerationResources();
                chunk?.ReleaseViewToPool();
            }

            Chunk.DisposeSharedTerrainColumnGenerationCache();
            scheduledBlockDataChunks.Clear();
            scheduledBlockDataQueue.Clear();
            scheduledBlockDataChunkBuffer.Clear();
            dirtyMeshChunks.Clear();
            dirtyMeshChunkBuffer.Clear();
            interactiveDirtyMeshChunks.Clear();
            interactiveDirtyMeshQueue.Clear();
            interactiveSourceMeshChunks.Clear();
            urgentChunksToCreate.Clear();
            queuedForUrgentCreate.Clear();
            urgentChunksToGenerate.Clear();
            queuedForUrgentGenerate.Clear();
            urgentChunksToActivate.Clear();
            queuedForUrgentActivation.Clear();
            nearbyColliderChunks.Clear();
            previousNearbyColliderChunks.Clear();
            nearbyColliderCandidates.Clear();
            pendingTargetChunks.Clear();
            pendingTargetCoordinateBuffer.Clear();
            previousTargetCoordinateBuffer.Clear();
            urgentTargetChunks.Clear();
            urgentActivatedChunks.Clear();
            currentActiveCoordinateBuffer.Clear();
            nextActiveCoordinateBuffer.Clear();
            chunksToUnload.Clear();
            queuedForUnload.Clear();
            residentChunkScanQueue.Clear();
            residentChunkScanSet.Clear();
            registeredResidentChunks.Clear();
            residentChunkCountsByColumn.Clear();
            Chunks.Clear();

            Instance = null;
        }

        private void DisposeTrackedBlockDataJobs()
        {
            scheduledBlockDataChunkBuffer.Clear();
            foreach (Chunk scheduledChunk in scheduledBlockDataChunks)
                scheduledBlockDataChunkBuffer.Add(scheduledChunk);

            for (int i = 0; i < scheduledBlockDataChunkBuffer.Count; i++)
                scheduledBlockDataChunkBuffer[i]?.DisposeGenerationResources();

            scheduledBlockDataChunks.Clear();
            scheduledBlockDataQueue.Clear();
            scheduledBlockDataChunkBuffer.Clear();
        }

        private IEnumerator InitializeTerrain()
        {
            loadedTerrain = false;
            IsWorldReady = false;

            int originalViewDistance = viewDistance;
            int originalViewDistanceY = viewDistanceY;

            SetLoadingProgress(0f);

            NoiseSettings.Instance?.EnsureInitialized();

            float time = Time.realtimeSinceStartup;
            Vector3Int initialChunkCoordinate = Vector3Int.zero;
            int sampledSpawnHeight = int.MinValue;
            bool hasResolvedSpawn = false;
            bool usedPreloadPass = false;

            if (SaveController.TryGetLoadedPlayerPosition(out Vector3 savedPlayerPosition))
            {
                initialChunkCoordinate = ChunkUtility.GetChunkCoordinateFromPosition(savedPlayerPosition);
                hasResolvedSpawn = SaveController.TryRestoreLoadedPlayer(player);
                if (hasResolvedSpawn)
                    playerTransform = player.transform;
            }
            else if (TrySampleTerrainSpawnYAtColumn(0, 0, out sampledSpawnHeight))
            {
                playerTransform.position = new Vector3(0.5f, sampledSpawnHeight + 2f, 0.5f);
                initialChunkCoordinate = ChunkUtility.GetChunkCoordinateFromPosition(playerTransform.position);
                hasResolvedSpawn = true;
            }

            // Configure simulation around the actual load target before chunks begin
            // registering fluid/falling work. This avoids a bulk rescan when the
            // prefab's old position differs from the saved/generated spawn.
            ConfigureFluidSimulation(initialChunkCoordinate);
            ConfigureFallingBlockSimulation(initialChunkCoordinate);

            // Saved worlds and normally generated worlds already have an exact target
            // position. A preload pass in those cases duplicates most chunk work.
            if (!hasResolvedSpawn)
            {
                usedPreloadPass = true;
                viewDistance = preloadViewDistance;
                viewDistanceY = preloadViewDistanceY;
                UpdateViewDistance();

                List<Vector3Int> initialVisibleChunks = BuildViewChunkCoordinates(initialChunkCoordinate);
                List<Vector3Int> initialRequiredChunks = BuildRequiredChunkCoordinates(initialVisibleChunks, initialChunkCoordinate);
                yield return LoadChunkArea(initialRequiredChunks, initialVisibleChunks, 0f, 0.45f);
                yield return WaitForMeshWork(0.45f, 0.5f);

                if (TryGetHighestSolidBlockYAtColumn(0, 0, out int highestPosY))
                    playerTransform.position = new Vector3(0.5f, highestPosY + 2.0f, 0.5f);
                else
                    Debug.LogWarning("Found no valid spawn block at x:0 z:0");
            }

            viewDistance = originalViewDistance;
            viewDistanceY = originalViewDistanceY;
            UpdateViewDistance();

            Vector3Int playerChunk = ChunkUtility.GetChunkCoordinateFromPosition(playerTransform.position);
            Vector3 playerPos = player.transform.position;
            ConfigureFluidSimulation(playerChunk);
            ConfigureFallingBlockSimulation(playerChunk);
            List<Vector3Int> finalVisibleChunks = BuildViewChunkCoordinates(playerChunk);
            List<Vector3Int> finalRequiredChunks = BuildRequiredChunkCoordinates(finalVisibleChunks, playerChunk);

            float finalLoadProgressStart = usedPreloadPass ? 0.5f : 0f;
            yield return LoadChunkArea(finalRequiredChunks, finalVisibleChunks, finalLoadProgressStart, 0.95f);

            currentActiveChunks.Clear();
            currentActiveCoordinateBuffer.Clear();
            nextActiveCoordinateBuffer.Clear();
            foreach (Vector3Int coordinate in finalVisibleChunks)
            {
                currentActiveChunks.Add(coordinate);
                currentActiveCoordinateBuffer.Add(coordinate);
            }

            if (AddColliders)
                UpdateNearbyColliders(playerPos);

            SetChunksActiveForView(finalVisibleChunks);

            if (shouldDisableChunks)
                UpdateChunkVisibility(playerChunk);

            lastActiveChunks.Clear();
            foreach (var pos in currentActiveChunks)
                lastActiveChunks.Add(pos);

            chunksToCreate.Clear();
            chunksToGenerate.Clear();
            queuedForCreate.Clear();
            queuedForGenerate.Clear();
            urgentChunksToCreate.Clear();
            queuedForUrgentCreate.Clear();
            urgentChunksToGenerate.Clear();
            queuedForUrgentGenerate.Clear();
            urgentChunksToActivate.Clear();
            queuedForUrgentActivation.Clear();
            createIndex = 0;
            generateIndex = 0;
            lastPlayerChunk = playerChunk;

            yield return WaitForMeshWork(0.95f, 1f);
            ChunkMeshGenerator.Update(isLoading: true);
            SetLoadingProgress(1f);

            SaveController.RestoreLoadedFallingBlocks();

            Debug.Log($"Generating Terrain Took: {Time.realtimeSinceStartup - time }");

            IsWorldReady = true;
            loadedTerrain = true;
            OnLoadedTerrain?.Invoke();
        }

        private List<Vector3Int> BuildViewChunkCoordinates(Vector3Int centerChunk)
        {
            if (skipHiddenUndergroundChunks)
                return BuildSurfaceAwareViewChunkCoordinates(centerChunk);

            int minChunkY = GetMinimumVisibleChunkY(centerChunk);
            int maxChunkY = centerChunk.y + viewDistanceY;
            int verticalCount = Mathf.Max(1, maxChunkY - minChunkY + 1);

            List<Vector3Int> coordinates = new List<Vector3Int>(horizontalOffsets.Length * verticalCount);
            for (int i = 0; i < horizontalOffsets.Length; i++)
            {
                Vector3Int rel = horizontalOffsets[i];
                int chunkX = centerChunk.x + rel.x;
                int chunkZ = centerChunk.z + rel.z;

                for (int y = minChunkY; y <= maxChunkY; y++)
                    coordinates.Add(new Vector3Int(chunkX, y, chunkZ));
            }

            coordinates.Sort((a, b) =>
            {
                Vector3Int da = a - centerChunk;
                Vector3Int db = b - centerChunk;
                int aDistance = da.x * da.x + da.y * da.y + da.z * da.z;
                int bDistance = db.x * db.x + db.y * db.y + db.z * db.z;
                return aDistance - bDistance;
            });

            return coordinates;
        }

        private List<Vector3Int> BuildSurfaceAwareViewChunkCoordinates(Vector3Int centerChunk)
        {
            int playerRangeY = Mathf.Max(0, hiddenUndergroundPlayerRangeY);
            int playerMinY = centerChunk.y - playerRangeY;
            int playerMaxY = centerChunk.y + playerRangeY;
            int playerRangeXZ = Mathf.Max(0, hiddenUndergroundPlayerRangeXZ);
            int playerRangeXZSq = playerRangeXZ * playerRangeXZ;
            var uniqueCoordinates = new HashSet<Vector3Int>();
            for (int i = 0; i < horizontalOffsets.Length; i++)
            {
                Vector3Int rel = horizontalOffsets[i];
                int chunkX = centerChunk.x + rel.x;
                int chunkZ = centerChunk.z + rel.z;
                bool isNearPlayer = rel.x * rel.x + rel.z * rel.z <= playerRangeXZSq;

                if (isNearPlayer)
                    AddChunkYRange(uniqueCoordinates, chunkX, chunkZ, playerMinY, playerMaxY);

                if (!TryGetSurfaceChunkSpan(chunkX, chunkZ, out SurfaceChunkSpan surfaceSpan))
                {
                    // Preserve a usable fallback column if terrain settings or sampling
                    // buffers are unavailable instead of leaving a visible hole.
                    if (!isNearPlayer)
                        AddChunkYRange(uniqueCoordinates, chunkX, chunkZ, playerMinY, playerMaxY);
                    continue;
                }

                int surfaceMinY = surfaceSpan.MinChunkY - visibleSurfaceChunkDepth;
                int surfaceMaxY = surfaceSpan.MaxChunkY + visibleSurfaceChunkHeadroom;
                AddChunkYRange(uniqueCoordinates, chunkX, chunkZ, surfaceMinY, surfaceMaxY);
            }

            List<Vector3Int> coordinates = new List<Vector3Int>(uniqueCoordinates);
            coordinates.Sort((a, b) =>
            {
                Vector3Int da = a - centerChunk;
                Vector3Int db = b - centerChunk;
                int aDistance = da.x * da.x + da.y * da.y + da.z * da.z;
                int bDistance = db.x * db.x + db.y * db.y + db.z * db.z;
                return aDistance - bDistance;
            });

            return coordinates;
        }

        private static void AddChunkYRange(HashSet<Vector3Int> coordinates, int chunkX, int chunkZ, int minChunkY, int maxChunkY)
        {
            if (minChunkY > maxChunkY)
                (minChunkY, maxChunkY) = (maxChunkY, minChunkY);

            for (int y = minChunkY; y <= maxChunkY; y++)
                coordinates.Add(new Vector3Int(chunkX, y, chunkZ));
        }

        private bool TryGetSurfaceChunkSpan(int chunkX, int chunkZ, out SurfaceChunkSpan span)
        {
            Vector2Int key = new Vector2Int(chunkX, chunkZ);
            if (surfaceChunkSpanCache.TryGetValue(key, out span))
                return true;

            if (!TrySampleSurfaceChunkSpan(chunkX, chunkZ, out span))
                return false;

            surfaceChunkSpanCache[key] = span;
            return true;
        }

        private bool TrySampleSurfaceChunkSpan(int chunkX, int chunkZ, out SurfaceChunkSpan span)
        {
            span = default;

            NoiseSettings settings = NoiseSettings.Instance;
            if (settings == null)
                return false;

            settings.EnsureInitialized();

            const int sampleGridSize = 5;
            const int sampleCount = sampleGridSize * sampleGridSize;

            EnsureTerrainHeightSampleBuffers();

            float sampleStep = (Chunk.CHUNK_SIZE - 1f) / (sampleGridSize - 1f);
            GenerateTerrainHeightMapJob heightJob = CreateTerrainHeightMapJob(
                terrainSampleHeightMap,
                terrainSampleBiomeMap,
                terrainSampleSurfaceBiomeMap,
                terrainSampleBiomeBlendMap,
                terrainSampleDesertEdgeMap,
                terrainSampleRiverMap,
                terrainSampleRiverSurfaceMap,
                sampleGridSize,
                new float2(chunkX * Chunk.CHUNK_SIZE, chunkZ * Chunk.CHUNK_SIZE),
                new float2(sampleStep, sampleStep),
                settings);

            heightJob.Run(sampleCount);

            int minSurfaceY = int.MaxValue;
            int maxSurfaceY = int.MinValue;
            int waterSurfaceY = settings.GroundOffset + settings.WaterLevel;

            for (int i = 0; i < sampleCount; i++)
            {
                int terrainY = terrainSampleHeightMap[i];
                int visibleTopY = Mathf.Max(terrainY, waterSurfaceY);
                minSurfaceY = Mathf.Min(minSurfaceY, terrainY);
                maxSurfaceY = Mathf.Max(maxSurfaceY, visibleTopY);
            }

            span = new SurfaceChunkSpan
            {
                MinChunkY = Mathf.FloorToInt(minSurfaceY / (float)Chunk.CHUNK_HEIGHT),
                MaxChunkY = Mathf.FloorToInt(maxSurfaceY / (float)Chunk.CHUNK_HEIGHT)
            };
            return true;
        }

        private static GenerateTerrainHeightMapJob CreateTerrainHeightMapJob(
            NativeArray<int> heightMap,
            NativeArray<byte> biomeMap,
            NativeArray<byte> surfaceBiomeMap,
            NativeArray<byte> biomeBlendMap,
            NativeArray<byte> desertEdgeMap,
            NativeArray<byte> riverMap,
            NativeArray<int> riverSurfaceMap,
            int chunkSize,
            float2 chunkOrigin,
            float2 sampleStep,
            NoiseSettings settings)
        {
            settings.GetNoiseLayers(out var continentLayer, out var mountainLayer, out var detailLayer, out var ridgeLayer);
            settings.GetBiomeLayers(out var temperatureLayer, out var moistureLayer, out var erosionLayer);
            settings.GetRedDesertLayer(out var redDesertLayer);
            settings.GetTerrainVarietyLayers(out var landformLayer, out var cliffLayer, out _);
            settings.GetHydrologyLayers(out var riverLayer);

            return new GenerateTerrainHeightMapJob
            {
                HeightMap = heightMap,
                BiomeMap = biomeMap,
                SurfaceBiomeMap = surfaceBiomeMap,
                BiomeBlendMap = biomeBlendMap,
                DesertEdgeMap = desertEdgeMap,
                RiverMap = riverMap,
                RiverSurfaceMap = riverSurfaceMap,
                ChunkSize = chunkSize,
                ChunkOrigin = chunkOrigin,
                SampleStep = sampleStep,
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
        }

        private void EnsureTerrainHeightSampleBuffers()
        {
            if (terrainSampleHeightMap.IsCreated &&
                terrainSampleBiomeMap.IsCreated &&
                terrainSampleSurfaceBiomeMap.IsCreated &&
                terrainSampleBiomeBlendMap.IsCreated &&
                terrainSampleDesertEdgeMap.IsCreated &&
                terrainSampleRiverMap.IsCreated &&
                terrainSampleRiverSurfaceMap.IsCreated)
            {
                return;
            }

            DisposeTerrainHeightSampleBuffers();

            try
            {
                terrainSampleHeightMap = new NativeArray<int>(
                    TerrainHeightSampleBufferCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                terrainSampleBiomeMap = new NativeArray<byte>(
                    TerrainHeightSampleBufferCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                terrainSampleSurfaceBiomeMap = new NativeArray<byte>(
                    TerrainHeightSampleBufferCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                terrainSampleBiomeBlendMap = new NativeArray<byte>(
                    TerrainHeightSampleBufferCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                terrainSampleDesertEdgeMap = new NativeArray<byte>(
                    TerrainHeightSampleBufferCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                terrainSampleRiverMap = new NativeArray<byte>(
                    TerrainHeightSampleBufferCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                terrainSampleRiverSurfaceMap = new NativeArray<int>(
                    TerrainHeightSampleBufferCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
            }
            catch
            {
                DisposeTerrainHeightSampleBuffers();
                throw;
            }
        }

        private void DisposeTerrainHeightSampleBuffers()
        {
            if (terrainSampleHeightMap.IsCreated) terrainSampleHeightMap.Dispose();
            if (terrainSampleBiomeMap.IsCreated) terrainSampleBiomeMap.Dispose();
            if (terrainSampleSurfaceBiomeMap.IsCreated) terrainSampleSurfaceBiomeMap.Dispose();
            if (terrainSampleBiomeBlendMap.IsCreated) terrainSampleBiomeBlendMap.Dispose();
            if (terrainSampleDesertEdgeMap.IsCreated) terrainSampleDesertEdgeMap.Dispose();
            if (terrainSampleRiverMap.IsCreated) terrainSampleRiverMap.Dispose();
            if (terrainSampleRiverSurfaceMap.IsCreated) terrainSampleRiverSurfaceMap.Dispose();

            terrainSampleHeightMap = default;
            terrainSampleBiomeMap = default;
            terrainSampleSurfaceBiomeMap = default;
            terrainSampleBiomeBlendMap = default;
            terrainSampleDesertEdgeMap = default;
            terrainSampleRiverMap = default;
            terrainSampleRiverSurfaceMap = default;
        }

        private List<Vector3Int> BuildRequiredChunkCoordinates(
            List<Vector3Int> visibleCoordinates,
            Vector3Int centerChunk)
        {
            HashSet<Vector3Int> required = new HashSet<Vector3Int>();

            for (int i = 0; i < visibleCoordinates.Count; i++)
            {
                Vector3Int coordinate = visibleCoordinates[i];
                required.Add(coordinate);

                for (int neighborIndex = 0; neighborIndex < NeighborChunkOffsets.Length; neighborIndex++)
                    required.Add(coordinate + NeighborChunkOffsets[neighborIndex]);
            }

            List<Vector3Int> coordinates = new List<Vector3Int>(required);
            coordinates.Sort((a, b) =>
            {
                Vector3Int da = a - centerChunk;
                Vector3Int db = b - centerChunk;
                int aDistance = da.x * da.x + da.y * da.y + da.z * da.z;
                int bDistance = db.x * db.x + db.y * db.y + db.z * db.z;
                return aDistance - bDistance;
            });

            return coordinates;
        }

        private IEnumerator LoadChunkArea(
            List<Vector3Int> requiredCoordinates,
            List<Vector3Int> visibleCoordinates,
            float progressStart,
            float progressEnd)
        {
            HashSet<Vector3Int> visibleSet = new HashSet<Vector3Int>(visibleCoordinates);
            int totalOperations = Mathf.Max(1, requiredCoordinates.Count + visibleCoordinates.Count);
            int completedOperations = 0;
            int createdThisFrame = 0;
            int scheduledThisFrame = 0;
            float frameWorkStartedAt = Time.realtimeSinceStartup;

            for (int i = 0; i < requiredCoordinates.Count; i++)
            {
                Vector3Int coordinate = requiredCoordinates[i];
                bool createdChunk = false;
                if (!Chunks.TryGetValue(coordinate, out Chunk chunk))
                {
                    chunk = CreatePreparedChunk(coordinate);
                    Chunks.Add(chunk.Coordinate, chunk);
                    RegisterResidentChunk(chunk.Coordinate);
                    createdChunk = true;
                }

                // Start data work as soon as the logical chunk exists so worker jobs
                // overlap the remaining cheap chunk-coordinate setup.
                if (!chunk.HasBlockData && !chunk.IsBlockDataGenerationScheduled)
                {
                    chunk.ScheduleBlockDataGeneration();
                    scheduledThisFrame++;
                }

                if (chunk.GameObject != null)
                    chunk.SetActive(visibleSet.Contains(coordinate));

                completedOperations++;
                if (createdChunk)
                    createdThisFrame++;
                SetLoadingProgress(Mathf.Lerp(progressStart, progressEnd, completedOperations / (float)totalOperations));

                bool exceededLoadingBudget =
                    (Time.realtimeSinceStartup - frameWorkStartedAt) * 1000f >= loadingFrameBudgetMilliseconds;
                if (createdThisFrame >= Mathf.Max(1, maxChunksCreatePerLoadingFrame) ||
                    scheduledThisFrame >= Mathf.Max(1, maxChunkDataSchedulesPerFrame) ||
                    exceededLoadingBudget)
                {
                    createdThisFrame = 0;
                    scheduledThisFrame = 0;
                    ChunkMeshGenerator.Update(isLoading: true);
                    yield return null;
                    frameWorkStartedAt = Time.realtimeSinceStartup;
                }
            }

            int generatedThisFrame = 0;
            blockDataCompletionsRemaining = Mathf.Max(1, maxBlockDataCompletionsPerLoadingFrame);
            frameWorkStartedAt = Time.realtimeSinceStartup;
            for (int i = 0; i < visibleCoordinates.Count; i++)
            {
                Vector3Int coordinate = visibleCoordinates[i];
                if (Chunks.TryGetValue(coordinate, out Chunk chunk) && !chunk.IsGenerated)
                {
                    if (chunk.GameObject != null)
                        chunk.SetActive(true);

                    if (!ChunkUtility.HasAllNeighborChunks(chunk.Coordinate))
                        EnsureImmediateNeighborChunks(chunk.Coordinate);

                    while (!TryCompleteChunkAndImmediateNeighborData(chunk))
                    {
                        ChunkMeshGenerator.Update(isLoading: true);
                        yield return null;
                        blockDataCompletionsRemaining = Mathf.Max(1, maxBlockDataCompletionsPerLoadingFrame);
                        frameWorkStartedAt = Time.realtimeSinceStartup;
                    }

                    chunk.Generate(MeshRequestPriority.Streaming);
                    generatedThisFrame++;
                }

                completedOperations++;
                SetLoadingProgress(Mathf.Lerp(progressStart, progressEnd, completedOperations / (float)totalOperations));

                bool exceededLoadingBudget =
                    (Time.realtimeSinceStartup - frameWorkStartedAt) * 1000f >= loadingFrameBudgetMilliseconds;
                if (generatedThisFrame >= Mathf.Max(1, maxChunksGeneratePerLoadingFrame) ||
                    exceededLoadingBudget)
                {
                    generatedThisFrame = 0;
                    ChunkMeshGenerator.Update(isLoading: true);
                    yield return null;
                    blockDataCompletionsRemaining = Mathf.Max(1, maxBlockDataCompletionsPerLoadingFrame);
                    frameWorkStartedAt = Time.realtimeSinceStartup;
                }
            }

            ChunkMeshGenerator.Update(isLoading: true);
        }

        private IEnumerator ScheduleChunkDataForCoordinates(List<Vector3Int> coordinates)
        {
            int scheduledThisFrame = 0;
            for (int i = 0; i < coordinates.Count; i++)
            {
                if (!Chunks.TryGetValue(coordinates[i], out Chunk chunk))
                    continue;

                if (!chunk.HasBlockData && !chunk.IsBlockDataGenerationScheduled)
                {
                    chunk.ScheduleBlockDataGeneration();
                    scheduledThisFrame++;

                    if (scheduledThisFrame >= Mathf.Max(1, maxChunkDataSchedulesPerFrame))
                    {
                        scheduledThisFrame = 0;
                        ChunkMeshGenerator.Update();
                        yield return null;
                    }
                }
            }
        }

        private void EnsureImmediateNeighborChunks(Vector3Int coordinate)
        {
            for (int i = 0; i < NeighborChunkOffsets.Length; i++)
            {
                Vector3Int neighborCoordinate = coordinate + NeighborChunkOffsets[i];
                if (Chunks.ContainsKey(neighborCoordinate))
                    continue;

                if (loadedTerrain)
                {
                    if (queuedForCreate.Add(neighborCoordinate))
                        chunksToCreate.Add(neighborCoordinate);
                    continue;
                }

                Chunk chunk = CreatePreparedChunk(neighborCoordinate);
                Chunks.Add(chunk.Coordinate, chunk);
                RegisterResidentChunk(chunk.Coordinate);

                if (chunk.GameObject != null)
                    chunk.SetActive(false);
            }
        }

        private void SetChunksActiveForView(List<Vector3Int> visibleCoordinates)
        {
            HashSet<Vector3Int> visibleSet = new HashSet<Vector3Int>(visibleCoordinates);
            foreach (KeyValuePair<Vector3Int, Chunk> entry in Chunks)
            {
                Chunk chunk = entry.Value;
                if (chunk?.GameObject == null)
                    continue;

                chunk.SetActive(visibleSet.Contains(entry.Key));
            }
        }

        private IEnumerator WaitForMeshWork(float progressStart, float progressEnd)
        {
            // Initial meshes can be built while an upper streamed chunk is still
            // unknown. That conservative snapshot is intentionally dark, and the
            // upper chunk marks every affected lower chunk dirty once its block data
            // becomes available. Do not dismiss the loading screen after only the
            // first mesh requests: drain those lighting-stabilization rebuilds too.
            // Otherwise the world appears with black chunk-sized patches which are
            // corrected several frames later by the normal runtime dirty-mesh budget.
            while (ChunkMeshGenerator.HasPendingMeshWork || dirtyMeshChunks.Count > 0)
            {
                ProcessDirtyChunkMeshRebuilds();
                ChunkMeshGenerator.Update(isLoading: true);
                SetLoadingProgress(Mathf.Min(progressEnd, Mathf.Max(progressStart, loadTerrainImage != null ? loadTerrainImage.fillAmount : progressStart)));
                yield return null;
            }

            ChunkMeshGenerator.Update(isLoading: true);
            SetLoadingProgress(progressEnd);
        }

        private void SetLoadingProgress(float progress)
        {
            float clampedProgress = Mathf.Clamp01(progress);

            if (loadTerrainImage != null)
                loadTerrainImage.fillAmount = clampedProgress;

            int percentage = Mathf.RoundToInt(clampedProgress * 100f);
            if (loadTerrainTxt != null && percentage != lastLoadingPercentage)
                loadTerrainTxt.SetText("{0}%", percentage);

            lastLoadingPercentage = percentage;
        }


        private bool TryGetHighestSolidBlockYAtColumn(int worldX, int worldZ, out int highestY)
        {
            highestY = int.MinValue;

            Chunk highestChunk = ChunkUtility.GetHighestChunkAt(new Vector3(worldX, 0f, worldZ));
            if (highestChunk == null)
            {
                return false;
            }

            int chunkX = Mathf.FloorToInt((float)worldX / Chunk.CHUNK_SIZE);
            int chunkZ = Mathf.FloorToInt((float)worldZ / Chunk.CHUNK_SIZE);

            int localX = worldX - chunkX * Chunk.CHUNK_SIZE;
            int localZ = worldZ - chunkZ * Chunk.CHUNK_SIZE;

            if (localX < 0) localX += Chunk.CHUNK_SIZE;
            if (localZ < 0) localZ += Chunk.CHUNK_SIZE;

            int minChunkY = highestChunk.Coordinate.y;
            foreach (Vector3Int coordinate in Chunks.Keys)
            {
                if (coordinate.x == chunkX && coordinate.z == chunkZ && coordinate.y < minChunkY)
                {
                    minChunkY = coordinate.y;
                }
            }

            for (int chunkY = highestChunk.Coordinate.y; chunkY >= minChunkY; chunkY--)
            {
                Vector3Int coordinate = new Vector3Int(chunkX, chunkY, chunkZ);
                if (!Chunks.TryGetValue(coordinate, out Chunk chunk) || chunk.Blocks == null)
                {
                    continue;
                }

                for (int localY = Chunk.CHUNK_HEIGHT - 1; localY >= 0; localY--)
                {
                    int blockId = chunk.Blocks[localX, localY, localZ];
                    if (blockId == Chunk.BLOCK_AIR)
                    {
                        continue;
                    }

                    BlockData block = AssetsContainer.GetBlock(blockId);
                    if (block != null && block.IsFluid)
                    {
                        continue;
                    }

                    highestY = chunkY * Chunk.CHUNK_HEIGHT + localY;
                    return true;
                }
            }

            return false;
        }

        private bool TrySampleTerrainSpawnYAtColumn(int worldX, int worldZ, out int spawnY)
        {
            spawnY = int.MinValue;

            NoiseSettings settings = NoiseSettings.Instance;
            if (settings == null)
                return false;

            settings.EnsureInitialized();
            EnsureTerrainHeightSampleBuffers();

            GenerateTerrainHeightMapJob heightJob = CreateTerrainHeightMapJob(
                terrainSampleHeightMap,
                terrainSampleBiomeMap,
                terrainSampleSurfaceBiomeMap,
                terrainSampleBiomeBlendMap,
                terrainSampleDesertEdgeMap,
                terrainSampleRiverMap,
                terrainSampleRiverSurfaceMap,
                1,
                new float2(worldX, worldZ),
                new float2(1f, 1f),
                settings);

            heightJob.Run(1);

            int waterSurfaceY = settings.GroundOffset + settings.WaterLevel;
            spawnY = Mathf.Max(terrainSampleHeightMap[0], waterSurfaceY);
            return true;
        }

        public void UpdateViewDistance()
        {
            viewDistanceXZSq = viewDistance * viewDistance;

            float maxColliderDistance = Chunk.CHUNK_SIZE + Mathf.Max(0f, addColliderDistance);
            addColliderDistanceSq = maxColliderDistance * maxColliderDistance;

            GenerateHorizontalOffsets();

            lastViewDistance = viewDistance;
            lastViewDistanceY = viewDistanceY;

            lastPlayerChunk = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            targetRebuildPending = false;
            targetRebuildCenter = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            targetRebuildHorizontalIndex = 0;
            targetRebuildPhase = TargetRebuildPhase.None;
            targetColumnPrepared = false;
            targetColumnRangeCount = 0;
            targetColumnRangeIndex = 0;
            targetTransitionCoordinateIndex = 0;
            targetRemovalCoordinateIndex = 0;
            CancelTargetActiveSnapshot();
            pendingTargetChunks.Clear();
            pendingTargetCoordinateBuffer.Clear();
            previousTargetCoordinateBuffer.Clear();
            ReleaseUrgentOnlyActivations();
            urgentTargetChunks.Clear();
            nextActiveCoordinateBuffer.Clear();
            urgentChunksToCreate.Clear();
            queuedForUrgentCreate.Clear();
            urgentChunksToGenerate.Clear();
            queuedForUrgentGenerate.Clear();
            urgentChunksToActivate.Clear();
            queuedForUrgentActivation.Clear();
        }

        private void GenerateHorizontalOffsets()
        {
            int rx = viewDistance;
            int rz = viewDistance;

            int cap = (2 * rx + 1) * (2 * rz + 1);
            var tmp = new List<Vector3Int>(cap);

            for (int x = -rx; x <= rx; x++)
            {
                int xSq = x * x;

                for (int z = -rz; z <= rz; z++)
                {
                    int xzSq = xSq + z * z;
                    if (xzSq > viewDistanceXZSq)
                        continue;

                    tmp.Add(new Vector3Int(x, 0, z));
                }
            }

            // nearest-first
            tmp.Sort(static (a, b) =>
            {
                int aDist = a.x * a.x + a.z * a.z;
                int bDist = b.x * b.x + b.z * b.z;
                return aDist - bDist;
            });

            horizontalOffsets = tmp.ToArray();
        }

        private void Update()
        {
            blockDataSchedulesRemaining = Mathf.Max(1, maxChunkDataSchedulesPerFrame);
            int completionLimit = Mathf.Max(1, maxBlockDataCompletionsPerFrame);
            harvestBlockDataCompletionsRemaining = scheduledBlockDataQueue.Count > 0 ? 1 : 0;
            blockDataCompletionsRemaining = Mathf.Max(
                0,
                completionLimit - harvestBlockDataCompletionsRemaining);

            if (!loadedTerrain)
            {
                adaptiveRuntimeChunkBudget?.EndFrame();
                return;
            }

            if (lastViewDistance != viewDistance || lastViewDistanceY != viewDistanceY)
                UpdateViewDistance();

            Vector3 playerPosition = playerTransform.position;
            Vector3Int playerChunk = ChunkUtility.GetChunkCoordinateFromPosition(playerPosition);
            adaptiveRuntimeChunkBudget?.BeginFrame(this, HasRuntimeChunkBacklog());
            ConfigureFluidSimulation(playerChunk);
            ConfigureFallingBlockSimulation(playerChunk);
            ProcessInteractiveRuntimeWork();

            FallingBlockSimulator.Update(Time.deltaTime);
            FluidSimulator.Update(Time.deltaTime);

            bool playerChunkChanged = playerChunk != lastPlayerChunk;
            if (playerChunkChanged &&
                (!targetRebuildPending || targetRebuildCenter != playerChunk))
            {
                BeginTargetChunkRebuild(playerChunk);
            }

            if (targetRebuildPending)
                ProcessTargetChunkRebuild(playerChunk);
            ProcessUrgentChunkActivations();

            // Collider availability is safety-critical near the player. Run it before
            // optional mesh callbacks and eviction; the shared controller still limits
            // how many cooks may start in this frame.
            if (AddColliders)
                UpdateNearbyColliders(playerPosition);

            ChunkMeshGenerator.Update();
            ProcessDirtyChunkMeshRebuilds();

            bool hasPendingWork = targetRebuildPending ||
                                  urgentChunksToCreate.Count > 0 ||
                                  urgentChunksToGenerate.Count > 0 ||
                                  urgentChunksToActivate.Count > 0 ||
                                  createIndex < chunksToCreate.Count ||
                                  generateIndex < chunksToGenerate.Count;

            if (!playerChunkChanged && !hasPendingWork)
            {
                HarvestCompletedBlockDataJobs();
                ProcessDistantChunkUnloads(playerChunk);
                return;
            }

            ProcessChunkCreation();
            ProcessChunkGeneration();

            HarvestCompletedBlockDataJobs();
            ProcessDistantChunkUnloads(playerChunk);
        }

        private bool HasRuntimeChunkBacklog()
        {
            return targetRebuildPending ||
                   urgentChunksToCreate.Count > 0 ||
                   urgentChunksToGenerate.Count > 0 ||
                   urgentChunksToActivate.Count > 0 ||
                   createIndex < chunksToCreate.Count ||
                   generateIndex < chunksToGenerate.Count ||
                   dirtyMeshChunks.Count > 0 ||
                   interactiveDirtyMeshQueue.Count > 0 ||
                   scheduledBlockDataChunks.Count > 0 ||
                   chunksToUnload.Count > 0 ||
                   ChunkMeshGenerator.HasPendingMeshWork;
        }

        private void LateUpdate()
        {
            if (!loadedTerrain)
            {
                adaptiveRuntimeChunkBudget?.EndFrame();
                return;
            }

            try
            {
                // Input scripts can run either before or after TerrainGenerator.Update.
                // A second fast-lane dispatch here prevents script order from adding a
                // full frame of latency to a player edit.
                ProcessInteractiveRuntimeWork();
            }
            finally
            {
                adaptiveRuntimeChunkBudget?.EndFrame();
            }
        }

        private void ProcessInteractiveRuntimeWork()
        {
            // At a very small budget, both snapshot submission and mesh upload may
            // need the single urgent allowance. Alternate their order so sustained
            // editing cannot let either half monopolize that allowance indefinitely.
            if ((Time.frameCount & 1) == 0)
            {
                ChunkMeshGenerator.UpdateInteractive();
                ProcessInteractiveChunkMeshRebuilds();
            }
            else
            {
                ProcessInteractiveChunkMeshRebuilds();
                ChunkMeshGenerator.UpdateInteractive();
            }
        }

        internal static void RegisterScheduledBlockDataChunk(Chunk chunk)
        {
            if (Instance != null && chunk != null)
            {
                if (Instance.scheduledBlockDataChunks.Add(chunk))
                    Instance.scheduledBlockDataQueue.Enqueue(chunk);
            }
        }

        internal static void UnregisterScheduledBlockDataChunk(Chunk chunk)
        {
            if (Instance != null && chunk != null)
                Instance.scheduledBlockDataChunks.Remove(chunk);
        }

        private void HarvestCompletedBlockDataJobs()
        {
            if (scheduledBlockDataQueue.Count == 0)
                return;

            int entriesToInspect = Mathf.Min(
                scheduledBlockDataQueue.Count,
                MaxScheduledBlockJobsInspectedPerFrame);
            for (int i = 0; i < entriesToInspect; i++)
            {
                Chunk chunk = scheduledBlockDataQueue.Dequeue();
                if (chunk == null || !scheduledBlockDataChunks.Contains(chunk))
                    continue;

                if (chunk == null || chunk.HasBlockData || !chunk.IsBlockDataGenerationScheduled)
                {
                    scheduledBlockDataChunks.Remove(chunk);
                    continue;
                }

                if (!chunk.IsBlockDataGenerationComplete)
                {
                    scheduledBlockDataQueue.Enqueue(chunk);
                    continue;
                }

                if (harvestBlockDataCompletionsRemaining <= 0)
                {
                    scheduledBlockDataQueue.Enqueue(chunk);
                    continue;
                }

                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.HarvestBlockDataCompletion,
                        urgent: false,
                        allowStarvation: true,
                        out long completionStartedAt))
                {
                    scheduledBlockDataQueue.Enqueue(chunk);
                    break;
                }

                try
                {
                    if (chunk.CompleteBlockDataGenerationIfReady())
                        harvestBlockDataCompletionsRemaining--;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
                finally
                {
                    CompleteRuntimeChunkWork(
                        ChunkRuntimeWorkKind.HarvestBlockDataCompletion,
                        completionStartedAt);
                    if (chunk.HasBlockData || !chunk.IsBlockDataGenerationScheduled)
                        scheduledBlockDataChunks.Remove(chunk);
                    else
                        scheduledBlockDataQueue.Enqueue(chunk);
                }
            }
        }

        public static void MarkChunkMeshDirty(Chunk chunk)
        {
            MarkChunkMeshDirty(chunk, prioritizeForInteraction: false);
        }

        internal static void QueueChunkMeshRetry(Chunk chunk, bool prioritizeForInteraction)
        {
            if (Instance == null)
                return;

            Instance.MarkChunkMeshDirtyInternal(
                chunk,
                prioritizeForInteraction
                    ? MeshRequestPriority.Interactive
                    : MeshRequestPriority.Background);
        }

        private static void MarkChunkMeshDirty(Chunk chunk, bool prioritizeForInteraction)
        {
            if (Instance == null)
            {
                if (chunk != null && chunk.Blocks != null && chunk.IsGenerated)
                {
                    chunk.Generate(prioritizeForInteraction
                        ? MeshRequestPriority.Interactive
                        : MeshRequestPriority.Background);
                }
                return;
            }

            bool skylightChanged = chunk != null && chunk.ConsumeSkylightInvalidationPending();
            bool blockLightChanged = chunk != null && chunk.ConsumeBlockLightInvalidationPending();

            if (prioritizeForInteraction)
            {
                Instance.MarkChunkMeshDirtyInternal(
                    chunk,
                    MeshRequestPriority.Interactive,
                    isInteractiveSource: true);
            }

            if (skylightChanged)
            {
                Instance.MarkChunkSkylightDependentsDirtyInternal(
                    chunk,
                    includeSource: !prioritizeForInteraction);
            }
            else if (!prioritizeForInteraction)
            {
                Instance.MarkChunkMeshDirtyInternal(chunk);
            }

            if (blockLightChanged)
            {
                Instance.MarkChunkBlockLightDependentsDirtyInternal(
                    chunk,
                    includeSource: !prioritizeForInteraction);
            }
        }

        public static bool TrySampleVoxelLighting(Vector3 worldPosition, out Vector2 lighting)
        {
            return TrySampleVoxelLighting(ChunkUtility.SnapPosition(worldPosition), out lighting);
        }

        public static bool TrySampleVoxelLighting(Vector3Int worldPosition, out Vector2 lighting)
        {
            lighting = Vector2.zero;
            Vector3Int chunkCoordinate = ChunkUtility.GetChunkCoordinateFromPosition(worldPosition);
            if (!Chunks.TryGetValue(chunkCoordinate, out Chunk chunk) || chunk == null)
                return false;

            var localPosition = new Vector3Int(
                worldPosition.x - chunkCoordinate.x * Chunk.CHUNK_SIZE,
                worldPosition.y - chunkCoordinate.y * Chunk.CHUNK_HEIGHT,
                worldPosition.z - chunkCoordinate.z * Chunk.CHUNK_SIZE);
            if (!chunk.TryGetVoxelLighting(localPosition, out byte packedLighting))
                return false;

            lighting = new Vector2(
                (packedLighting & 0x0F) / (float)ChunkMeshGenerator.MaximumSkylight,
                ((packedLighting >> 4) & 0x0F) / (float)ChunkMeshGenerator.MaximumBlockLight);
            return true;
        }

        public static void MarkChunkMeshDirty(Vector3Int chunkCoordinate)
        {
            if (Chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                MarkChunkMeshDirty(chunk);
        }

        public static void MarkChunkMeshDirty(Chunk chunk, Vector3Int localPosition)
        {
            MarkChunkMeshDirty(chunk, localPosition, prioritizeForInteraction: false);
        }

        public static void MarkChunkMeshDirty(
            Chunk chunk,
            Vector3Int localPosition,
            bool prioritizeForInteraction)
        {
            MarkChunkMeshDirty(chunk, prioritizeForInteraction);

            if (chunk == null)
                return;

            if (localPosition.x == 0)
                MarkChunkGeometryDirty(chunk.Coordinate + Vector3Int.left, prioritizeForInteraction);
            else if (localPosition.x == Chunk.CHUNK_SIZE - 1)
                MarkChunkGeometryDirty(chunk.Coordinate + Vector3Int.right, prioritizeForInteraction);

            if (localPosition.y == 0)
                MarkChunkGeometryDirty(chunk.Coordinate + Vector3Int.down, prioritizeForInteraction);
            else if (localPosition.y == Chunk.CHUNK_HEIGHT - 1)
                MarkChunkGeometryDirty(chunk.Coordinate + Vector3Int.up, prioritizeForInteraction);

            if (localPosition.z == 0)
                MarkChunkGeometryDirty(chunk.Coordinate + Vector3Int.back, prioritizeForInteraction);
            else if (localPosition.z == Chunk.CHUNK_SIZE - 1)
                MarkChunkGeometryDirty(chunk.Coordinate + Vector3Int.forward, prioritizeForInteraction);
        }

        internal static void MarkChunkSkylightDependentsDirty(Chunk sourceChunk)
        {
            if (Instance != null)
                Instance.MarkChunkSkylightDependentsDirtyInternal(sourceChunk, includeSource: false);
        }

        internal static void MarkChunkBlockLightDependentsDirty(Chunk sourceChunk)
        {
            if (Instance != null)
                Instance.MarkChunkBlockLightDependentsDirtyInternal(sourceChunk, includeSource: false);
        }

        private static void MarkChunkGeometryDirty(
            Vector3Int chunkCoordinate,
            bool prioritizeForInteraction)
        {
            if (!Chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                return;

            if (Instance == null)
            {
                if (chunk.Blocks != null && chunk.IsGenerated)
                {
                    chunk.Generate(prioritizeForInteraction
                        ? MeshRequestPriority.Interactive
                        : MeshRequestPriority.Background);
                }
                return;
            }

            Instance.MarkChunkMeshDirtyInternal(
                chunk,
                prioritizeForInteraction
                    ? MeshRequestPriority.Interactive
                    : MeshRequestPriority.Background);
        }

        private void MarkChunkSkylightDependentsDirtyInternal(Chunk sourceChunk, bool includeSource)
        {
            if (sourceChunk == null)
                return;

            Vector3Int source = sourceChunk.Coordinate;
            int bottomChunkY = Math.Min(source.y, GetTerrainBottomChunkY());
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    for (int chunkY = source.y; chunkY >= bottomChunkY; chunkY--)
                    {
                        var coordinate = new Vector3Int(
                            source.x + offsetX,
                            chunkY,
                            source.z + offsetZ);
                        if (!Chunks.TryGetValue(coordinate, out Chunk candidate) ||
                            candidate == null ||
                            candidate.Blocks == null ||
                            !candidate.IsGenerated)
                        {
                            continue;
                        }

                        if (!includeSource && ReferenceEquals(candidate, sourceChunk))
                            continue;

                        MarkChunkMeshDirtyInternal(candidate);
                    }
                }
            }
        }

        private void MarkChunkBlockLightDependentsDirtyInternal(Chunk sourceChunk, bool includeSource)
        {
            if (sourceChunk == null)
                return;

            Vector3Int source = sourceChunk.Coordinate;
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            {
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (!includeSource && offsetX == 0 && offsetY == 0 && offsetZ == 0)
                            continue;

                        var coordinate = new Vector3Int(
                            source.x + offsetX,
                            source.y + offsetY,
                            source.z + offsetZ);
                        if (Chunks.TryGetValue(coordinate, out Chunk candidate) &&
                            candidate != null &&
                            candidate.Blocks != null &&
                            candidate.IsGenerated)
                        {
                            MarkChunkMeshDirtyInternal(candidate);
                        }
                    }
                }
            }
        }

        private void MarkChunkMeshDirtyInternal(
            Chunk chunk,
            MeshRequestPriority priority = MeshRequestPriority.Background,
            bool isInteractiveSource = false)
        {
            if (chunk == null || chunk.Blocks == null)
                return;

            if (priority == MeshRequestPriority.Interactive)
            {
                if (!chunk.IsGenerated && !isInteractiveSource)
                    return;

                if (isInteractiveSource)
                    interactiveSourceMeshChunks.Add(chunk);

                dirtyMeshChunks.Remove(chunk);
                if (interactiveDirtyMeshChunks.Add(chunk))
                    interactiveDirtyMeshQueue.Enqueue(chunk);
            }
            else if (chunk.IsGenerated && !interactiveDirtyMeshChunks.Contains(chunk))
            {
                dirtyMeshChunks.Add(chunk);
            }
        }

        private void ProcessInteractiveChunkMeshRebuilds()
        {
            int frame = Time.frameCount;
            if (interactiveMeshSubmissionFrame != frame)
            {
                interactiveMeshSubmissionFrame = frame;
                interactiveMeshSubmissionsRemaining = Mathf.Max(1, maxInteractiveChunkMeshesPerFrame);
            }

            while (interactiveMeshSubmissionsRemaining > 0 && interactiveDirtyMeshQueue.Count > 0)
            {
                Chunk chunk = interactiveDirtyMeshQueue.Peek();
                bool isInteractiveSource = interactiveSourceMeshChunks.Contains(chunk);

                if (chunk == null || chunk.Blocks == null ||
                    (!chunk.IsGenerated && !isInteractiveSource))
                {
                    interactiveDirtyMeshQueue.Dequeue();
                    interactiveDirtyMeshChunks.Remove(chunk);
                    dirtyMeshChunks.Remove(chunk);
                    interactiveSourceMeshChunks.Remove(chunk);
                    continue;
                }

                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.InteractiveMeshSubmission,
                        urgent: true,
                        allowStarvation: true,
                        out long submissionStartedAt))
                {
                    break;
                }

                interactiveDirtyMeshQueue.Dequeue();
                interactiveDirtyMeshChunks.Remove(chunk);
                dirtyMeshChunks.Remove(chunk);
                interactiveSourceMeshChunks.Remove(chunk);

                if (isInteractiveSource && currentActiveChunks.Add(chunk.Coordinate))
                {
                    currentActiveCoordinateBuffer.Add(chunk.Coordinate);
                    if (targetRebuildPending && pendingTargetChunks.Add(chunk.Coordinate))
                        pendingTargetCoordinateBuffer.Add(chunk.Coordinate);

                    if (targetRebuildPhase == TargetRebuildPhase.SnapshotCurrent)
                    {
                        targetActiveSnapshotCount = currentActiveCoordinateBuffer.Count;
                    }
                    else if ((targetRebuildPhase == TargetRebuildPhase.ApplyNew ||
                              targetRebuildPhase == TargetRebuildPhase.RemoveOld) &&
                             lastActiveChunks.Add(chunk.Coordinate))
                    {
                        nextActiveCoordinateBuffer.Add(chunk.Coordinate);
                    }
                }

                try
                {
                    chunk.Generate(MeshRequestPriority.Interactive);
                }
                finally
                {
                    CompleteRuntimeChunkWork(
                        ChunkRuntimeWorkKind.InteractiveMeshSubmission,
                        submissionStartedAt);
                }

                if (isInteractiveSource)
                    chunk.SetActive(true);

                interactiveMeshSubmissionsRemaining--;
            }
        }

        private void ProcessDirtyChunkMeshRebuilds()
        {
            if (dirtyMeshChunks.Count == 0)
                return;

            dirtyMeshChunkBuffer.Clear();
            int candidateLimit = Mathf.Max(16, maxDirtyChunkMeshesPerFrame * 2);
            foreach (Chunk chunk in dirtyMeshChunks)
            {
                dirtyMeshChunkBuffer.Add(chunk);
                if (dirtyMeshChunkBuffer.Count >= candidateLimit)
                    break;
            }

            int rebuilt = 0;
            for (int i = 0; i < dirtyMeshChunkBuffer.Count && rebuilt < maxDirtyChunkMeshesPerFrame; i++)
            {
                Chunk chunk = dirtyMeshChunkBuffer[i];

                if (chunk == null || chunk.Blocks == null || !chunk.IsGenerated)
                {
                    dirtyMeshChunks.Remove(chunk);
                    continue;
                }

                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.BackgroundMeshSubmission,
                        urgent: false,
                        allowStarvation: true,
                        out long submissionStartedAt))
                {
                    break;
                }

                dirtyMeshChunks.Remove(chunk);

                try
                {
                    chunk.Generate();
                }
                finally
                {
                    CompleteRuntimeChunkWork(
                        ChunkRuntimeWorkKind.BackgroundMeshSubmission,
                        submissionStartedAt);
                }

                rebuilt++;
            }

            dirtyMeshChunkBuffer.Clear();
        }

        private void BeginTargetChunkRebuild(Vector3Int playerChunk)
        {
            CancelTargetActiveSnapshot();
            targetRebuildPending = true;
            targetRebuildCenter = playerChunk;
            targetRebuildHorizontalIndex = 0;
            targetRebuildPhase = TargetRebuildPhase.Sampling;
            targetTransitionCoordinateIndex = 0;
            targetRemovalCoordinateIndex = 0;
            targetColumnPrepared = false;
            targetColumnRangeCount = 0;
            targetColumnRangeIndex = 0;
            pendingTargetChunks.Clear();
            pendingTargetCoordinateBuffer.Clear();
            previousTargetCoordinateBuffer.Clear();
            nextActiveCoordinateBuffer.Clear();
            urgentChunksToCreate.Clear();
            queuedForUrgentCreate.Clear();
            urgentChunksToGenerate.Clear();
            queuedForUrgentGenerate.Clear();
            urgentChunksToActivate.Clear();
            queuedForUrgentActivation.Clear();

            // A full surface-aware target may take several frames to discover on a
            // cold cache. Keep a tiny, known-safe band around the player's current
            // chunk moving immediately so fast travel cannot pin collision/generation
            // work to the previously committed area.
            RefreshUrgentPlayerTargets(playerChunk);
        }

        private bool ProcessTargetChunkRebuild(Vector3Int playerChunk)
        {
            if (!targetRebuildPending)
                return false;

            if (targetRebuildCenter != playerChunk)
            {
                BeginTargetChunkRebuild(playerChunk);
            }

            if (targetRebuildPhase != TargetRebuildPhase.Sampling)
                return ProcessTargetTransition(playerChunk);

            int processedColumns = 0;
            int sampledColumns = 0;
            int processedCoordinates = 0;
            int columnLimit = Mathf.Max(8, maxTargetColumnsProcessedPerFrame);
            int sampleLimit = Mathf.Max(1, maxSurfaceSpanSamplesPerFrame);
            int coordinateLimit = Mathf.Max(8, maxTargetCoordinatesProcessedPerFrame);

            while (targetRebuildHorizontalIndex < horizontalOffsets.Length &&
                   processedColumns < columnLimit &&
                   processedCoordinates < coordinateLimit)
            {
                if (!targetColumnPrepared)
                {
                    if (!TryPrepareTargetColumn(
                            canSampleSurface: sampledColumns < sampleLimit,
                            out bool sampledSurface))
                    {
                        break;
                    }

                    if (sampledSurface)
                        sampledColumns++;
                }

                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.TargetCommit,
                        urgent: false,
                        allowStarvation: true,
                        out long targetBuildStartedAt))
                {
                    break;
                }

                TargetCommitMarker.Begin();
                try
                {
                    int batchCount = 0;
                    while (targetColumnPrepared &&
                           processedCoordinates < coordinateLimit &&
                           batchCount < TargetTransitionBatchSize)
                    {
                        ChunkYRange range = targetColumnRanges[targetColumnRangeIndex];
                        Vector3Int coordinate = new Vector3Int(
                            targetColumnX,
                            targetColumnNextY,
                            targetColumnZ);
                        if (pendingTargetChunks.Add(coordinate))
                            pendingTargetCoordinateBuffer.Add(coordinate);

                        targetColumnNextY++;
                        processedCoordinates++;
                        batchCount++;

                        if (targetColumnNextY > range.Max)
                        {
                            targetColumnRangeIndex++;
                            if (targetColumnRangeIndex < targetColumnRangeCount)
                            {
                                targetColumnNextY = targetColumnRanges[targetColumnRangeIndex].Min;
                            }
                            else
                            {
                                targetColumnPrepared = false;
                                targetRebuildHorizontalIndex++;
                                processedColumns++;
                            }
                        }
                    }
                }
                finally
                {
                    TargetCommitMarker.End();
                    CompleteRuntimeChunkWork(ChunkRuntimeWorkKind.TargetCommit, targetBuildStartedAt);
                }
            }

            if (targetRebuildHorizontalIndex < horizontalOffsets.Length)
                return false;

            Vector3Int latestPlayerChunk = GetPlayerChunkCoordinate();
            if (latestPlayerChunk != targetRebuildCenter)
            {
                BeginTargetChunkRebuild(latestPlayerChunk);
                return false;
            }

            BeginTargetTransitionSnapshot();
            return ProcessTargetTransition(playerChunk);
        }

        private bool TryPrepareTargetColumn(bool canSampleSurface, out bool sampledSurface)
        {
            sampledSurface = false;
            Vector3Int relative = horizontalOffsets[targetRebuildHorizontalIndex];
            targetColumnX = targetRebuildCenter.x + relative.x;
            targetColumnZ = targetRebuildCenter.z + relative.z;
            targetColumnRangeCount = 0;
            targetColumnRangeIndex = 0;

            if (!skipHiddenUndergroundChunks)
            {
                AddPreparedTargetColumnRange(
                    GetMinimumVisibleChunkY(targetRebuildCenter),
                    targetRebuildCenter.y + viewDistanceY);
                FinishPreparingTargetColumn();
                return true;
            }

            int playerRangeY = Mathf.Max(0, hiddenUndergroundPlayerRangeY);
            int playerRangeXZ = Mathf.Max(0, hiddenUndergroundPlayerRangeXZ);
            bool isNearPlayer = relative.x * relative.x + relative.z * relative.z <=
                                playerRangeXZ * playerRangeXZ;
            if (isNearPlayer)
            {
                AddPreparedTargetColumnRange(
                    targetRebuildCenter.y - playerRangeY,
                    targetRebuildCenter.y + playerRangeY);
            }

            Vector2Int columnKey = new Vector2Int(targetColumnX, targetColumnZ);
            if (!surfaceChunkSpanCache.TryGetValue(columnKey, out SurfaceChunkSpan surfaceSpan))
            {
                if (!canSampleSurface ||
                    !TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.SurfaceSampling,
                        urgent: false,
                        allowStarvation: true,
                        out long sampleStartedAt))
                {
                    targetColumnRangeCount = 0;
                    return false;
                }

                bool sampled;
                SurfaceSpanSampleMarker.Begin();
                try
                {
                    sampled = TrySampleSurfaceChunkSpan(targetColumnX, targetColumnZ, out surfaceSpan);
                }
                finally
                {
                    SurfaceSpanSampleMarker.End();
                    CompleteRuntimeChunkWork(ChunkRuntimeWorkKind.SurfaceSampling, sampleStartedAt);
                }

                sampledSurface = true;
                if (sampled)
                {
                    surfaceChunkSpanCache[columnKey] = surfaceSpan;
                }
                else
                {
                    if (!isNearPlayer)
                    {
                        AddPreparedTargetColumnRange(
                            targetRebuildCenter.y - playerRangeY,
                            targetRebuildCenter.y + playerRangeY);
                    }

                    FinishPreparingTargetColumn();
                    return true;
                }
            }

            AddPreparedTargetColumnRange(
                surfaceSpan.MinChunkY - visibleSurfaceChunkDepth,
                surfaceSpan.MaxChunkY + visibleSurfaceChunkHeadroom);
            FinishPreparingTargetColumn();
            return true;
        }

        private void AddPreparedTargetColumnRange(int minChunkY, int maxChunkY)
        {
            if (targetColumnRangeCount >= targetColumnRanges.Length)
                return;

            if (minChunkY > maxChunkY)
                (minChunkY, maxChunkY) = (maxChunkY, minChunkY);

            targetColumnRanges[targetColumnRangeCount++] = new ChunkYRange
            {
                Min = minChunkY,
                Max = maxChunkY
            };
        }

        private void FinishPreparingTargetColumn()
        {
            // Every path above supplies at least one fallback range. Keep this guard
            // defensive for malformed runtime settings.
            if (targetColumnRangeCount == 0)
            {
                AddPreparedTargetColumnRange(targetRebuildCenter.y, targetRebuildCenter.y);
            }

            targetColumnRangeIndex = 0;
            targetColumnNextY = targetColumnRanges[0].Min;
            targetColumnPrepared = true;
        }

        private void BeginTargetTransitionSnapshot()
        {
            CancelTargetActiveSnapshot();
            previousTargetCoordinateBuffer.Clear();
            targetActiveSnapshotIndex = 0;
            targetActiveSnapshotCount = currentActiveCoordinateBuffer.Count;
            targetTransitionCoordinateIndex = 0;
            targetRemovalCoordinateIndex = 0;
            targetRebuildPhase = TargetRebuildPhase.SnapshotCurrent;
        }

        private bool ProcessTargetTransition(Vector3Int playerChunk)
        {
            if (playerChunk != targetRebuildCenter)
            {
                BeginTargetChunkRebuild(playerChunk);
                return false;
            }

            int coordinateLimit = Mathf.Max(8, maxTargetCoordinatesProcessedPerFrame);
            int processedCoordinates = 0;

            while (processedCoordinates < coordinateLimit)
            {
                try
                {
                    switch (targetRebuildPhase)
                    {
                        case TargetRebuildPhase.SnapshotCurrent:
                            if (!ProcessTargetSnapshotBatch(ref processedCoordinates, coordinateLimit))
                                return false;
                            break;
                        case TargetRebuildPhase.ApplyNew:
                            if (!ProcessTargetApplyBatch(ref processedCoordinates, coordinateLimit))
                                return false;
                            break;
                        case TargetRebuildPhase.RemoveOld:
                            if (!ProcessTargetRemovalBatch(ref processedCoordinates, coordinateLimit))
                                return false;
                            if (targetRebuildPhase == TargetRebuildPhase.None)
                                return true;
                            break;
                        default:
                            return false;
                    }
                }
                catch
                {
                    CancelTargetActiveSnapshot();
                    throw;
                }
            }

            return false;
        }

        private bool ProcessTargetSnapshotBatch(ref int processedCoordinates, int coordinateLimit)
        {
            if (!TryBeginRuntimeChunkWork(
                    ChunkRuntimeWorkKind.TargetCommit,
                    urgent: false,
                    allowStarvation: true,
                    out long transitionStartedAt))
            {
                return false;
            }

            TargetCommitMarker.Begin();
            try
            {
                int batchCount = 0;
                while (targetActiveSnapshotIndex < targetActiveSnapshotCount &&
                       processedCoordinates < coordinateLimit &&
                       batchCount < TargetTransitionBatchSize)
                {
                    Vector3Int coordinate = currentActiveCoordinateBuffer[targetActiveSnapshotIndex++];
                    if (currentActiveChunks.Contains(coordinate))
                        previousTargetCoordinateBuffer.Add(coordinate);
                    processedCoordinates++;
                    batchCount++;
                }
            }
            finally
            {
                TargetCommitMarker.End();
                CompleteRuntimeChunkWork(ChunkRuntimeWorkKind.TargetCommit, transitionStartedAt);
            }

            if (targetActiveSnapshotIndex < targetActiveSnapshotCount)
                return true;

            CancelTargetActiveSnapshot();
            nextActiveCoordinateBuffer.Clear();
            lastActiveChunks.Clear();
            targetTransitionCoordinateIndex = 0;
            targetRebuildPhase = TargetRebuildPhase.ApplyNew;
            return true;
        }

        private bool ProcessTargetApplyBatch(ref int processedCoordinates, int coordinateLimit)
        {
            if (targetTransitionCoordinateIndex >= pendingTargetCoordinateBuffer.Count)
            {
                targetRemovalCoordinateIndex = 0;
                targetRebuildPhase = TargetRebuildPhase.RemoveOld;
                return true;
            }

            if (!TryBeginRuntimeChunkWork(
                    ChunkRuntimeWorkKind.TargetCommit,
                    urgent: false,
                    allowStarvation: true,
                    out long transitionStartedAt))
            {
                return false;
            }

            TargetCommitMarker.Begin();
            try
            {
                int batchCount = 0;
                while (targetTransitionCoordinateIndex < pendingTargetCoordinateBuffer.Count &&
                       processedCoordinates < coordinateLimit &&
                       batchCount < TargetTransitionBatchSize)
                {
                    Vector3Int coordinate = pendingTargetCoordinateBuffer[targetTransitionCoordinateIndex++];
                    if (lastActiveChunks.Add(coordinate))
                        nextActiveCoordinateBuffer.Add(coordinate);
                    QueueRuntimeTargetCoordinate(coordinate);
                    processedCoordinates++;
                    batchCount++;
                }
            }
            finally
            {
                TargetCommitMarker.End();
                CompleteRuntimeChunkWork(ChunkRuntimeWorkKind.TargetCommit, transitionStartedAt);
            }

            return true;
        }

        private bool ProcessTargetRemovalBatch(ref int processedCoordinates, int coordinateLimit)
        {
            if (targetRemovalCoordinateIndex >= previousTargetCoordinateBuffer.Count)
            {
                CompleteTargetTransition();
                return true;
            }

            if (!TryBeginRuntimeChunkWork(
                    ChunkRuntimeWorkKind.TargetCommit,
                    urgent: false,
                    allowStarvation: true,
                    out long transitionStartedAt))
            {
                return false;
            }

            TargetCommitMarker.Begin();
            try
            {
                int batchCount = 0;
                while (targetRemovalCoordinateIndex < previousTargetCoordinateBuffer.Count &&
                       processedCoordinates < coordinateLimit &&
                       batchCount < TargetTransitionBatchSize)
                {
                    Vector3Int coordinate = previousTargetCoordinateBuffer[targetRemovalCoordinateIndex++];
                    if (!pendingTargetChunks.Contains(coordinate) &&
                        Chunks.TryGetValue(coordinate, out Chunk previousChunk))
                    {
                        previousChunk.SetMeshCollidersEnabled(false);
                        if (shouldDisableChunks)
                            previousChunk.SetActive(false);
                    }

                    processedCoordinates++;
                    batchCount++;
                }
            }
            finally
            {
                TargetCommitMarker.End();
                CompleteRuntimeChunkWork(ChunkRuntimeWorkKind.TargetCommit, transitionStartedAt);
            }

            if (targetRemovalCoordinateIndex >= previousTargetCoordinateBuffer.Count)
                CompleteTargetTransition();

            return true;
        }

        private void CompleteTargetTransition()
        {
            lastPlayerChunk = targetRebuildCenter;
            targetRebuildPending = false;
            targetRebuildPhase = TargetRebuildPhase.None;
            targetRebuildHorizontalIndex = 0;
            targetTransitionCoordinateIndex = 0;
            targetRemovalCoordinateIndex = 0;
            targetColumnPrepared = false;
            HashSet<Vector3Int> oldActiveChunks = currentActiveChunks;
            currentActiveChunks = pendingTargetChunks;
            pendingTargetChunks = oldActiveChunks;
            List<Vector3Int> oldActiveCoordinateBuffer = currentActiveCoordinateBuffer;
            currentActiveCoordinateBuffer = nextActiveCoordinateBuffer;
            nextActiveCoordinateBuffer = oldActiveCoordinateBuffer;
            nextActiveCoordinateBuffer.Clear();
            ReleaseUrgentOnlyActivations();
            urgentTargetChunks.Clear();
            urgentChunksToActivate.Clear();
            queuedForUrgentActivation.Clear();
            pendingTargetChunks.Clear();
            pendingTargetCoordinateBuffer.Clear();
            previousTargetCoordinateBuffer.Clear();
        }

        private void CancelTargetActiveSnapshot()
        {
            targetActiveSnapshotIndex = 0;
            targetActiveSnapshotCount = 0;
        }

        private void RefreshUrgentPlayerTargets(Vector3Int playerChunk)
        {
            urgentTargetCoordinateBuffer[0] = playerChunk;
            urgentTargetCoordinateBuffer[1] = playerChunk + Vector3Int.down;
            urgentTargetCoordinateBuffer[2] = playerChunk + Vector3Int.right;
            urgentTargetCoordinateBuffer[3] = playerChunk + Vector3Int.left;
            urgentTargetCoordinateBuffer[4] = playerChunk + Vector3Int.forward;
            urgentTargetCoordinateBuffer[5] = playerChunk + Vector3Int.back;
            urgentTargetCoordinateBuffer[6] = playerChunk + Vector3Int.up;

            ReleaseUrgentOnlyActivations(preserveNextUrgentTargets: true);
            urgentTargetChunks.Clear();
            for (int i = 0; i < urgentTargetCoordinateBuffer.Length; i++)
                AddUrgentPlayerCoordinate(urgentTargetCoordinateBuffer[i], activateExisting: true);
        }

        private void AddUrgentPlayerCoordinate(Vector3Int coordinate, bool activateExisting)
        {
            if (!urgentTargetChunks.Add(coordinate))
                return;

            if (QueueRuntimeTargetCoordinate(
                coordinate,
                activateExisting,
                prioritize: true))
            {
                urgentActivatedChunks.Add(coordinate);
            }
        }

        private bool QueueRuntimeTargetCoordinate(
            Vector3Int coordinate,
            bool activateExisting = true,
            bool prioritize = false)
        {
            if (!Chunks.TryGetValue(coordinate, out Chunk chunk))
            {
                if (prioritize)
                {
                    if (queuedForUrgentCreate.Add(coordinate))
                        urgentChunksToCreate.Enqueue(coordinate);
                }
                else if (queuedForCreate.Add(coordinate))
                {
                    chunksToCreate.Add(coordinate);
                }
                return false;
            }

            RegisterResidentChunk(coordinate);
            bool activated = false;
            if (activateExisting && chunk.GameObject != null)
            {
                if (prioritize && !chunk.GameObject.activeSelf)
                {
                    if (queuedForUrgentActivation.Add(coordinate))
                        urgentChunksToActivate.Enqueue(coordinate);
                }
                else
                {
                    activated = !chunk.GameObject.activeSelf;
                    chunk.SetActive(true);
                }
            }

            if (!chunk.IsGenerated)
            {
                if (prioritize)
                {
                    if (queuedForUrgentGenerate.Add(coordinate))
                        urgentChunksToGenerate.Enqueue(coordinate);
                }
                else if (queuedForGenerate.Add(coordinate))
                {
                    chunksToGenerate.Add(coordinate);
                }
            }

            return activated;
        }

        private void ProcessUrgentChunkActivations()
        {
            const int MaxActivationsPerFrame = 2;
            int activatedCount = 0;
            int inspected = 0;
            int inspectionLimit = Mathf.Min(urgentChunksToActivate.Count, 8);
            while (activatedCount < MaxActivationsPerFrame &&
                   inspected < inspectionLimit &&
                   urgentChunksToActivate.Count > 0)
            {
                Vector3Int coordinate = urgentChunksToActivate.Peek();
                inspected++;
                if (!urgentTargetChunks.Contains(coordinate) ||
                    !Chunks.TryGetValue(coordinate, out Chunk chunk) ||
                    chunk?.GameObject == null ||
                    chunk.GameObject.activeSelf)
                {
                    urgentChunksToActivate.Dequeue();
                    queuedForUrgentActivation.Remove(coordinate);
                    continue;
                }

                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.TargetCommit,
                        urgent: true,
                        allowStarvation: true,
                        out long activationStartedAt))
                {
                    break;
                }

                urgentChunksToActivate.Dequeue();
                queuedForUrgentActivation.Remove(coordinate);
                try
                {
                    chunk.SetActive(true);
                    if (!currentActiveChunks.Contains(coordinate))
                        urgentActivatedChunks.Add(coordinate);
                }
                finally
                {
                    CompleteRuntimeChunkWork(
                        ChunkRuntimeWorkKind.TargetCommit,
                        activationStartedAt);
                }

                activatedCount++;
            }
        }

        private void ReleaseUrgentOnlyActivations(bool preserveNextUrgentTargets = false)
        {
            if (urgentActivatedChunks.Count == 0)
                return;

            if (shouldDisableChunks)
            {
                urgentActivationRetentionBuffer.Clear();
                foreach (Vector3Int coordinate in urgentActivatedChunks)
                {
                    if (currentActiveChunks.Contains(coordinate))
                        continue;

                    if (preserveNextUrgentTargets && IsNextUrgentTarget(coordinate))
                    {
                        urgentActivationRetentionBuffer.Add(coordinate);
                        continue;
                    }

                    if (!Chunks.TryGetValue(coordinate, out Chunk chunk))
                        continue;

                    chunk.SetMeshCollidersEnabled(false);
                    chunk.SetActive(false);
                }

                urgentActivatedChunks.Clear();
                for (int i = 0; i < urgentActivationRetentionBuffer.Count; i++)
                    urgentActivatedChunks.Add(urgentActivationRetentionBuffer[i]);
                urgentActivationRetentionBuffer.Clear();
                return;
            }

            urgentActivatedChunks.Clear();
        }

        private bool IsNextUrgentTarget(Vector3Int coordinate)
        {
            for (int i = 0; i < urgentTargetCoordinateBuffer.Length; i++)
            {
                if (urgentTargetCoordinateBuffer[i] == coordinate)
                    return true;
            }

            return false;
        }

        private bool IsRuntimeTargetCoordinate(Vector3Int coordinate)
        {
            return currentActiveChunks.Contains(coordinate) ||
                   pendingTargetChunks.Contains(coordinate) ||
                   urgentTargetChunks.Contains(coordinate);
        }

        private void RegisterResidentChunk(Vector3Int coordinate)
        {
            if (registeredResidentChunks.Add(coordinate))
            {
                Vector2Int column = new Vector2Int(coordinate.x, coordinate.z);
                residentChunkCountsByColumn.TryGetValue(column, out int count);
                residentChunkCountsByColumn[column] = count + 1;
            }

            if (residentChunkScanSet.Add(coordinate))
                residentChunkScanQueue.Enqueue(coordinate);
        }

        private bool UnregisterResidentChunk(Vector3Int coordinate)
        {
            if (!registeredResidentChunks.Remove(coordinate))
                return !residentChunkCountsByColumn.ContainsKey(new Vector2Int(coordinate.x, coordinate.z));

            Vector2Int column = new Vector2Int(coordinate.x, coordinate.z);
            if (!residentChunkCountsByColumn.TryGetValue(column, out int count) || count <= 1)
            {
                residentChunkCountsByColumn.Remove(column);
                return true;
            }

            residentChunkCountsByColumn[column] = count - 1;
            return false;
        }

        private void QueueChunkForUnload(Vector3Int coordinate)
        {
            if (queuedForUnload.Add(coordinate))
                chunksToUnload.Enqueue(coordinate);
        }

        private void ScanResidentChunksForUnload(Vector3Int playerChunk)
        {
            if (!unloadDistantChunks || residentChunkScanQueue.Count == 0)
                return;

            int retentionRadius = GetChunkRetentionRadius();
            int retentionRadiusSquared = retentionRadius * retentionRadius;
            int verticalRetentionRadius = GetVerticalChunkRetentionRadius();
            int scanLimit = Mathf.Min(
                residentChunkScanQueue.Count,
                Mathf.Max(16, Mathf.Max(1, maxChunkUnloadsPerFrame) * 8));
            for (int i = 0; i < scanLimit; i++)
            {
                Vector3Int coordinate = residentChunkScanQueue.Dequeue();
                if (!residentChunkScanSet.Contains(coordinate))
                    continue;

                if (!Chunks.ContainsKey(coordinate))
                {
                    residentChunkScanSet.Remove(coordinate);
                    queuedForUnload.Remove(coordinate);
                    continue;
                }

                // Keep resident coordinates in a rotating ring. This replaces the
                // previous all-chunks enumeration at target commit with a bounded,
                // allocation-free maintenance pass.
                residentChunkScanQueue.Enqueue(coordinate);
                int dx = coordinate.x - playerChunk.x;
                int dz = coordinate.z - playerChunk.z;
                int dy = Mathf.Abs(coordinate.y - playerChunk.y);
                if ((dx * dx + dz * dz <= retentionRadiusSquared &&
                     dy <= verticalRetentionRadius) ||
                    IsChunkRequiredForMeshing(coordinate))
                {
                    continue;
                }

                QueueChunkForUnload(coordinate);
            }
        }

        private void ProcessDistantChunkUnloads(Vector3Int playerChunk)
        {
            ScanResidentChunksForUnload(playerChunk);
            if (!unloadDistantChunks || chunksToUnload.Count == 0 || SaveController.IsSaveInProgress)
                return;

            int retentionRadius = GetChunkRetentionRadius();
            int retentionRadiusSquared = retentionRadius * retentionRadius;
            int verticalRetentionRadius = GetVerticalChunkRetentionRadius();
            int unloaded = 0;
            int unloadLimit = Mathf.Max(1, maxChunkUnloadsPerFrame);
            int inspectionLimit = Mathf.Min(chunksToUnload.Count, Mathf.Max(16, unloadLimit * 8));
            int inspected = 0;

            while (unloaded < unloadLimit && inspected < inspectionLimit && chunksToUnload.Count > 0)
            {
                Vector3Int coordinate = chunksToUnload.Dequeue();
                queuedForUnload.Remove(coordinate);
                inspected++;
                int dx = coordinate.x - playerChunk.x;
                int dz = coordinate.z - playerChunk.z;
                int dy = Mathf.Abs(coordinate.y - playerChunk.y);
                if ((dx * dx + dz * dz <= retentionRadiusSquared &&
                     dy <= verticalRetentionRadius) ||
                    IsChunkRequiredForMeshing(coordinate) ||
                    !Chunks.TryGetValue(coordinate, out Chunk chunk) ||
                    chunk == null)
                {
                    continue;
                }

                if (chunk.IsBlockDataGenerationScheduled ||
                    scheduledBlockDataChunks.Contains(chunk) ||
                    FallingBlockSimulator.IsChunkRequiredByActiveEntity(coordinate))
                {
                    QueueChunkForUnload(coordinate);
                    continue;
                }

                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.ChunkEviction,
                        urgent: false,
                        allowStarvation: true,
                        out long evictionStartedAt))
                {
                    QueueChunkForUnload(coordinate);
                    break;
                }

                bool stagedForUnload = false;
                ChunkEvictionMarker.Begin();
                try
                {
                    // Persisted chunks need a compact recreation snapshot. A clean,
                    // untouched procedural chunk takes the fast path and is regenerated
                    // deterministically if the player returns.
                    stagedForUnload = SaveController.TryStageChunkForUnload(chunk);
                    if (stagedForUnload)
                    {
                        FluidSimulator.ReleaseChunk(chunk);
                        FallingBlockSimulator.ReleaseChunk(chunk);
                        dirtyMeshChunks.Remove(chunk);
                        interactiveDirtyMeshChunks.Remove(chunk);
                        interactiveSourceMeshChunks.Remove(chunk);
                        nearbyColliderChunks.Remove(coordinate);
                        previousNearbyColliderChunks.Remove(coordinate);
                        queuedForCreate.Remove(coordinate);
                        queuedForGenerate.Remove(coordinate);

                        chunk.ReleaseRuntimeDataForStreaming();
                        Chunks.Remove(coordinate);
                        if (UnregisterResidentChunk(coordinate))
                        {
                            surfaceChunkSpanCache.Remove(new Vector2Int(coordinate.x, coordinate.z));
                            Chunk.ReleaseColumnCaches(coordinate);
                        }
                    }
                }
                finally
                {
                    ChunkEvictionMarker.End();
                    CompleteRuntimeChunkWork(ChunkRuntimeWorkKind.ChunkEviction, evictionStartedAt);
                }

                if (!stagedForUnload)
                {
                    QueueChunkForUnload(coordinate);
                    break;
                }

                unloaded++;
            }
        }

        private int GetChunkRetentionRadius()
        {
            int activeRadius = Mathf.Max(
                Mathf.Max(viewDistance, fluidSimulationRange),
                fallingBlockSimulationRange);
            return activeRadius + Mathf.Max(1, chunkRetentionMargin);
        }

        private int GetVerticalChunkRetentionRadius()
        {
            int activeRadius = Mathf.Max(
                Mathf.Max(viewDistanceY, fluidSimulationRangeY),
                fallingBlockSimulationRange);
            return activeRadius + Mathf.Max(1, chunkRetentionMargin);
        }

        private bool IsChunkRequiredForMeshing(Vector3Int coordinate)
        {
            if (currentActiveChunks.Contains(coordinate) ||
                pendingTargetChunks.Contains(coordinate) ||
                urgentTargetChunks.Contains(coordinate))
                return true;

            // Chunk snapshots and greedy face generation consume the immediate six
            // neighbors. Keep that halo resident even when a surface band is far above
            // or below the player's own vertical retention window.
            for (int i = 0; i < NeighborChunkOffsets.Length; i++)
            {
                Vector3Int neighbor = coordinate + NeighborChunkOffsets[i];
                if (currentActiveChunks.Contains(neighbor) ||
                    pendingTargetChunks.Contains(neighbor) ||
                    urgentTargetChunks.Contains(neighbor))
                    return true;
            }

            return false;
        }

        private void UpdateNearbyColliders(Vector3 playerPosition)
        {
            Vector3Int playerChunk = ChunkUtility.GetChunkCoordinateFromPosition(playerPosition);
            float maxDistance = math.sqrt(addColliderDistanceSq);
            int radiusXZ = Mathf.Max(1, Mathf.CeilToInt(maxDistance / Chunk.CHUNK_SIZE));
            int radiusY = Mathf.Max(1, Mathf.CeilToInt(maxDistance / Chunk.CHUNK_HEIGHT));

            nearbyColliderChunks.Clear();
            nearbyColliderCandidates.Clear();

            // Collider range is intentionally independent of the render-distance set.
            // Looking up this small coordinate cube avoids scanning every active surface
            // and underground chunk on every frame while flying.
            for (int offsetY = -radiusY; offsetY <= radiusY; offsetY++)
            {
                for (int offsetZ = -radiusXZ; offsetZ <= radiusXZ; offsetZ++)
                {
                    for (int offsetX = -radiusXZ; offsetX <= radiusXZ; offsetX++)
                    {
                        Vector3Int coordinate = new Vector3Int(
                            playerChunk.x + offsetX,
                            playerChunk.y + offsetY,
                            playerChunk.z + offsetZ);
                        if (!IsRuntimeTargetCoordinate(coordinate) ||
                            !Chunks.TryGetValue(coordinate, out Chunk chunk) ||
                            chunk == null ||
                            !chunk.IsGenerated ||
                            chunk.IsAirOnly)
                        {
                            continue;
                        }

                        float3 delta = chunk.Position - playerPosition;
                        float distanceSquared = math.lengthsq(delta);
                        if (distanceSquared > addColliderDistanceSq)
                            continue;

                        nearbyColliderChunks.Add(coordinate);
                        if (chunk.MeshCollider == null || !chunk.MeshCollider.enabled)
                        {
                            nearbyColliderCandidates.Add(new ColliderCandidate
                            {
                                Chunk = chunk,
                                DistanceSquared = distanceSquared
                            });
                        }
                    }
                }
            }

            foreach (Vector3Int previousCoordinate in previousNearbyColliderChunks)
            {
                if (!nearbyColliderChunks.Contains(previousCoordinate) &&
                    Chunks.TryGetValue(previousCoordinate, out Chunk previousChunk))
                {
                    previousChunk.SetMeshCollidersEnabled(false);
                }
            }

            nearbyColliderCandidates.Sort(static (a, b) =>
                a.DistanceSquared.CompareTo(b.DistanceSquared));
            int colliderAdds = Mathf.Min(
                Mathf.Max(1, maxColliderAddsPerFrame),
                nearbyColliderCandidates.Count);
            for (int i = 0; i < colliderAdds; i++)
            {
                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.ColliderCooking,
                        urgent: i == 0,
                        allowStarvation: true,
                        out long colliderStartedAt))
                {
                    break;
                }

                try
                {
                    nearbyColliderCandidates[i].Chunk.AddMeshCollider();
                }
                finally
                {
                    CompleteRuntimeChunkWork(
                        ChunkRuntimeWorkKind.ColliderCooking,
                        colliderStartedAt);
                }
            }

            previousNearbyColliderChunks.Clear();
            foreach (Vector3Int coordinate in nearbyColliderChunks)
                previousNearbyColliderChunks.Add(coordinate);
        }

        private void ProcessChunkCreation()
        {
            int created = 0;
            int inspected = 0;
            int inspectionLimit = Mathf.Max(16, Mathf.Max(1, maxChunksCreatePerFrame) * 4);

            while (created < maxChunksCreatePerFrame &&
                   inspected < inspectionLimit &&
                   TryPeekChunkCreation(out Vector3Int coordinate, out bool isUrgent))
            {
                inspected++;
                if (!IsChunkRequiredForMeshing(coordinate))
                {
                    ConsumeChunkCreation(coordinate, isUrgent);
                    continue;
                }

                if (Chunks.TryGetValue(coordinate, out Chunk existingChunk))
                {
                    ConsumeChunkCreation(coordinate, isUrgent);
                    RegisterResidentChunk(coordinate);
                    if (!existingChunk.IsGenerated && IsRuntimeTargetCoordinate(coordinate))
                        QueueChunkForGeneration(coordinate, isUrgent || urgentTargetChunks.Contains(coordinate));
                    continue;
                }

                if (!TryBeginRuntimeChunkWork(
                        ChunkRuntimeWorkKind.ChunkCreation,
                        urgent: isUrgent,
                        allowStarvation: true,
                        out long creationStartedAt))
                {
                    break;
                }

                ConsumeChunkCreation(coordinate, isUrgent);

                Chunk chunk;
                try
                {
                    chunk = CreatePreparedChunk(coordinate);
                    Chunks.Add(chunk.Coordinate, chunk);
                    RegisterResidentChunk(chunk.Coordinate);
                }
                finally
                {
                    CompleteRuntimeChunkWork(
                        ChunkRuntimeWorkKind.ChunkCreation,
                        creationStartedAt);
                }

                if (IsRuntimeTargetCoordinate(chunk.Coordinate) &&
                    !chunk.IsGenerated)
                {
                    QueueChunkForGeneration(
                        chunk.Coordinate,
                        isUrgent || urgentTargetChunks.Contains(chunk.Coordinate));
                }

                created++;
            }

            // (Ben-Scr) Compact list when fully consumed
            if (createIndex >= chunksToCreate.Count)
            {
                chunksToCreate.Clear();
                createIndex = 0;
            }
        }

        private bool TryPeekChunkCreation(out Vector3Int coordinate, out bool isUrgent)
        {
            if (urgentChunksToCreate.Count > 0)
            {
                coordinate = urgentChunksToCreate.Peek();
                isUrgent = true;
                return true;
            }

            if (createIndex < chunksToCreate.Count)
            {
                coordinate = chunksToCreate[createIndex];
                isUrgent = false;
                return true;
            }

            coordinate = default;
            isUrgent = false;
            return false;
        }

        private void ConsumeChunkCreation(Vector3Int coordinate, bool isUrgent)
        {
            if (isUrgent)
            {
                urgentChunksToCreate.Dequeue();
                queuedForUrgentCreate.Remove(coordinate);
                return;
            }

            createIndex++;
            queuedForCreate.Remove(coordinate);
        }

        private void QueueChunkForGeneration(Vector3Int coordinate, bool prioritize)
        {
            if (prioritize)
            {
                if (queuedForUrgentGenerate.Add(coordinate))
                    urgentChunksToGenerate.Enqueue(coordinate);
                return;
            }

            if (queuedForGenerate.Add(coordinate))
                chunksToGenerate.Add(coordinate);
        }

        private static Chunk CreatePreparedChunk(Vector3Int coordinate)
        {
            if (SaveController.TryCreateLoadedChunk(coordinate, out Chunk loadedChunk))
            {
                loadedChunk.Prepare_Load();
                return loadedChunk;
            }

            var chunk = new Chunk(coordinate.x, coordinate.y, coordinate.z);
            chunk.Prepare();
            return chunk;
        }

        private void ProcessChunkGeneration()
        {
            int generated = 0;
            int inspected = 0;

            // Keep target order nearest-first. A blocked nearest chunk is revisited on
            // the next frame after its neighbor/data jobs have had time to progress.
            int maxInspect = Mathf.Max(1, maxChunksGeneratePerFrame * 2);

            while (TryPeekChunkGeneration(out Vector3Int coordinate, out bool isUrgent) &&
                   generated < maxChunksGeneratePerFrame &&
                   inspected < maxInspect)
            {
                inspected++;

                if (!IsRuntimeTargetCoordinate(coordinate))
                {
                    ConsumeChunkGeneration(coordinate, isUrgent);
                    continue;
                }

                if (!Chunks.TryGetValue(coordinate, out var chunk) || chunk.IsGenerated)
                {
                    ConsumeChunkGeneration(coordinate, isUrgent);
                    continue;
                }

                if (!ChunkUtility.HasAllNeighborChunks(chunk.Coordinate))
                    EnsureImmediateNeighborChunks(chunk.Coordinate);

                // Start the complete seven-chunk data batch together. Mesh readiness
                // is checked afterward, so one unfinished neighbor no longer serializes
                // scheduling of all the remaining neighbors across later frames.
                ScheduleChunkAndImmediateNeighborData(chunk, isUrgent);

                if (!ChunkUtility.HasAllNeighborChunks(chunk.Coordinate))
                {
                    break;
                }

                if (!TryCompleteChunkAndImmediateNeighborData(chunk, isUrgent))
                {
                    break;
                }

                ChunkRuntimeWorkKind submissionKind = isUrgent
                    ? ChunkRuntimeWorkKind.InteractiveMeshSubmission
                    : ChunkRuntimeWorkKind.StreamingMeshSubmission;
                if (!TryBeginRuntimeChunkWork(
                        submissionKind,
                        urgent: isUrgent,
                        allowStarvation: true,
                        out long submissionStartedAt))
                {
                    break;
                }

                try
                {
                    bool wasViewActive = chunk.GameObject != null && chunk.GameObject.activeSelf;
                    chunk.Generate(isUrgent
                        ? MeshRequestPriority.Interactive
                        : MeshRequestPriority.Streaming);
                    if (isUrgent &&
                        !wasViewActive &&
                        !currentActiveChunks.Contains(coordinate) &&
                        chunk.GameObject != null &&
                        chunk.GameObject.activeSelf)
                    {
                        urgentActivatedChunks.Add(coordinate);
                    }
                }
                finally
                {
                    CompleteRuntimeChunkWork(
                        submissionKind,
                        submissionStartedAt);
                }

                generated++;
                ConsumeChunkGeneration(coordinate, isUrgent);
            }

            CompactChunkGenerationQueue();
        }

        private bool TryPeekChunkGeneration(out Vector3Int coordinate, out bool isUrgent)
        {
            if (urgentChunksToGenerate.Count > 0)
            {
                coordinate = urgentChunksToGenerate.Peek();
                isUrgent = true;
                return true;
            }

            if (generateIndex < chunksToGenerate.Count)
            {
                coordinate = chunksToGenerate[generateIndex];
                isUrgent = false;
                return true;
            }

            coordinate = default;
            isUrgent = false;
            return false;
        }

        private void ConsumeChunkGeneration(Vector3Int coordinate, bool isUrgent)
        {
            if (isUrgent)
            {
                urgentChunksToGenerate.Dequeue();
                queuedForUrgentGenerate.Remove(coordinate);
                return;
            }

            generateIndex++;
            queuedForGenerate.Remove(coordinate);
        }

        private void ScheduleChunkAndImmediateNeighborData(Chunk chunk, bool urgent = false)
        {
            ScheduleChunkData(chunk, urgent);

            for (int i = 0; i < NeighborChunkOffsets.Length; i++)
            {
                Vector3Int neighborCoordinate = chunk.Coordinate + NeighborChunkOffsets[i];
                if (Chunks.TryGetValue(neighborCoordinate, out Chunk neighbor))
                    ScheduleChunkData(neighbor, urgent);
            }
        }

        private void ScheduleChunkData(Chunk chunk, bool urgent = false)
        {
            if (chunk == null ||
                chunk.HasBlockData ||
                chunk.IsBlockDataGenerationScheduled ||
                blockDataSchedulesRemaining <= 0)
            {
                return;
            }

                if (!TryBeginRuntimeChunkWork(
                    ChunkRuntimeWorkKind.BlockDataScheduling,
                    urgent: urgent,
                    allowStarvation: true,
                    out long schedulingStartedAt))
            {
                return;
            }

            try
            {
                chunk.ScheduleBlockDataGeneration();
            }
            finally
            {
                CompleteRuntimeChunkWork(
                    ChunkRuntimeWorkKind.BlockDataScheduling,
                    schedulingStartedAt);
            }

            blockDataSchedulesRemaining--;
        }

        private bool TryCompleteChunkAndImmediateNeighborData(Chunk chunk, bool urgent = false)
        {
            bool allReady = TryCompleteChunkData(chunk, urgent);

            for (int i = 0; i < NeighborChunkOffsets.Length; i++)
            {
                Vector3Int neighborCoordinate = chunk.Coordinate + NeighborChunkOffsets[i];
                if (!Chunks.TryGetValue(neighborCoordinate, out Chunk neighbor))
                {
                    allReady = false;
                    continue;
                }

                if (!TryCompleteChunkData(neighbor, urgent))
                    allReady = false;
            }

            return allReady;
        }

        private bool TryCompleteChunkData(Chunk chunk, bool urgent = false)
        {
            if (chunk == null)
                return false;

            if (chunk.HasBlockData)
                return true;

            if (!chunk.IsBlockDataGenerationScheduled)
                return false;

            if (!chunk.IsBlockDataGenerationComplete || blockDataCompletionsRemaining <= 0)
                return false;

                if (!TryBeginRuntimeChunkWork(
                    ChunkRuntimeWorkKind.ForegroundBlockDataCompletion,
                    urgent: urgent,
                    allowStarvation: true,
                    out long completionStartedAt))
            {
                return false;
            }

            bool completed;
            try
            {
                completed = chunk.CompleteBlockDataGenerationIfReady();
            }
            finally
            {
                CompleteRuntimeChunkWork(
                    ChunkRuntimeWorkKind.ForegroundBlockDataCompletion,
                    completionStartedAt);
            }

            if (!completed)
                return false;

            blockDataCompletionsRemaining--;
            return true;
        }

        private void CompactChunkGenerationQueue()
        {
            if (chunksToGenerate.Count == 0)
                return;

            if (generateIndex < chunksToGenerate.Count && generateIndex <= 64)
                return;

            int writeIndex = 0;
            queuedForGenerate.Clear();

            // Entries before generateIndex were already consumed (generated, invalid,
            // or no longer targeted). Retaining them can resurrect a stale prefix and
            // starve the newer nearest-first suffix after every compaction.
            for (int i = generateIndex; i < chunksToGenerate.Count; i++)
            {
                Vector3Int coordinate = chunksToGenerate[i];
                if (!IsRuntimeTargetCoordinate(coordinate) ||
                    !Chunks.TryGetValue(coordinate, out Chunk chunk) ||
                    chunk == null ||
                    chunk.IsGenerated)
                {
                    continue;
                }

                chunksToGenerate[writeIndex++] = coordinate;
                queuedForGenerate.Add(coordinate);
            }

            if (writeIndex < chunksToGenerate.Count)
                chunksToGenerate.RemoveRange(writeIndex, chunksToGenerate.Count - writeIndex);

            generateIndex = 0;
        }

        private void UpdateChunkVisibility(Vector3Int playerChunk)
        {
            int minVisibleY = GetMinimumVisibleChunkY(playerChunk);
            int maxVisibleY = playerChunk.y + viewDistanceY;

            // (Ben-Scr) Disable chunks no longer in range
            foreach (var position in lastActiveChunks)
            {
                if (currentActiveChunks.Contains(position))
                    continue;

                if (Chunks.TryGetValue(position, out var chunk))
                {
                    chunk.SetMeshCollidersEnabled(false);
                    chunk.SetActive(false);
                }
            }

            foreach (var position in currentActiveChunks)
            {
                if (!Chunks.TryGetValue(position, out var chunk))
                    continue;

                int dx = playerChunk.x - position.x;
                int dz = playerChunk.z - position.z;

                bool visibleXZ = dx * dx + dz * dz <= viewDistanceXZSq;
                bool visibleY = skipHiddenUndergroundChunks ||
                                (position.y >= minVisibleY && position.y <= maxVisibleY);

                chunk.SetActive(visibleXZ && visibleY);
            }

            lastActiveChunks.Clear();
            foreach (var pos in currentActiveChunks)
                lastActiveChunks.Add(pos);
        }

        private void ConfigureFluidSimulation(Vector3Int playerChunk)
        {
            FluidSimulator.Configure(
                simulateFluids,
                waterTickInterval,
                maxWaterBlocksPerTick,
                maxFluidHorizontalSpreadDistance,
                playerChunk,
                fluidSimulationRange,
                fluidSimulationRangeY);
        }

        private int GetMinimumVisibleChunkY(Vector3Int centerChunk)
        {
            int regularWindowBottom = centerChunk.y - viewDistanceY;
            if (!loadTerrainColumnsToBedrock)
                return regularWindowBottom;

            return Mathf.Min(regularWindowBottom, GetTerrainBottomChunkY());
        }

        private static int GetTerrainBottomChunkY()
        {
            int bedrockLevel = NoiseSettings.Instance != null ? NoiseSettings.Instance.BedrockLevel : -256;
            return Mathf.FloorToInt(bedrockLevel / (float)Chunk.CHUNK_HEIGHT);
        }

        private Vector3Int GetPlayerChunkCoordinate()
        {
            return playerTransform != null
                ? ChunkUtility.GetChunkCoordinateFromPosition(playerTransform.position)
                : Vector3Int.zero;
        }

        private void ConfigureFallingBlockSimulation(Vector3Int playerChunk)
        {
            FallingBlockSimulator.Configure(
                simulateFallingBlocks,
                fallingBlockTickInterval,
                maxFallingBlocksPerTick,
                playerChunk,
                fallingBlockSimulationRange);
        }
    }
}
