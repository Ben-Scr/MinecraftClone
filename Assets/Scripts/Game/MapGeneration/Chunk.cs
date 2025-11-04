using System.Collections.Generic;
using UnityEngine;

public class Chunk
{
    public const int CHUNK_SIZE = 16;
    public const int CHUNK_HEIGHT = 64;

    public static int TEXTURE_BLOCKS_ROWS;
    public static int TEXTURE_BLOCKS_COLS;
    public static float BLOCK_W;
    public static float BLOCK_H;

    // Block types
    public const int BLOCK_AIR = 0;
    public const int BLOCK_DIRT = 1;
    public const int BLOCK_GRASS = 2;
    public const int BLOCK_STONE = 3;
    public const int BLOCK_WOOD = 4;
    public const int BLOCK_LEAVES = 5;

    public byte[,,] blocks;

    public GameObject gameObject;
    public MeshRenderer meshRenderer;
    public MeshFilter meshFilter;
    public MeshCollider meshCollider;
    public MeshData meshData = new MeshData();

    public Vector3Int coordinate;
    public Vector3 position;

    private int vertexIndex = 0;

    public Chunk(int x, int z)
    {
        coordinate = new Vector3Int(x, 0, z); // ERROR: CUBIC
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

        if (meshCollider != null)
            meshCollider.sharedMesh = mesh;
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
                Update();

                // Update neighbors
                Chunk front = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x, coordinate.z + 1);
                Chunk back = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x, coordinate.z - 1);
                Chunk right = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x + 1, coordinate.z);
                Chunk left = WorldGenerator.instance.GetChunkByCoordinate(coordinate.x - 1, coordinate.z);

                if (front != null && blockPosition.z == CHUNK_SIZE - 1) front.Update();
                if (back != null && blockPosition.z == 0) back.Update();
                if (right != null && blockPosition.x == CHUNK_SIZE - 1) right.Update();
                if (left != null && blockPosition.x == 0) left.Update();
            }
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

        meshRenderer.material = WorldGenerator.instance.blockMaterial;
        gameObject.transform.position = new Vector3(coordinate.x * CHUNK_SIZE, 0, coordinate.z * CHUNK_SIZE);
        position = gameObject.transform.position;

        PrepareCubes();
    }

    private void PrepareCubes()
    {
        blocks = new byte[CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE];

        var world = WorldGenerator.instance;
        byte[] map = Noise.GenerateMap(position, world.noiseHeight, world.noiseScale, world.noiseOffset, world.groundOffset);
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
                    // Oberfläche finden: höchstes y mit GRASS und darüber AIR
                    for (int y = CHUNK_HEIGHT - 2; y >= 1; y--)
                    {
                        if (blocks[x, y, z] == BLOCK_GRASS && blocks[x, y + 1, z] == BLOCK_AIR)
                        {
                            if (UnityEngine.Random.Range(0, 50) == 0)
                                AddTree(x, y + 1, z);   // jetzt wird nichts mehr überschrieben
                            break; // für diese Säule fertig
                        }
                    }
                }
        }

        return;
        byte[,] groundLevelMap = null;
        Noise.GenerateGroundLevelMap(ref groundLevelMap, position, world.noiseHeight, world.noiseScale, world.noiseOffset, world.groundOffset);
        for (int x = 0; x < CHUNK_SIZE; x++)
        {
            for (int z = 0; z < CHUNK_SIZE; z++)
            {
                byte groundLevel = groundLevelMap[x, z];

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
                            if (x == 0 || z == 0 || x == CHUNK_SIZE - 1 || z == CHUNK_SIZE - 1)
                            {
                                blocks[x, y, z] = BLOCK_STONE;
                                blocks[x, y + 1, z] = BLOCK_STONE;
                                blocks[x, y + 2, z] = BLOCK_STONE;
                                blocks[x, y + 3, z] = BLOCK_STONE;
                                blocks[x, y + 4, z] = BLOCK_STONE;
                            }
                            else
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

            if (neighborBlock == BLOCK_AIR || neighborBlock == BLOCK_LEAVES)
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
                Block blockType = WorldGenerator.instance.blockTypes[blockId];
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
        // Spalte weiter 0..COLS-1, links -> rechts
        int col = textureId % TEXTURE_BLOCKS_COLS;

        // Reihe jetzt von OBEN gezählt: 0 = oberste Reihe
        int rowFromTop = TEXTURE_BLOCKS_ROWS - 1 - (textureId / TEXTURE_BLOCKS_COLS);

        float u = col * BLOCK_W;
        float v = rowFromTop * BLOCK_H;

        // Halber Texel als Epsilon (gegen Mipmap-Bleeding)
        // Nimm am besten dieselbe Textur wie im Material für die Maße
        float epsU = 0.5f / meshRenderer.material.mainTexture.width;
        float epsV = 0.5f / meshRenderer.material.mainTexture.height;

        float u0 = u + epsU;
        float v0 = v + epsV;
        float u1 = u + BLOCK_W - epsU;
        float v1 = v + BLOCK_H - epsV;

        // Reihenfolge MUSS zur Vertex-/Indexreihenfolge deiner Meshdaten passen!
        // Hier: BL, TL, BR, TR
        meshData.uvs.Add(new Vector2(u0, v0)); // bottom-left
        meshData.uvs.Add(new Vector2(u0, v1)); // top-left
        meshData.uvs.Add(new Vector2(u1, v0)); // bottom-right
        meshData.uvs.Add(new Vector2(u1, v1)); // top-right
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
            return WorldGenerator.instance.GetBlockAtPosition(position + this.position);
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