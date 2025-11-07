using UnityEngine;

namespace BenScr.MCC
{
    public static class ChunkUtility
    {
        public static int GetBlockAtPosition(Vector3 worldPos)
        {
            var wx = Mathf.FloorToInt(worldPos.x);
            var wy = Mathf.FloorToInt(worldPos.y);
            var wz = Mathf.FloorToInt(worldPos.z);
            return GetBlockAtBlock(new Vector3Int(wx, wy, wz));
        }
        public static int GetBlockAtBlock(Vector3Int world)
        {
            var cx = Mathf.FloorToInt((float)world.x / Chunk.CHUNK_SIZE);
            var cy = Mathf.FloorToInt((float)world.y / Chunk.CHUNK_HEIGHT);
            var cz = Mathf.FloorToInt((float)world.z / Chunk.CHUNK_SIZE);
            var cCoord = new Vector3Int(cx, cy, cz);

            if (!TerrainGenerator.chunks.TryGetValue(cCoord, out var chunk))
                return Chunk.BLOCK_AIR;

            var lx = world.x - cx * Chunk.CHUNK_SIZE;
            var ly = world.y - cy * Chunk.CHUNK_HEIGHT;
            var lz = world.z - cz * Chunk.CHUNK_SIZE;


            if ((uint)lx >= Chunk.CHUNK_SIZE || (uint)ly >= Chunk.CHUNK_HEIGHT || (uint)lz >= Chunk.CHUNK_SIZE)
                return Chunk.BLOCK_AIR;

            return chunk.blocks[lx, ly, lz];
        }

        public static bool IsInsideChunk(Vector3Int relativePosition)
        {
            if (relativePosition.x < 0 || relativePosition.y < 0 || relativePosition.z < 0 ||
                relativePosition.x > Chunk.CHUNK_SIZE - 1 || relativePosition.y > Chunk.CHUNK_HEIGHT - 1 || relativePosition.z > Chunk.CHUNK_SIZE - 1)
            {
                return false;
            }

            return true;
        }
        public static Chunk GetChunkByCoordinate(Vector3Int coordinate)
        {
            if (TerrainGenerator.chunks.TryGetValue(coordinate, out Chunk chunk))
            {
                return chunk;
            }

            return null;
        }

        public static Vector3Int GetChunkCoordinateFromPosition(Vector3 position)
        {
            int chunkX = Mathf.FloorToInt(position.x / Chunk.CHUNK_SIZE);
            int chunkY = Mathf.FloorToInt(position.y / Chunk.CHUNK_HEIGHT);
            int chunkZ = Mathf.FloorToInt(position.z / Chunk.CHUNK_SIZE);

            return new Vector3Int(chunkX, chunkY, chunkZ);
        }

        public static Chunk GetChunkByPosition(Vector3 position)
        {
            Vector3Int coordinate = GetChunkCoordinateFromPosition(position);

            if (TerrainGenerator.chunks.TryGetValue(new Vector3Int(coordinate.x, coordinate.y, coordinate.z), out Chunk chunk))
            {
                return chunk;
            }

            return null;
        }

    }
}
