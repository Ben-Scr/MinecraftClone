using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace BenScr.MCC
{
    public static class NoiseGenerator
    {
        public static byte[] GenerateBlocksParallel(Vector3 position, float noiseHeight, float noiseScale, Vector2 noiseOffset, int groundOffset)
        {
            const int LENGTH = Chunk.CHUNK_SIZE * Chunk.CHUNK_SIZE * Chunk.CHUNK_HEIGHT;
            var map = new NativeArray<byte>(LENGTH, Allocator.TempJob);

            GenerateBlocksJob job = new GenerateBlocksJob
            {
                Map = map,
                position = position,
                noiseHeight = noiseHeight,
                noiseScale = noiseScale,
                noiseOffset = noiseOffset,
                groundOffset = groundOffset
            };

            job.Schedule(LENGTH, 64).Complete();

            byte[] result = map.ToArray();
            map.Dispose();
            return result;
        }


        [BurstCompile]
        public struct GenerateBlocksJob : IJobParallelFor
        {
            [WriteOnly] public NativeArray<byte> Map;

            [ReadOnly] public Vector3 position;
            [ReadOnly] public float noiseHeight;
            [ReadOnly] public float noiseScale;
            [ReadOnly] public Vector2 noiseOffset;
            [ReadOnly] public int groundOffset;

            public void Execute(int index)
            {
                int y = index % Chunk.CHUNK_HEIGHT;
                int t = index / Chunk.CHUNK_HEIGHT;
                int z = t % Chunk.CHUNK_SIZE;
                int x = t / Chunk.CHUNK_SIZE;

                short groundLevel = (short)(math.floor(PerlinNoise2D.Perlin2D((position.x + x + noiseOffset.x) / noiseScale, (position.z + z + noiseOffset.y) / noiseScale) * noiseHeight) + groundOffset);
                y += (int)position.y;

                if (y > groundLevel)
                {
                    Map[index] = Chunk.BLOCK_AIR;
                }
                else
                {
                    if (y == groundLevel)
                    {
                        Map[index] = Chunk.BLOCK_GRASS;
                    }
                    else
                    {
                        if (y > groundLevel - 5)
                        {
                            Map[index] = Chunk.BLOCK_DIRT;
                        }
                        else
                        {
                            Map[index] = Chunk.BLOCK_STONE;
                        }
                    }
                }
            }
        }
    }

    public struct NoiseLayer
    {
        public float frequency;
        public float amplitude;
        public float redistribution;
        public float2 offset;
    }


    [BurstCompile]
    public struct GenerateTerrainHeightMapJob : IJobParallelFor
    {
        [WriteOnly]
        public NativeArray<float> HeightMap;

        [ReadOnly]
        public int ChunkSize;

        [ReadOnly]
        public float2 ChunkOrigin;

        [ReadOnly]
        public NoiseLayer ContinentLayer;

        [ReadOnly]
        public NoiseLayer MountainLayer;

        [ReadOnly]
        public NoiseLayer DetailLayer;

        [ReadOnly]
        public NoiseLayer RidgeLayer;

        [ReadOnly]
        public float FlatlandsHeightMultiplier;

        [ReadOnly]
        public float MountainHeightMultiplier;

        [ReadOnly]
        public float MountainBlendStart;

        [ReadOnly]
        public float MountainBlendSharpness;

        public void Execute(int index)
        {
            int x = index % ChunkSize;
            int z = index / ChunkSize;

            float2 worldPosition = ChunkOrigin + new float2(x, z);

            float height = TerrainNoiseUtility.SampleNormalizedHeight(
                worldPosition,
                ContinentLayer,
                MountainLayer,
                DetailLayer,
                RidgeLayer,
                FlatlandsHeightMultiplier,
                MountainHeightMultiplier,
                MountainBlendStart,
                MountainBlendSharpness);

            HeightMap[index] = height;
        }
    }
}