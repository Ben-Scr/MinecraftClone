using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace BenScr.MinecraftClone
{
    public class NoiseSettings : MonoBehaviour
    {
        private const float DefaultTemperatureScale = 2600f;
        private const float DefaultMoistureScale = 2400f;
        private const float DefaultRedDesertScale = 4200f;
        private const float DefaultErosionScale = 1800f;
        private const float DefaultLandformScale = 2400f;
        private const float DefaultCliffScale = 900f;
        private const float DefaultTreeDensityScale = 520f;
        private const float DefaultRiverScale = 680f;

        [Serializable]
        public struct NoiseLayerSettings
        {
            [FormerlySerializedAs("scale")]
            public float Scale;
            [FormerlySerializedAs("amplitude")]
            public float Amplitude;
            [FormerlySerializedAs("redistribution")]
            public float Redistribution;
            [FormerlySerializedAs("offset")]
            public Vector2 Offset;
        }

        [Serializable]
        public struct CaveNoiseSettings
        {
            [FormerlySerializedAs("scale")]
            [Min(0.0001f)] public float Scale;
            [FormerlySerializedAs("verticalScale")]
            [Min(0.0001f)] public float VerticalScale;
            [FormerlySerializedAs("threshold")]
            [Range(0f, 1f)] public float Threshold;
            [FormerlySerializedAs("offset")]
            public Vector3 Offset;
            [FormerlySerializedAs("surfaceClearance")]
            [Min(0)] public int SurfaceClearance;
            [Min(0)] public int DepthFadeDistance;

            [Header("Overworld Connections")]
            public bool EnableSurfaceConnections;
            [Min(64)] public int SurfaceConnectionCellSize;
            [Range(0f, 1f)] public float SurfaceConnectionChance;
            [Min(3)] public int SurfaceConnectionMinGroundAboveWater;
            [Range(0.45f, 0.90f)] public float SurfaceBreakthroughThreshold;
            [Min(12)] public int SurfaceBreakthroughProbeDepth;

            [Header("Tunnels")]
            [Min(0.0001f)] public float TunnelScale;
            [Min(0.0001f)] public float TunnelVerticalScale;
            [Range(0f, 0.35f)] public float TunnelWidth;
            [Min(0)] public int TunnelMinDepth;

            [Header("Rooms")]
            [Min(0.0001f)] public float RoomScale;
            [Range(0f, 1f)] public float RoomThreshold;
            [Min(0)] public int RoomMinDepth;

            [Header("Ravines")]
            [Min(16)] public int RavineCellSize;
            [Range(0f, 1f)] public float RavineChance;
            [Min(0f)] public float RavineWidth;
            [Min(0)] public int RavineMinDepth;

            [Header("Lava")]
            [Min(1)] public int LavaMinDepth;
            [Tooltip("Controls how readily deep cave regions become lava caves. Very small values are recommended; 0 disables lava caves.")]
            [Range(0f, 1f)] public float LavaChance;
            [Min(4f)] public float LavaPatchScale;

            [Header("Water Caves")]
            [Tooltip("Minimum distance below the overworld water level for a water cave's pool surface.")]
            [Min(4)] public int WaterCaveMinDepthBelowWater;
            [Tooltip("Maximum distance below the overworld water level for a water cave's pool surface.")]
            [Min(8)] public int WaterCaveMaxDepthBelowWater;
            [Tooltip("Chance for each underground region to contain a water cave. Set to 0 to disable them.")]
            [Range(0f, 1f)] public float WaterCaveChance;
            [Tooltip("Horizontal spacing and approximate size of water-cave regions.")]
            [Min(64)] public int WaterCaveRegionSize;
            [Tooltip("Maximum depth of the pool inside a water cave.")]
            [Range(2, 16)] public int WaterCaveMaxPoolDepth;
        }

        [Serializable]
        public struct LushCaveBiomeSettings
        {
            public bool Enable;
            [Tooltip("Anchors a lush cavern region below the world origin wherever the terrain leaves enough rock above bedrock.")]
            public bool GuaranteeAtWorldOrigin;
            [Min(192)] public int RegionCellSize;
            [Range(0f, 1f)] public float RegionChance;

            [Header("Cavern Shape")]
            [Min(32f)] public float MinHorizontalRadius;
            [Min(32f)] public float MaxHorizontalRadius;
            [Min(24)] public int MinDepthBelowWater;
            [Min(24)] public int MaxDepthBelowWater;
            [Min(16f)] public float MinHeight;
            [Min(16f)] public float MaxHeight;
            [Min(4)] public int SurfaceClearance;

            [Header("Underground River")]
            [Min(1.5f)] public float RiverHalfWidth;
            [Min(1)] public int RiverDepth;

            [Header("Cave Forest")]
            [Min(8)] public int TreeSpacing;
            [Range(0f, 1f)] public float TreeChance;
        }

        [Header("Cave Noise")]
        [FormerlySerializedAs("enableCaves")]
        public bool EnableCaves = true;

        [FormerlySerializedAs("caveNoise")]
        public CaveNoiseSettings CaveNoise = new CaveNoiseSettings
        {
            Scale = 48f,
            VerticalScale = 24f,
            Threshold = 0.64f,
            Offset = Vector3.zero,
            SurfaceClearance = 2,
            DepthFadeDistance = 60,
            EnableSurfaceConnections = true,
            SurfaceConnectionCellSize = 112,
            SurfaceConnectionChance = 0.24f,
            SurfaceConnectionMinGroundAboveWater = 6,
            SurfaceBreakthroughThreshold = 0.60f,
            SurfaceBreakthroughProbeDepth = 42,
            TunnelScale = 88f,
            TunnelVerticalScale = 38f,
            TunnelWidth = 0.125f,
            TunnelMinDepth = 3,
            RoomScale = 124f,
            RoomThreshold = 0.78f,
            RoomMinDepth = 10,
            RavineCellSize = 132,
            RavineChance = 0.12f,
            RavineWidth = 7f,
            RavineMinDepth = 5,
            LavaMinDepth = 72,
            LavaChance = 0.01f,
            LavaPatchScale = 64f,
            WaterCaveMinDepthBelowWater = 12,
            WaterCaveMaxDepthBelowWater = 72,
            WaterCaveChance = 0.22f,
            WaterCaveRegionSize = 136,
            WaterCaveMaxPoolDepth = 8
        };

        [Header("Lush Cave Biome")]
        public LushCaveBiomeSettings LushCaveBiome = new LushCaveBiomeSettings
        {
            Enable = true,
            GuaranteeAtWorldOrigin = true,
            RegionCellSize = 360,
            RegionChance = 0.38f,
            MinHorizontalRadius = 86f,
            MaxHorizontalRadius = 132f,
            MinDepthBelowWater = 58,
            MaxDepthBelowWater = 72,
            MinHeight = 38f,
            MaxHeight = 52f,
            SurfaceClearance = 10,
            RiverHalfWidth = 4.5f,
            RiverDepth = 4,
            TreeSpacing = 11,
            TreeChance = 0.70f
        };


        [Header("Terrain Noise Layers")]
        [FormerlySerializedAs("continentNoise")]
        public NoiseLayerSettings ContinentNoise = new NoiseLayerSettings
        {
            Scale = 900f,
            Amplitude = 1f,
            Redistribution = 1.12f,
            Offset = Vector2.zero
        };
        [Min(0.1f)] public float ContinentScaleMultiplier = 3f;
        [Range(-0.25f, 0.25f)] public float LandBias = 0.075f;
        [FormerlySerializedAs("mountainNoise")]
        public NoiseLayerSettings MountainNoise = new NoiseLayerSettings
        {
            Scale = 720f,
            Amplitude = 1f,
            Redistribution = 1.10f,
            Offset = Vector2.zero
        };
        [FormerlySerializedAs("detailNoise")]
        public NoiseLayerSettings DetailNoise = new NoiseLayerSettings
        {
            Scale = 60f,
            Amplitude = 1f,
            Redistribution = 1f,
            Offset = Vector2.zero
        };
        [FormerlySerializedAs("ridgeNoise")]
        public NoiseLayerSettings RidgeNoise = new NoiseLayerSettings
        {
            Scale = 300f,
            Amplitude = 1f,
            Redistribution = 1.3f,
            Offset = Vector2.zero
        };
        public NoiseLayerSettings LandformNoise = new NoiseLayerSettings
        {
            Scale = 2400f,
            Amplitude = 1f,
            Redistribution = 1f,
            Offset = Vector2.zero
        };
        public NoiseLayerSettings CliffNoise = new NoiseLayerSettings
        {
            Scale = 900f,
            Amplitude = 1f,
            Redistribution = 1.10f,
            Offset = Vector2.zero
        };

        [Header("Biome Noise Layers")]
        public NoiseLayerSettings TemperatureNoise = new NoiseLayerSettings
        {
            Scale = 2600f,
            Amplitude = 1f,
            Redistribution = 1f,
            Offset = Vector2.zero
        };
        public NoiseLayerSettings MoistureNoise = new NoiseLayerSettings
        {
            Scale = 2400f,
            Amplitude = 1f,
            Redistribution = 1f,
            Offset = Vector2.zero
        };
        public NoiseLayerSettings RedDesertNoise = new NoiseLayerSettings
        {
            Scale = 4200f,
            Amplitude = 1f,
            Redistribution = 0.95f,
            Offset = Vector2.zero
        };
        public NoiseLayerSettings ErosionNoise = new NoiseLayerSettings
        {
            Scale = 1800f,
            Amplitude = 1f,
            Redistribution = 1f,
            Offset = Vector2.zero
        };

        [Header("Biome Shape")]
        [Range(1, 4)] public int BiomeNoiseOctaves = 3;
        [Range(0.5f, 2.5f)] public float BiomeContrast = 1.15f;
        [Range(0.02f, 0.30f)] public float BiomeTransitionWidth = 0.08f;

        [Header("Terrain Variety")]
        [Range(0.5f, 2.5f)] public float LandformContrast = 1.12f;
        [Range(0f, 1f)] public float HillStrength = 0.65f;
        [Range(0f, 1f)] public float MountainRegionStrength = 0.90f;
        [Range(0f, 1f)] public float CliffStrength = 0.55f;
        [Min(2f)] public float CliffStepHeight = 24f;

        [Header("Mountain Types")]
        [Range(0f, 0.80f)] public float TallMountainExtraHeight = 0.34f;
        [Range(0f, 0.90f)] public float GiantMountainExtraHeight = 0.42f;
        [Range(0f, 1f)] public float MountainTypeVariation = 0.85f;
        [Range(0f, 1f)] public float PlateauMountainStrength = 0.72f;
        [Range(0f, 1f)] public float PlateauMountainFlatness = 0.82f;

        [Header("Coasts")]
        [Range(0.02f, 0.35f)] public float CoastLowlandWidth = 0.12f;
        [Range(0f, 0.25f)] public float CoastHeightScale = 0.01f;
        [Range(0f, 1f)] public float CoastMountainFade = 0.95f;

        [Header("Rivers")]
        public bool EnableRivers = true;
        public NoiseLayerSettings RiverNoise = new NoiseLayerSettings
        {
            Scale = 680f,
            Amplitude = 1f,
            Redistribution = 1f,
            Offset = Vector2.zero
        };
        [Range(0.005f, 0.20f)] public float RiverWidth = 0.045f;
        [Range(0.02f, 0.35f)] public float RiverBankWidth = 0.13f;
        [Min(1)] public int RiverDepth = 5;
        [Range(0f, 0.35f)] public float RiverMinLandDistance = 0.075f;
        [Range(0f, 1f)] public float RiverMaxMountainMask = 0.78f;

        [Header("Lakes")]
        public bool EnableLakes = true;
        [Min(64)] public int LakeCellSize = 180;
        [Range(0f, 1f)] public float LakeChance = 0.42f;
        [Min(3f)] public float LakeMinRadius = 12f;
        [Min(4f)] public float LakeMaxRadius = 56f;
        [Min(1)] public int LakeDepth = 12;
        [Min(1f)] public float LakeShoreWidth = 12f;
        [Range(0f, 0.35f)] public float LakeMinLandDistance = 0.12f;
        [Range(0f, 1f)] public float LakeMaxMountainMask = 0.46f;

        [Header("Vegetation Variety")]
        public NoiseLayerSettings TreeDensityNoise = new NoiseLayerSettings
        {
            Scale = 520f,
            Amplitude = 1f,
            Redistribution = 0.85f,
            Offset = Vector2.zero
        };
        [Range(0.5f, 2.5f)] public float TreeDensityContrast = 1.35f;

        [Header("Terrain Noise Blending")]
        [FormerlySerializedAs("flatlandsHeightMultiplier")]
        [Range(0.1f, 100f)] public float FlatlandsHeightMultiplier = 0.45f;
        [FormerlySerializedAs("mountainHeightMultiplier")]
        [Range(0.5f, 100f)] public float MountainHeightMultiplier = 2.75f;
        [FormerlySerializedAs("mountainBlendStart")]
        [Range(0f, 1f)] public float MountainBlendStart = 0.55f;
        [FormerlySerializedAs("mountainBlendSharpness")]
        [Range(0.1f, 4f)] public float MountainBlendSharpness = 1.6f;

        [Header("Biome Terrain")]
        [Range(0.05f, 0.8f)] public float OceanThreshold = 0.28f;
        [Range(0.05f, 0.9f)] public float BeachThreshold = 0.34f;
        [Min(1)] public int OceanDepth = 176;
        [Min(0)] public int MinLandAboveWater = 1;
        [Range(0f, 1f)] public float PlainsFlattening = 0.86f;

        [Header("Desert Oases")]
        public bool EnableOases = true;
        [Min(32)] public int OasisCellSize = 112;
        [Range(0f, 1f)] public float OasisChance = 0.16f;
        [Min(4)] public int OasisRadius = 14;
        [Min(2)] public int OasisWaterRadius = 5;

        [Header("Structures")]
        public bool EnableStructures = true;
        [Min(32)] public int StructureCellSize = 96;
        [Range(0f, 1f)] public float StructureChance = 0.10f;
        [Range(0f, 1f)] public float RuinStructureChance = 0.60f;

        [Header("Underground")]
        public int BedrockLevel = -256;
        [Min(1)] public int BedrockThickness = 5;

        [FormerlySerializedAs("noiseScale")]
        public float NoiseScale = 20.0f;
        [FormerlySerializedAs("noiseHeight")]
        public float NoiseHeight = 10.0f;

        [FormerlySerializedAs("waterLevel")]
        public int WaterLevel = 4;
        [FormerlySerializedAs("groundOffset")]
        public int GroundOffset = 10;


        [FormerlySerializedAs("seed")]
        public int Seed;

        internal Vector2 noiseOffset;

        private Vector2 continentNoiseRuntimeOffset;
        private Vector2 mountainNoiseRuntimeOffset;
        private Vector2 detailNoiseRuntimeOffset;
        private Vector2 ridgeNoiseRuntimeOffset;
        private Vector2 temperatureNoiseRuntimeOffset;
        private Vector2 moistureNoiseRuntimeOffset;
        private Vector2 redDesertNoiseRuntimeOffset;
        private Vector2 erosionNoiseRuntimeOffset;
        private Vector2 landformNoiseRuntimeOffset;
        private Vector2 cliffNoiseRuntimeOffset;
        private Vector2 treeDensityNoiseRuntimeOffset;
        private Vector2 riverNoiseRuntimeOffset;
        internal Vector3 caveNoiseRuntimeOffset;

        // Cached noise layers to avoid re-creating structs per chunk
        private NoiseLayer cachedContinent, cachedMountain, cachedDetail, cachedRidge;
        private NoiseLayer cachedTemperature, cachedMoisture, cachedRedDesert, cachedErosion;
        private NoiseLayer cachedLandform, cachedCliff, cachedTreeDensity;
        private NoiseLayer cachedRiver;
        private bool layersCached;
        private bool initialized;

        public static NoiseSettings Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            EnsureInitialized();
        }

        public void EnsureInitialized()
        {
            if (initialized)
                return;

            if (SaveController.WorldInfo != null)
            {
                Debug.Log("Using saved world seed: " + SaveController.WorldInfo.Seed);
                Seed = SaveController.WorldInfo.Seed;
            }
            else if (Seed == 0)
            {
                Seed = (int)DateTime.Now.Ticks;
            }

            UnityEngine.Random.InitState(Seed);

            continentNoiseRuntimeOffset = GenerateOffset2D();
            mountainNoiseRuntimeOffset = GenerateOffset2D();
            detailNoiseRuntimeOffset = GenerateOffset2D();
            ridgeNoiseRuntimeOffset = GenerateOffset2D();
            temperatureNoiseRuntimeOffset = GenerateOffset2D();
            moistureNoiseRuntimeOffset = GenerateOffset2D();
            redDesertNoiseRuntimeOffset = GenerateOffset2D();
            erosionNoiseRuntimeOffset = GenerateOffset2D();
            landformNoiseRuntimeOffset = GenerateOffset2D();
            cliffNoiseRuntimeOffset = GenerateOffset2D();
            treeDensityNoiseRuntimeOffset = GenerateOffset2D();
            riverNoiseRuntimeOffset = GenerateOffset2D();
            noiseOffset = GenerateOffset2D();
            caveNoiseRuntimeOffset = GenerateOffset3D();

            CacheLayers();
            initialized = true;
        }

        private void CacheLayers()
        {
            float continentScaleMultiplier = ContinentScaleMultiplier > 0f ? ContinentScaleMultiplier : 3f;
            cachedContinent = CreateNoiseLayer(ContinentNoise, continentNoiseRuntimeOffset, NoiseScale, continentScaleMultiplier);
            cachedMountain = CreateNoiseLayer(MountainNoise, mountainNoiseRuntimeOffset);
            cachedDetail = CreateNoiseLayer(DetailNoise, detailNoiseRuntimeOffset);
            cachedRidge = CreateNoiseLayer(RidgeNoise, ridgeNoiseRuntimeOffset);
            cachedTemperature = CreateNoiseLayer(TemperatureNoise, temperatureNoiseRuntimeOffset, DefaultTemperatureScale);
            cachedMoisture = CreateNoiseLayer(MoistureNoise, moistureNoiseRuntimeOffset, DefaultMoistureScale);
            cachedRedDesert = CreateNoiseLayer(RedDesertNoise, redDesertNoiseRuntimeOffset, DefaultRedDesertScale);
            cachedErosion = CreateNoiseLayer(ErosionNoise, erosionNoiseRuntimeOffset, DefaultErosionScale);
            cachedLandform = CreateNoiseLayer(LandformNoise, landformNoiseRuntimeOffset, DefaultLandformScale);
            cachedCliff = CreateNoiseLayer(CliffNoise, cliffNoiseRuntimeOffset, DefaultCliffScale);
            cachedTreeDensity = CreateNoiseLayer(TreeDensityNoise, treeDensityNoiseRuntimeOffset, DefaultTreeDensityScale);
            cachedRiver = CreateNoiseLayer(RiverNoise, riverNoiseRuntimeOffset, DefaultRiverScale);
            layersCached = true;
        }


        private Vector2 GenerateOffset2D()
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


        public void GetNoiseLayers(out NoiseLayer continentLayer, out NoiseLayer mountainLayer, out NoiseLayer detailLayer, out NoiseLayer ridgeLayer)
        {
            EnsureInitialized();
            if (!layersCached) CacheLayers();
            continentLayer = cachedContinent;
            mountainLayer = cachedMountain;
            detailLayer = cachedDetail;
            ridgeLayer = cachedRidge;
        }

        public void GetBiomeLayers(out NoiseLayer temperatureLayer, out NoiseLayer moistureLayer, out NoiseLayer erosionLayer)
        {
            EnsureInitialized();
            if (!layersCached) CacheLayers();
            temperatureLayer = cachedTemperature;
            moistureLayer = cachedMoisture;
            erosionLayer = cachedErosion;
        }

        public void GetRedDesertLayer(out NoiseLayer redDesertLayer)
        {
            EnsureInitialized();
            if (!layersCached) CacheLayers();
            redDesertLayer = cachedRedDesert;
        }

        public void GetTerrainVarietyLayers(out NoiseLayer landformLayer, out NoiseLayer cliffLayer, out NoiseLayer treeDensityLayer)
        {
            EnsureInitialized();
            if (!layersCached) CacheLayers();
            landformLayer = cachedLandform;
            cliffLayer = cachedCliff;
            treeDensityLayer = cachedTreeDensity;
        }

        public void GetHydrologyLayers(out NoiseLayer riverLayer)
        {
            EnsureInitialized();
            if (!layersCached) CacheLayers();
            riverLayer = cachedRiver;
        }

        NoiseLayer CreateNoiseLayer(NoiseLayerSettings settings, Vector2 runtimeOffset)
        {
            return CreateNoiseLayer(settings, runtimeOffset, NoiseScale);
        }

        NoiseLayer CreateNoiseLayer(NoiseLayerSettings settings, Vector2 runtimeOffset, float fallbackScale)
        {
            return CreateNoiseLayer(settings, runtimeOffset, fallbackScale, 1f);
        }

        NoiseLayer CreateNoiseLayer(NoiseLayerSettings settings, Vector2 runtimeOffset, float fallbackScale, float scaleMultiplier)
        {
            float scale = settings.Scale > 0f ? settings.Scale : Mathf.Max(0.0001f, fallbackScale);
            scale *= Mathf.Max(0.0001f, scaleMultiplier);

            return new NoiseLayer
            {
                Frequency = 1f / Mathf.Max(0.0001f, scale),
                Amplitude = Mathf.Max(0f, settings.Amplitude),
                Redistribution = Mathf.Max(0.0001f, settings.Redistribution),
                Offset = new float2(
                    settings.Offset.x + runtimeOffset.x + noiseOffset.x,
                    settings.Offset.y + runtimeOffset.y + noiseOffset.y)
            };
        }

    }
}
