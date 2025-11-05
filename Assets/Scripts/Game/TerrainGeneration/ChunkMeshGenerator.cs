using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using UnityEngine;
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
                            int neighborBlockId = GetHalo(haloBlocks, position + Chunk.cubeNormals[face]);
                            Block neighbourBlock = GetBlock(neighborBlockId);

                            if (neighbourBlock.isTransparent)
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
