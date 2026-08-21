using BenScr.MinecraftClone;
using BenScr.UnityStack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public class SaveController : MonoBehaviour
{
    private const int CurrentSaveVersion = 9;
    private const int BlockCountPerChunk = Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT * Chunk.CHUNK_SIZE;
    private const int EncodedBlockCountPerChunk = ((BlockCountPerChunk + 2) / 3) * 4;

    private static readonly Dictionary<Vector3Int, ChunkSaveData> loadedChunks = new();
    private static readonly HashSet<Vector3Int> persistedChunkCoordinates = new();
    private static readonly Dictionary<Vector3Int, HashSet<Vector3Int>> loadedFallingBlockChecks = new();
    private static readonly HashSet<Vector3Int> fallingBlockCheckCaptureBuffer = new();
    private static readonly List<FallingBlockSaveData> loadedFallingBlocks = new();
    private static PlayerSaveData loadedPlayer;
    private static InventorySaveData loadedInventory;
    private static List<WorldInfo> worldInfos = new();
    private static bool catalogLoaded;
    private static bool saveInProgress;
    private static bool worldTransitionInProgress;

    public static string WorldDirPath { get; private set; }
    public static string WorldInfosFilePath { get; private set; }

    public static WorldInfo WorldInfo { get; private set; }
    public static bool IsSaveInProgress => saveInProgress;

    public readonly struct OperationResult
    {
        public bool Success { get; }
        public string Error { get; }

        private OperationResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        internal static OperationResult Succeeded()
        {
            return new OperationResult(true, null);
        }

        internal static OperationResult Failed(string error)
        {
            return new OperationResult(false, error);
        }
    }

    private void Awake()
    {
        InitializePaths();

        if (!EnsureCatalogLoaded(out string error))
            Debug.LogError(error);
    }

    public static IReadOnlyList<WorldInfo> GetWorldInfos()
    {
        InitializePaths();

        if (!EnsureCatalogLoaded(out string error))
        {
            Debug.LogError(error);
            return Array.Empty<WorldInfo>();
        }

        return worldInfos
            .OrderByDescending(info => info.LastPlayedUtcTicks)
            .ThenBy(info => info.WorldName)
            .ToArray();
    }

    public static void ClearActiveWorld()
    {
        if (saveInProgress || worldTransitionInProgress)
        {
            Debug.LogWarning("Cannot clear the active world while a world file operation is in progress.");
            return;
        }

        WorldInfo = null;
        FluidSimulator.Clear();
        FallingBlockSimulator.Clear();
        loadedChunks.Clear();
        persistedChunkCoordinates.Clear();
        loadedFallingBlockChecks.Clear();
        fallingBlockCheckCaptureBuffer.Clear();
        loadedFallingBlocks.Clear();
        loadedPlayer = null;
        loadedInventory = null;
    }

    public static bool TryCreateWorld(string worldName, int seed, out WorldInfo worldInfo, out string error)
    {
        InitializePaths();
        worldInfo = null;
        error = null;

        if (saveInProgress || worldTransitionInProgress)
        {
            error = "Another world file operation is already in progress.";
            return false;
        }

        if (!EnsureCatalogLoaded(out error))
            return false;

        string normalizedName = string.IsNullOrWhiteSpace(worldName)
            ? $"World {worldInfos.Count + 1}"
            : worldName.Trim();

        if (normalizedName.Length > 64)
            normalizedName = normalizedName.Substring(0, 64);

        DateTime now = DateTime.UtcNow;
        worldInfo = new WorldInfo
        {
            Version = CurrentSaveVersion,
            Guid = System.Guid.NewGuid().ToString("N"),
            WorldName = normalizedName,
            Description = "A world created in Voxel Builder",
            Seed = seed,
            LastPlayedUtcTicks = now.Ticks,
            CreationDateUtcTicks = now.Ticks
        };

        var worldData = new WorldData
        {
            Version = CurrentSaveVersion
        };

        string dataFilePath = GetWorldDataFilePath(worldInfo.Guid);

        try
        {
            Json.Serialize(dataFilePath, worldData, compress: true);
            worldInfos.Add(worldInfo);
            SaveWorldInfos();
            ActivateWorld(worldInfo, worldData);
            return true;
        }
        catch (Exception ex)
        {
            worldInfos.Remove(worldInfo);

            if (File.Exists(dataFilePath))
                File.Delete(dataFilePath);

            error = $"Failed to create world '{normalizedName}': {ex.Message}";
            worldInfo = null;
            return false;
        }
    }

    public static bool TryLoadWorld(string guid, out string error)
    {
        InitializePaths();
        error = null;

        if (saveInProgress || worldTransitionInProgress)
        {
            error = "Another world file operation is already in progress.";
            return false;
        }

        if (!TryResolveWorld(guid, out WorldInfo worldInfo, out error))
            return false;

        if (!Json.TryDeserialize(GetWorldDataFilePath(worldInfo.Guid), out WorldData worldData, out error))
            return false;

        if (!TryValidateWorldData(worldData, out error))
            return false;

        return TryFinishWorldLoad(worldInfo, worldData, out error);
    }

    public static async Task<OperationResult> TryLoadWorldAsync(string guid)
    {
        InitializePaths();

        if (saveInProgress || worldTransitionInProgress)
            return OperationResult.Failed("Another world file operation is already in progress.");

        worldTransitionInProgress = true;
        try
        {
            if (!TryResolveWorld(guid, out WorldInfo worldInfo, out string error))
                return OperationResult.Failed(error);

            string dataPath = GetWorldDataFilePath(worldInfo.Guid);
            WorldDataReadResult readResult;

            try
            {
                readResult = await Task.Run(() => ReadWorldData(dataPath));
            }
            catch (Exception ex)
            {
                return OperationResult.Failed($"Could not read {dataPath}: {ex.Message}");
            }

            if (!readResult.Success)
                return OperationResult.Failed(readResult.Error);

            worldInfo.LastPlayedUtcTicks = DateTime.UtcNow.Ticks;
            List<WorldInfo> catalogSnapshot = CloneWorldInfos(worldInfos);

            try
            {
                await Task.Run(() => Json.SerializeList(WorldInfosFilePath, catalogSnapshot));
            }
            catch (Exception ex)
            {
                return OperationResult.Failed(
                    $"Failed to update world '{worldInfo.WorldName}': {ex.Message}");
            }

            ActivateWorld(worldInfo, readResult.WorldData);
            return OperationResult.Succeeded();
        }
        finally
        {
            worldTransitionInProgress = false;
        }
    }

    public static bool TrySaveWorld(out string error)
    {
        InitializePaths();
        error = null;

        if (saveInProgress || worldTransitionInProgress)
        {
            error = "Another world file operation is already in progress.";
            return false;
        }

        saveInProgress = true;
        try
        {
            if (!TryBuildWorldSaveSnapshot(out WorldSaveSnapshot snapshot, out error))
                return false;

            WriteWorldSaveSnapshot(snapshot);
            MarkSnapshotChunksClean(snapshot);
            FinishSuccessfulWorldSave(snapshot);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to save world '{WorldInfo?.WorldName}': {ex.Message}";
            return false;
        }
        finally
        {
            saveInProgress = false;
        }
    }

    public static async Task<OperationResult> TrySaveWorldAsync()
    {
        InitializePaths();

        if (saveInProgress || worldTransitionInProgress)
            return OperationResult.Failed("Another world file operation is already in progress.");

        saveInProgress = true;
        WorldSaveSnapshot snapshot = null;

        try
        {
            if (!TryBuildWorldSaveSnapshot(out snapshot, out string error))
                return OperationResult.Failed(error);

            // Reset the dirty flags before yielding. Any gameplay change made while
            // the immutable snapshot is being written will set its chunk dirty again.
            MarkSnapshotChunksClean(snapshot);

            await Task.Run(() => WriteWorldSaveSnapshot(snapshot));
            FinishSuccessfulWorldSave(snapshot);
            return OperationResult.Succeeded();
        }
        catch (Exception ex)
        {
            RestoreSnapshotDirtyFlags(snapshot);
            return OperationResult.Failed(
                $"Failed to save world '{WorldInfo?.WorldName}': {ex.Message}");
        }
        finally
        {
            saveInProgress = false;
        }
    }

    public static string GetWorldDataFilePath(string guid)
    {
        InitializePaths();

        if (!IsValidGuid(guid))
            throw new ArgumentException("A valid world GUID is required.", nameof(guid));

        return Path.Combine(WorldDirPath, guid + ".json");
    }

    public static bool TryGetLoadedPlayerPosition(out Vector3 position)
    {
        position = default;

        if (loadedPlayer == null || !loadedPlayer.IsValid)
            return false;

        position = loadedPlayer.Position;
        return true;
    }

    public static bool TryRestoreLoadedPlayer(PlayerController player)
    {
        if (player == null || loadedPlayer == null || !loadedPlayer.IsValid)
            return false;

        player.RestoreSavedTransform(
            loadedPlayer.Position,
            loadedPlayer.BodyRotation,
            loadedPlayer.CameraRotation);
        return true;
    }

    public static bool TryRestoreLoadedInventory(InventoryManager inventory)
    {
        if (inventory == null || loadedInventory == null || !loadedInventory.IsValid)
            return false;

        return loadedInventory.TryApply(inventory);
    }

    public static void RestoreLoadedFallingBlockChecks(Vector3Int chunkCoordinate)
    {
        if (!loadedFallingBlockChecks.TryGetValue(
                chunkCoordinate,
                out HashSet<Vector3Int> checks))
        {
            return;
        }

        foreach (Vector3Int worldPosition in checks)
            FallingBlockSimulator.RestorePendingCheck(worldPosition);

        loadedFallingBlockChecks.Remove(chunkCoordinate);
    }

    public static void RestoreLoadedFallingBlocks()
    {
        if (loadedFallingBlocks.Count == 0)
            return;

        for (int i = loadedFallingBlocks.Count - 1; i >= 0; i--)
        {
            FallingBlockSaveData state = loadedFallingBlocks[i];
            if (!FallingBlockSimulator.IsSavedEntityAreaReady(state))
                continue;

            FallingBlockSimulator.TryRestoreActiveEntity(state);
            loadedFallingBlocks.RemoveAt(i);
        }
    }

    public static bool TryCreateLoadedChunk(Vector3Int coordinate, out Chunk chunk)
    {
        chunk = null;

        if (!loadedChunks.TryGetValue(coordinate, out ChunkSaveData savedChunk))
            return false;

        if (!savedChunk.TryCreateChunk(out chunk))
        {
            Debug.LogWarning($"Ignoring invalid saved chunk at {coordinate}.");
            loadedChunks.Remove(coordinate);
            persistedChunkCoordinates.Remove(coordinate);
            return false;
        }

        // The live chunk now owns an uncompressed copy of the data. Keeping the
        // Base64 payload as well would retain roughly another 87 KB per chunk.
        loadedChunks.Remove(coordinate);
        return true;
    }

    /// <summary>
    /// Retains the state needed to recreate a resident chunk before terrain streaming
    /// releases its runtime data. Clean procedural chunks do not need a snapshot and
    /// can be regenerated deterministically when they are visited again.
    /// </summary>
    public static bool TryStageChunkForUnload(Chunk chunk)
    {
        if (chunk == null)
            return true;

        // FinishSuccessfulWorldSave replaces the staged-chunk dictionary. Deferring
        // every eviction while an immutable save is in flight also lets the failure
        // path restore dirty flags on the still-resident chunk without losing data.
        if (saveInProgress)
            return false;

        if (chunk.Blocks != null)
            DroppedItemManager.ReleaseViewsForChunk(chunk);

        try
        {
            Vector3Int coordinate = chunk.Coordinate;
            ChunkSaveData stagedChunk = null;
            if (chunk.Blocks != null &&
                (chunk.HasChanged || persistedChunkCoordinates.Contains(coordinate)))
            {
                stagedChunk = ChunkSaveData.FromChunk(chunk);
            }

            fallingBlockCheckCaptureBuffer.Clear();
            if (loadedFallingBlockChecks.TryGetValue(
                    coordinate,
                    out HashSet<Vector3Int> stagedChecks))
            {
                fallingBlockCheckCaptureBuffer.UnionWith(stagedChecks);
            }

            FallingBlockSimulator.CopyPendingChecksInChunk(
                coordinate,
                fallingBlockCheckCaptureBuffer);

            if (stagedChunk != null)
                loadedChunks[coordinate] = stagedChunk;
            else
                loadedChunks.Remove(coordinate);

            if (fallingBlockCheckCaptureBuffer.Count > 0)
            {
                loadedFallingBlockChecks[coordinate] =
                    new HashSet<Vector3Int>(fallingBlockCheckCaptureBuffer);
            }
            else
            {
                loadedFallingBlockChecks.Remove(coordinate);
            }

            return true;
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            return false;
        }
    }

    private static void InitializePaths()
    {
        if (!string.IsNullOrEmpty(WorldDirPath))
            return;

        WorldDirPath = Path.Combine(Application.persistentDataPath, "Worlds");
        WorldInfosFilePath = Path.Combine(WorldDirPath, "worldInfos.json");
    }

    private static bool TryResolveWorld(string guid, out WorldInfo worldInfo, out string error)
    {
        worldInfo = null;
        error = null;

        if (!EnsureCatalogLoaded(out error))
            return false;

        if (!IsValidGuid(guid))
        {
            error = "The selected world has an invalid GUID.";
            return false;
        }

        worldInfo = worldInfos.FirstOrDefault(
            info => string.Equals(info.Guid, guid, StringComparison.OrdinalIgnoreCase));

        if (worldInfo == null)
        {
            error = $"No world with GUID '{guid}' exists in the world catalog.";
            return false;
        }

        if (worldInfo.Version > CurrentSaveVersion)
        {
            error = $"World '{worldInfo.WorldName}' was created by a newer, unsupported save format.";
            return false;
        }

        return true;
    }

    private static bool TryFinishWorldLoad(
        WorldInfo worldInfo,
        WorldData worldData,
        out string error)
    {
        error = null;
        worldInfo.LastPlayedUtcTicks = DateTime.UtcNow.Ticks;

        try
        {
            SaveWorldInfos();
        }
        catch (Exception ex)
        {
            error = $"Failed to update world '{worldInfo.WorldName}': {ex.Message}";
            return false;
        }

        ActivateWorld(worldInfo, worldData);
        return true;
    }

    private static WorldDataReadResult ReadWorldData(string path)
    {
        if (!Json.TryDeserialize(path, out WorldData worldData, out string error))
            return WorldDataReadResult.Failed(error);

        if (!TryValidateWorldData(worldData, out error))
            return WorldDataReadResult.Failed(error);

        return WorldDataReadResult.Succeeded(worldData);
    }

    private static bool TryBuildWorldSaveSnapshot(
        out WorldSaveSnapshot snapshot,
        out string error)
    {
        snapshot = null;
        error = null;

        if (NoiseSettings.Instance == null)
        {
            error = "Cannot save the world because NoiseSettings is not initialized.";
            return false;
        }

        if (WorldInfo == null || !IsValidGuid(WorldInfo.Guid))
        {
            error = "Cannot save because no catalog world is currently active.";
            return false;
        }

        PlacedBlockManager.PrepareForSave();
        DroppedItemManager.PrepareForSave();

        var chunksToSave = new Dictionary<Vector3Int, ChunkSaveData>(loadedChunks);
        var liveChunkStates = new List<LiveChunkSaveState>();

        foreach (Chunk chunk in TerrainGenerator.Chunks.Values)
        {
            if (chunk?.Blocks == null ||
                (!chunk.HasChanged && !persistedChunkCoordinates.Contains(chunk.Coordinate)))
            {
                continue;
            }

            bool wasDirty = chunk.HasChanged;
            chunksToSave[chunk.Coordinate] = ChunkSaveData.FromChunk(chunk);
            liveChunkStates.Add(new LiveChunkSaveState(chunk, wasDirty));
        }

        var worldData = new WorldData
        {
            Version = CurrentSaveVersion,
            Chunks = chunksToSave.Values
                .OrderBy(chunk => chunk.X)
                .ThenBy(chunk => chunk.Y)
                .ThenBy(chunk => chunk.Z)
                .ToList(),
            Player = PlayerController.Instance != null
                ? PlayerSaveData.FromPlayer(PlayerController.Instance)
                : loadedPlayer,
            Inventory = InventoryManager.Instance != null
                ? InventorySaveData.FromInventory(InventoryManager.Instance)
                : loadedInventory?.Clone(),
            FallingBlockChecks = CreateFallingBlockCheckSnapshot(),
            FallingBlocks = CreateFallingBlockSnapshot()
        };

        WorldInfo.Version = CurrentSaveVersion;
        WorldInfo.Seed = NoiseSettings.Instance.Seed;
        WorldInfo.LastPlayedUtcTicks = DateTime.UtcNow.Ticks;
        UpsertWorldInfo(WorldInfo);

        snapshot = new WorldSaveSnapshot(
            GetWorldDataFilePath(WorldInfo.Guid),
            worldData,
            CloneWorldInfos(worldInfos),
            liveChunkStates);
        return true;
    }

    private static void WriteWorldSaveSnapshot(WorldSaveSnapshot snapshot)
    {
        Json.Serialize(snapshot.DataPath, snapshot.WorldData, compress: true);
        Json.SerializeList(WorldInfosFilePath, snapshot.WorldInfos);
    }

    private static void FinishSuccessfulWorldSave(WorldSaveSnapshot snapshot)
    {
        loadedChunks.Clear();
        persistedChunkCoordinates.Clear();

        foreach (ChunkSaveData chunk in snapshot.WorldData.Chunks)
        {
            Vector3Int coordinate = chunk.Coordinate;
            persistedChunkCoordinates.Add(coordinate);

            // Resident chunks can be recreated for the next save from their live
            // block buffers, so retaining their Base64 strings only wastes memory.
            if (!TerrainGenerator.Chunks.ContainsKey(coordinate))
                loadedChunks[coordinate] = chunk;
        }

        loadedPlayer = snapshot.WorldData.Player;
        loadedInventory = snapshot.WorldData.Inventory?.Clone();
    }

    private static void MarkSnapshotChunksClean(WorldSaveSnapshot snapshot)
    {
        if (snapshot == null)
            return;

        for (int i = 0; i < snapshot.LiveChunks.Count; i++)
            snapshot.LiveChunks[i].Chunk.HasChanged = false;
    }

    private static void RestoreSnapshotDirtyFlags(WorldSaveSnapshot snapshot)
    {
        if (snapshot == null)
            return;

        for (int i = 0; i < snapshot.LiveChunks.Count; i++)
        {
            LiveChunkSaveState state = snapshot.LiveChunks[i];
            if (state.WasDirty)
                state.Chunk.HasChanged = true;
        }
    }

    private static List<WorldInfo> CloneWorldInfos(IReadOnlyList<WorldInfo> source)
    {
        var result = new List<WorldInfo>(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            WorldInfo info = source[i];
            if (info == null)
                continue;

            result.Add(new WorldInfo
            {
                Version = info.Version,
                Guid = info.Guid,
                WorldName = info.WorldName,
                Description = info.Description,
                Seed = info.Seed,
                LastPlayedUtcTicks = info.LastPlayedUtcTicks,
                CreationDateUtcTicks = info.CreationDateUtcTicks
            });
        }

        return result;
    }

    private static List<FallingBlockCheckSaveData> CreateFallingBlockCheckSnapshot()
    {
        var worldPositions = new HashSet<Vector3Int>();
        foreach (HashSet<Vector3Int> checks in loadedFallingBlockChecks.Values)
            worldPositions.UnionWith(checks);

        FallingBlockSimulator.CopyAllPendingChecks(worldPositions);

        return worldPositions
            .OrderBy(position => position.x)
            .ThenBy(position => position.y)
            .ThenBy(position => position.z)
            .Select(position => new FallingBlockCheckSaveData
            {
                X = position.x,
                Y = position.y,
                Z = position.z
            })
            .ToList();
    }

    private static List<FallingBlockSaveData> CreateFallingBlockSnapshot()
    {
        var result = new List<FallingBlockSaveData>(loadedFallingBlocks.Count);
        var startPositions = new HashSet<Vector3Int>();

        for (int i = 0; i < loadedFallingBlocks.Count; i++)
        {
            FallingBlockSaveData state = loadedFallingBlocks[i];
            if (state != null && state.IsValid && startPositions.Add(state.StartWorldPosition))
                result.Add(state.Clone());
        }

        List<FallingBlockSaveData> activeStates = FallingBlockSimulator.CaptureActiveEntities();
        for (int i = 0; i < activeStates.Count; i++)
        {
            FallingBlockSaveData state = activeStates[i];
            if (state != null && state.IsValid && startPositions.Add(state.StartWorldPosition))
                result.Add(state);
        }

        result.Sort((first, second) =>
        {
            int comparison = first.StartX.CompareTo(second.StartX);
            if (comparison != 0)
                return comparison;

            comparison = first.StartY.CompareTo(second.StartY);
            return comparison != 0
                ? comparison
                : first.StartZ.CompareTo(second.StartZ);
        });
        return result;
    }

    private static bool EnsureCatalogLoaded(out string error)
    {
        error = null;

        if (catalogLoaded)
            return true;

        if (!Json.TryDeserializeList(WorldInfosFilePath, out List<WorldInfo> infos, out error))
            return false;

        worldInfos = infos
            .Where(info => info != null && IsValidGuid(info.Guid))
            .GroupBy(info => info.Guid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        catalogLoaded = true;
        return true;
    }

    private static void SaveWorldInfos()
    {
        Json.SerializeList(WorldInfosFilePath, worldInfos);
    }

    private static void UpsertWorldInfo(WorldInfo worldInfo)
    {
        int index = worldInfos.FindIndex(
            info => string.Equals(info.Guid, worldInfo.Guid, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
            worldInfos[index] = worldInfo;
        else
            worldInfos.Add(worldInfo);
    }

    private static bool TryValidateWorldData(WorldData worldData, out string error)
    {
        error = null;

        if (worldData == null)
        {
            error = "The selected world data file is empty.";
            return false;
        }

        if (worldData.Version > CurrentSaveVersion)
        {
            error = "The selected world was created by a newer, unsupported save format.";
            return false;
        }

        return true;
    }

    private static void ActivateWorld(WorldInfo worldInfo, WorldData worldData)
    {
        loadedChunks.Clear();
        persistedChunkCoordinates.Clear();
        loadedFallingBlockChecks.Clear();
        loadedFallingBlocks.Clear();

        if (worldData.Chunks != null)
        {
            foreach (ChunkSaveData chunk in worldData.Chunks)
            {
                if (chunk == null || !chunk.HasStructurallyValidBlockData)
                {
                    Debug.LogWarning($"Ignoring an invalid chunk in world '{worldInfo.WorldName}'.");
                    continue;
                }

                if (worldData.Version < 7)
                    chunk.RemoveDefaultFluidSourceStates();

                if (worldData.Version < 8)
                    chunk.NonAirBlockCount = -1;

                loadedChunks[chunk.Coordinate] = chunk;
                persistedChunkCoordinates.Add(chunk.Coordinate);
            }
        }

        if (worldData.FallingBlockChecks != null)
        {
            for (int i = 0; i < worldData.FallingBlockChecks.Count; i++)
            {
                FallingBlockCheckSaveData check = worldData.FallingBlockChecks[i];
                if (check == null)
                    continue;

                Vector3Int worldPosition = check.WorldPosition;
                Vector3Int chunkCoordinate = ChunkUtility.GetChunkCoordinateFromPosition(worldPosition);
                if (!loadedFallingBlockChecks.TryGetValue(
                        chunkCoordinate,
                        out HashSet<Vector3Int> checks))
                {
                    checks = new HashSet<Vector3Int>();
                    loadedFallingBlockChecks.Add(chunkCoordinate, checks);
                }

                checks.Add(worldPosition);
            }
        }

        if (worldData.FallingBlocks != null)
        {
            for (int i = 0; i < worldData.FallingBlocks.Count; i++)
            {
                FallingBlockSaveData state = worldData.FallingBlocks[i];
                if (state != null && state.IsValid)
                    loadedFallingBlocks.Add(state.Clone());
            }
        }

        loadedPlayer = worldData.Player != null && worldData.Player.IsValid
            ? worldData.Player
            : null;
        loadedInventory = worldData.Inventory != null && worldData.Inventory.IsValid
            ? worldData.Inventory.Clone()
            : null;
        WorldInfo = worldInfo;
    }

    public static int CreateRandomSeed()
    {
        int seed;

        do
        {
            seed = System.Guid.NewGuid().GetHashCode();
        }
        while (seed == 0);

        return seed;
    }

    private static bool IsValidGuid(string guid)
    {
        return System.Guid.TryParseExact(guid, "N", out _);
    }

    private sealed class WorldDataReadResult
    {
        public bool Success { get; private set; }
        public WorldData WorldData { get; private set; }
        public string Error { get; private set; }

        public static WorldDataReadResult Succeeded(WorldData worldData)
        {
            return new WorldDataReadResult
            {
                Success = true,
                WorldData = worldData
            };
        }

        public static WorldDataReadResult Failed(string error)
        {
            return new WorldDataReadResult
            {
                Error = error
            };
        }
    }

    private sealed class WorldSaveSnapshot
    {
        public string DataPath { get; }
        public WorldData WorldData { get; }
        public List<WorldInfo> WorldInfos { get; }
        public List<LiveChunkSaveState> LiveChunks { get; }

        public WorldSaveSnapshot(
            string dataPath,
            WorldData worldData,
            List<WorldInfo> worldInfos,
            List<LiveChunkSaveState> liveChunks)
        {
            DataPath = dataPath;
            WorldData = worldData;
            WorldInfos = worldInfos;
            LiveChunks = liveChunks;
        }
    }

    private readonly struct LiveChunkSaveState
    {
        public Chunk Chunk { get; }
        public bool WasDirty { get; }

        public LiveChunkSaveState(Chunk chunk, bool wasDirty)
        {
            Chunk = chunk;
            WasDirty = wasDirty;
        }
    }

    [Serializable]
    public sealed class ChunkSaveData
    {
        public int X;
        public int Y;
        public int Z;
        public string BlocksBase64;
        public short LowestGroundLevel;
        public short HighestGroundLevel;
        public bool IsAirOnly;
        public int NonAirBlockCount = -1;
        public List<DroppedItemData> DroppedItems = new();
        public List<PlacedBlockData> PlacedBlocks = new();
        public List<FluidBlockSaveData> FluidStates = new();

        public Vector3Int Coordinate => new Vector3Int(X, Y, Z);

        public bool HasStructurallyValidBlockData =>
            BlocksBase64 != null &&
            BlocksBase64.Length == EncodedBlockCountPerChunk &&
            BlocksBase64[EncodedBlockCountPerChunk - 1] == '=';

        public static ChunkSaveData FromChunk(Chunk chunk)
        {
            return new ChunkSaveData
            {
                X = chunk.Coordinate.x,
                Y = chunk.Coordinate.y,
                Z = chunk.Coordinate.z,
                BlocksBase64 = Convert.ToBase64String(chunk.Blocks.Data),
                LowestGroundLevel = chunk.LowestGroundLevel,
                HighestGroundLevel = chunk.HighestGroundLevel,
                IsAirOnly = chunk.IsAirOnly,
                NonAirBlockCount = chunk.NonAirBlockCount,
                DroppedItems = CloneDroppedItems(chunk.DroppedItems),
                PlacedBlocks = ClonePlacedBlocks(chunk.PlacedBlocks),
                FluidStates = CreateFluidStates(chunk)
            };
        }

        public bool TryCreateChunk(out Chunk chunk)
        {
            chunk = null;

            if (!TryDecodeBlocks(out byte[] blocks))
                return false;

            var blockVolume = new VoxelBuffer<byte>(Chunk.CHUNK_SIZE, Chunk.CHUNK_HEIGHT, Chunk.CHUNK_SIZE, blocks);

            List<FluidBlockSaveData> fluidStates = CloneFluidStates(FluidStates);
            chunk = new Chunk(X, Y, Z)
            {
                Blocks = blockVolume,
                LowestGroundLevel = LowestGroundLevel,
                HighestGroundLevel = HighestGroundLevel,
                HasChanged = true,
                DroppedItems = CloneDroppedItems(DroppedItems),
                PlacedBlocks = ClonePlacedBlocks(PlacedBlocks)
            };

            if (!chunk.TryRestoreBlockStats(NonAirBlockCount))
                chunk.RecalculateBlockStats();
            RestoreFluidStates(chunk, fluidStates);
            return true;
        }

        private bool TryDecodeBlocks(out byte[] blocks)
        {
            blocks = null;

            if (string.IsNullOrEmpty(BlocksBase64))
                return false;

            try
            {
                blocks = Convert.FromBase64String(BlocksBase64);
                return blocks.Length == BlockCountPerChunk;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static List<DroppedItemData> CloneDroppedItems(
            List<DroppedItemData> droppedItems)
        {
            var result = new List<DroppedItemData>();
            if (droppedItems == null)
                return result;

            for (int i = 0; i < droppedItems.Count; i++)
            {
                DroppedItemData item = droppedItems[i];
                if (item != null && item.IsValid)
                    result.Add(item.Clone());
            }

            return result;
        }

        private static List<PlacedBlockData> ClonePlacedBlocks(
            List<PlacedBlockData> placedBlocks)
        {
            var result = new List<PlacedBlockData>();
            if (placedBlocks == null)
                return result;

            for (int i = 0; i < placedBlocks.Count; i++)
            {
                PlacedBlockData placedBlock = placedBlocks[i];
                if (placedBlock != null && placedBlock.IsValid)
                    result.Add(placedBlock.Clone());
            }

            return result;
        }

        private static List<FluidBlockSaveData> CreateFluidStates(Chunk chunk)
        {
            var result = new List<FluidBlockSaveData>();
            if (chunk?.Blocks == null)
                return result;

            Vector3Int origin = new Vector3Int(
                chunk.Coordinate.x * Chunk.CHUNK_SIZE,
                chunk.Coordinate.y * Chunk.CHUNK_HEIGHT,
                chunk.Coordinate.z * Chunk.CHUNK_SIZE);

            for (int z = 0; z < Chunk.CHUNK_SIZE; z++)
            {
                for (int y = 0; y < Chunk.CHUNK_HEIGHT; y++)
                {
                    for (int x = 0; x < Chunk.CHUNK_SIZE; x++)
                    {
                        int blockId = chunk.Blocks[x, y, z];
                        Vector3Int worldPosition = origin + new Vector3Int(x, y, z);
                        if (!FluidSimulator.TryGetFluidStateData(
                                worldPosition,
                                blockId,
                                out int depth,
                                out bool isSource,
                                out bool isFalling))
                        {
                            continue;
                        }

                        result.Add(new FluidBlockSaveData
                        {
                            X = x,
                            Y = y,
                            Z = z,
                            Depth = depth,
                            IsSource = isSource,
                            IsFalling = isFalling
                        });
                    }
                }
            }

            return result;
        }

        private static List<FluidBlockSaveData> CloneFluidStates(
            List<FluidBlockSaveData> fluidStates)
        {
            var result = new List<FluidBlockSaveData>();
            if (fluidStates == null)
                return result;

            for (int i = 0; i < fluidStates.Count; i++)
            {
                FluidBlockSaveData state = fluidStates[i];
                if (state != null && state.IsValid)
                    result.Add(state.Clone());
            }

            return result;
        }

        public void RemoveDefaultFluidSourceStates()
        {
            if (FluidStates == null || FluidStates.Count == 0)
                return;

            FluidStates.RemoveAll(state =>
                state == null ||
                (state.IsSource && state.Depth == 0 && !state.IsFalling));
        }

        private static void RestoreFluidStates(Chunk chunk, List<FluidBlockSaveData> fluidStates)
        {
            if (chunk?.Blocks == null || fluidStates == null)
                return;

            Vector3Int origin = new Vector3Int(
                chunk.Coordinate.x * Chunk.CHUNK_SIZE,
                chunk.Coordinate.y * Chunk.CHUNK_HEIGHT,
                chunk.Coordinate.z * Chunk.CHUNK_SIZE);

            for (int i = 0; i < fluidStates.Count; i++)
            {
                FluidBlockSaveData state = fluidStates[i];
                if (state == null || !state.IsValid)
                    continue;

                int blockId = chunk.Blocks[state.X, state.Y, state.Z];
                FluidSimulator.RestoreFluidStateData(
                    origin + new Vector3Int(state.X, state.Y, state.Z),
                    blockId,
                    state.Depth,
                    state.IsSource,
                    state.IsFalling);
            }
        }
    }

    [Serializable]
    public sealed class FluidBlockSaveData
    {
        public int X;
        public int Y;
        public int Z;
        public int Depth;
        public bool IsSource;
        public bool IsFalling;

        public bool IsValid =>
            X >= 0 &&
            X < Chunk.CHUNK_SIZE &&
            Y >= 0 &&
            Y < Chunk.CHUNK_HEIGHT &&
            Z >= 0 &&
            Z < Chunk.CHUNK_SIZE &&
            Depth >= 0;

        public FluidBlockSaveData Clone()
        {
            return new FluidBlockSaveData
            {
                X = X,
                Y = Y,
                Z = Z,
                Depth = Depth,
                IsSource = IsSource,
                IsFalling = IsFalling
            };
        }
    }

    [Serializable]
    public sealed class FallingBlockCheckSaveData
    {
        public int X;
        public int Y;
        public int Z;

        public Vector3Int WorldPosition => new Vector3Int(X, Y, Z);
    }

    [Serializable]
    public sealed class FallingBlockSaveData
    {
        private const int MaxWorldCoordinateMagnitude = 16_000_000;

        public int StartX;
        public int StartY;
        public int StartZ;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float VerticalVelocity;
        public int BlockId;
        public PlacedBlockData PlacedBlock;
        public bool IsPrimedExplosive;
        public float FuseRemaining;
        public float TntFuseSeconds;
        public float TntDestructionRadius;
        public int TntMaxDestroyedBlocks;
        public bool TntDestroyFluids;
        public bool TntDestroyIndestructibleBlocks;
        public bool TntDropDestroyedBlocks;
        public bool TntPrimeNearbyTnt;
        public float TntChainedFuseSeconds;

        public Vector3Int StartWorldPosition => new Vector3Int(StartX, StartY, StartZ);
        public Vector3 Position => new Vector3(PositionX, PositionY, PositionZ);

        public FallingBlockSimulator.TntExplosionSettings ExplosionSettings =>
            new FallingBlockSimulator.TntExplosionSettings
            {
                FuseSeconds = TntFuseSeconds,
                DestructionRadius = TntDestructionRadius,
                MaxDestroyedBlocks = TntMaxDestroyedBlocks,
                DestroyFluids = TntDestroyFluids,
                DestroyIndestructibleBlocks = TntDestroyIndestructibleBlocks,
                DropDestroyedBlocks = TntDropDestroyedBlocks,
                PrimeNearbyTnt = TntPrimeNearbyTnt,
                ChainedFuseSeconds = TntChainedFuseSeconds
            };

        public bool IsValid =>
            BlockId > Chunk.BLOCK_AIR &&
            StartX >= -MaxWorldCoordinateMagnitude &&
            StartX <= MaxWorldCoordinateMagnitude &&
            StartY >= -MaxWorldCoordinateMagnitude &&
            StartY <= MaxWorldCoordinateMagnitude &&
            StartZ >= -MaxWorldCoordinateMagnitude &&
            StartZ <= MaxWorldCoordinateMagnitude &&
            IsFinite(PositionX) &&
            Mathf.Abs(PositionX) <= MaxWorldCoordinateMagnitude &&
            IsFinite(PositionY) &&
            Mathf.Abs(PositionY) <= MaxWorldCoordinateMagnitude &&
            IsFinite(PositionZ) &&
            Mathf.Abs(PositionZ) <= MaxWorldCoordinateMagnitude &&
            IsFinite(VerticalVelocity) &&
            Mathf.Abs(VerticalVelocity) <= FallingBlockSimulator.MaximumFallSpeed &&
            IsFinite(FuseRemaining) &&
            FuseRemaining >= 0f &&
            (PlacedBlock == null || PlacedBlock.IsValid) &&
            ((!IsPrimedExplosive && FuseRemaining == 0f) ||
             (BlockId == Chunk.BLOCK_TNT &&
              IsFinite(TntFuseSeconds) &&
              TntFuseSeconds >= 0.05f &&
              TntFuseSeconds <= FallingBlockSimulator.MaxTntFuseSeconds &&
              FuseRemaining <= TntFuseSeconds &&
              IsFinite(TntDestructionRadius) &&
              TntDestructionRadius >= 0.1f &&
              TntDestructionRadius <= FallingBlockSimulator.MaxTntDestructionRadius &&
              TntMaxDestroyedBlocks > 0 &&
              TntMaxDestroyedBlocks <= FallingBlockSimulator.MaxTntDestroyedBlocks &&
              IsFinite(TntChainedFuseSeconds) &&
              TntChainedFuseSeconds >= 0.05f &&
              TntChainedFuseSeconds <= FallingBlockSimulator.MaxTntFuseSeconds));

        public FallingBlockSaveData Clone()
        {
            return new FallingBlockSaveData
            {
                StartX = StartX,
                StartY = StartY,
                StartZ = StartZ,
                PositionX = PositionX,
                PositionY = PositionY,
                PositionZ = PositionZ,
                VerticalVelocity = VerticalVelocity,
                BlockId = BlockId,
                PlacedBlock = PlacedBlock?.Clone(),
                IsPrimedExplosive = IsPrimedExplosive,
                FuseRemaining = FuseRemaining,
                TntFuseSeconds = TntFuseSeconds,
                TntDestructionRadius = TntDestructionRadius,
                TntMaxDestroyedBlocks = TntMaxDestroyedBlocks,
                TntDestroyFluids = TntDestroyFluids,
                TntDestroyIndestructibleBlocks = TntDestroyIndestructibleBlocks,
                TntDropDestroyedBlocks = TntDropDestroyedBlocks,
                TntPrimeNearbyTnt = TntPrimeNearbyTnt,
                TntChainedFuseSeconds = TntChainedFuseSeconds
            };
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    [Serializable]
    public sealed class PlayerSaveData
    {
        public float PositionX;
        public float PositionY;
        public float PositionZ;

        public float BodyRotationX;
        public float BodyRotationY;
        public float BodyRotationZ;
        public float BodyRotationW;

        public float CameraRotationX;
        public float CameraRotationY;
        public float CameraRotationZ;
        public float CameraRotationW;

        public Vector3 Position => new Vector3(PositionX, PositionY, PositionZ);
        public Quaternion BodyRotation => NormalizeQuaternion(
            new Quaternion(BodyRotationX, BodyRotationY, BodyRotationZ, BodyRotationW));
        public Quaternion CameraRotation => NormalizeQuaternion(
            new Quaternion(CameraRotationX, CameraRotationY, CameraRotationZ, CameraRotationW));

        public bool IsValid =>
            IsFinite(PositionX) &&
            IsFinite(PositionY) &&
            IsFinite(PositionZ) &&
            IsValidQuaternion(BodyRotationX, BodyRotationY, BodyRotationZ, BodyRotationW) &&
            IsValidQuaternion(CameraRotationX, CameraRotationY, CameraRotationZ, CameraRotationW);

        public static PlayerSaveData FromPlayer(PlayerController player)
        {
            Vector3 position = player.transform.position;
            Quaternion bodyRotation = player.SavedBodyRotation;
            Quaternion cameraRotation = player.SavedCameraRotation;

            return new PlayerSaveData
            {
                PositionX = position.x,
                PositionY = position.y,
                PositionZ = position.z,
                BodyRotationX = bodyRotation.x,
                BodyRotationY = bodyRotation.y,
                BodyRotationZ = bodyRotation.z,
                BodyRotationW = bodyRotation.w,
                CameraRotationX = cameraRotation.x,
                CameraRotationY = cameraRotation.y,
                CameraRotationZ = cameraRotation.z,
                CameraRotationW = cameraRotation.w
            };
        }

        private static bool IsValidQuaternion(float x, float y, float z, float w)
        {
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(w))
                return false;

            float magnitudeSquared = x * x + y * y + z * z + w * w;
            return magnitudeSquared > 0.000001f;
        }

        private static Quaternion NormalizeQuaternion(Quaternion rotation)
        {
            float magnitude = Mathf.Sqrt(
                rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w);

            if (magnitude <= 0.000001f)
                return Quaternion.identity;

            return new Quaternion(
                rotation.x / magnitude,
                rotation.y / magnitude,
                rotation.z / magnitude,
                rotation.w / magnitude);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    [Serializable]
    public sealed class InventorySaveData
    {
        public int PlayerSlotCount;
        public int BarSlotCount;
        public int CurrentBarSlotIndex;
        public List<InventorySlotSaveData> Slots = new();

        public bool IsValid =>
            PlayerSlotCount > 0 &&
            BarSlotCount > 0 &&
            Slots != null &&
            Slots.All(slot => slot != null && slot.IsValid);

        public static InventorySaveData FromInventory(InventoryManager inventory)
        {
            var saveData = new InventorySaveData();

            if (inventory == null ||
                InventoryManager.SlotDatas == null ||
                InventoryManager.BarSlots == null)
            {
                return saveData;
            }

            saveData.PlayerSlotCount = InventoryManager.PlayerSlotsCount;
            saveData.BarSlotCount = InventoryManager.BarSlots.Length;
            saveData.CurrentBarSlotIndex = inventory.CurrentSlotIndex;

            int slotCount = Mathf.Min(InventoryManager.PlayerSlotsCount, InventoryManager.SlotDatas.Count);
            for (int i = 0; i < slotCount; i++)
                AddOrMergeSlot(saveData.Slots, i, InventoryManager.SlotDatas[i].Item);

            DraggedItem draggingItem = DragAndDropSystem.DraggingItem;
            if (draggingItem?.Item != null && draggingItem.SlotData != null)
            {
                int draggedSlotIndex = InventoryManager.SlotDatas.IndexOf(draggingItem.SlotData);
                if (draggedSlotIndex >= 0 && draggedSlotIndex < InventoryManager.PlayerSlotsCount)
                    AddOrMergeSlot(saveData.Slots, draggedSlotIndex, draggingItem.Item);
            }

            return saveData;
        }

        public bool TryApply(InventoryManager inventory)
        {
            if (inventory == null ||
                InventoryManager.SlotDatas == null ||
                InventoryManager.BarSlots == null ||
                !IsValid)
            {
                return false;
            }

            inventory.ClearPlayerInventory();

            int availableSlots = Mathf.Min(
                InventoryManager.PlayerSlotsCount,
                InventoryManager.SlotDatas.Count);

            for (int i = 0; i < Slots.Count; i++)
            {
                InventorySlotSaveData slot = Slots[i];
                if (slot == null || !slot.IsValid)
                    continue;

                if (slot.SlotIndex < 0 || slot.SlotIndex >= availableSlots)
                    continue;

                ItemData itemData = AssetsContainer.GetItem(slot.ItemId);
                if (itemData == null)
                    continue;

                int amount = Mathf.Clamp(slot.Amount, 1, itemData.StackSize);
                InventoryManager.CreateNewItem(
                    itemData,
                    amount,
                    slot.Duration,
                    InventoryManager.SlotDatas[slot.SlotIndex]);
            }

            inventory.SetCurrentSlotIndex(CurrentBarSlotIndex);
            inventory.UpdateSlot();
            return true;
        }

        public InventorySaveData Clone()
        {
            var clone = new InventorySaveData
            {
                PlayerSlotCount = PlayerSlotCount,
                BarSlotCount = BarSlotCount,
                CurrentBarSlotIndex = CurrentBarSlotIndex,
                Slots = new List<InventorySlotSaveData>()
            };

            if (Slots == null)
                return clone;

            for (int i = 0; i < Slots.Count; i++)
            {
                InventorySlotSaveData slot = Slots[i];
                if (slot != null && slot.IsValid)
                    clone.Slots.Add(slot.Clone());
            }

            return clone;
        }

        private static void AddOrMergeSlot(
            List<InventorySlotSaveData> slots,
            int slotIndex,
            Item item)
        {
            if (item?.ItemData == null || item.Amount <= 0)
                return;

            if (!AssetsContainer.TryGetItemId(item.ItemData, out int itemId))
                return;

            InventorySlotSaveData existingSlot = slots.Find(slot => slot.SlotIndex == slotIndex);
            if (existingSlot != null)
            {
                if (existingSlot.ItemId == itemId && existingSlot.Duration == item.Duration)
                    existingSlot.Amount += item.Amount;

                return;
            }

            slots.Add(new InventorySlotSaveData
            {
                SlotIndex = slotIndex,
                ItemId = itemId,
                Amount = item.Amount,
                Duration = item.Duration
            });
        }
    }

    [Serializable]
    public sealed class InventorySlotSaveData
    {
        public int SlotIndex;
        public int ItemId;
        public int Amount;
        public int Duration;

        public bool IsValid =>
            SlotIndex >= 0 &&
            ItemId >= 0 &&
            Amount > 0;

        public InventorySlotSaveData Clone()
        {
            return new InventorySlotSaveData
            {
                SlotIndex = SlotIndex,
                ItemId = ItemId,
                Amount = Amount,
                Duration = Duration
            };
        }
    }
}

[Serializable]
public sealed class WorldInfo
{
    public int Version;
    public string Guid;
    public string WorldName;
    public string Description;
    public int Seed;
    public long LastPlayedUtcTicks;
    public long CreationDateUtcTicks;

    public DateTime LastPlayedUtc => LastPlayedUtcTicks > 0
        ? new DateTime(LastPlayedUtcTicks, DateTimeKind.Utc)
        : DateTime.MinValue;

    public DateTime CreationDateUtc => CreationDateUtcTicks > 0
        ? new DateTime(CreationDateUtcTicks, DateTimeKind.Utc)
        : DateTime.MinValue;
}

[Serializable]
public sealed class WorldData
{
    public int Version;
    public List<SaveController.ChunkSaveData> Chunks = new();
    public List<SaveController.FallingBlockCheckSaveData> FallingBlockChecks = new();
    public List<SaveController.FallingBlockSaveData> FallingBlocks = new();
    public SaveController.PlayerSaveData Player;
    public SaveController.InventorySaveData Inventory;
}
