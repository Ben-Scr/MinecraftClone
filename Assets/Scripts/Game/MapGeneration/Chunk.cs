using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Tilemaps;

public class Chunk
{
    public const int CHUNK_SIZE = 32;
    public const int CHUNK_HEIGHT = 32;

    public static int TEXTURE_BLOCKS_ROWS;
    public static int TEXTURE_BLOCKS_COLS;
    public static float BLOCK_W;
    public static float BLOCK_H;
    public static float TEXTURE_WIDTH;
    public static float TEXTURE_HEIGHT;

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
        meshCollider = gameObject.AddComponent<MeshCollider>();
        meshCollider.sharedMesh = meshFilter.mesh;
    }

    public void SetBlock(Vector3 position, int id, bool update = true)
    {
        Vector3Int blockPosition = new Vector3Int(
                    Mathf.FloorToInt(position.x),
                    Mathf.FloorToInt(position.y),
                    Mathf.FloorToInt(position.z)
            );

        if (IsInsideChunk(blockPosition.x, blockPosition.y, blockPosition.z))
        {
            blocks[blockPosition.x, blockPosition.y, blockPosition.z] = (byte)id;

            if (update)
            {
                Generate();

                Chunk front = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x, coordinate.y,coordinate.z + 1);
                Chunk back = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x, coordinate.y, coordinate.z - 1);
                Chunk right = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x + 1, coordinate.y, coordinate.z);
                Chunk left = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x - 1, coordinate.y, coordinate.z);

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
        gameObject = GameObject.Instantiate(WorldGenerator.instance.chunkPrefab);
        gameObject.name = $"Chunk_{coordinate.x}_{coordinate.y}_{coordinate.z}";
        meshFilter = gameObject.GetComponent<MeshFilter>();
        meshRenderer = gameObject.GetComponent<MeshRenderer>();

        meshRenderer.material = WorldGenerator.instance.blockMaterial;
        gameObject.transform.position = new Vector3(coordinate.x * CHUNK_SIZE, coordinate.y * CHUNK_HEIGHT, coordinate.z * CHUNK_SIZE);
        position = gameObject.transform.position;

        PrepareCubes();
    }
    private void PrepareCubes()
    {
        blocks = new byte[CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE];

        var world = WorldGenerator.instance;
        byte[] map = Noise.GenerateBlocksParallel(position, world.noiseHeight, world.noiseScale, world.noiseOffset, world.groundOffset);

        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                for (int y = 0; y < CHUNK_HEIGHT; y++)
                {
                    int flat = ((x * CHUNK_SIZE) + z) * CHUNK_HEIGHT + y;
                    byte block = map[flat];
                    blocks[x, y, z] = block;
                }
            }
        }

        if (world.addTrees)
        {
            for (int x = 4; x < CHUNK_SIZE - 4; x++)
                for (int z = 4; z < CHUNK_SIZE - 4; z++)
                {
                    for (int y = CHUNK_HEIGHT - 7; y >= 1; y--)
                    {
                        if (blocks[x, y, z] == BLOCK_GRASS && blocks[x, y + 1, z] == BLOCK_AIR)
                        {
                            if (UnityEngine.Random.Range(0, 50) == 0)
                                AddTree(x, y + 1, z);
                            break;
                        }
                    }
                }
        }
    }

    private void AddTree(int x, int y, int z)
    {
        int height = UnityEngine.Random.Range(4, 7);

        for (int i = 0; i < height; i++)
        {
            if(IsInsideChunk(x, y + i, z))
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
                        if (IsInsideChunk(blockPos.x, blockPos.y, blockPos.z))
                            blocks[blockPos.x, blockPos.y, blockPos.z] = BLOCK_LEAVES;
                    }
                }
            }
        }
    }

    private void RequestMeshData()
    {
        WorldGenerator.instance.RequestMeshData(BuildHaloBlockArray(), OnMeshDataReceived);
    }

    private void OnMeshDataReceived(ChunkMeshData meshData)
    {
        Mesh mesh = new Mesh();
        mesh.vertices = meshData.vertices;
        mesh.normals = meshData.normals;
        mesh.triangles = meshData.triangles;
        mesh.uv = meshData.uvs;

        meshFilter.mesh = mesh;

        if (meshCollider != null)
            meshCollider.sharedMesh = mesh;

        if (coordinate.y != 0)
            gameObject.name = $"Chunk_{coordinate.x}_{coordinate.y}_{coordinate.z} Build Mesh";
    }

    public byte[,,] BuildHaloBlockArray()
    {
        int SX = CHUNK_SIZE, SY = CHUNK_HEIGHT, SZ = CHUNK_SIZE;

        int originX = coordinate.x * SX;
        int originY = coordinate.y * SY;
        int originZ = coordinate.z * SZ;

        var halo = new byte[SX + 2, SY + 2, SZ + 2];

        for (int x = -1; x <= SX; x++)
            for (int y = -1; y <= SY; y++)
                for (int z = -1; z <= SZ; z++)
                {
                    var wx = originX + x;
                    var wy = originY + y;
                    var wz = originZ + z;
                    byte id = (byte)WorldGenerator.instance.GetBlockAtPosition(new Vector3(wx, wy, wz));
                    halo[x + 1, y + 1, z + 1] = id;
                }

        return halo;
    }

    bool IsInsideChunk(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 ||
            x > CHUNK_SIZE - 1 || y > CHUNK_HEIGHT - 1 || z > CHUNK_SIZE - 1)
        {
            return false;
        }

        return true;
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

public class MeshData
{
    public List<Vector3> vertices = new List<Vector3>();
    public List<Vector3> normals = new List<Vector3>();
    public List<int> triangles = new List<int>();
    public List<Vector2> uvs = new List<Vector2>();

    public MeshData()
    {
    }

    public MeshData(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, List<Vector2> uvs)
    {
        this.vertices = vertices;
        this.normals = normals;
        this.triangles = triangles;
        this.uvs = uvs;
    }
}