using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    using static AssetsContainer;

    public class ChunkMeshGenerator
    {
        private const float UV_EPSILON = 0.0001f;
        public static readonly ConcurrentQueue<ThreadInfo<MeshData>> meshDataThreadInfoQueue = new ConcurrentQueue<ThreadInfo<MeshData>>();

        public static void Update()
        {
            while (meshDataThreadInfoQueue.TryDequeue(out var threadInfo))
            {
                threadInfo.calback(threadInfo.parameter);
            }
        }

        public static void RequestMeshData(byte[,,] haloBlocks, Action<MeshData> callback)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                MeshDataThread(haloBlocks, callback);
            });
        }

        private static void MeshDataThread([ReadOnly] in byte[,,] haloBlocks, Action<MeshData> callback)
        {
            MeshData meshData = GenerateMeshData(haloBlocks);
            meshDataThreadInfoQueue.Enqueue(new ThreadInfo<MeshData>(callback, meshData));
        }


        public static MeshData GenerateMeshData([ReadOnly] in byte[,,] haloBlocks)
        {
            const int initialFaceCapacity = 2048;
            const int initialIndexCapacity = 3072;

            List<Vector3> solidVertices = new List<Vector3>(initialFaceCapacity);
            List<Vector3> solidNormals = new List<Vector3>(initialFaceCapacity);
            List<int> solidTriangles = new List<int>(initialIndexCapacity);
            List<Vector2> solidUvs = new List<Vector2>(initialFaceCapacity);

            List<Vector3> fluidVertices = new List<Vector3>(256);
            List<Vector3> fluidNormals = new List<Vector3>(256);
            List<int> fluidTriangles = new List<int>(384);
            List<Vector2> fluidUvs = new List<Vector2>(256);

            List<Vector3> transparentVertices = new List<Vector3>(512);
            List<Vector3> transparentNormals = new List<Vector3>(512);
            List<int> transparentTriangles = new List<int>(768);
            List<Vector2> transparentUvs = new List<Vector2>(512);

            BlockData[] blockDefinitions = AssetsContainer.instance.blocks;

            // Allocate mask/stamp once and reuse across all 6 face directions
            int maxMaskSize = Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;
            GreedyCell[] sharedMask = new GreedyCell[maxMaskSize];
            int[] sharedUsedStamp = new int[maxMaskSize];

            for (int face = 0; face < 6; face++)
            {
                BuildGreedyFacesForDirection(
                    haloBlocks,
                    blockDefinitions,
                    face,
                    sharedMask,
                    sharedUsedStamp,
                    solidVertices,
                    solidNormals,
                    solidTriangles,
                    solidUvs,
                    fluidVertices,
                    fluidNormals,
                    fluidTriangles,
                    fluidUvs,
                    transparentVertices,
                    transparentNormals,
                    transparentTriangles,
                    transparentUvs);
            }

            return new MeshData(
               new MeshSection(solidTriangles, solidVertices, solidNormals, solidUvs),
               new MeshSection(fluidTriangles, fluidVertices, fluidNormals, fluidUvs),
               new MeshSection(transparentTriangles, transparentVertices, transparentNormals, transparentUvs));
        }

        private static void BuildGreedyFacesForDirection(
                   byte[,,] haloBlocks,
                   BlockData[] blockDefinitions,
                   int face,
                   GreedyCell[] mask,
                   int[] usedStamp,
                   List<Vector3> solidVertices,
                   List<Vector3> solidNormals,
                   List<int> solidTriangles,
                   List<Vector2> solidUvs,
                   List<Vector3> fluidVertices,
                   List<Vector3> fluidNormals,
                   List<int> fluidTriangles,
                   List<Vector2> fluidUvs,
                   List<Vector3> transparentVertices,
                   List<Vector3> transparentNormals,
                   List<int> transparentTriangles,
                   List<Vector2> transparentUvs)
        {
            GetMaskSizeForFace(face, out int width, out int height, out int slices);
            // Reset stamp counter per face direction; arrays are reused
            Array.Clear(usedStamp, 0, width * height);
            int stamp = 1;

            for (int slice = 0; slice < slices; slice++)
            {
                Array.Clear(mask, 0, mask.Length);
                stamp++;

                if (stamp == int.MaxValue)
                {
                    Array.Clear(usedStamp, 0, usedStamp.Length);
                    stamp = 1;
                }

                for (int v = 0; v < height; v++)
                {
                    for (int u = 0; u < width; u++)
                    {
                        Vector3Int position = GetPositionForFace(face, u, v, slice);
                        int blockId = GetHalo(haloBlocks, position);

                        if (blockId == Chunk.BLOCK_AIR)
                        {
                            continue;
                        }

                        if ((uint)blockId >= (uint)blockDefinitions.Length)
                        {
                            continue;
                        }

                        BlockData block = blockDefinitions[blockId];
                        if (block == null)
                        {
                            continue;
                        }

                        int neighborBlockId = GetHalo(haloBlocks, position + cubeNormals[face]);
                        if ((uint)neighborBlockId >= (uint)blockDefinitions.Length)
                        {
                            continue;
                        }

                        BlockData neighbourBlock = blockDefinitions[neighborBlockId];
                        if (neighbourBlock == null) { continue; }

                        bool neighbourIsOpaque = !neighbourBlock.isTransparent;
                        bool hidesFaceBecauseSameFluid = block.isFluid && neighbourBlock.id == block.id;

                        bool hidesFace = block.isTransparent
                            ? (block.isFluid ? (hidesFaceBecauseSameFluid || neighbourIsOpaque) : neighbourIsOpaque)
                            : neighbourIsOpaque;

                        if (hidesFace)
                        {
                            continue;
                        }

                        mask[u + v * width] = new GreedyCell
                        {
                            valid = true,
                            blockId = blockId,
                            textureData = block.GetTexture(face),
                            isFluid = block.isFluid,
                            isTransparent = block.isTransparent,
                        };
                    }
                }

                for (int v = 0; v < height; v++)
                {
                    for (int u = 0; u < width; u++)
                    {
                        int startIdx = u + v * width;
                        GreedyCell cell = mask[startIdx];

                        if (!cell.valid || usedStamp[startIdx] == stamp)
                        {
                            continue;
                        }

                        int quadWidth = 1;
                        while (u + quadWidth < width)
                        {
                            int idx = u + quadWidth + v * width;
                            if (usedStamp[idx] == stamp || !mask[idx].Matches(cell))
                            {
                                break;
                            }

                            quadWidth++;
                        }

                        int quadHeight = 1;
                        bool canGrow = true;
                        while (v + quadHeight < height && canGrow)
                        {
                            for (int checkU = 0; checkU < quadWidth; checkU++)
                            {
                                int idx = (u + checkU) + (v + quadHeight) * width;
                                if (usedStamp[idx] == stamp || !mask[idx].Matches(cell))
                                {
                                    canGrow = false;
                                    break;
                                }
                            }

                            if (canGrow)
                            {
                                quadHeight++;
                            }
                        }

                        for (int markV = 0; markV < quadHeight; markV++)
                        {
                            for (int markU = 0; markU < quadWidth; markU++)
                            {
                                usedStamp[(u + markU) + (v + markV) * width] = stamp;
                            }
                        }

                        GetQuadForFace(face, u, v, slice, quadWidth, quadHeight, out Vector3 origin, out Vector3 du, out Vector3 dv);

                        if (cell.isFluid)
                        {
                            AddQuad(origin, du, dv, quadHeight, quadWidth, face, cell.textureData, fluidVertices, fluidNormals, fluidTriangles, fluidUvs);
                        }
                        else if (cell.isTransparent)
                        {
                            AddQuad(origin, du, dv, quadHeight, quadWidth, face, cell.textureData, transparentVertices, transparentNormals, transparentTriangles, transparentUvs);
                        }
                        else
                        {
                            AddQuad(origin, du, dv, quadHeight, quadWidth, face, cell.textureData, solidVertices, solidNormals, solidTriangles, solidUvs);
                        }
                    }
                }
            }
        }

        private static void AddQuad(
                  Vector3 origin,
                  Vector3 du,
                  Vector3 dv,
                  int duTiles,
                  int dvTiles,
                  int face,
                  BlockData.FaceTextureData textureData,
                  List<Vector3> vertices,
                  List<Vector3> normals,
                  List<int> triangles,
                  List<Vector2> uvs)
        {
            int vertexIndex = vertices.Count;

            vertices.Add(origin);
            vertices.Add(origin + du);
            vertices.Add(origin + dv);
            vertices.Add(origin + du + dv);

            for (int i = 0; i < 4; i++)
            {
                normals.Add(cubeNormals[face]);
            }

            AddTexture(textureData, duTiles, dvTiles, ref uvs);

            triangles.Add(vertexIndex);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 3);
        }
        private static Vector3Int GetPositionForFace(int face, int u, int v, int slice)
        {
            return face switch
            {
                0 or 1 => new Vector3Int(u, v, slice),
                2 or 3 => new Vector3Int(u, slice, v),
                4 or 5 => new Vector3Int(slice, v, u),
                _ => throw new ArgumentOutOfRangeException(nameof(face), face, null),
            };
        }

        private static void GetMaskSizeForFace(int face, out int width, out int height, out int slices)
        {
            switch (face)
            {
                case 0:
                case 1:
                    width = Chunk.CHUNK_SIZE;
                    height = Chunk.CHUNK_HEIGHT;
                    slices = Chunk.CHUNK_SIZE;
                    break;
                case 2:
                case 3:
                    width = Chunk.CHUNK_SIZE;
                    height = Chunk.CHUNK_SIZE;
                    slices = Chunk.CHUNK_HEIGHT;
                    break;
                case 4:
                case 5:
                    width = Chunk.CHUNK_SIZE;
                    height = Chunk.CHUNK_HEIGHT;
                    slices = Chunk.CHUNK_SIZE;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face), face, null);
            }
        }

        private static void GetQuadForFace(int face, int u, int v, int slice, int quadWidth, int quadHeight, out Vector3 origin, out Vector3 du, out Vector3 dv)
        {
            switch (face)
            {
                case 0: // back
                    origin = new Vector3(u, v, slice);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(quadWidth, 0, 0);
                    break;
                case 1: // front
                    origin = new Vector3(u + quadWidth, v, slice + 1);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(-quadWidth, 0, 0);
                    break;
                case 2: // top
                    origin = new Vector3(u, slice + 1, v);
                    du = new Vector3(0, 0, quadHeight);
                    dv = new Vector3(quadWidth, 0, 0);
                    break;
                case 3: // bottom
                    origin = new Vector3(u + quadWidth, slice, v);
                    du = new Vector3(0, 0, quadHeight);
                    dv = new Vector3(-quadWidth, 0, 0);
                    break;
                case 4: // left
                    origin = new Vector3(slice, v, u + quadWidth);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(0, 0, -quadWidth);
                    break;
                case 5: // right
                    origin = new Vector3(slice + 1, v, u);
                    du = new Vector3(0, quadHeight, 0);
                    dv = new Vector3(0, 0, quadWidth);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face), face, null);
            }
        }

        internal struct GreedyCell
        {
            public bool valid;
            public int blockId;
            public BlockData.FaceTextureData textureData;
            public bool isFluid;
            public bool isTransparent;

            public bool Matches(in GreedyCell other)
            {
                return valid
                    && other.valid
                    && blockId == other.blockId
                    && textureData.Matches(other.textureData)
                    && isFluid == other.isFluid
                    && isTransparent == other.isTransparent;
            }
        }

        private static byte GetHalo(byte[,,] haloBlocks, Vector3Int pos)
        {
            return haloBlocks[pos.x + 1, pos.y + 1, pos.z + 1];
        }

        private static void AddTexture(BlockData.FaceTextureData textureData, int duTiles, int dvTiles, ref List<Vector2> uvs)
        {
            float tileWidth = textureData.uvMax.x - textureData.uvMin.x;
            float tileHeight = textureData.uvMax.y - textureData.uvMin.y;

            // Keep all corner UVs well inside one atlas tile so interpolation never crosses tile boundaries.
            // This prevents per-fragment tile selection in the block atlas shader from producing texture strips.
            float uInset = Mathf.Max(UV_EPSILON, tileWidth * 0.05f);
            float vInset = Mathf.Max(UV_EPSILON, tileHeight * 0.05f);

            float u0 = textureData.uvMin.x + uInset;
            float v0 = textureData.uvMin.y + vInset;
            float u1 = textureData.uvMax.x - uInset;
            float v1 = textureData.uvMax.y - vInset;

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

    public readonly struct MeshData
    {
        public readonly MeshSection solidMesh;
        public readonly MeshSection fluidMesh;
        public readonly MeshSection transparentMesh;

        public MeshData(MeshSection solidMesh, MeshSection fluidMesh, MeshSection transparentMesh)
        {
            this.solidMesh = solidMesh;
            this.fluidMesh = fluidMesh;
            this.transparentMesh = transparentMesh;
        }
    }

    public readonly struct MeshSection
    {
        public readonly int[] triangles;
        public readonly Vector3[] vertices;
        public readonly Vector3[] normals;
        public readonly Vector2[] uvs;

        public MeshSection(List<int> triangles, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs)
        {
            this.triangles = triangles.ToArray();
            this.vertices = vertices.ToArray();
            this.normals = normals.ToArray();
            this.uvs = uvs.ToArray();
        }
    }
}