using System;
using System.Collections.Generic;
using BenScr.CubeDash;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public sealed class FallingBlockEntity : MonoBehaviour
    {
        private const string PersistentEntityPoolCategory = "Falling Block Entities";
        private static readonly Vector3 CollisionSize = Vector3.one * 0.98f;
        private static readonly Vector3 BlockCenterOffset = Vector3.one * 0.5f;
        private static readonly Dictionary<int, Mesh> MeshCache = new();
        private static readonly Dictionary<PoolKey, Stack<FallingBlockEntity>> EntityPool = new();
        private static readonly List<Renderer> RendererBuffer = new(8);
        private static readonly Color32 WhiteBlockTint = new Color32(255, 255, 255, 255);
        private static readonly Color32 DefaultGrassTint = new Color32(150, 220, 82, 255);
        private static readonly Color32 DefaultLeavesTint = new Color32(88, 156, 72, 255);
        private const string UseMeshBlockUvProperty = "_UseMeshBlockUv";
        private static readonly int UseVoxelLightingProperty = Shader.PropertyToID("_UseVoxelLighting");
        private static readonly int UseObjectLightingProperty = Shader.PropertyToID("_UseObjectLighting");
        private static readonly int ObjectLightingProperty = Shader.PropertyToID("_ObjectLighting");
        private const int MaxPooledEntities = 256;
        private const int MaxPooledEntitiesPerKey = 64;
        private static Material fallingBlockMaterial;
        private static Material primedExplosiveMaterial;
        private static BlockData[] cachedBlockRegistry;
        private static int pooledEntityCount;

        private Vector3Int startWorldPosition;
        private Vector3 currentPosition;
        private Transform cachedTransform;
        private int blockId;
        private PlacedBlockData placedBlockData;
        private FallingBlockSimulator.TntExplosionSettings tntExplosionSettings;
        private PoolKey poolKey;
        private float verticalVelocity;
        private bool isSettling;
        private bool isPrimedExplosive;
        private bool hasExploded;
        private bool isRegistered;
        private bool canReturnToPool;
        private float fuseRemaining;
        private Renderer[] voxelLightingRenderers;
        private MaterialPropertyBlock voxelLightingPropertyBlock;
        private float ownBlockLight;
        private int lastPackedObjectLighting = -1;

        internal Vector3 Position => currentPosition;
        internal float VerticalVelocity => verticalVelocity;
        internal bool IsSettling => isSettling;
        internal bool IsPrimedExplosive => isPrimedExplosive;
        internal bool SimulatesWhenFallingBlocksDisabled => isPrimedExplosive;
        internal int SimulatorIndex { get; set; } = -1;
        internal int PrimedSimulatorIndex { get; set; } = -1;

        private readonly struct PoolKey : IEquatable<PoolKey>
        {
            public readonly int BlockId;
            public readonly bool IsPrimedExplosive;

            public PoolKey(int blockId, bool isPrimedExplosive)
            {
                BlockId = blockId;
                IsPrimedExplosive = isPrimedExplosive;
            }

            public bool Equals(PoolKey other)
            {
                return BlockId == other.BlockId &&
                       IsPrimedExplosive == other.IsPrimedExplosive;
            }

            public override bool Equals(object obj)
            {
                return obj is PoolKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (BlockId * 397) ^ (IsPrimedExplosive ? 1 : 0);
                }
            }
        }

        internal static void ClearMeshCache()
        {
            ClearEntityPool();

            foreach (Mesh mesh in MeshCache.Values)
            {
                if (mesh != null)
                    Destroy(mesh);
            }

            MeshCache.Clear();

            if (fallingBlockMaterial != null)
            {
                Destroy(fallingBlockMaterial);
                fallingBlockMaterial = null;
            }

            if (primedExplosiveMaterial != null)
            {
                Destroy(primedExplosiveMaterial);
                primedExplosiveMaterial = null;
            }

            cachedBlockRegistry = null;
        }

        internal static void EnsureMeshCacheForBlocks(BlockData[] blocks)
        {
            if (IsSameBlockRegistry(blocks))
                return;

            ClearMeshCache();
            cachedBlockRegistry = blocks != null ? (BlockData[])blocks.Clone() : null;
        }

        internal static void ClearEntityPool()
        {
            foreach (Stack<FallingBlockEntity> entities in EntityPool.Values)
            {
                while (entities.Count > 0)
                {
                    FallingBlockEntity entity = entities.Pop();
                    if (entity == null)
                        continue;

                    entity.canReturnToPool = false;
                    Destroy(entity.gameObject);
                }
            }

            EntityPool.Clear();
            pooledEntityCount = 0;
        }

        public static FallingBlockEntity Spawn(
            Vector3Int worldPosition,
            int blockId,
            BlockData block,
            PlacedBlockData placedBlockData)
        {
            return SpawnInternal(
                worldPosition,
                blockId,
                block,
                placedBlockData,
                false,
                default);
        }

        public static FallingBlockEntity SpawnPrimedExplosive(
            Vector3Int worldPosition,
            int blockId,
            BlockData block,
            PlacedBlockData placedBlockData,
            FallingBlockSimulator.TntExplosionSettings explosionSettings)
        {
            return SpawnInternal(
                worldPosition,
                blockId,
                block,
                placedBlockData,
                true,
                explosionSettings);
        }

        internal SaveController.FallingBlockSaveData CreateSaveData()
        {
            if (!isRegistered || isSettling || hasExploded)
                return null;

            return new SaveController.FallingBlockSaveData
            {
                StartX = startWorldPosition.x,
                StartY = startWorldPosition.y,
                StartZ = startWorldPosition.z,
                PositionX = currentPosition.x,
                PositionY = currentPosition.y,
                PositionZ = currentPosition.z,
                VerticalVelocity = verticalVelocity,
                BlockId = blockId,
                PlacedBlock = placedBlockData?.Clone(),
                IsPrimedExplosive = isPrimedExplosive,
                FuseRemaining = Mathf.Max(0f, fuseRemaining),
                TntFuseSeconds = tntExplosionSettings.FuseSeconds,
                TntDestructionRadius = tntExplosionSettings.DestructionRadius,
                TntMaxDestroyedBlocks = tntExplosionSettings.MaxDestroyedBlocks,
                TntDestroyFluids = tntExplosionSettings.DestroyFluids,
                TntDestroyIndestructibleBlocks = tntExplosionSettings.DestroyIndestructibleBlocks,
                TntDropDestroyedBlocks = tntExplosionSettings.DropDestroyedBlocks,
                TntPrimeNearbyTnt = tntExplosionSettings.PrimeNearbyTnt,
                TntChainedFuseSeconds = tntExplosionSettings.ChainedFuseSeconds
            };
        }

        internal static FallingBlockEntity Restore(SaveController.FallingBlockSaveData state)
        {
            if (state == null || !state.IsValid)
                return null;

            BlockData block = AssetsContainer.GetBlock(state.BlockId);
            if (block == null)
                return null;

            if ((state.IsPrimedExplosive && state.BlockId != Chunk.BLOCK_TNT) ||
                (!state.IsPrimedExplosive && !FallingBlockSimulator.IsFallingBlock(state.BlockId)))
            {
                return null;
            }

            FallingBlockEntity entity = state.IsPrimedExplosive
                ? SpawnPrimedExplosive(
                    state.StartWorldPosition,
                    state.BlockId,
                    block,
                    state.PlacedBlock?.Clone(),
                    state.ExplosionSettings)
                : Spawn(
                    state.StartWorldPosition,
                    state.BlockId,
                    block,
                    state.PlacedBlock?.Clone());

            if (entity == null)
                return null;

            entity.ApplyRestoredState(
                state.Position,
                state.VerticalVelocity,
                state.FuseRemaining);
            return entity;
        }

        private static FallingBlockEntity SpawnInternal(
            Vector3Int worldPosition,
            int blockId,
            BlockData block,
            PlacedBlockData placedBlockData,
            bool primedExplosive,
            FallingBlockSimulator.TntExplosionSettings explosionSettings)
        {
            bool usesCustomView = block.UsesCustomModel && block.ModelPrefab != null;
            bool canPool = !usesCustomView;
            PoolKey key = new PoolKey(blockId, primedExplosive);
            if (canPool && TryTakeFromPool(key, out FallingBlockEntity pooledEntity))
            {
                pooledEntity.Initialize(
                    worldPosition,
                    blockId,
                    placedBlockData,
                    primedExplosive,
                    explosionSettings,
                    key,
                    true);
                return pooledEntity;
            }

            GameObject entityObject = new GameObject($"FallingBlock_{block.name}");
            Transform entityTransform = entityObject.transform;
            entityTransform.position = (Vector3)worldPosition + BlockCenterOffset;

            if (usesCustomView)
                AddCustomModelView(entityObject, block, placedBlockData);
            else
                AddCubeView(entityObject, blockId, block, primedExplosive);

            BoxCollider boxCollider = entityObject.AddComponent<BoxCollider>();
            boxCollider.size = Vector3.one;

            Rigidbody rigidbody = entityObject.AddComponent<Rigidbody>();
            rigidbody.isKinematic = true;
            rigidbody.useGravity = false;

            FallingBlockEntity entity = entityObject.AddComponent<FallingBlockEntity>();
            entity.cachedTransform = entityTransform;
            entity.Initialize(
                worldPosition,
                blockId,
                placedBlockData,
                primedExplosive,
                explosionSettings,
                key,
                canPool);
            return entity;
        }

        private static bool TryTakeFromPool(PoolKey key, out FallingBlockEntity entity)
        {
            entity = null;
            if (!EntityPool.TryGetValue(key, out Stack<FallingBlockEntity> entities))
                return false;

            while (entities.Count > 0)
            {
                FallingBlockEntity candidate = entities.Pop();
                if (pooledEntityCount > 0)
                    pooledEntityCount--;
                if (candidate == null)
                    continue;

                entity = candidate;
                return true;
            }

            return false;
        }

        private static bool IsSameBlockRegistry(BlockData[] blocks)
        {
            if (blocks == null || cachedBlockRegistry == null || blocks.Length != cachedBlockRegistry.Length)
                return blocks == null && cachedBlockRegistry == null;

            for (int i = 0; i < blocks.Length; i++)
            {
                if (blocks[i] != cachedBlockRegistry[i])
                    return false;
            }

            return true;
        }

        private void Initialize(
            Vector3Int worldPosition,
            int fallingBlockId,
            PlacedBlockData sourcePlacedBlockData,
            bool primedExplosive,
            FallingBlockSimulator.TntExplosionSettings explosionSettings,
            PoolKey entityPoolKey,
            bool poolable)
        {
            if (cachedTransform == null)
                cachedTransform = transform;

            startWorldPosition = worldPosition;
            blockId = fallingBlockId;
            placedBlockData = sourcePlacedBlockData;
            isPrimedExplosive = primedExplosive;
            tntExplosionSettings = primedExplosive ? explosionSettings.Sanitized() : default;
            fuseRemaining = primedExplosive ? tntExplosionSettings.FuseSeconds : 0f;
            verticalVelocity = 0f;
            isSettling = false;
            hasExploded = false;
            poolKey = entityPoolKey;
            canReturnToPool = poolable;
            SimulatorIndex = -1;
            PrimedSimulatorIndex = -1;
            BlockData definition = AssetsContainer.GetBlock(fallingBlockId);
            ownBlockLight = definition != null
                ? Mathf.Clamp(definition.LightEmission, 0, ChunkMeshGenerator.MaximumBlockLight) /
                  (float)ChunkMeshGenerator.MaximumBlockLight
                : 0f;
            lastPackedObjectLighting = -1;
            CacheVoxelLightingRenderers();
            SetPosition((Vector3)worldPosition + BlockCenterOffset);

            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            isRegistered = true;
            FallingBlockSimulator.RegisterEntity(this);
        }

        public bool OccupiesWorldPosition(Vector3Int worldPosition)
        {
            Vector3 offset = currentPosition - ((Vector3)worldPosition + BlockCenterOffset);
            return Mathf.Abs(offset.x) <= CollisionSize.x &&
                   Mathf.Abs(offset.y) <= CollisionSize.y &&
                   Mathf.Abs(offset.z) <= CollisionSize.z;
        }

        internal void ApplySimulation(Vector3 position, float velocity)
        {
            if (isSettling)
                return;

            SetPosition(position);
            verticalVelocity = velocity;
        }

        private void ApplyRestoredState(Vector3 position, float velocity, float savedFuseRemaining)
        {
            SetPosition(position);
            verticalVelocity = Mathf.Clamp(
                velocity,
                -FallingBlockSimulator.MaximumFallSpeed,
                FallingBlockSimulator.MaximumFallSpeed);
            if (isPrimedExplosive)
                fuseRemaining = Mathf.Clamp(savedFuseRemaining, 0f, tntExplosionSettings.FuseSeconds);
        }

        internal bool TickPrimedExplosive(float deltaTime)
        {
            if (!isPrimedExplosive || hasExploded || isSettling)
                return false;

            fuseRemaining -= Mathf.Max(0f, deltaTime);
            if (fuseRemaining > 0f)
                return false;

            ExplodeNow();
            return true;
        }

        internal void SettleFromSimulation(Vector3Int targetWorldPosition)
        {
            isSettling = true;

            if (FallingBlockSimulator.TryPlaceSettledBlock(
                    targetWorldPosition,
                    blockId,
                    placedBlockData,
                    out Vector3Int placedWorldPosition))
            {
                SetPosition((Vector3)placedWorldPosition + BlockCenterOffset);
            }

            DisposeRegistration();
            ReleaseOrDestroy();
        }

        private void ExplodeNow()
        {
            if (hasExploded)
                return;

            hasExploded = true;
            isSettling = true;
            FallingBlockSimulator.ExplodePrimedTnt(currentPosition, tntExplosionSettings);
            DisposeRegistration();
            ReleaseOrDestroy();
        }

        private void OnDestroy()
        {
            DisposeRegistration();
        }

        internal void DestroyForSimulatorClear()
        {
            DisposeRegistration();
            canReturnToPool = false;
            Destroy(gameObject);
        }

        private void DisposeRegistration()
        {
            if (!isRegistered)
                return;

            isRegistered = false;
            FallingBlockSimulator.OnEntityDisposed(startWorldPosition);
            FallingBlockSimulator.UnregisterEntity(this);
        }

        private void ReleaseOrDestroy()
        {
            placedBlockData = null;
            verticalVelocity = 0f;

            if (!canReturnToPool || pooledEntityCount >= MaxPooledEntities)
            {
                canReturnToPool = false;
                Destroy(gameObject);
                return;
            }

            if (!EntityPool.TryGetValue(poolKey, out Stack<FallingBlockEntity> entities))
            {
                entities = new Stack<FallingBlockEntity>(MaxPooledEntitiesPerKey);
                EntityPool.Add(poolKey, entities);
            }

            if (entities.Count >= MaxPooledEntitiesPerKey)
            {
                canReturnToPool = false;
                Destroy(gameObject);
                return;
            }

            PersistentObjectPool.Store(gameObject, PersistentEntityPoolCategory);
            entities.Push(this);
            pooledEntityCount++;
        }

        private void SetPosition(Vector3 position)
        {
            bool positionChanged = !currentPosition.Equals(position);
            currentPosition = position;
            if (positionChanged)
                cachedTransform.position = position;

            RefreshVoxelLighting();
        }

        private void CacheVoxelLightingRenderers()
        {
            if (voxelLightingRenderers != null && voxelLightingRenderers.Length > 0)
                return;

            voxelLightingRenderers = GetComponentsInChildren<Renderer>(true);
        }

        private void RefreshVoxelLighting()
        {
            if (voxelLightingRenderers == null || voxelLightingRenderers.Length == 0)
                return;

            Vector3Int center = ChunkUtility.SnapPosition(currentPosition);
            Vector2 sampledLighting = Vector2.zero;
            bool hasSample = AccumulateVoxelLighting(center, ref sampledLighting);
            for (int i = 0; i < ChunkMeshGenerator.CubeNormals.Length; i++)
                hasSample |= AccumulateVoxelLighting(center + ChunkMeshGenerator.CubeNormals[i], ref sampledLighting);

            // A generated chunk can still be waiting for its first asynchronous
            // lighting snapshot. Treat that gap as dark so an entity cannot carry
            // outdoor skylight into a cave while the new data is in flight.
            if (!hasSample)
                sampledLighting = Vector2.zero;

            sampledLighting.y = Mathf.Max(sampledLighting.y, ownBlockLight);
            int skyLevel = Mathf.Clamp(
                Mathf.RoundToInt(sampledLighting.x * ChunkMeshGenerator.MaximumSkylight),
                0,
                ChunkMeshGenerator.MaximumSkylight);
            int blockLevel = Mathf.Clamp(
                Mathf.RoundToInt(sampledLighting.y * ChunkMeshGenerator.MaximumBlockLight),
                0,
                ChunkMeshGenerator.MaximumBlockLight);
            int packedLighting = skyLevel | (blockLevel << 4);
            if (packedLighting == lastPackedObjectLighting)
                return;

            lastPackedObjectLighting = packedLighting;
            Vector4 objectLighting = new Vector4(
                skyLevel / (float)ChunkMeshGenerator.MaximumSkylight,
                blockLevel / (float)ChunkMeshGenerator.MaximumBlockLight,
                0f,
                0f);

            voxelLightingPropertyBlock ??= new MaterialPropertyBlock();
            for (int i = 0; i < voxelLightingRenderers.Length; i++)
            {
                Renderer target = voxelLightingRenderers[i];
                if (target == null)
                    continue;

                voxelLightingPropertyBlock.Clear();
                target.GetPropertyBlock(voxelLightingPropertyBlock);
                voxelLightingPropertyBlock.SetFloat(UseVoxelLightingProperty, 1f);
                voxelLightingPropertyBlock.SetFloat(UseObjectLightingProperty, 1f);
                voxelLightingPropertyBlock.SetVector(ObjectLightingProperty, objectLighting);
                target.SetPropertyBlock(voxelLightingPropertyBlock);
            }
        }

        private static bool AccumulateVoxelLighting(Vector3Int worldPosition, ref Vector2 lighting)
        {
            if (!TerrainGenerator.TrySampleVoxelLighting(worldPosition, out Vector2 sample))
                return false;

            lighting.x = Mathf.Max(lighting.x, sample.x);
            lighting.y = Mathf.Max(lighting.y, sample.y);
            return true;
        }

        private static void AddCubeView(
            GameObject entityObject,
            int meshBlockId,
            BlockData block,
            bool usePrimedExplosiveMaterial)
        {
            MeshFilter meshFilter = entityObject.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = entityObject.AddComponent<MeshRenderer>();
            meshFilter.sharedMesh = GetOrBuildCubeMesh(meshBlockId, block);
            meshRenderer.sharedMaterial = usePrimedExplosiveMaterial
                ? GetPrimedExplosiveMaterial()
                : GetFallingBlockMaterial();
        }

        private static void AddCustomModelView(
            GameObject entityObject,
            BlockData block,
            PlacedBlockData sourcePlacedBlockData)
        {
            GameObject modelView = GameObject.Instantiate(block.ModelPrefab, entityObject.transform);
            modelView.name = $"{block.name}_FallingView";

            int rotationY = sourcePlacedBlockData != null ? sourcePlacedBlockData.RotationY : 0;
            Quaternion placementRotation = Quaternion.Euler(0f, rotationY, 0f);

            modelView.transform.localPosition = placementRotation * block.ModelPositionOffset;
            modelView.transform.localRotation = placementRotation * Quaternion.Euler(block.ModelRotationOffset);
            modelView.transform.localScale = block.ModelScale;

            ApplyMaterialOverride(modelView, block.ModelMaterialOverride);
        }

        private static void ApplyMaterialOverride(GameObject view, Material material)
        {
            if (view == null || material == null)
                return;

            RendererBuffer.Clear();
            view.GetComponentsInChildren(true, RendererBuffer);
            for (int i = 0; i < RendererBuffer.Count; i++)
                RendererBuffer[i].sharedMaterial = material;
            RendererBuffer.Clear();
        }

        private static Mesh GetOrBuildCubeMesh(int meshBlockId, BlockData block)
        {
            if (MeshCache.TryGetValue(meshBlockId, out Mesh mesh))
                return mesh;

            mesh = BuildCubeMesh(meshBlockId, block);
            MeshCache[meshBlockId] = mesh;
            return mesh;
        }

        private static Material GetFallingBlockMaterial()
        {
            if (fallingBlockMaterial != null)
                return fallingBlockMaterial;

            Material sourceMaterial = AssetsContainer.Instance != null
                ? AssetsContainer.Instance.BlockMaterial
                : null;

            if (sourceMaterial == null)
                return null;

            fallingBlockMaterial = new Material(sourceMaterial)
            {
                name = "Falling Block Material"
            };
            EnableMeshBlockUv(fallingBlockMaterial);
            return fallingBlockMaterial;
        }

        private static Material GetPrimedExplosiveMaterial()
        {
            if (primedExplosiveMaterial != null)
                return primedExplosiveMaterial;

            Material sourceMaterial = AssetsContainer.Instance != null
                ? AssetsContainer.Instance.BlockMaterial
                : null;

            if (sourceMaterial != null)
            {
                primedExplosiveMaterial = new Material(sourceMaterial)
                {
                    name = "Primed TNT Material"
                };
                EnableMeshBlockUv(primedExplosiveMaterial);

                if (primedExplosiveMaterial.HasProperty("_BaseColor"))
                    primedExplosiveMaterial.SetColor("_BaseColor", Color.white);

                if (primedExplosiveMaterial.HasProperty("_Color"))
                    primedExplosiveMaterial.SetColor("_Color", Color.white);

                return primedExplosiveMaterial;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Texture");

            if (shader != null)
                primedExplosiveMaterial = new Material(shader);
            else
                primedExplosiveMaterial = new Material(Shader.Find("Standard"));

            primedExplosiveMaterial.name = "Primed TNT Material";
            EnableMeshBlockUv(primedExplosiveMaterial);

            Texture atlasTexture = null;
            if (sourceMaterial != null)
            {
                if (sourceMaterial.HasProperty("_BaseMap"))
                    atlasTexture = sourceMaterial.GetTexture("_BaseMap");

                if (atlasTexture == null && sourceMaterial.HasProperty("_MainTex"))
                    atlasTexture = sourceMaterial.GetTexture("_MainTex");
            }

            if (atlasTexture != null)
            {
                if (primedExplosiveMaterial.HasProperty("_BaseMap"))
                    primedExplosiveMaterial.SetTexture("_BaseMap", atlasTexture);

                if (primedExplosiveMaterial.HasProperty("_MainTex"))
                    primedExplosiveMaterial.SetTexture("_MainTex", atlasTexture);
            }

            if (primedExplosiveMaterial.HasProperty("_BaseColor"))
                primedExplosiveMaterial.SetColor("_BaseColor", Color.white);

            if (primedExplosiveMaterial.HasProperty("_Color"))
                primedExplosiveMaterial.SetColor("_Color", Color.white);

            return primedExplosiveMaterial;
        }

        private static void EnableMeshBlockUv(Material material)
        {
            if (material != null && material.HasProperty(UseMeshBlockUvProperty))
                material.SetFloat(UseMeshBlockUvProperty, 1f);
        }

        private static Mesh BuildCubeMesh(int meshBlockId, BlockData block)
        {
            List<Vector3> vertices = new List<Vector3>(24);
            List<Vector3> normals = new List<Vector3>(24);
            List<Vector2> uvs = new List<Vector2>(24);
            List<Vector2> textureLayers = new List<Vector2>(24);
            List<Color32> colors = new List<Color32>(24);
            List<int> triangles = new List<int>(36);
            Color32 tint = GetMeshTint(meshBlockId, block);

            AddFace(block, 0, tint, new Vector3(-0.5f, -0.5f, -0.5f), Vector3.up, Vector3.right, vertices, normals, uvs, textureLayers, colors, triangles);
            AddFace(block, 1, tint, new Vector3(0.5f, -0.5f, 0.5f), Vector3.up, Vector3.left, vertices, normals, uvs, textureLayers, colors, triangles);
            AddFace(block, 2, tint, new Vector3(-0.5f, 0.5f, -0.5f), Vector3.forward, Vector3.right, vertices, normals, uvs, textureLayers, colors, triangles);
            AddFace(block, 3, tint, new Vector3(0.5f, -0.5f, -0.5f), Vector3.forward, Vector3.left, vertices, normals, uvs, textureLayers, colors, triangles);
            AddFace(block, 4, tint, new Vector3(-0.5f, -0.5f, 0.5f), Vector3.up, Vector3.back, vertices, normals, uvs, textureLayers, colors, triangles);
            AddFace(block, 5, tint, new Vector3(0.5f, -0.5f, -0.5f), Vector3.up, Vector3.forward, vertices, normals, uvs, textureLayers, colors, triangles);

            Mesh mesh = new Mesh
            {
                name = $"{block.name}_FallingMesh",
                vertices = vertices.ToArray(),
                normals = normals.ToArray(),
                uv = uvs.ToArray(),
                uv2 = textureLayers.ToArray(),
                colors32 = colors.ToArray(),
                triangles = triangles.ToArray()
            };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static Color32 GetMeshTint(int meshBlockId, BlockData block)
        {
            if (meshBlockId == Chunk.BLOCK_GRASS)
                return DefaultGrassTint;

            if (meshBlockId == Chunk.BLOCK_LEAVES ||
                (block != null && Chunk.IsTintedLeavesBlockName(block.name)))
            {
                return DefaultLeavesTint;
            }

            return WhiteBlockTint;
        }

        private static void AddFace(
            BlockData block,
            int face,
            Color32 tint,
            Vector3 origin,
            Vector3 up,
            Vector3 right,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Vector2> textureLayers,
            List<Color32> colors,
            List<int> triangles)
        {
            int vertexIndex = vertices.Count;
            Vector3 normal = Vector3.Cross(up, right);

            vertices.Add(origin);
            vertices.Add(origin + up);
            vertices.Add(origin + right);
            vertices.Add(origin + up + right);

            for (int i = 0; i < 4; i++)
                normals.Add(normal);

            AddFaceUvs(block.GetTexture(face), uvs, textureLayers);
            colors.Add(tint);
            colors.Add(tint);
            colors.Add(tint);
            colors.Add(tint);

            triangles.Add(vertexIndex);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 2);
            triangles.Add(vertexIndex + 1);
            triangles.Add(vertexIndex + 3);
        }

        private static void AddFaceUvs(
            BlockData.FaceTextureData textureData,
            List<Vector2> uvs,
            List<Vector2> textureLayers)
        {
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(0f, 1f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));

            Vector2 textureLayer = new Vector2(
                Mathf.Max(0, textureData.TextureLayer),
                Mathf.Max(0, textureData.OverlayTextureLayer));
            textureLayers.Add(textureLayer);
            textureLayers.Add(textureLayer);
            textureLayers.Add(textureLayer);
            textureLayers.Add(textureLayer);
        }
    }
}
