using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class Chunk
{
    public const int CHUNK_SIZE = 32;
    public const int CHUNK_HEIGHT = 32;

    // Block types
    public const int BLOCK_AIR = 0;
    public const int BLOCK_DIRT = 1;
    public const int BLOCK_GRASS = 2;
    public const int BLOCK_STONE = 3;
    public const int BLOCK_WOOD = 4;
    public const int BLOCK_LEAVES = 5;

    public byte[,,] blocks;

    public bool isGenerated = false;
    public GameObject gameObject;
    public MeshRenderer meshRenderer;
    public MeshFilter meshFilter;
    public MeshCollider meshCollider;

    public Vector3Int coordinate;
    public Vector3 position;
    //public bool isTopChunk;

    public Chunk(int x, int y, int z)
    {
        coordinate = new Vector3Int(x, y, z);
    }

    public void AddMeshCollider()
    {
        if (isGenerated)
        {
            meshCollider = gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = meshFilter.mesh;
        }
    }

    public void SetBlock(Vector3 position, int id, bool update = true)
    {
        Vector3Int blockPosition = new Vector3Int(
                    Mathf.FloorToInt(position.x),
                    Mathf.FloorToInt(position.y),
                    Mathf.FloorToInt(position.z)
            );

        if (ChunkUtility.IsInsideChunk(blockPosition))
        {
            blocks[blockPosition.x, blockPosition.y, blockPosition.z] = (byte)id;

            if (update)
            {
                Generate();

                Chunk front = ChunkUtility.GetChunkByCoordinate(coordinate.x, coordinate.y, coordinate.z + 1);
                Chunk back = ChunkUtility.GetChunkByCoordinate(coordinate.x, coordinate.y, coordinate.z - 1);
                Chunk right = ChunkUtility.GetChunkByCoordinate(coordinate.x + 1, coordinate.y, coordinate.z);
                Chunk left = ChunkUtility.GetChunkByCoordinate(coordinate.x - 1, coordinate.y, coordinate.z);

                if (front != null && blockPosition.z == CHUNK_SIZE - 1) front.Generate();
                if (back != null && blockPosition.z == 0) back.Generate();
                if (right != null && blockPosition.x == CHUNK_SIZE - 1) right.Generate();
                if (left != null && blockPosition.x == 0) left.Generate();
            }
        }
    }

    public void Generate()
    {
        RequestMeshData();
        isGenerated = true;
    }

    public void Prepare()
    {
        gameObject = GameObject.Instantiate(TerrainGenerator.instance.chunkPrefab);
        gameObject.name = $"Chunk_{coordinate.x}_{coordinate.y}_{coordinate.z}";
        meshFilter = gameObject.GetComponent<MeshFilter>();
        meshRenderer = gameObject.GetComponent<MeshRenderer>();

        meshRenderer.material = AssetsContainer.instance.blockMaterial;
        gameObject.transform.position = new Vector3(coordinate.x * CHUNK_SIZE, coordinate.y * CHUNK_HEIGHT, coordinate.z * CHUNK_SIZE);
        position = gameObject.transform.position;

        PrepareCubes();
    }
    private void PrepareCubes()
    {
        blocks = new byte[CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE];
        NativeArray<float> heightMap = new NativeArray<float>(CHUNK_SIZE * CHUNK_SIZE, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

        try // Max sw time: 2 ms
        {
            TerrainGenerator.instance.GetNoiseLayers(out var continentLayer, out var mountainLayer, out var detailLayer, out var ridgeLayer);

            GenerateTerrainHeightMapJob heightJob = new GenerateTerrainHeightMapJob
            {
                HeightMap = heightMap,
                ChunkSize = CHUNK_SIZE,
                ChunkOrigin = new float2(coordinate.x * CHUNK_SIZE, coordinate.z * CHUNK_SIZE),
                ContinentLayer = continentLayer,
                MountainLayer = mountainLayer,
                DetailLayer = detailLayer,
                RidgeLayer = ridgeLayer,
                FlatlandsHeightMultiplier = TerrainGenerator.instance.flatlandsHeightMultiplier,
                MountainHeightMultiplier = TerrainGenerator.instance.mountainHeightMultiplier,
                MountainBlendStart = TerrainGenerator.instance.mountainBlendStart,
                MountainBlendSharpness = TerrainGenerator.instance.mountainBlendSharpness
            };

            JobHandle heightHandle = heightJob.Schedule(heightMap.Length, 64);
            heightHandle.Complete();



            for (int x = 0; x < CHUNK_SIZE; x++)
            {
                for (int z = 0; z < CHUNK_SIZE; z++)
                {
                    int index = z * CHUNK_SIZE + x;
                    float normalizedHeight = math.clamp(heightMap[index], 0f, 1f);
                    int groundLevel = (int)math.floor(normalizedHeight * TerrainGenerator.instance.noiseHeight) + TerrainGenerator.instance.groundOffset;

                    for (int y = 0; y < CHUNK_HEIGHT; y++)
                    {
                        if (blocks[x, y, z] != BLOCK_AIR)
                        {
                            continue;
                        }

                        int worldY = coordinate.y * CHUNK_HEIGHT + y;

                        if (worldY > groundLevel)
                        {
                            blocks[x, y, z] = BLOCK_AIR;
                        }
                        else
                        {
                            if (worldY == groundLevel)
                            {
                                blocks[x, y, z] = BLOCK_GRASS;

                                if (TerrainGenerator.instance.addTrees)
                                {
                                    if (x > 3 && z > 3 && x < CHUNK_SIZE - 3 && z < CHUNK_SIZE - 3)
                                    {
                                        if (UnityEngine.Random.Range(0, 50) == 0)
                                        {
                                            AddTree(x, y + 1, z);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (worldY > groundLevel - 5)
                                {
                                    blocks[x, y, z] = BLOCK_DIRT;
                                }
                                else
                                {
                                    blocks[x, y, z] = BLOCK_STONE;
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (heightMap.IsCreated)
            {
                heightMap.Dispose();
            }
        }
    }

    private void AddTree(int x, int y, int z)
    {
        int height = UnityEngine.Random.Range(4, 7);

        for (int i = 0; i < height; i++)
        {
            if (ChunkUtility.IsInsideChunk(new Vector3Int(x, y + i, z)))
                blocks[x, y + i, z] = BLOCK_WOOD;
        }

        int treeHeadRadius = UnityEngine.Random.Range(4, 6);

        for (int relativeX = -treeHeadRadius; relativeX < treeHeadRadius + 1; relativeX++)
        {
            for (int relativeY = 0; relativeY < treeHeadRadius + 1; relativeY++)
            {
                for (int relativeZ = -treeHeadRadius; relativeZ < treeHeadRadius + 1; relativeZ++)
                {
                    Vector3 center = new Vector3(x, y + height + treeHeadRadius / 8.0f, z);
                    Vector3Int blockPos = new Vector3Int(x + relativeX, y + relativeY + height, z + relativeZ);

                    if ((blockPos - center).magnitude < treeHeadRadius)
                    {
                        if (ChunkUtility.IsInsideChunk(blockPos))
                            blocks[blockPos.x, blockPos.y, blockPos.z] = BLOCK_LEAVES;
                    }
                }
            }
        }
    }

    private void RequestMeshData()
    {
        Stopwatch sw = Stopwatch.StartNew();
        ChunkMeshGenerator.RequestMeshData(BuildHaloBlockArray(), OnMeshDataReceived);
        UnityEngine.Debug.Log(sw.ElapsedMilliseconds + " ms to prepare chunk at " + coordinate);
    }

    public byte[,,] BuildHaloBlockArray()
    {
        const int SX = CHUNK_SIZE, SY = CHUNK_HEIGHT, SZ = CHUNK_SIZE;

        int originX = coordinate.x * SX;
        int originY = coordinate.y * SY;
        int originZ = coordinate.z * SZ;

        var halo = new byte[SX + 2, SY + 2, SZ + 2];

        for (int x = 0; x < SX + 2; x++)
        {
            int wx = originX + x - 1;
            for (int z = 0; z < SZ + 2; z++)
            {
                int wz = originZ + z - 1;
                for (int y = 0; y < SY + 2; y++)
                {
                    int wy = originY + y - 1;
                    halo[x, y, z] = (byte)ChunkUtility.GetBlockAtPosition(new Vector3(wx, wy, wz));
                }
            }
        }

        return halo;
    }

    private void OnMeshDataReceived([ReadOnly] ChunkMeshData meshData)
    {
        if (meshFilter == null) return;

        Mesh mesh = new Mesh();
        mesh.vertices = meshData.vertices;
        mesh.normals = meshData.normals;
        mesh.triangles = meshData.triangles;
        mesh.uv = meshData.uvs;
        meshFilter.mesh = mesh;

        if (meshCollider != null)
            meshCollider.sharedMesh = mesh;
    }

    public void SetActive(bool enabled)
    {
        gameObject.SetActive(enabled);
    }

    public static readonly Vector3[] cubeVertices = new Vector3[8] {
        new Vector3(0.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 1.0f, 1.0f),
        new Vector3(0.0f, 1.0f, 1.0f),
    };

    public static readonly Vector3Int[] cubeNormals = new Vector3Int[6] {
        new Vector3Int(0, 0, -1),
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 1, 0),
        new Vector3Int(0, -1, 0),
        new Vector3Int(-1, 0, 0),
        new Vector3Int(1, 0, 0)
    };

    public static readonly int[,] cubeTriangles = new int[6, 4] {
        // Back, Front, Top, Bottom, Left, Right

		// 0 1 2 2 1 3
		{0, 3, 1, 2}, // Back Face
		{5, 6, 4, 7}, // Front Face
		{3, 7, 2, 6}, // Top Face
		{1, 5, 0, 4}, // Bottom Face
		{4, 7, 0, 3}, // Left Face
		{1, 2, 5, 6} // Right Face
	};

    public static readonly Vector2[] cubeUVs = new Vector2[4] {
        new Vector2 (0.0f, 0.0f),
        new Vector2 (0.0f, 1.0f),
        new Vector2 (1.0f, 0.0f),
        new Vector2 (1.0f, 1.0f)
    };
}