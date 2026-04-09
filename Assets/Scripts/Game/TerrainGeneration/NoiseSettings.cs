using System;
using Unity.Mathematics;
using UnityEngine;

namespace BenScr.MinecraftClone
{
    public class NoiseSettings : MonoBehaviour
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
        [Range(0.1f, 100f)] public float flatlandsHeightMultiplier = 0.65f;
        [Range(0.5f, 100f)] public float mountainHeightMultiplier = 2.5f;
        [Range(0f, 1f)] public float mountainBlendStart = 0.55f;
        [Range(0.1f, 4f)] public float mountainBlendSharpness = 2f;


        public float noiseScale = 20.0f;
        public float noiseHeight = 10.0f;

        public int waterLevel = 4;
        public int groundOffset = 10;


        public int seed;

        internal Vector2 noiseOffset;

        private Vector2 continentNoiseRuntimeOffset;
        private Vector2 mountainNoiseRuntimeOffset;
        private Vector2 detailNoiseRuntimeOffset;
        private Vector2 ridgeNoiseRuntimeOffset;
        internal Vector3 caveNoiseRuntimeOffset;

        // Cached noise layers to avoid re-creating structs per chunk
        private NoiseLayer cachedContinent, cachedMountain, cachedDetail, cachedRidge;
        private bool layersCached;

        public static NoiseSettings instance;

        private void Awake()
        {
            instance = this;
        }

        private void Start()
        {
            if (seed == 0)
                seed = (int)DateTime.Now.Ticks;

            UnityEngine.Random.InitState(seed);

            continentNoiseRuntimeOffset = GenerateOffset2D();
            mountainNoiseRuntimeOffset = GenerateOffset2D();
            detailNoiseRuntimeOffset = GenerateOffset2D();
            ridgeNoiseRuntimeOffset = GenerateOffset2D();
            noiseOffset = GenerateOffset2D();
            caveNoiseRuntimeOffset = GenerateOffset3D();

            CacheLayers();
        }

        private void CacheLayers()
        {
            cachedContinent = CreateNoiseLayer(continentNoise, continentNoiseRuntimeOffset);
            cachedMountain = CreateNoiseLayer(mountainNoise, mountainNoiseRuntimeOffset);
            cachedDetail = CreateNoiseLayer(detailNoise, detailNoiseRuntimeOffset);
            cachedRidge = CreateNoiseLayer(ridgeNoise, ridgeNoiseRuntimeOffset);
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
            if (!layersCached) CacheLayers();
            continentLayer = cachedContinent;
            mountainLayer = cachedMountain;
            detailLayer = cachedDetail;
            ridgeLayer = cachedRidge;
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

    }
}
