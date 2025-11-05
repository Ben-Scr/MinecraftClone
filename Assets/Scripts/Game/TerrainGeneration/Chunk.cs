using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BenScr.MCC
{
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

        public void SetBlock(Vector3 position, int blockId, bool update = true)
        {
            Vector3Int blockPosition = new Vector3Int(
                        Mathf.FloorToInt(position.x),
                        Mathf.FloorToInt(position.y),
                        Mathf.FloorToInt(position.z)
                );

            if (ChunkUtility.IsInsideChunk(blockPosition))
            {
                blocks[blockPosition.x, blockPosition.y, blockPosition.z] = (byte)blockId;

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

        public void Prepare() // average sw time: 0 ms (max 1ms)
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
        private void PrepareCubes() // average sw time: 0 ms (max 1ms)
        {
            blocks = new byte[CHUNK_SIZE, CHUNK_HEIGHT, CHUNK_SIZE];
            NativeArray<float> heightMap = new NativeArray<float>(CHUNK_SIZE * CHUNK_SIZE, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            try
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

        private void RequestMeshData() // average sw time: 0 ms
        {
            ChunkMeshGenerator.RequestMeshData(BuildHaloBlockArray(), OnMeshDataReceived);
        }

        public byte[,,] BuildHaloBlockArray() // average sw time: 0ms (Code optimized by ChatGPT, from 3ms delay to 0ms)
        {
            const int SX = CHUNK_SIZE, SY = CHUNK_HEIGHT, SZ = CHUNK_SIZE;

            int originX = coordinate.x * SX;
            int originY = coordinate.y * SY;
            int originZ = coordinate.z * SZ;

            var halo = new byte[SX + 2, SY + 2, SZ + 2];

            // Copy the inner chunk blocks directly.
            for (int x = 0; x < SX; x++)
            {
                for (int y = 0; y < SY; y++)
                {
                    for (int z = 0; z < SZ; z++)
                    {
                        halo[x + 1, y + 1, z + 1] = blocks[x, y, z];
                    }
                }
            }

            // Fetch neighbouring chunks for the six faces.
            Chunk negX = ChunkUtility.GetChunkByCoordinate(coordinate.x - 1, coordinate.y, coordinate.z);
            Chunk posX = ChunkUtility.GetChunkByCoordinate(coordinate.x + 1, coordinate.y, coordinate.z);
            Chunk negY = ChunkUtility.GetChunkByCoordinate(coordinate.x, coordinate.y - 1, coordinate.z);
            Chunk posY = ChunkUtility.GetChunkByCoordinate(coordinate.x, coordinate.y + 1, coordinate.z);
            Chunk negZ = ChunkUtility.GetChunkByCoordinate(coordinate.x, coordinate.y, coordinate.z - 1);
            Chunk posZ = ChunkUtility.GetChunkByCoordinate(coordinate.x, coordinate.y, coordinate.z + 1);

            // West face (x = -1 relative to chunk).
            for (int y = 0; y < SY; y++)
            {
                int worldY = originY + y;
                for (int z = 0; z < SZ; z++)
                {
                    int worldZ = originZ + z;
                    if (negX != null)
                    {
                        halo[0, y + 1, z + 1] = negX.blocks[SX - 1, y, z];
                    }
                    else
                    {
                        halo[0, y + 1, z + 1] = (byte)ChunkUtility.GetBlockAtBlock(new Vector3Int(originX - 1, worldY, worldZ));
                    }
                }
            }

            // East face (x = +1 relative to chunk).
            for (int y = 0; y < SY; y++)
            {
                int worldY = originY + y;
                for (int z = 0; z < SZ; z++)
                {
                    int worldZ = originZ + z;
                    if (posX != null)
                    {
                        halo[SX + 1, y + 1, z + 1] = posX.blocks[0, y, z];
                    }
                    else
                    {
                        halo[SX + 1, y + 1, z + 1] = (byte)ChunkUtility.GetBlockAtBlock(new Vector3Int(originX + SX, worldY, worldZ));
                    }
                }
            }

            // Bottom face (y = -1 relative to chunk).
            for (int x = 0; x < SX; x++)
            {
                int worldX = originX + x;
                for (int z = 0; z < SZ; z++)
                {
                    int worldZ = originZ + z;
                    if (negY != null)
                    {
                        halo[x + 1, 0, z + 1] = negY.blocks[x, SY - 1, z];
                    }
                    else
                    {
                        halo[x + 1, 0, z + 1] = (byte)ChunkUtility.GetBlockAtBlock(new Vector3Int(worldX, originY - 1, worldZ));
                    }
                }
            }

            // Top face (y = +1 relative to chunk).
            for (int x = 0; x < SX; x++)
            {
                int worldX = originX + x;
                for (int z = 0; z < SZ; z++)
                {
                    int worldZ = originZ + z;
                    if (posY != null)
                    {
                        halo[x + 1, SY + 1, z + 1] = posY.blocks[x, 0, z];
                    }
                    else
                    {
                        halo[x + 1, SY + 1, z + 1] = (byte)ChunkUtility.GetBlockAtBlock(new Vector3Int(worldX, originY + SY, worldZ));
                    }
                }
            }

            // South face (z = -1 relative to chunk).
            for (int x = 0; x < SX; x++)
            {
                int worldX = originX + x;
                for (int y = 0; y < SY; y++)
                {
                    int worldY = originY + y;
                    if (negZ != null)
                    {
                        halo[x + 1, y + 1, 0] = negZ.blocks[x, y, SZ - 1];
                    }
                    else
                    {
                        halo[x + 1, y + 1, 0] = (byte)ChunkUtility.GetBlockAtBlock(new Vector3Int(worldX, worldY, originZ - 1));
                    }
                }
            }

            // North face (z = +1 relative to chunk).
            for (int x = 0; x < SX; x++)
            {
                int worldX = originX + x;
                for (int y = 0; y < SY; y++)
                {
                    int worldY = originY + y;
                    if (posZ != null)
                    {
                        halo[x + 1, y + 1, SZ + 1] = posZ.blocks[x, y, 0];
                    }
                    else
                    {
                        halo[x + 1, y + 1, SZ + 1] = (byte)ChunkUtility.GetBlockAtBlock(new Vector3Int(worldX, worldY, originZ + SZ));
                    }
                }
            }

            int maxX = SX + 1;
            int maxY = SY + 1;
            int maxZ = SZ + 1;

            // Fill remaining edges and corners using the utility method.
            for (int x = 0; x <= maxX; x++)
            {
                int worldX = originX + x - 1;
                bool boundaryX = x == 0 || x == maxX;
                for (int y = 0; y <= maxY; y++)
                {
                    int worldY = originY + y - 1;
                    bool boundaryY = y == 0 || y == maxY;
                    for (int z = 0; z <= maxZ; z++)
                    {
                        bool boundaryZ = z == 0 || z == maxZ;
                        int boundaryCount = (boundaryX ? 1 : 0) + (boundaryY ? 1 : 0) + (boundaryZ ? 1 : 0);

                        if (boundaryCount >= 2)
                        {
                            int worldZ = originZ + z - 1;
                            halo[x, y, z] = (byte)ChunkUtility.GetBlockAtBlock(new Vector3Int(worldX, worldY, worldZ));
                        }
                    }
                }
            }

            return halo;
        }
        private void OnMeshDataReceived([ReadOnly] ChunkMeshData meshData) // average sw time: 0 ms
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
    }
}