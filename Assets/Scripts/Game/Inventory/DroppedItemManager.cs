using System;
using System.Collections.Generic;
using BenScr.CubeDash;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    [Serializable]
    public sealed class DroppedItemData
    {
        public int ItemId;
        public int Amount;
        public int Duration;

        public float PositionX;
        public float PositionY;
        public float PositionZ;

        public float VelocityX;
        public float VelocityY;
        public float VelocityZ;

        [NonSerialized] public DroppedItem View;
        [NonSerialized] public DroppedItemData CombineTarget;
        [NonSerialized] public DroppedItemData CombineSource;
        [NonSerialized] public float CombineStartedAt;

        public bool IsCombining => CombineTarget != null || CombineSource != null;

        public Vector3 Position
        {
            get => new Vector3(PositionX, PositionY, PositionZ);
            set
            {
                PositionX = value.x;
                PositionY = value.y;
                PositionZ = value.z;
            }
        }

        public Vector3 Velocity
        {
            get => new Vector3(VelocityX, VelocityY, VelocityZ);
            set
            {
                VelocityX = value.x;
                VelocityY = value.y;
                VelocityZ = value.z;
            }
        }

        public bool IsValid =>
            ItemId >= 0 &&
            Amount > 0 &&
            IsFinite(PositionX) &&
            IsFinite(PositionY) &&
            IsFinite(PositionZ) &&
            IsFinite(VelocityX) &&
            IsFinite(VelocityY) &&
            IsFinite(VelocityZ);

        public DroppedItemData Clone()
        {
            return new DroppedItemData
            {
                ItemId = ItemId,
                Amount = Amount,
                Duration = Duration,
                PositionX = PositionX,
                PositionY = PositionY,
                PositionZ = PositionZ,
                VelocityX = VelocityX,
                VelocityY = VelocityY,
                VelocityZ = VelocityZ
            };
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public class DroppedItemManager : MonoBehaviour
    {
        private const string PersistentViewPoolCategory = "Dropped Item Views";

        [Header("References")]
        [SerializeField] private DroppedItem droppedItemPrefab;

        [Header("Processing Range (Chunks)")]
        [SerializeField, Min(0)] private int horizontalChunkRange = 2;
        [SerializeField, Min(0)] private int verticalChunkRange = 1;

        [Header("Pickup")]
        [SerializeField, Min(0.1f)] private float attractionRadius = 2.5f;
        [SerializeField, Min(0.01f)] private float collectDistance = 0.1f;
        [SerializeField, Min(0f)] private float pickupDelay = 0.75f;
        [SerializeField, Min(0f)] private float pickupRetryDelay = 0.25f;
        [SerializeField, Min(0f)] private float minimumAttractionTime = 0.1f;
        [SerializeField, Min(0f)] private float attractionDuration = 0.4f;
        [SerializeField] private Vector3 attractionTargetOffset = new Vector3(0f, 0.5f, 0f);

        [Header("Drop")]
        [SerializeField, Min(0f)] private float forwardDistance = 1.5f;
        [SerializeField, Min(0f)] private float throwSpeed = 2.5f;
        [SerializeField, Min(0f)] private float upwardThrowSpeed = 1.5f;
        [SerializeField, Min(0f)] private float positionDropPhysicsReleaseDelay = 0.2f;

        [Header("Visual")]
        [SerializeField, Min(0f)] private float bobHeight = 0.08f;
        [SerializeField, Min(0f)] private float bobSpeed = 2.5f;
        [SerializeField, Min(0f)] private float spinSpeed = 45f;

        [Header("Combining")]
        [SerializeField] private bool combineDroppedItems = true;
        [SerializeField, Min(0.1f)] private float combineRadius = 2f;
        [SerializeField, Min(0.01f)] private float combineAttractionDuration = 0.25f;
        [SerializeField, Min(0.01f)] private float combineCompleteDistance = 0.12f;
        [SerializeField, Min(1)] private int maxCombinePairsPerFrame = 16;

        [Header("View Pool")]
        [SerializeField, Min(0)] private int viewPoolPrewarm = 0;
        [SerializeField, Min(0)] private int viewPoolLimit = 256;

        private readonly HashSet<Vector3Int> processedChunks = new();
        private readonly HashSet<Vector3Int> previouslyProcessedChunks = new();
        private readonly List<ChunkTransfer> pendingTransfers = new();
        private readonly List<CombineCandidate> combineCandidates = new();
        private readonly Dictionary<Vector3Int, List<int>> combineCells = new();
        private readonly Stack<List<int>> combineCellListPool = new();
        private readonly List<List<int>> activeCombineCellLists = new();
        private readonly HashSet<DroppedItemData> combineFrameLocks = new();
        private static readonly Stack<DroppedItem> ViewPool = new();

        public static DroppedItemManager Instance { get; private set; }

        private readonly struct CombineCandidate
        {
            public readonly DroppedItemData State;
            public readonly ItemData ItemData;
            public readonly Vector3 Position;

            public CombineCandidate(DroppedItemData state, ItemData itemData)
            {
                State = state;
                ItemData = itemData;
                Position = state.View.transform.position;
            }
        }

        private readonly struct ChunkTransfer
        {
            public readonly Chunk Source;
            public readonly Chunk Target;
            public readonly DroppedItemData Item;

            public ChunkTransfer(Chunk source, Chunk target, DroppedItemData item)
            {
                Source = source;
                Target = target;
                Item = item;
            }
        }

        private void Awake()
        {
            Instance = this;
            TrimViewPool();
            PrewarmViewPool();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            TrimViewPool();
        }

        private void Update()
        {
            if (!TerrainGenerator.IsWorldReady)
                return;

            PlayerController player = PlayerController.Instance;
            if (player == null || droppedItemPrefab == null)
                return;

            Vector3Int playerChunk = ChunkUtility.GetChunkCoordinateFromPosition(player.transform.position);
            processedChunks.Clear();
            pendingTransfers.Clear();
            combineCandidates.Clear();
            combineFrameLocks.Clear();
            ClearCombineSpatialCells();

            int horizontalRangeSquared = horizontalChunkRange * horizontalChunkRange;

            for (int x = -horizontalChunkRange; x <= horizontalChunkRange; x++)
            {
                for (int z = -horizontalChunkRange; z <= horizontalChunkRange; z++)
                {
                    if (x * x + z * z > horizontalRangeSquared)
                        continue;

                    for (int y = -verticalChunkRange; y <= verticalChunkRange; y++)
                    {
                        Vector3Int coordinate = playerChunk + new Vector3Int(x, y, z);
                        if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk))
                            continue;

                        processedChunks.Add(coordinate);
                        ProcessChunk(chunk, player);
                    }
                }
            }

            StartCombinePairs();
            ApplyPendingTransfers();
            UnloadViewsOutsideRange();
            ClearCombineSpatialCells();

            previouslyProcessedChunks.Clear();
            foreach (Vector3Int coordinate in processedChunks)
                previouslyProcessedChunks.Add(coordinate);
        }

        public static bool TryDrop(ItemData itemData, int amount, int duration)
        {
            return Instance != null && Instance.Drop(itemData, amount, duration);
        }

        public static bool TryDropAt(ItemData itemData, int amount, int duration, Vector3 position)
        {
            return Instance != null && Instance.DropAt(itemData, amount, duration, position);
        }

        public static void PrepareForSave()
        {
            if (Instance != null)
                Instance.SynchronizeAllViews();
        }

        public static void ReleaseViewsForChunk(Chunk chunk)
        {
            if (Instance == null || chunk?.DroppedItems == null)
                return;

            for (int i = 0; i < chunk.DroppedItems.Count; i++)
            {
                DroppedItemData state = chunk.DroppedItems[i];
                if (state?.View != null)
                {
                    SynchronizeState(state, chunk);
                    chunk.HasChanged = true;
                }

                Instance.DestroyView(state);
            }
        }

        private bool Drop(ItemData itemData, int amount, int duration)
        {
            PlayerController player = PlayerController.Instance;
            if (player == null || droppedItemPrefab == null || itemData == null || amount <= 0)
                return false;

            if (!AssetsContainer.TryGetItemId(itemData, out int itemId))
            {
                Debug.LogError($"Item '{itemData.name}' is not registered in AssetsContainer.", itemData);
                return false;
            }

            Transform view = Camera.main != null ? Camera.main.transform : player.transform;
            Vector3 forward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.01f)
                forward = player.transform.forward;

            Vector3 origin = player.transform.position + Vector3.up * 0.25f;
            float spawnDistance = forwardDistance;
            int collisionMask = ~(1 << player.gameObject.layer);

            if (Physics.SphereCast(
                    origin,
                    0.25f,
                    forward,
                    out RaycastHit hit,
                    spawnDistance,
                    collisionMask,
                    QueryTriggerInteraction.Ignore))
            {
                spawnDistance = Mathf.Max(0.65f, hit.distance - 0.3f);
            }

            var state = new DroppedItemData
            {
                ItemId = itemId,
                Amount = amount,
                Duration = duration,
                Position = origin + forward * spawnDistance,
                Velocity = forward * throwSpeed + Vector3.up * upwardThrowSpeed
            };

            Vector3Int coordinate = ChunkUtility.GetChunkCoordinateFromPosition(state.Position);
            if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk ownerChunk))
            {
                coordinate = ChunkUtility.GetChunkCoordinateFromPosition(player.transform.position);
                if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out ownerChunk))
                    return false;
            }

            ownerChunk.DroppedItems ??= new List<DroppedItemData>();
            ownerChunk.DroppedItems.Add(state);
            ownerChunk.HasChanged = true;
            state.View = CreateView(state, itemData);
            return state.View != null;
        }

        private bool DropAt(ItemData itemData, int amount, int duration, Vector3 position)
        {
            if (droppedItemPrefab == null || itemData == null || amount <= 0)
                return false;

            if (!IsFinite(position))
                return false;

            if (!AssetsContainer.TryGetItemId(itemData, out int itemId))
            {
                Debug.LogError($"Item '{itemData.name}' is not registered in AssetsContainer.", itemData);
                return false;
            }

            Vector3Int coordinate = ChunkUtility.GetChunkCoordinateFromPosition(position);
            if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk ownerChunk))
                return false;

            var state = new DroppedItemData
            {
                ItemId = itemId,
                Amount = amount,
                Duration = duration,
                Position = position,
                Velocity = Vector3.zero
            };

            ownerChunk.DroppedItems ??= new List<DroppedItemData>();
            ownerChunk.DroppedItems.Add(state);
            ownerChunk.HasChanged = true;

            state.View = CreateView(state, itemData, positionDropPhysicsReleaseDelay);
            if (state.View != null)
                return true;

            ownerChunk.DroppedItems.Remove(state);
            ownerChunk.HasChanged = true;
            return false;
        }

        private void ProcessChunk(Chunk chunk, PlayerController player)
        {
            if (chunk.DroppedItems == null || chunk.DroppedItems.Count == 0)
                return;

            for (int i = chunk.DroppedItems.Count - 1; i >= 0; i--)
            {
                DroppedItemData state = chunk.DroppedItems[i];
                if (state == null || !state.IsValid)
                {
                    DestroyView(state);
                    chunk.DroppedItems.RemoveAt(i);
                    chunk.HasChanged = true;
                    continue;
                }

                ItemData itemData = AssetsContainer.GetItem(state.ItemId);
                if (itemData == null)
                    continue;

                if (state.View == null)
                    state.View = CreateView(state, itemData);

                if (state.View == null)
                    continue;

                state.View.UpdateVisual(Time.time, bobHeight, bobSpeed, spinSpeed);

                if (TickCombine(state, itemData, chunk))
                {
                    DestroyView(state);
                    chunk.DroppedItems.RemoveAt(i);
                    chunk.HasChanged = true;
                    continue;
                }

                if (!state.IsCombining && TickPickup(state, itemData, player))
                {
                    DestroyView(state);
                    chunk.DroppedItems.RemoveAt(i);
                    chunk.HasChanged = true;
                    continue;
                }

                SynchronizeState(state, chunk);

                Vector3Int currentCoordinate =
                    ChunkUtility.GetChunkCoordinateFromPosition(state.Position);

                if (currentCoordinate != chunk.Coordinate &&
                    TerrainGenerator.Chunks.TryGetValue(currentCoordinate, out Chunk targetChunk))
                {
                    pendingTransfers.Add(new ChunkTransfer(chunk, targetChunk, state));
                }

                RegisterCombineCandidate(state, itemData);
            }
        }

        private bool TickPickup(
            DroppedItemData state,
            ItemData itemData,
            PlayerController player)
        {
            DroppedItem view = state.View;
            if (!view.CanBePickedUp)
                return false;

            Vector3 target = player.transform.position + attractionTargetOffset;
            float distanceSquared = (target - view.transform.position).sqrMagnitude;
            float attractionRadiusSquared = attractionRadius * attractionRadius;

            if (!view.IsAttracting && distanceSquared > attractionRadiusSquared)
                return false;

            if (!InventoryManager.CanAcceptItem(itemData, state.Duration))
            {
                if (view.IsAttracting)
                {
                    view.StopAttraction();
                    view.DelayPickup(pickupRetryDelay);
                }

                return false;
            }

            view.BeginAttraction();
            view.LerpTo(target, attractionDuration);
            distanceSquared = (target - view.transform.position).sqrMagnitude;

            if (!view.IsAttracting ||
                Time.time - view.AttractionStartedAt < minimumAttractionTime ||
                distanceSquared > collectDistance * collectDistance)
            {
                return false;
            }

            int remaining = InventoryManager.AddItemFromOther(
                itemData,
                state.Amount,
                state.Duration);

            state.Amount = remaining;
            if (remaining <= 0)
                return true;

            view.SetAmount(itemData, remaining);
            view.DelayPickup(pickupRetryDelay);
            view.StopAttraction();
            return false;
        }

        private void RegisterCombineCandidate(DroppedItemData state, ItemData itemData)
        {
            if (!combineDroppedItems ||
                itemData == null ||
                itemData.StackSize <= 1 ||
                state == null ||
                !state.IsValid ||
                state.View == null ||
                state.IsCombining)
            {
                return;
            }

            int index = combineCandidates.Count;
            combineCandidates.Add(new CombineCandidate(state, itemData));

            float radius = GetCombineRadius();
            Vector3Int cell = GetCombineCell(state.View.transform.position, radius);
            if (!combineCells.TryGetValue(cell, out List<int> cellItems))
            {
                cellItems = GetCombineCellList();
                activeCombineCellLists.Add(cellItems);
                combineCells.Add(cell, cellItems);
            }

            cellItems.Add(index);
        }

        private void StartCombinePairs()
        {
            if (!combineDroppedItems || combineCandidates.Count < 2)
                return;

            float radius = GetCombineRadius();
            float radiusSquared = radius * radius;
            int startedPairs = 0;
            int maxPairs = Mathf.Max(1, maxCombinePairsPerFrame);

            for (int i = 0; i < combineCandidates.Count && startedPairs < maxPairs; i++)
            {
                CombineCandidate candidate = combineCandidates[i];
                if (!CanUseCombineCandidate(candidate))
                    continue;

                if (!TryFindCombinePair(
                        i,
                        candidate,
                        radius,
                        radiusSquared,
                        out DroppedItemData source,
                        out DroppedItemData target,
                        out ItemData itemData))
                {
                    continue;
                }

                if (TryStartCombine(source, target, itemData))
                    startedPairs++;
            }
        }

        private bool TryFindCombinePair(
            int candidateIndex,
            CombineCandidate candidate,
            float cellSize,
            float radiusSquared,
            out DroppedItemData source,
            out DroppedItemData target,
            out ItemData itemData)
        {
            source = null;
            target = null;
            itemData = null;

            Vector3Int centerCell = GetCombineCell(candidate.Position, cellSize);
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        Vector3Int cell = centerCell + new Vector3Int(x, y, z);
                        if (!combineCells.TryGetValue(cell, out List<int> indexes))
                            continue;

                        for (int i = 0; i < indexes.Count; i++)
                        {
                            int otherIndex = indexes[i];
                            if (otherIndex == candidateIndex)
                                continue;

                            CombineCandidate other = combineCandidates[otherIndex];
                            if (TryChooseCombineDirection(
                                    candidate,
                                    other,
                                    radiusSquared,
                                    out source,
                                    out target,
                                    out itemData))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private bool TryChooseCombineDirection(
            CombineCandidate first,
            CombineCandidate second,
            float radiusSquared,
            out DroppedItemData source,
            out DroppedItemData target,
            out ItemData itemData)
        {
            source = null;
            target = null;
            itemData = first.ItemData;

            if (!CanUseCombineCandidate(first) ||
                !CanUseCombineCandidate(second) ||
                first.State == second.State ||
                first.State.ItemId != second.State.ItemId ||
                first.State.Duration != second.State.Duration ||
                itemData == null ||
                itemData.StackSize <= 1 ||
                (first.Position - second.Position).sqrMagnitude > radiusSquared)
            {
                return false;
            }

            int stackSize = itemData.StackSize;
            bool firstCanReceive = first.State.Amount < stackSize;
            bool secondCanReceive = second.State.Amount < stackSize;
            if (!firstCanReceive && !secondCanReceive)
                return false;

            if (firstCanReceive && secondCanReceive)
            {
                bool firstIsLarger = first.State.Amount >= second.State.Amount;
                target = firstIsLarger ? first.State : second.State;
                source = firstIsLarger ? second.State : first.State;
            }
            else if (firstCanReceive)
            {
                target = first.State;
                source = second.State;
            }
            else
            {
                target = second.State;
                source = first.State;
            }

            return source.Amount > 0 && target.Amount < stackSize;
        }

        private bool TryStartCombine(
            DroppedItemData source,
            DroppedItemData target,
            ItemData itemData)
        {
            if (!CanStacksCombine(source, target, itemData) ||
                source.IsCombining ||
                target.IsCombining ||
                target.Amount >= itemData.StackSize)
            {
                return false;
            }

            source.CombineTarget = target;
            source.CombineStartedAt = Time.time;
            target.CombineSource = source;

            source.View.BeginAttraction();
            source.View.DelayPickup(combineAttractionDuration + pickupRetryDelay);
            target.View.DelayPickup(combineAttractionDuration + pickupRetryDelay);

            combineFrameLocks.Add(source);
            combineFrameLocks.Add(target);
            return true;
        }

        private bool TickCombine(
            DroppedItemData source,
            ItemData itemData,
            Chunk ownerChunk)
        {
            DroppedItemData target = source.CombineTarget;
            if (target == null)
                return false;

            if (!CanStacksCombine(source, target, itemData) ||
                target.Amount >= itemData.StackSize)
            {
                ClearCombineLinks(source);
                return false;
            }

            source.View.BeginAttraction();
            source.View.LerpTo(target.View.transform.position, combineAttractionDuration);

            float completeDistanceSquared = combineCompleteDistance * combineCompleteDistance;
            if ((target.View.transform.position - source.View.transform.position).sqrMagnitude >
                completeDistanceSquared)
            {
                return false;
            }

            int stackSize = itemData.StackSize;
            int space = stackSize - target.Amount;
            int moved = Mathf.Min(space, source.Amount);
            if (moved <= 0)
            {
                ClearCombineLinks(source);
                return false;
            }

            target.Amount += moved;
            source.Amount -= moved;
            target.View.SetAmount(itemData, target.Amount);
            MarkDroppedItemChanged(target);
            ownerChunk.HasChanged = true;

            if (source.Amount <= 0)
            {
                source.Amount = 0;
                ClearCombineLinks(source);
                return true;
            }

            source.View.SetAmount(itemData, source.Amount);
            ClearCombineLinks(source);
            source.View.DelayPickup(pickupRetryDelay);
            return false;
        }

        private bool CanUseCombineCandidate(CombineCandidate candidate)
        {
            DroppedItemData state = candidate.State;
            return state != null &&
                   state.IsValid &&
                   state.View != null &&
                   !state.IsCombining &&
                   !combineFrameLocks.Contains(state);
        }

        private static bool CanStacksCombine(
            DroppedItemData source,
            DroppedItemData target,
            ItemData itemData)
        {
            return source != null &&
                   target != null &&
                   source != target &&
                   source.IsValid &&
                   target.IsValid &&
                   source.View != null &&
                   target.View != null &&
                   itemData != null &&
                   itemData.StackSize > 1 &&
                   source.ItemId == target.ItemId &&
                   source.Duration == target.Duration;
        }

        private void ClearCombineSpatialCells()
        {
            for (int i = 0; i < activeCombineCellLists.Count; i++)
            {
                List<int> list = activeCombineCellLists[i];
                list.Clear();
                combineCellListPool.Push(list);
            }

            activeCombineCellLists.Clear();
            combineCells.Clear();
        }

        private List<int> GetCombineCellList()
        {
            return combineCellListPool.Count > 0
                ? combineCellListPool.Pop()
                : new List<int>(4);
        }

        private float GetCombineRadius()
        {
            return Mathf.Max(0.1f, combineRadius);
        }

        private static Vector3Int GetCombineCell(Vector3 position, float cellSize)
        {
            return new Vector3Int(
                Mathf.FloorToInt(position.x / cellSize),
                Mathf.FloorToInt(position.y / cellSize),
                Mathf.FloorToInt(position.z / cellSize));
        }

        private static void ClearCombineLinks(DroppedItemData state)
        {
            if (state == null)
                return;

            DroppedItemData target = state.CombineTarget;
            DroppedItemData source = state.CombineSource;

            if (target != null && target.CombineSource == state)
                target.CombineSource = null;

            if (source != null && source.CombineTarget == state)
            {
                source.CombineTarget = null;
                source.CombineStartedAt = 0f;
                source.View?.StopAttraction();
            }

            if (target != null)
                state.View?.StopAttraction();

            state.CombineTarget = null;
            state.CombineSource = null;
            state.CombineStartedAt = 0f;
        }

        private static void MarkDroppedItemChanged(DroppedItemData state)
        {
            if (state == null)
                return;

            Vector3Int coordinate = ChunkUtility.GetChunkCoordinateFromPosition(state.Position);
            if (TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk))
                chunk.HasChanged = true;
        }

        private DroppedItem CreateView(DroppedItemData state, ItemData itemData)
        {
            return CreateView(state, itemData, 0f);
        }

        private DroppedItem CreateView(DroppedItemData state, ItemData itemData, float physicsReleaseDelay)
        {
            DroppedItem view = GetPooledView();
            if (view == null)
            {
                view = Instantiate(
                    droppedItemPrefab,
                    state.Position,
                    Quaternion.identity,
                    transform);
            }
            else
            {
                PersistentObjectPool.MoveToParent(view.gameObject, transform, false);
                view.transform.SetPositionAndRotation(state.Position, Quaternion.identity);
                view.gameObject.SetActive(true);
            }

            view.Initialize(itemData, state, pickupDelay, physicsReleaseDelay);
            return view;
        }

        private void PrewarmViewPool()
        {
            if (droppedItemPrefab == null || viewPoolPrewarm <= 0 || viewPoolLimit == 0)
                return;

            int targetCount = Mathf.Min(viewPoolPrewarm, viewPoolLimit);
            Transform poolRoot = PersistentObjectPool.GetRoot(PersistentViewPoolCategory);
            for (int i = ViewPool.Count; i < targetCount; i++)
            {
                DroppedItem view = Instantiate(
                    droppedItemPrefab,
                    poolRoot.position,
                    Quaternion.identity,
                    poolRoot);

                ReleaseView(view);
            }
        }

        private DroppedItem GetPooledView()
        {
            while (ViewPool.Count > 0)
            {
                DroppedItem view = ViewPool.Pop();
                if (view != null)
                    return view;
            }

            return null;
        }

        private void ReleaseView(DroppedItem view)
        {
            if (view == null)
                return;

            while (ViewPool.Count > 0 && ViewPool.Peek() == null)
                ViewPool.Pop();

            if (viewPoolLimit == 0 || ViewPool.Count >= viewPoolLimit)
            {
                Destroy(view.gameObject);
                return;
            }

            view.ResetForPool();
            PersistentObjectPool.Store(view.gameObject, PersistentViewPoolCategory);
            ViewPool.Push(view);
        }

        private void TrimViewPool()
        {
            if (ViewPool.Count == 0)
                return;

            int limit = Mathf.Max(0, viewPoolLimit);
            var liveViews = new Stack<DroppedItem>(Mathf.Min(ViewPool.Count, limit));
            while (ViewPool.Count > 0)
            {
                DroppedItem view = ViewPool.Pop();
                if (view == null)
                    continue;

                if (liveViews.Count < limit)
                    liveViews.Push(view);
                else
                    Destroy(view.gameObject);
            }

            while (liveViews.Count > 0)
                ViewPool.Push(liveViews.Pop());
        }

        private static void SynchronizeState(DroppedItemData state, Chunk ownerChunk)
        {
            if (state.View == null)
                return;

            Vector3 oldPosition = state.Position;
            state.Position = state.View.transform.position;
            state.Velocity = state.View.IsAttracting
                ? Vector3.zero
                : state.View.Rigidbody.linearVelocity;

            if ((oldPosition - state.Position).sqrMagnitude > 0.000001f)
                ownerChunk.HasChanged = true;
        }

        private void ApplyPendingTransfers()
        {
            for (int i = 0; i < pendingTransfers.Count; i++)
            {
                ChunkTransfer transfer = pendingTransfers[i];
                if (!transfer.Source.DroppedItems.Remove(transfer.Item))
                    continue;

                transfer.Target.DroppedItems ??= new List<DroppedItemData>();
                transfer.Target.DroppedItems.Add(transfer.Item);
                transfer.Source.HasChanged = true;
                transfer.Target.HasChanged = true;

                if (processedChunks.Count > 0 &&
                    !processedChunks.Contains(transfer.Target.Coordinate))
                {
                    DestroyView(transfer.Item);
                }
            }

            pendingTransfers.Clear();
        }

        private void UnloadViewsOutsideRange()
        {
            foreach (Vector3Int coordinate in previouslyProcessedChunks)
            {
                if (processedChunks.Contains(coordinate))
                    continue;

                if (!TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk) ||
                    chunk.DroppedItems == null)
                {
                    continue;
                }

                for (int i = 0; i < chunk.DroppedItems.Count; i++)
                    DestroyView(chunk.DroppedItems[i]);
            }
        }

        private void SynchronizeAllViews()
        {
            pendingTransfers.Clear();

            foreach (Chunk chunk in TerrainGenerator.Chunks.Values)
            {
                if (chunk.DroppedItems == null)
                    continue;

                for (int i = 0; i < chunk.DroppedItems.Count; i++)
                {
                    DroppedItemData state = chunk.DroppedItems[i];
                    if (state?.View == null)
                        continue;

                    SynchronizeState(state, chunk);
                    Vector3Int coordinate =
                        ChunkUtility.GetChunkCoordinateFromPosition(state.Position);

                    if (coordinate != chunk.Coordinate &&
                        TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk targetChunk))
                    {
                        pendingTransfers.Add(new ChunkTransfer(chunk, targetChunk, state));
                    }
                }
            }

            ApplyPendingTransfers();
        }

        private void DestroyView(DroppedItemData state)
        {
            if (state == null)
                return;

            ClearCombineLinks(state);

            if (state.View == null)
                return;

            DroppedItem view = state.View;
            state.View = null;
            ReleaseView(view);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) &&
                   !float.IsNaN(value.y) &&
                   !float.IsNaN(value.z) &&
                   !float.IsInfinity(value.x) &&
                   !float.IsInfinity(value.y) &&
                   !float.IsInfinity(value.z);
        }
    }
}
