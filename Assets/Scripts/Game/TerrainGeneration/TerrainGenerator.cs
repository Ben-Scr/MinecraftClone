using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace BenScr.MCC
{
    public class TerrainGenerator : MonoBehaviour
    {
        [Serializable]
        public struct NoiseLayerSettings
        {
            public float scale;
            public float amplitude;
            public float redistribution;
            public Vector2 offset;
        }

        [Serializable]
        public struct CaveNoiseSettings
        {
            [Min(0.0001f)] public float scale;
            [Min(0.0001f)] public float verticalScale;
            [Range(0f, 1f)] public float threshold;
            public Vector3 offset;
            [Min(0)] public int surfaceClearance;
        }

        [Header("Cave Noise")]
        public bool enableCaves = true;

        public CaveNoiseSettings caveNoise = new CaveNoiseSettings
        {
            scale = 48f,
            verticalScale = 32f,
            threshold = 0.6f,
            offset = Vector3.zero,
            surfaceClearance = 3
        };


        [Header("Terrain Noise Layers")]
        public NoiseLayerSettings continentNoise = new NoiseLayerSettings
        {
            scale = 320f,
            amplitude = 1f,
            redistribution = 1.15f,
            offset = Vector2.zero
        };

        public NoiseLayerSettings mountainNoise = new NoiseLayerSettings
        {
            scale = 120f,
            amplitude = 1f,
            redistribution = 1.05f,
            offset = Vector2.zero
        };

        public NoiseLayerSettings detailNoise = new NoiseLayerSettings
        {
            scale = 40f,
            amplitude = 0.5f,
            redistribution = 1f,
            offset = Vector2.zero
        };

        public NoiseLayerSettings ridgeNoise = new NoiseLayerSettings
        {
            scale = 60f,
            amplitude = 0.8f,
            redistribution = 2f,
            offset = Vector2.zero
        };

        [Header("Terrain Noise Blending")]
        [Range(0.1f, 3f)] public float flatlandsHeightMultiplier = 0.65f;
        [Range(0.5f, 5f)] public float mountainHeightMultiplier = 2.5f;
        [Range(0f, 1f)] public float mountainBlendStart = 0.55f;
        [Range(0.1f, 4f)] public float mountainBlendSharpness = 2f;

        public GameObject chunkPrefab;
        [SerializeField] private int seed = 0;
        public bool addColliders = false;
        public bool addTrees = true;
        [SerializeField] private int viewDistance = 5;
        [SerializeField] private int viewDistanceY = 2;
        public float noiseScale = 20.0f;
        public float noiseHeight = 10.0f;
        public int groundOffset = 10;
        internal Vector2 noiseOffset;
        [SerializeField] private float chunkUpdateThreshold = 1.0f;
        [SerializeField] private bool shouldDisableChunks = false;


        public static readonly Dictionary<Vector3Int, Chunk> chunks = new();
        private readonly HashSet<Vector3Int> lastActiveChunks = new();
        private readonly Queue<Vector3Int> chunksToCreate = new();
        private readonly Queue<Vector3Int> chunksToGenerate = new();

        private readonly HashSet<Vector3Int> queuedChunks = new();
        private readonly HashSet<Vector3Int> currentActiveChunks = new();

        private Vector3 lastChunkUpdatePlayerPosition;
        [SerializeField] private int maxChunksCreatePerFrame = 2;
        [SerializeField] private int maxChunksGeneratePerFrame = 2;
        [SerializeField] private float addColliderDistance = 10f;

        public static TerrainGenerator instance;
        private Vector3Int[] poses;
        private float chunkUpdateThresholdSq;
        private int viewDistanceXZSq;
        private int viewDistanceYSq;


        private int lastViewDistance;

        Vector2 continentNoiseRuntimeOffset;
        Vector2 mountainNoiseRuntimeOffset;
        Vector2 detailNoiseRuntimeOffset;
        Vector2 ridgeNoiseRuntimeOffset;
        Vector3 caveNoiseRuntimeOffset;

        private void Awake()
        {
            instance = this;
            chunks.Clear();
            chunkUpdateThresholdSq = chunkUpdateThreshold * chunkUpdateThreshold;
            UpdateViewDistance();
        }

        void Start()
        {
            UnityEngine.Random.InitState(seed);

            continentNoiseRuntimeOffset = GenerateOffset();
            mountainNoiseRuntimeOffset = GenerateOffset();
            detailNoiseRuntimeOffset = GenerateOffset();
            ridgeNoiseRuntimeOffset = GenerateOffset();
            noiseOffset = GenerateOffset();
            caveNoiseRuntimeOffset = GenerateOffset3D();
        }

        private Vector2 GenerateOffset()
        {
            return new Vector2(
                UnityEngine.Random.Range(-100_000f, 100_000f),
                UnityEngine.Random.Range(-100_000f, 100_000f)
            );
        }
        private Vector3 GenerateOffset3D()
        {
            return new Vector3(
                UnityEngine.Random.Range(-100000f, 100000f),
                UnityEngine.Random.Range(-100000f, 100000f),
                UnityEngine.Random.Range(-100000f, 100000f)
            );
        }


        public void UpdateViewDistance()
        {
            viewDistanceXZSq = viewDistance * viewDistance;
            viewDistanceYSq = viewDistanceY * viewDistanceY;

            foreach (Vector3Int chunkCoord in lastActiveChunks)
            {
                Vector3 playerPosition = PlayerController.instance.transform.position;
                Vector3Int playerChunk = ChunkUtility.GetChunkCoordinateFromPosition(playerPosition);

                bool visible = (playerChunk.x - chunkCoord.x) * (playerChunk.x - chunkCoord.x)
                                + (playerChunk.z - chunkCoord.z) * (playerChunk.z - chunkCoord.z) < viewDistanceXZSq;

                if (chunks.TryGetValue(chunkCoord, out var ch))
                    ch.SetActive(visible);
            }

            GeneratePoses();
            lastViewDistance = viewDistance;
        }
        private void GeneratePoses()
        {
            int rx = viewDistance;
            int ry = viewDistanceY > 0 ? viewDistanceY : viewDistance;
            int rz = viewDistance;


            int cap = (2 * rx + 1) * (2 * ry + 1) * (2 * rz + 1);
            var tmp = new List<Vector3Int>(cap);

            for (int x = -rx; x <= rx; x++)
                for (int y = -ry; y <= ry; y++)
                    for (int z = -rz; z <= rz; z++)
                        tmp.Add(new Vector3Int(x, y, z));

            tmp.Sort((a, b) =>
            {
                int ca = Math.Max(Math.Max(Mathf.Abs(a.x), Mathf.Abs(a.y)), Mathf.Abs(a.z));
                int cb = Math.Max(Math.Max(Mathf.Abs(b.x), Mathf.Abs(b.y)), Mathf.Abs(b.z));
                if (ca != cb) return ca - cb;

                int da = a.x * a.x + a.y * a.y + a.z * a.z;
                int db = b.x * b.x + b.y * b.y + b.z * b.z;
                return da - db;
            });

            poses = tmp.ToArray();
        }

        public void Update()
        {
            if (lastViewDistance != viewDistance)
                UpdateViewDistance();

            ChunkMeshGenerator.Update();
            UpdateChunks(PlayerController.instance.transform.position);
        }


        public void UpdateChunks(Vector3 playerPosition)
        { 
            bool movedEnough = (playerPosition - lastChunkUpdatePlayerPosition).sqrMagnitude >= chunkUpdateThresholdSq;

            if (!movedEnough && chunksToCreate.Count == 0 && chunksToGenerate.Count == 0)
                return;

            Vector3Int playerChunk = ChunkUtility.GetChunkCoordinateFromPosition(playerPosition);

            currentActiveChunks.Clear();

            for (int i = 0; i < poses.Length; i++)
            {
                Vector3Int pos = poses[i];

                var coordinate = new Vector3Int(playerChunk.x + pos.x, playerChunk.y + pos.y, playerChunk.z + pos.z);

                if (!chunks.TryGetValue(coordinate, out var chunk))
                {
                    if (queuedChunks.Add(coordinate))
                        chunksToCreate.Enqueue(coordinate);
                }
                else
                {
                    if (movedEnough)
                        currentActiveChunks.Add(coordinate);

                    if (addColliders && chunk.meshCollider == null)
                    {
                        float distanceX = math.abs(playerPosition.x - chunk.position.x);
                        float distanceY = math.abs(playerPosition.y - chunk.position.y);
                        float distanceZ = math.abs(playerPosition.z - chunk.position.z);

                        float maxDistance = Chunk.CHUNK_SIZE + addColliderDistance;
                        if (distanceX + distanceZ + distanceY <= maxDistance)
                            chunk.AddMeshCollider();
                    }
                }
            }

            int createChunksCount = math.min(chunksToCreate.Count, maxChunksCreatePerFrame);
            for (int i = 0; i < createChunksCount; i++)
            {
                var coordinate = chunksToCreate.Dequeue();
                queuedChunks.Remove(coordinate);

                var targetChunk = new Chunk(coordinate.x, coordinate.y, coordinate.z);
                targetChunk.Prepare();
                chunks.Add(targetChunk.coordinate, targetChunk);
                //if(!targetChunk.isGenerated)
                chunksToGenerate.Enqueue(targetChunk.coordinate);

                if (movedEnough) currentActiveChunks.Add(targetChunk.coordinate);
            }

            int generateChunksCount = math.min(chunksToGenerate.Count, maxChunksGeneratePerFrame);
            for (int i = 0; i < generateChunksCount; i++)
            {
                var coordinate = chunksToGenerate.Dequeue();
                var targetChunk = chunks[coordinate];

                if (!HasAllNeighborChunks(targetChunk.coordinate))
                {
                    chunksToGenerate.Enqueue(coordinate);
                    continue;
                }

                targetChunk.Generate();
                if (movedEnough) currentActiveChunks.Add(targetChunk.coordinate);
            }

            if (movedEnough && shouldDisableChunks)
            {
                foreach (var position in lastActiveChunks)
                {
                    float distanceX = playerChunk.x - position.x;
                    float distanceZ = playerChunk.z - position.z;
                    float distanceY = playerChunk.y - position.y;

                    bool visibleXZ = distanceX * distanceX + distanceZ * distanceZ <= viewDistanceXZSq;
                    bool visibleY = distanceY * distanceY <= viewDistanceYSq;

                    if (chunks.TryGetValue(position, out var chunk))
                        chunk.SetActive(visibleXZ && visibleY);
                }

                lastActiveChunks.Clear();
                foreach (var pos in currentActiveChunks)
                    lastActiveChunks.Add(pos);
            }

            lastChunkUpdatePlayerPosition = playerPosition;
        }

        public bool HasAllNeighborChunks(Vector3Int chunkCoord)
        {
            return chunks.ContainsKey(chunkCoord + Vector3Int.right) &&
                   chunks.ContainsKey(chunkCoord + Vector3Int.left) &&
                   chunks.ContainsKey(chunkCoord + Vector3Int.forward) &&
                   chunks.ContainsKey(chunkCoord + Vector3Int.back) &&
                   chunks.ContainsKey(chunkCoord + Vector3Int.up) &&
                   chunks.ContainsKey(chunkCoord + Vector3Int.down);
        }

        internal bool ShouldCarveCave(float3 worldPosition, int groundLevel)
        {
            if (!enableCaves)
                return false;

            if (worldPosition.y >= groundLevel - caveNoise.surfaceClearance)
                return false;

            float noiseValue = SampleCaveNoise01(worldPosition);
            return noiseValue > caveNoise.threshold;
        }

        internal float SampleCaveNoise01(float3 worldPosition)
        {
            float horizontalFrequency = 1f / Mathf.Max(0.0001f, caveNoise.scale);
            float verticalFrequency = 1f / Mathf.Max(0.0001f, caveNoise.verticalScale);

            float sampleX = worldPosition.x + caveNoise.offset.x + caveNoiseRuntimeOffset.x + noiseOffset.x;
            float sampleY = worldPosition.y + caveNoise.offset.y + caveNoiseRuntimeOffset.y;
            float sampleZ = worldPosition.z + caveNoise.offset.z + caveNoiseRuntimeOffset.z + noiseOffset.y;

            float3 sample = new float3(
                sampleX * horizontalFrequency,
                sampleY * verticalFrequency,
                sampleZ * horizontalFrequency
            );

            float noiseValue = noise.snoise(sample);
            return noiseValue * 0.5f + 0.5f;
        }

        internal void GetNoiseLayers(out NoiseLayer continentLayer, out NoiseLayer mountainLayer, out NoiseLayer detailLayer, out NoiseLayer ridgeLayer)
        {
            continentLayer = CreateNoiseLayer(continentNoise, continentNoiseRuntimeOffset);
            mountainLayer = CreateNoiseLayer(mountainNoise, mountainNoiseRuntimeOffset);
            detailLayer = CreateNoiseLayer(detailNoise, detailNoiseRuntimeOffset);
            ridgeLayer = CreateNoiseLayer(ridgeNoise, ridgeNoiseRuntimeOffset);
        }

        NoiseLayer CreateNoiseLayer(NoiseLayerSettings settings, Vector2 runtimeOffset)
        {
            float scale = settings.scale > 0f ? settings.scale : Mathf.Max(0.0001f, noiseScale);

            return new NoiseLayer
            {
                frequency = 1f / Mathf.Max(0.0001f, scale),
                amplitude = Mathf.Max(0f, settings.amplitude),
                redistribution = Mathf.Max(0.0001f, settings.redistribution),
                offset = new float2(
                    settings.offset.x + runtimeOffset.x + noiseOffset.x,
                    settings.offset.y + runtimeOffset.y + noiseOffset.y)
            };
        }

        public void SetBlock(Vector3 position, int blockId)
        {
            Chunk chunk = ChunkUtility.GetChunkByPosition(position);

            if (chunk != null)
            {
                chunk.SetBlock(position - chunk.position, blockId);
            }
            else
            {
                Debug.LogWarning("Position is outside of world: " + position);
            }
        }
    }
}