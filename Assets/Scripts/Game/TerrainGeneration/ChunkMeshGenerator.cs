using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using UnityEngine;

namespace BenScr.MCC
{
    using static AssetsContainer;

    public class ChunkMeshGenerator
    {
        public static readonly ConcurrentQueue<ThreadInfo<ChunkMeshData>> meshDataThreadInfoQueue = new ConcurrentQueue<ThreadInfo<ChunkMeshData>>();

        public static void Update()
        {
            while (meshDataThreadInfoQueue.TryDequeue(out var threadInfo))
            {
                threadInfo.calback(threadInfo.parameter);
            }
        }

        public static void RequestMeshData(byte[,,] haloBlocks, Action<ChunkMeshData> callback)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                MeshDataThread(haloBlocks, callback);
            });
        }

        private static void MeshDataThread([ReadOnly] in byte[,,] haloBlocks, Action<ChunkMeshData> callback)
        {
            ChunkMeshData meshData = GenerateMeshData(haloBlocks);
            meshDataThreadInfoQueue.Enqueue(new ThreadInfo<ChunkMeshData>(callback, meshData));
        }


        public static ChunkMeshData GenerateMeshData([ReadOnly] in byte[,,] haloBlocks)
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
                        Block block = GetBlock(blockId);

                        if (blockId != Chunk.BLOCK_AIR)
                        {
                            Vector3Int position = new Vector3Int(x, y, z);

                            for (int face = 0; face < 6; face++)
                            {
                                int neighborBlockId = GetHalo(haloBlocks, position + cubeNormals[face]);
                                Block neighbourBlock = GetBlock(neighborBlockId);

                                bool neighbourIsTransparent = neighbourBlock.isTransparent;
                                bool hidesFaceBecauseSameFluid = block.isFluid && neighbourBlock.id == block.id;

                                if (neighbourIsTransparent && !hidesFaceBecauseSameFluid)
                                {
                                    vertices.Add(position + cubeVertices[cubeTriangles[face, 0]]);
                                    vertices.Add(position + cubeVertices[cubeTriangles[face, 1]]);
                                    vertices.Add(position + cubeVertices[cubeTriangles[face, 2]]);
                                    vertices.Add(position + cubeVertices[cubeTriangles[face, 3]]);

                                    for (int i = 0; i < 4; i++)
                                    {
                                        normals.Add(cubeNormals[face]);
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

        private static void AddTexture(int textureId, ref List<Vector2> uvs)
        {
            int col = textureId % TEXTURE_BLOCKS_COLS;
            int rowFromTop = TEXTURE_BLOCKS_ROWS - 1 - (textureId / TEXTURE_BLOCKS_COLS);

            float u = col * BLOCK_W;
            float v = rowFromTop * BLOCK_H;


            float epsU = 0.5f / TEXTURE_WIDTH;
            float epsV = 0.5f / TEXTURE_HEIGHT;

            float u0 = u + epsU;
            float v0 = v + epsV;
            float u1 = u + BLOCK_W - epsU;
            float v1 = v + BLOCK_H - epsV;

            uvs.Add(new Vector2(u0, v0)); // bottom-left
            uvs.Add(new Vector2(u0, v1)); // top-left
            uvs.Add(new Vector2(u1, v0)); // bottom-right
            uvs.Add(new Vector2(u1, v1)); // top-right
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

    public struct ThreadInfo<T>
    {
        public readonly Action<T> calback;
        public readonly T parameter;

        public ThreadInfo(Action<T> callback, T parameter)
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
}