using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;
using static UnityEngine.Mesh;

public class WorldGenerator : MonoBehaviour
{
    public readonly ConcurrentQueue<MapThreadInfo<ChunkMeshData>> meshDataThreadInfoQueue = new ConcurrentQueue<MapThreadInfo<ChunkMeshData>>();

    public Material blockMaterial;
    [SerializeField] private int textureResolution = 16;
    public Block[] blockTypes;

    public GameObject chunkPrefab;
    [SerializeField] private int seed = 0;
    public bool addColliders = false;
    public bool addTrees = true;
    [SerializeField] private int viewDistance = 5;
    [SerializeField] private int viewDistanceY = 2;
    public float noiseScale = 20.0f;
    public float noiseHeight = 10.0f;
    public int groundOffset = 10;
    internal Vector2 noiseOffset;
    [SerializeField] private float chunkUpdateThreshold = 1.0f;
    [SerializeField] private bool disableChunks = false;


    public readonly Dictionary<Vector3Int, Chunk> chunks = new Dictionary<Vector3Int, Chunk>(512);
    private readonly HashSet<Vector3Int> lastActiveChunks = new HashSet<Vector3Int>(512);
    private readonly Queue<Vector3Int> chunksToCreate = new Queue<Vector3Int>(512);
    private readonly Queue<Vector3Int> chunksToGenerate = new Queue<Vector3Int>(512);

    private readonly HashSet<Vector3Int> queuedChunks = new HashSet<Vector3Int>(512);
    private readonly HashSet<Vector3Int> currentActiveChunks = new HashSet<Vector3Int>(512);

    private Vector3 lastChunkUpdatePlayerPosition;
    [SerializeField] private int maxChunksCreatedPerFrame = 2;
    [SerializeField] private int maxChunksGeneratePerFrame = 2;

    public static WorldGenerator instance;
    private Vector3Int[] poses;
    private float chunkUpdateThresholdSq;
    private int viewDistanceSq;
    private int viewDistanceVerticalSq;

    private int lastViewDistance;

    private void Awake()
    {
        int resolution = textureResolution;

        Texture mainTex = blockMaterial.mainTexture;
        Chunk.TEXTURE_BLOCKS_COLS = (int)(1f / mainTex.texelSize.x / resolution) + 1;
        Chunk.TEXTURE_BLOCKS_ROWS = (int)(1f / mainTex.texelSize.y / resolution) + 1;
        Chunk.BLOCK_W = 1f / Chunk.TEXTURE_BLOCKS_COLS;
        Chunk.BLOCK_H = 1f / Chunk.TEXTURE_BLOCKS_ROWS;
        Chunk.TEXTURE_WIDTH = mainTex.width;
        Chunk.TEXTURE_HEIGHT = mainTex.height;

        Debug.Log(mainTex.width + " " + mainTex.height);
        Debug.Log("Blocks in texture: " + Chunk.TEXTURE_BLOCKS_COLS + " x " + Chunk.TEXTURE_BLOCKS_ROWS);

        chunkUpdateThresholdSq = chunkUpdateThreshold * chunkUpdateThreshold;
        instance = this;
        UpdateViewDistance();
    }

    void Start()
    {
        UnityEngine.Random.InitState(seed);
        noiseOffset = new Vector2(UnityEngine.Random.Range(-100000f, 100000f), UnityEngine.Random.Range(-100000f, 100000f));
    }


    public void UpdateViewDistance()
    {
        viewDistanceSq = viewDistance * viewDistance;
        viewDistanceVerticalSq = viewDistanceY * viewDistanceY;

        foreach (Vector3Int chunkCoord in lastActiveChunks)
        {
            Vector3 playerPosition = PlayerController.instance.transform.position;
            Vector3Int playerChunk = GetChunkCoordinateFromPosition(playerPosition);

            bool visible = (playerChunk.x - chunkCoord.x) * (playerChunk.x - chunkCoord.x)
                            + (playerChunk.z - chunkCoord.z) * (playerChunk.z - chunkCoord.z) < viewDistanceSq;

            if (chunks.TryGetValue(chunkCoord, out var ch))
                ch.SetActive(visible);
        }

        GeneratePoses();
        lastViewDistance = viewDistance;
    }
    private void GeneratePoses()
    {
        int rx = viewDistance;
        int ry = viewDistanceY > 0 ? viewDistanceY : viewDistance;
        int rz = viewDistance;


        int cap = (2 * rx + 1) * (2 * ry + 1) * (2 * rz + 1);
        var tmp = new List<Vector3Int>(cap);

        for (int x = -rx; x <= rx; x++)
            for (int y = -ry; y <= ry; y++)
                for (int z = -rz; z <= rz; z++)
                    tmp.Add(new Vector3Int(x, y, z));

        tmp.Sort((a, b) =>
        {
            int ca = Math.Max(Math.Max(Mathf.Abs(a.x), Mathf.Abs(a.y)), Mathf.Abs(a.z));
            int cb = Math.Max(Math.Max(Mathf.Abs(b.x), Mathf.Abs(b.y)), Mathf.Abs(b.z));
            if (ca != cb) return ca - cb;

            int da = a.x * a.x + a.y * a.y + a.z * a.z;
            int db = b.x * b.x + b.y * b.y + b.z * b.z;
            return da - db;
        });

        poses = tmp.ToArray();
    }

    public void Update()
    {
        if (lastViewDistance != viewDistance)
            UpdateViewDistance();

        while (meshDataThreadInfoQueue.TryDequeue(out var threadInfo))
        {
            threadInfo.calback(threadInfo.parameter);
        }

        UpdateChunks(PlayerController.instance.transform.position);
    }


    public void UpdateChunks(Vector3 playerPosition)
    {
        bool movedEnough = (playerPosition - lastChunkUpdatePlayerPosition).sqrMagnitude >= chunkUpdateThresholdSq;
        if (!movedEnough && chunksToCreate.Count == 0 && chunksToGenerate.Count == 0)
            return;

        Vector3Int playerChunk = GetChunkCoordinateFromPosition(playerPosition);


        currentActiveChunks.Clear();

        for (int i = 0; i < poses.Length; i++)
        {
            var offset = poses[i];
            int cx = playerChunk.x + offset.x;
            int cy = playerChunk.y + offset.y;
            int cz = playerChunk.z + offset.z;


            if (offset.x * offset.x + offset.z * offset.z >= viewDistanceSq || offset.y * offset.y >= viewDistanceY)
                continue;

            var key = new Vector3Int(cx, cy, cz);

            if (!chunks.TryGetValue(key, out var chunk))
            {
                if (queuedChunks.Add(key))
                    chunksToCreate.Enqueue(key);
            }
            else
            {
                if (movedEnough)
                    currentActiveChunks.Add(key);

                if (addColliders && chunk.meshCollider == null)
                {
                    float distanceX = playerPosition.x - chunk.position.x;
                    float distanceY = playerPosition.y - chunk.position.y;
                    float distanceZ = playerPosition.z - chunk.position.z;

                    float maxDistance = Chunk.CHUNK_SIZE + 5f;
                    if (distanceX * distanceX + distanceZ * distanceZ + distanceY * distanceY <= maxDistance * maxDistance)
                        chunk.AddMeshCollider();
                }
            }
        }

        int createChunksCount = Math.Min(chunksToCreate.Count, maxChunksCreatedPerFrame);
        for (int i = 0; i < createChunksCount; i++)
        {
            var coordinate = chunksToCreate.Dequeue();
            queuedChunks.Remove(coordinate);

            var targetChunk = new Chunk(coordinate.x, coordinate.y, coordinate.z);
            targetChunk.Prepare();
            chunks.Add(targetChunk.coordinate, targetChunk);
            chunksToGenerate.Enqueue(targetChunk.coordinate);

            if (movedEnough) currentActiveChunks.Add(targetChunk.coordinate);
        }

        int generateChunksCount = Math.Min(chunksToGenerate.Count, maxChunksGeneratePerFrame);
        for (int i = 0; i < generateChunksCount; i++)
        {
            var coordinate = chunksToGenerate.Dequeue();
            var targetChunk = chunks[coordinate];

            if (!HasAllNeighborChunks(targetChunk.coordinate))
            {
                chunksToGenerate.Enqueue(coordinate);
                continue;
            }

            targetChunk.Generate();
            if (movedEnough) currentActiveChunks.Add(targetChunk.coordinate);
        }

        if (movedEnough && disableChunks)
        {
            foreach (var prev in lastActiveChunks)
            {
                bool visible = (playerChunk.x - prev.x) * (playerChunk.x - prev.x)
                               + (playerChunk.z - prev.z) * (playerChunk.z - prev.z) < viewDistanceSq;

                if (chunks.TryGetValue(prev, out var ch))
                    ch.SetActive(visible);
            }

            lastActiveChunks.Clear();
            foreach (var pos in currentActiveChunks)
                lastActiveChunks.Add(pos);
        }

        lastChunkUpdatePlayerPosition = playerPosition;
    }

    public bool HasAllNeighborChunks(Vector3Int chunkCoord)
    {
        return chunks.ContainsKey(new Vector3Int(chunkCoord.x + 1, 0, chunkCoord.z)) &&
               chunks.ContainsKey(new Vector3Int(chunkCoord.x - 1, 0, chunkCoord.z)) &&
               chunks.ContainsKey(new Vector3Int(chunkCoord.x, 0, chunkCoord.z + 1)) &&
               chunks.ContainsKey(new Vector3Int(chunkCoord.x, 0, chunkCoord.z - 1)) &&
               chunks.ContainsKey(new Vector3Int(chunkCoord.x, chunkCoord.y - 1, chunkCoord.z)) &&
               chunks.ContainsKey(new Vector3Int(chunkCoord.x, chunkCoord.y + 1, chunkCoord.z));
    }

    public void RequestMeshData(byte[,,] haloBlocks, Action<ChunkMeshData> callback)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            MeshDataThread(haloBlocks, callback);
        });
    }

    private void MeshDataThread(byte[,,] haloBlocks, Action<ChunkMeshData> callback)
    {
        ChunkMeshData meshData = GenerateMeshData(haloBlocks);
        meshDataThreadInfoQueue.Enqueue(new MapThreadInfo<ChunkMeshData>(callback, meshData));
    }
 

    public ChunkMeshData GenerateMeshData(byte[,,] haloBlocks)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();

        int vertexIndex = 0;

        for (int x = 0; x < Chunk.CHUNK_SIZE; x++)
        {
            for (int y = 0; y < Chunk.CHUNK_HEIGHT; y++)
            {
                for (int z = 0; z < Chunk.CHUNK_SIZE; z++)
                {
                    int blockId = haloBlocks[x + 1, y + 1, z + 1];
                    Block block = blockTypes[blockId];

                    if (blockId != Chunk.BLOCK_AIR)
                    {
                        Vector3Int position = new Vector3Int(x, y, z);

                        for (int face = 0; face < 6; face++)
                        {
                            int neighborBlock = GetHalo(haloBlocks, position + Chunk.cubeNormals[face]);

                            if (neighborBlock == Chunk.BLOCK_AIR /*|| neighborBlock == Chunk.BLOCK_LEAVES*/)
                            {
                                vertices.Add(position + Chunk.cubeVertices[Chunk.cubeTriangles[face, 0]]);
                                vertices.Add(position + Chunk.cubeVertices[Chunk.cubeTriangles[face, 1]]);
                                vertices.Add(position + Chunk.cubeVertices[Chunk.cubeTriangles[face, 2]]);
                                vertices.Add(position + Chunk.cubeVertices[Chunk.cubeTriangles[face, 3]]);

                                for (int i = 0; i < 4; i++)
                                {
                                    normals.Add(Chunk.cubeNormals[face]);
                                }

                                AddTexture(block.GetTexture(face), ref uvs);

                                triangles.Add(vertexIndex);
                                triangles.Add(vertexIndex + 1);
                                triangles.Add(vertexIndex + 2);
                                triangles.Add(vertexIndex + 2);
                                triangles.Add(vertexIndex + 1);
                                triangles.Add(vertexIndex + 3);

                                vertexIndex += 4;
                            }
                        }
                    }
                }
            }
        }

        return new ChunkMeshData(triangles, vertices, normals, uvs);
    }

    private static byte GetHalo(byte[,,] haloBlocks, Vector3Int pos)
    {
        return haloBlocks[pos.x + 1, pos.y + 1, pos.z + 1];
    }

    private void AddTexture(int textureId, ref List<Vector2> uvs)
    {
        int col = textureId % Chunk.TEXTURE_BLOCKS_COLS;
        int rowFromTop = Chunk.TEXTURE_BLOCKS_ROWS - 1 - (textureId / Chunk.TEXTURE_BLOCKS_COLS);

        float u = col * Chunk.BLOCK_W;
        float v = rowFromTop * Chunk.BLOCK_H;


        float epsU = 0.5f / Chunk.TEXTURE_WIDTH;
        float epsV = 0.5f / Chunk.TEXTURE_HEIGHT;

        float u0 = u + epsU;
        float v0 = v + epsV;
        float u1 = u + Chunk.BLOCK_W - epsU;
        float v1 = v + Chunk.BLOCK_H - epsV;

        uvs.Add(new Vector2(u0, v0)); // bottom-left
        uvs.Add(new Vector2(u0, v1)); // top-left
        uvs.Add(new Vector2(u1, v0)); // bottom-right
        uvs.Add(new Vector2(u1, v1)); // top-right
    }

    public int GetBlockAtPosition(Vector3 worldPos)
    {
        var wx = Mathf.FloorToInt(worldPos.x);
        var wy = Mathf.FloorToInt(worldPos.y);
        var wz = Mathf.FloorToInt(worldPos.z);
        return GetBlockAtBlock(new Vector3Int(wx, wy, wz));
    }


    public int GetBlockAtBlock(Vector3Int world)
    {
        var cx = Mathf.FloorToInt((float)world.x / Chunk.CHUNK_SIZE);
        var cy = Mathf.FloorToInt((float)world.y / Chunk.CHUNK_HEIGHT);
        var cz = Mathf.FloorToInt((float)world.z / Chunk.CHUNK_SIZE);
        var cCoord = new Vector3Int(cx, cy, cz);

        if (!chunks.TryGetValue(cCoord, out var chunk))
            return Chunk.BLOCK_AIR;

        var lx = world.x - cx * Chunk.CHUNK_SIZE;
        var ly = world.y - cy * Chunk.CHUNK_HEIGHT;
        var lz = world.z - cz * Chunk.CHUNK_SIZE;


        if ((uint)lx >= Chunk.CHUNK_SIZE || (uint)ly >= Chunk.CHUNK_HEIGHT || (uint)lz >= Chunk.CHUNK_SIZE)
            return Chunk.BLOCK_AIR;

        return chunk.blocks[lx, ly, lz];
    }

    public Chunk GetChunkByCoordinate(int chunkX, int chunkY, int chunkZ)
    {
        if (chunks.TryGetValue(new Vector3Int(chunkX, chunkY, chunkZ), out Chunk chunk))
        {
            return chunk;
        }

        return null;
    }

    Vector3Int GetChunkCoordinateFromPosition(Vector3 position)
    {
        int chunkX = Mathf.FloorToInt(position.x / Chunk.CHUNK_SIZE);
        int chunkY = Mathf.FloorToInt(position.y / Chunk.CHUNK_HEIGHT);
        int chunkZ = Mathf.FloorToInt(position.z / Chunk.CHUNK_SIZE);

        return new Vector3Int(chunkX, chunkY, chunkZ);
    }

    Chunk GetChunkByPosition(Vector3 position)
    {
        Vector3Int coordinate = GetChunkCoordinateFromPosition(position);

        if (chunks.TryGetValue(new Vector3Int(coordinate.x, coordinate.y, coordinate.z), out Chunk chunk))
        {
            return chunk;
        }

        return null;
    }

    public void SetBlock(Vector3 position, int id)
    {
        Chunk chunk = GetChunkByPosition(position);

        if (chunk != null)
        {
            chunk.SetBlock(position - chunk.position, id);
        }
        else
        {
            Debug.LogWarning("Position is outside of world: " + position);
        }
    }
}


public struct MapThreadInfo<T>
{
    public readonly Action<T> calback;
    public readonly T parameter;

    public MapThreadInfo(Action<T> callback, T parameter)
    {
        this.calback = callback;
        this.parameter = parameter;
    }
}

public readonly struct ChunkMeshData
{
    public readonly int[] triangles;
    public readonly Vector3[] vertices;
    public readonly Vector3[] normals;
    public readonly Vector2[] uvs;

    public ChunkMeshData(List<int> triangles, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs)
    {
        this.triangles = triangles.ToArray();
        this.vertices = vertices.ToArray();
        this.normals = normals.ToArray();
        this.uvs = uvs.ToArray();
    }
}


[Serializable]
public class BlockType
{
    public string name;
    public int id;

    public int backTexture;
    public int frontTexture;
    public int topTexture;
    public int bottomTexture;
    public int leftTexture;
    public int rightTexture;

    public int GetTexture(int face)
    {
        switch (face)
        {
            case 0:
                return backTexture;
            case 1:
                return frontTexture;
            case 2:
                return topTexture;
            case 3:
                return bottomTexture;
            case 4:
                return leftTexture;
            case 5:
                return rightTexture;
            default:
                Debug.LogWarning("Invalid face: " + face);
                return backTexture;
        }
    }
}