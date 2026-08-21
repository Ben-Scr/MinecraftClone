using Unity.Burst;
using Unity.Mathematics;

namespace BenScr.MinecraftClone
{
    public enum BiomeId : byte
    {
        Ocean = 0,
        Beach = 1,
        Plains = 2,
        Forest = 3,
        Snow = 4,
        Mountains = 5,
        Desert = 6,
        RedDesert = 7,
        Jungle = 8,
    }

    public static class TerrainNoiseUtility
    {
        public const float DesertTemperatureThreshold = 0.66f;
        public const float DesertMoistureThreshold = 0.40f;
        public const float SnowTemperatureThreshold = 0.28f;
        public const float ForestMoistureThreshold = 0.63f;
        public const float JungleTemperatureThreshold = 0.52f;
        public const float JungleMoistureThreshold = 0.66f;
        public const float MountainBiomeThreshold = 0.84f;
        // Keep red deserts as full-sized regions instead of rare islands inside the
        // ordinary desert climate band.
        public const float RedDesertRegionThreshold = 0.46f;

        [BurstCompile]
        public static float2 WarpBiomePosition(
            float2 worldPosition,
            NoiseLayer broadWarpLayer,
            NoiseLayer detailWarpLayer)
        {
            float2 broadSample = (worldPosition + broadWarpLayer.Offset + new float2(191.7f, -53.9f)) *
                math.max(0.00001f, broadWarpLayer.Frequency * 0.62f);
            float broadX = noise.snoise(broadSample);
            float broadY = noise.snoise(broadSample + new float2(43.1f, 97.7f));

            float2 detailSample = (worldPosition + detailWarpLayer.Offset + new float2(-73.4f, 181.2f)) *
                math.max(0.00001f, detailWarpLayer.Frequency * 1.85f);
            float detailX = noise.snoise(detailSample);
            float detailY = noise.snoise(detailSample + new float2(-127.6f, 38.2f));

            return worldPosition +
                   new float2(broadX, broadY) * 74f +
                   new float2(detailX, detailY) * 22f;
        }

        [BurstCompile]
        public static float GetBiomeTransitionStrength(float temperature, float moisture, float mountainMask, float transitionWidth)
        {
            float width = math.max(0.001f, transitionWidth);
            float nearestEdge = 1f;

            nearestEdge = math.min(nearestEdge, math.abs(mountainMask - MountainBiomeThreshold));
            nearestEdge = math.min(nearestEdge, math.abs(temperature - DesertTemperatureThreshold));
            nearestEdge = math.min(nearestEdge, math.abs(moisture - DesertMoistureThreshold));
            nearestEdge = math.min(nearestEdge, math.abs(temperature - SnowTemperatureThreshold));
            nearestEdge = math.min(nearestEdge, math.abs(moisture - ForestMoistureThreshold));
            nearestEdge = math.min(nearestEdge, math.abs(temperature - JungleTemperatureThreshold));
            nearestEdge = math.min(nearestEdge, math.abs(moisture - JungleMoistureThreshold));

            float strength = 1f - math.saturate(nearestEdge / width);
            return Smooth01(strength);
        }

        [BurstCompile]
        public static float GetDesertInfluence(float temperature, float moisture, float transitionWidth)
        {
            float width = math.max(0.001f, transitionWidth);
            float warm = math.saturate((temperature - (DesertTemperatureThreshold - width * 1.6f)) / (width * 2.4f));
            float dry = math.saturate(((DesertMoistureThreshold + width * 1.6f) - moisture) / (width * 2.4f));
            return Smooth01(math.min(warm, dry));
        }

        [BurstCompile]
        public static bool IsDryDesertBiome(byte biome)
        {
            return biome == (byte)BiomeId.Desert || biome == (byte)BiomeId.RedDesert;
        }

        [BurstCompile]
        public static float SampleNormalizedHeight(
            float2 worldPosition,
            NoiseLayer continentLayer,
            NoiseLayer mountainLayer,
            NoiseLayer detailLayer,
            NoiseLayer ridgeLayer,
            float flatlandsHeightMultiplier,
            float mountainHeightMultiplier,
            float mountainBlendStart,
            float mountainBlendSharpness)
        {
            float cont = Fbm01(worldPosition, continentLayer, octaves: 4, lacunarity: 2.0f, gain: 0.5f);
            cont = Redistribute01(cont, continentLayer.Redistribution);

            return SampleNormalizedHeightFromContinentalness(
                worldPosition,
                cont,
                mountainLayer,
                detailLayer,
                ridgeLayer,
                flatlandsHeightMultiplier,
                mountainHeightMultiplier,
                mountainBlendStart,
                mountainBlendSharpness);
        }

        [BurstCompile]
        public static float SampleNormalizedHeightFromContinentalness(
            float2 worldPosition,
            float redistributedContinentalness,
            NoiseLayer mountainLayer,
            NoiseLayer detailLayer,
            NoiseLayer ridgeLayer,
            float flatlandsHeightMultiplier,
            float mountainHeightMultiplier,
            float mountainBlendStart,
            float mountainBlendSharpness)
        {
            float cont = redistributedContinentalness;

            float mtn = Fbm01(worldPosition, mountainLayer, octaves: 5, lacunarity: 2.1f, gain: 0.52f);
            mtn = Redistribute01(mtn, mountainLayer.Redistribution);

            float rid = Ridged01(worldPosition, ridgeLayer, octaves: 4, lacunarity: 2.05f, gain: 0.5f);
            rid = Redistribute01(rid, ridgeLayer.Redistribution);

            float det = Fbm01(worldPosition, detailLayer, octaves: 6, lacunarity: 2.2f, gain: 0.48f);
            det = Redistribute01(det, detailLayer.Redistribution);


            float baseMask = math.saturate((cont - mountainBlendStart) * mountainBlendSharpness);
            baseMask = Smooth01(baseMask);
            float mtnMask = math.saturate(baseMask * math.lerp(0.35f, 1.0f, mtn));

            float baseHeight = cont * flatlandsHeightMultiplier;

            float mountainShape = (0.65f * mtn + 0.35f * rid);
            mountainShape = Smooth01(mountainShape);

            float mountainHeight = mountainShape * mountainHeightMultiplier * mtnMask;

            float detailAmount = math.lerp(0.06f, 0.14f, mtnMask);
            float detailHeight = (det - 0.5f) * 2.0f;
            detailHeight *= detailAmount;

            float h = baseHeight + mountainHeight + detailHeight;


            float estimatedMin = -0.20f;
            float estimatedMax = flatlandsHeightMultiplier + mountainHeightMultiplier + 0.20f;

            float h01 = math.unlerp(estimatedMin, estimatedMax, h);
            h01 = math.saturate(h01);

            h01 = Contrast01(h01, 1.10f);

            return h01;
        }

        [BurstCompile]
        public static byte SelectBiome(
            float continentalness,
            float temperature,
            float moisture,
            float mountainMask,
            float redDesertRegion,
            float oceanThreshold,
            float beachThreshold)
        {
            if (continentalness < oceanThreshold)
                return (byte)BiomeId.Ocean;

            if (continentalness < beachThreshold)
                return (byte)BiomeId.Beach;

            return SelectLandBiome(temperature, moisture, mountainMask, redDesertRegion);
        }

        [BurstCompile]
        public static byte SelectLandBiome(
            float temperature,
            float moisture,
            float mountainMask,
            float redDesertRegion)
        {
            if (temperature > DesertTemperatureThreshold && moisture < DesertMoistureThreshold)
            {
                float redDesertStrength = GetRedDesertStrength(
                    temperature,
                    moisture,
                    mountainMask,
                    redDesertRegion);

                if (redDesertStrength > 0.5f)
                    return (byte)BiomeId.RedDesert;

                return (byte)BiomeId.Desert;
            }

            if (mountainMask > MountainBiomeThreshold)
                return temperature < 0.48f ? (byte)BiomeId.Snow : (byte)BiomeId.Mountains;

            if (temperature < SnowTemperatureThreshold)
                return (byte)BiomeId.Snow;

            if (temperature > JungleTemperatureThreshold && moisture > JungleMoistureThreshold)
                return (byte)BiomeId.Jungle;

            if (moisture > ForestMoistureThreshold)
                return (byte)BiomeId.Forest;

            return (byte)BiomeId.Plains;
        }

        [BurstCompile]
        public static float GetRedDesertStrength(
            float temperature,
            float moisture,
            float mountainMask,
            float redDesertRegion)
        {
            float warm = math.saturate((temperature - DesertTemperatureThreshold) / 0.16f);
            float dry = math.saturate((DesertMoistureThreshold - moisture) / 0.16f);
            float extremeClimate = Smooth01(math.min(warm, dry));
            float reliefBoost = Smooth01(math.saturate((mountainMask - 0.18f) / 0.48f)) * 0.06f;

            // The broad region noise is the main separator so red deserts form large,
            // discoverable areas. Exceptionally hot/dry or mesa-like terrain lowers
            // the boundary without breaking the ordinary desert into tiny patches.
            float regionThreshold = RedDesertRegionThreshold - extremeClimate * 0.08f - reliefBoost;
            float region = math.saturate((redDesertRegion - (regionThreshold - 0.10f)) / 0.20f);
            return Smooth01(region);
        }

        [BurstCompile]
        public static float Fbm01(float2 p, NoiseLayer layer, int octaves, float lacunarity, float gain)
        {
            return math.saturate(FbmUnit01(p, layer, octaves, lacunarity, gain) * layer.Amplitude);
        }

        [BurstCompile]
        public static float FbmUnit01(float2 p, NoiseLayer layer, int octaves, float lacunarity, float gain)
        {
            float2 q = (p + layer.Offset) * layer.Frequency;

            float sum = 0f;
            float amp = 1f;
            float freq = 1f;
            float norm = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = noise.snoise(q * freq);
                sum += n * amp;
                norm += amp;

                freq *= lacunarity;
                amp *= gain;
            }

            float nrm = (norm > 0f) ? (sum / norm) : 0f;

            return math.saturate(nrm * 0.5f + 0.5f);
        }

        [BurstCompile]
        public static float Ridged01(float2 p, NoiseLayer layer, int octaves, float lacunarity, float gain)
        {
            return math.saturate(RidgedUnit01(p, layer, octaves, lacunarity, gain) * layer.Amplitude);
        }

        [BurstCompile]
        public static float RidgedUnit01(float2 p, NoiseLayer layer, int octaves, float lacunarity, float gain)
        {
            float2 q = (p + layer.Offset) * layer.Frequency;

            float sum = 0f;
            float amp = 1f;
            float freq = 1f;
            float norm = 0f;

            for (int i = 0; i < octaves; i++)
            {
                float n = noise.snoise(q * freq);
                n = 1f - math.abs(n);
                n = n * n;

                sum += n * amp;
                norm += amp;

                freq *= lacunarity;
                amp *= gain;
            }

            float out01 = (norm > 0f) ? (sum / norm) : 0f;
            return math.saturate(out01);
        }

        [BurstCompile]
        public static float PeaksAndValleys(float weirdness)
        {
            float value = -(math.abs(math.abs(weirdness) - 0.6666667f) - 0.33333334f) * 3f;
            return math.clamp(value, -1f, 1f);
        }

        [BurstCompile]
        public static float Redistribute01(float x01, float redistribution)
        {
            float r = math.max(0.0001f, redistribution);
            return math.pow(math.saturate(x01), r);
        }

        [BurstCompile]
        public static float Smooth01(float x) => x * x * (3f - 2f * x);

        [BurstCompile]
        public static float Contrast01(float x01, float k)
        {
            float x = math.saturate(x01);
            float a = math.pow(x, k);
            float b = math.pow(1f - x, k);
            return a / (a + b + 1e-6f);
        }
    }
}
