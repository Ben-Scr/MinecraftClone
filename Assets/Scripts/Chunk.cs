using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Chunk
{
    // Size = width and depth
    public const int CHUNK_SIZE = 16;
    public const int CHUNK_HEIGHT = 64;

    public const int TEXTURE_BLOCKS_COUNT = 4;
    public const float TEXTURE_BLOCK_SIZE = 0.25f;

    // Block types
    public const int BLOCK_AIR = 0;
    public const int BLOCK_DIRT = 1;
    public const int BLOCK_GRASS = 2;
    public const int BLOCK_STONE = 3;
    public const int BLOCK_WOOD = 4;
    public const int BLOCK_LEAVES = 5;

    public int[,,] blocks;

    public WorldGenerator world;
    public GameObject gameObject;

    public MeshRenderer meshRenderer;
    public MeshFilter meshFilter;
    public MeshCollider meshCollider;

    public MeshData meshData = new MeshData();

    public Vector3Int coordinate;
    public Vector3 position;

    private int vertexIndex = 0;

    public Chunk(int x, int z, WorldGenerator world)
    {
        this.coordinate = new Vector3Int(x, 0, z);
        this.world = world;
    }

    private void GenerateCubes()
    {
        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                for (int y = 0; y < CHUNK_HEIGHT; y++)
                {
                    int blockType = blocks[x, y, z];

                    if (blockType != BLOCK_AIR)
                    {
                        Vector3Int relativePosition = new Vector3Int(x, y, z);
                        GenerateCube(relativePosition);
                    }
                }
            }
        }

        Mesh mesh = new Mesh();
        mesh.vertices = meshData.vertices.ToArray();
        mesh.normals = meshData.normals.ToArray();
        mesh.triangles = meshData.triangles.ToArray();
        mesh.uv = meshData.uvs.ToArray();

        meshFilter.mesh = mesh;
        meshCollider.sharedMesh = mesh;
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
            blocks[blockPosition.x, blockPosition.y, blockPosition.z] = id;

            if (update)
            {
                Update();

                // Update neighbors
                Chunk front = world.GetChunkByCoordinate(coordinate.x, coordinate.z + 1);
                Chunk back = world.GetChunkByCoordinate(coordinate.x, coordinate.z - 1);
                Chunk right = world.GetChunkByCoordinate(coordinate.x + 1, coordinate.z);
                Chunk left = world.GetChunkByCoordinate(coordinate.x - 1, coordinate.z);

                if (front != null && blockPosition.z == CHUNK_SIZE - 1) front.Update();
                if (back != null && blockPosition.z == 0) back.Update();
                if (right != null && blockPosition.x == CHUNK_SIZE - 1) right.Update();
                if (left != null && blockPosition.x == 0) left.Update();
            }
        }
        else
        {
            Debug.LogWarning("Position is outside of chunk: " + blockPosition);
        }
    }

    public void Update()
    {
        meshData = new MeshData();
        vertexIndex = 0;

        GenerateCubes();
    }

    public void Prepare()
    {
        gameObject = new GameObject("Chunk " + coordinate.x + " " + coordinate.z);
        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshCollider = gameObject.AddComponent<MeshCollider>();

        meshRenderer.material = world.blockMaterial;

        gameObject.transform.SetParent(world.transform);
        gameObject.transform.position = new Vector3(coordinate.x * CHUNK_SIZE, 0, coordinate.z * CHUNK_SIZE);

        position = gameObject.transform.position;

        PrepareCubes();
    }

    private void PrepareCubes()
    {
        blocks = new int[CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE];
        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                int groundLevel = Mathf.FloorToInt(
                    Mathf.PerlinNoise((position.x + x + world.noiseOffset) / world.noiseScale, (position.z + z + world.noiseOffset) / world.noiseScale) * world.noiseHeight
                    ) + world.groundOffset;
                for (int y = 0; y < CHUNK_HEIGHT; y++)
                {
                    if (blocks[x, y, z] != BLOCK_AIR) continue;

                    if (y > groundLevel)
                    {
                        blocks[x, y, z] = BLOCK_AIR;
                    }
                    else
                    {
                        if (y == groundLevel)
                        {
                            blocks[x, y, z] = BLOCK_GRASS;

                            if (world.addTrees)
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
                            if (y > groundLevel - 5)
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

    private void AddTree(int x, int y, int z)
    {
        int height = UnityEngine.Random.Range(4, 7);

        for (int i = 0; i < height; i++)
        {
            SetBlock(new Vector3(x, y + i, z), BLOCK_WOOD, false);
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
                        SetBlock(new Vector3(blockPos.x, blockPos.y, blockPos.z), BLOCK_LEAVES, false);
                    }
                }
            }
        }
    }

    public void GenerateCube(Vector3Int position)
    {
        for (int face = 0; face < 6; face++)
        {
            int neighborBlock = GetBlockAtPosition(position + cubeNormals[face]);

            if (neighborBlock == BLOCK_AIR)
            {
                meshData.vertices.Add(position + cubeVertices[cubeTriangles[face, 0]]);
                meshData.vertices.Add(position + cubeVertices[cubeTriangles[face, 1]]);
                meshData.vertices.Add(position + cubeVertices[cubeTriangles[face, 2]]);
                meshData.vertices.Add(position + cubeVertices[cubeTriangles[face, 3]]);

                for (int i = 0; i < 4; i++)
                {
                    meshData.normals.Add(cubeNormals[face]);
                }

                int blockId = blocks[position.x, position.y, position.z];
                BlockType blockType = world.blockTypes[blockId];
                AddTexture(blockType.GetTexture(face));

                meshData.triangles.Add(vertexIndex);
                meshData.triangles.Add(vertexIndex + 1);
                meshData.triangles.Add(vertexIndex + 2);
                meshData.triangles.Add(vertexIndex + 2);
                meshData.triangles.Add(vertexIndex + 1);
                meshData.triangles.Add(vertexIndex + 3);

                vertexIndex += 4;
            }
        }
    }

    private void AddTexture(int textureId)
    {
        float y = textureId / TEXTURE_BLOCKS_COUNT;
        float x = textureId - (y * TEXTURE_BLOCKS_COUNT);

        x *= TEXTURE_BLOCK_SIZE;
        y *= TEXTURE_BLOCK_SIZE;

        y = 1f - y - TEXTURE_BLOCK_SIZE;

        meshData.uvs.Add(new Vector2(x, y));
        meshData.uvs.Add(new Vector2(x, y + TEXTURE_BLOCK_SIZE));
        meshData.uvs.Add(new Vector2(x + TEXTURE_BLOCK_SIZE, y));
        meshData.uvs.Add(new Vector2(x + TEXTURE_BLOCK_SIZE, y + TEXTURE_BLOCK_SIZE));
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

    public int GetBlockAtPosition(Vector3 position)
    {
        int x = Mathf.FloorToInt(position.x);
        int y = Mathf.FloorToInt(position.y);
        int z = Mathf.FloorToInt(position.z);

        if (y < 0 || y > CHUNK_HEIGHT - 1)
        {
            return BLOCK_AIR;
        }

        if (x < 0 || z < 0 || x > CHUNK_SIZE - 1 || z > CHUNK_SIZE - 1)
        {
            return world.GetBlockAtPosition(position + this.position);
        }

        return blocks[x, y, z];
    }

    public static Vector3[] cubeVertices = new Vector3[8] {
        new Vector3(0.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 1.0f, 0.0f),
        new Vector3(0.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 0.0f, 1.0f),
        new Vector3(1.0f, 1.0f, 1.0f),
        new Vector3(0.0f, 1.0f, 1.0f),
    };

    public static Vector3[] cubeNormals = new Vector3[6] {
        new Vector3(0.0f, 0.0f, -1.0f),
        new Vector3(0.0f, 0.0f, 1.0f),
        new Vector3(0.0f, 1.0f, 0.0f),
        new Vector3(0.0f, -1.0f, 0.0f),
        new Vector3(-1.0f, 0.0f, 0.0f),
        new Vector3(1.0f, 0.0f, 0.0f)
    };

    public static int[,] cubeTriangles = new int[6, 4] {
        // Back, Front, Top, Bottom, Left, Right

		// 0 1 2 2 1 3
		{0, 3, 1, 2}, // Back Face
		{5, 6, 4, 7}, // Front Face
		{3, 7, 2, 6}, // Top Face
		{1, 5, 0, 4}, // Bottom Face
		{4, 7, 0, 3}, // Left Face
		{1, 2, 5, 6} // Right Face
	};

    public static Vector2[] cubeUVs = new Vector2[4] {
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