using UnityEngine;

namespace BenScr.MinecraftClone
{
    public static class ChunkUtility
    {
        public static int GetBlockAtPosition(Vector3 worldPos) => GetBlockAtPosition(SnapPosition(worldPos));
        public static int GetBlockAtPosition(Vector3Int worldPos)
        {
            Vector3Int chunkCoordinate = GetChunkCoordinateFromPosition(worldPos);

            if (!TerrainGenerator.Chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                return Chunk.BLOCK_AIR;

            if (chunk.Blocks == null)
                return Chunk.BLOCK_AIR;

            int lx = worldPos.x - chunkCoordinate.x * Chunk.CHUNK_SIZE;
            int ly = worldPos.y - chunkCoordinate.y * Chunk.CHUNK_HEIGHT;
            int lz = worldPos.z - chunkCoordinate.z * Chunk.CHUNK_SIZE;


            if ((uint)lx >= Chunk.CHUNK_SIZE || (uint)ly >= Chunk.CHUNK_HEIGHT || (uint)lz >= Chunk.CHUNK_SIZE)
                return Chunk.BLOCK_AIR;

            return chunk.Blocks[lx, ly, lz];
        }

        public static Chunk GetChunkAtCoordinate(Vector3Int chunkCoord)
        {
            if (TerrainGenerator.Chunks.TryGetValue(chunkCoord, out Chunk chunk))
            {
                return chunk;
            }

            return null;
        }
        public static Chunk GetChunkAtPosition(Vector3 position)
        {
            Vector3Int coordinate = GetChunkCoordinateFromPosition(position);

            if (TerrainGenerator.Chunks.TryGetValue(coordinate, out Chunk chunk))
            {
                return chunk;
            }

            return null;
        }
        public static Chunk GetHighestChunkAt(Vector3 worldPosition)
        {
            int chunkX = Mathf.FloorToInt(worldPosition.x / Chunk.CHUNK_SIZE);
            int chunkZ = Mathf.FloorToInt(worldPosition.z / Chunk.CHUNK_SIZE);
            Chunk highestChunk = null;

            foreach (Chunk chunk in TerrainGenerator.Chunks.Values)
            {
                if (chunk == null ||
                    chunk.Coordinate.x != chunkX ||
                    chunk.Coordinate.z != chunkZ ||
                    !chunk.IsTop)
                {
                    continue;
                }

                if (highestChunk == null || chunk.Coordinate.y > highestChunk.Coordinate.y)
                    highestChunk = chunk;
            }

            return highestChunk;
        }

        public static bool IsInsideChunk(Vector3Int relativePosition)
        {
            return (uint)relativePosition.x < Chunk.CHUNK_SIZE &&
                   (uint)relativePosition.y < Chunk.CHUNK_HEIGHT &&
                   (uint)relativePosition.z < Chunk.CHUNK_SIZE;
        }
        public static bool HasAllNeighborChunks(Vector3Int chunkCoord)
        {
            return TerrainGenerator.Chunks.ContainsKey(chunkCoord + Vector3Int.right) &&
                   TerrainGenerator.Chunks.ContainsKey(chunkCoord + Vector3Int.left) &&
                   TerrainGenerator.Chunks.ContainsKey(chunkCoord + Vector3Int.forward) &&
                   TerrainGenerator.Chunks.ContainsKey(chunkCoord + Vector3Int.back) &&
                   TerrainGenerator.Chunks.ContainsKey(chunkCoord + Vector3Int.up) &&
                   TerrainGenerator.Chunks.ContainsKey(chunkCoord + Vector3Int.down);
        }

        public static bool HasAllNeighborChunkData(Vector3Int chunkCoord)
        {
            return HasChunkData(chunkCoord + Vector3Int.right) &&
                   HasChunkData(chunkCoord + Vector3Int.left) &&
                   HasChunkData(chunkCoord + Vector3Int.forward) &&
                   HasChunkData(chunkCoord + Vector3Int.back) &&
                   HasChunkData(chunkCoord + Vector3Int.up) &&
                   HasChunkData(chunkCoord + Vector3Int.down);
        }

        private static bool HasChunkData(Vector3Int chunkCoord)
        {
            return TerrainGenerator.Chunks.TryGetValue(chunkCoord, out Chunk chunk) &&
                   chunk != null &&
                   chunk.Blocks != null;
        }

        public static Vector3Int GetChunkCoordinateFromPosition(Vector3 position)
        {
            int chunkX = Mathf.FloorToInt(position.x / Chunk.CHUNK_SIZE);
            int chunkY = Mathf.FloorToInt(position.y / Chunk.CHUNK_HEIGHT);
            int chunkZ = Mathf.FloorToInt(position.z / Chunk.CHUNK_SIZE);

            return new Vector3Int(chunkX, chunkY, chunkZ);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static Vector3Int GetChunkCoordinateFromPosition(Vector3Int position)
        {
            return new Vector3Int(
                FloorDivide(position.x, Chunk.CHUNK_SIZE),
                FloorDivide(position.y, Chunk.CHUNK_HEIGHT),
                FloorDivide(position.z, Chunk.CHUNK_SIZE));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static int FloorDivide(int value, int divisor)
        {
            int quotient = System.Math.DivRem(value, divisor, out int remainder);
            return remainder < 0 ? quotient - 1 : quotient;
        }

        public static Vector3Int SnapPosition(Vector3 position)
                => new Vector3Int(Mathf.FloorToInt(position.x),
                                  Mathf.FloorToInt(position.y),
                                  Mathf.FloorToInt(position.z));
    }
}
