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

            int[] dimensions = { Chunk.CHUNK_SIZE, Chunk.CHUNK_HEIGHT, Chunk.CHUNK_SIZE };

            foreach (FaceDefinition face in faceDefinitions)
            {
                int axisSize = dimensions[face.Axis];
                int uSize = dimensions[face.UAxis];
                int vSize = dimensions[face.VAxis];

                FaceData[,] mask = new FaceData[uSize, vSize];

                for (int axisPos = 0; axisPos < axisSize; axisPos++)
                {
                    Array.Clear(mask, 0, mask.Length);

                    for (int u = 0; u < uSize; u++)
                    {
                        int actualU = face.UStep > 0 ? u : uSize - 1 - u;

                        for (int v = 0; v < vSize; v++)
                        {
                            int actualV = face.VStep > 0 ? v : vSize - 1 - v;

                            Vector3Int blockCoords = GetBlockCoords(face, axisPos, actualU, actualV);
                            int blockId = GetBlockId(haloBlocks, blockCoords);

                            if (blockId == Chunk.BLOCK_AIR)
                            {
                                continue;
                            }

                            Block block = GetBlock(blockId);

                            if (block == null)
                            {
                                continue;
                            }

                            Vector3Int neighbourCoords = blockCoords + face.NormalInt;
                            int neighbourId = GetBlockId(haloBlocks, neighbourCoords);
                            Block neighbourBlock = GetBlock(neighbourId);

                            if (neighbourBlock == null || neighbourBlock.isTransparent)
                            {
                                int textureId = block.GetTexture(face.FaceIndex);
                                mask[u, v] = new FaceData(true, textureId);
                            }
                        }
                    }

                    for (int u = 0; u < uSize; u++)
                    {
                        for (int v = 0; v < vSize;)
                        {
                            FaceData faceData = mask[u, v];

                            if (!faceData.Exists)
                            {
                                v++;
                                continue;
                            }

                            int width = 1;

                            while (v + width < vSize)
                            {
                                FaceData next = mask[u, v + width];

                                if (!next.Exists || next.TextureId != faceData.TextureId)
                                {
                                    break;
                                }

                                width++;
                            }

                            int height = 1;
                            bool stop = false;

                            while (u + height < uSize && !stop)
                            {
                                for (int offset = 0; offset < width; offset++)
                                {
                                    FaceData next = mask[u + height, v + offset];

                                    if (!next.Exists || next.TextureId != faceData.TextureId)
                                    {
                                        stop = true;
                                        break;
                                    }
                                }

                                if (!stop)
                                {
                                    height++;
                                }
                            }

                            int uMin = GetMinimalCoordinate(face.UStep, u, height, uSize);
                            int vMin = GetMinimalCoordinate(face.VStep, v, width, vSize);

                            Vector3Int baseCoords = Vector3Int.zero;
                            SetCoord(ref baseCoords, face.Axis, axisPos);
                            SetCoord(ref baseCoords, face.UAxis, uMin);
                            SetCoord(ref baseCoords, face.VAxis, vMin);

                            AddQuad(vertices, normals, triangles, uvs, face, baseCoords, height, width, faceData.TextureId, ref vertexIndex);

                            for (int du = 0; du < height; du++)
                            {
                                for (int dv = 0; dv < width; dv++)
                                {
                                    mask[u + du, v + dv] = default;
                                }
                            }

                            v += width;
                        }
                    }
                }
            }

            return new ChunkMeshData(triangles, vertices, normals, uvs);
        }

        private static void AddTexture(int textureId, FaceDefinition face, ref List<Vector2> uvs)
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

            float uStart = face.UStep > 0 ? u0 : u1;
            float uEnd = face.UStep > 0 ? u1 : u0;
            float vStart = face.VStep > 0 ? v0 : v1;
            float vEnd = face.VStep > 0 ? v1 : v0;

            uvs.Add(new Vector2(uStart, vStart)); // bottom-left
            uvs.Add(new Vector2(uEnd, vStart)); // bottom-right
            uvs.Add(new Vector2(uStart, vEnd)); // top-left
            uvs.Add(new Vector2(uEnd, vEnd)); // top-right
        }

        private static Vector3Int GetBlockCoords(FaceDefinition face, int axisPos, int uCoord, int vCoord)
        {
            Vector3Int coords = Vector3Int.zero;
            SetCoord(ref coords, face.Axis, axisPos);
            SetCoord(ref coords, face.UAxis, uCoord);
            SetCoord(ref coords, face.VAxis, vCoord);
            return coords;
        }

        private static int GetBlockId(byte[,,] haloBlocks, Vector3Int coords)
        {
            return haloBlocks[coords.x + 1, coords.y + 1, coords.z + 1];
        }

        private static int GetMinimalCoordinate(int step, int start, int length, int size)
        {
            return step >= 0 ? start : size - (start + length);
        }

        private static void SetCoord(ref Vector3Int coords, int axis, int value)
        {
            switch (axis)
            {
                case 0:
                    coords.x = value;
                    break;
                case 1:
                    coords.y = value;
                    break;
                case 2:
                    coords.z = value;
                    break;
            }
        }

        private static void AddQuad(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, List<Vector2> uvs,
            FaceDefinition face, Vector3Int baseCoords, int uLength, int vLength, int textureId, ref int vertexIndex)
        {
            Vector3 basePosition = new Vector3(baseCoords.x, baseCoords.y, baseCoords.z);

            switch (face.Axis)
            {
                case 0:
                    if (face.NormalInt.x > 0)
                    {
                        basePosition.x += 1f;
                    }
                    break;
                case 1:
                    if (face.NormalInt.y > 0)
                    {
                        basePosition.y += 1f;
                    }
                    break;
                case 2:
                    if (face.NormalInt.z > 0)
                    {
                        basePosition.z += 1f;
                    }
                    break;
            }

            Vector3 uDirection = face.UDirection;
            Vector3 vDirection = face.VDirection;

            if (uDirection.x < 0f)
            {
                basePosition.x += uLength * -uDirection.x;
            }

            if (uDirection.y < 0f)
            {
                basePosition.y += uLength * -uDirection.y;
            }

            if (uDirection.z < 0f)
            {
                basePosition.z += uLength * -uDirection.z;
            }

            if (vDirection.x < 0f)
            {
                basePosition.x += vLength * -vDirection.x;
            }

            if (vDirection.y < 0f)
            {
                basePosition.y += vLength * -vDirection.y;
            }

            if (vDirection.z < 0f)
            {
                basePosition.z += vLength * -vDirection.z;
            }

            Vector3 v0 = basePosition;
            Vector3 v1 = basePosition + uDirection * uLength;
            Vector3 v2 = basePosition + vDirection * vLength;
            Vector3 v3 = basePosition + uDirection * uLength + vDirection * vLength;

            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);

            Vector3 normal = face.Normal;
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            triangles.Add(vertexIndex);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 3);

            AddTexture(textureId, face, ref uvs);

            vertexIndex += 4;
        }

        private static readonly FaceDefinition[] faceDefinitions = new FaceDefinition[6]
        {
            new FaceDefinition(faceIndex: 0, axis: 2, normal: new Vector3Int(0, 0, -1), uAxis: 1, uStep: 1, vAxis: 0, vStep: 1),
            new FaceDefinition(faceIndex: 1, axis: 2, normal: new Vector3Int(0, 0, 1), uAxis: 1, uStep: 1, vAxis: 0, vStep: -1),
            new FaceDefinition(faceIndex: 2, axis: 1, normal: new Vector3Int(0, 1, 0), uAxis: 2, uStep: 1, vAxis: 0, vStep: 1),
            new FaceDefinition(faceIndex: 3, axis: 1, normal: new Vector3Int(0, -1, 0), uAxis: 2, uStep: 1, vAxis: 0, vStep: -1),
            new FaceDefinition(faceIndex: 4, axis: 0, normal: new Vector3Int(-1, 0, 0), uAxis: 1, uStep: 1, vAxis: 2, vStep: -1),
            new FaceDefinition(faceIndex: 5, axis: 0, normal: new Vector3Int(1, 0, 0), uAxis: 1, uStep: 1, vAxis: 2, vStep: 1)
        };

        private readonly struct FaceDefinition
        {
            public readonly int FaceIndex;
            public readonly int Axis;
            public readonly int UAxis;
            public readonly int VAxis;
            public readonly int UStep;
            public readonly int VStep;
            public readonly Vector3Int NormalInt;
            public readonly Vector3 Normal;
            public readonly Vector3 UDirection;
            public readonly Vector3 VDirection;

            public FaceDefinition(int faceIndex, int axis, Vector3Int normal, int uAxis, int uStep, int vAxis, int vStep)
            {
                FaceIndex = faceIndex;
                Axis = axis;
                UAxis = uAxis;
                VAxis = vAxis;
                UStep = uStep;
                VStep = vStep;
                NormalInt = normal;
                Normal = new Vector3(normal.x, normal.y, normal.z);
                UDirection = new Vector3(uAxis == 0 ? uStep : 0, uAxis == 1 ? uStep : 0, uAxis == 2 ? uStep : 0);
                VDirection = new Vector3(vAxis == 0 ? vStep : 0, vAxis == 1 ? vStep : 0, vAxis == 2 ? vStep : 0);
            }
        }

        private readonly struct FaceData
        {
            public readonly bool Exists;
            public readonly int TextureId;

            public FaceData(bool exists, int textureId)
            {
                Exists = exists;
                TextureId = textureId;
            }
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