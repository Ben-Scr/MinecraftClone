using Unity.Mathematics;

namespace BenScr.MCC
{
    public static class TerrainNoiseUtility
    {
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
            float continentValue = SampleLayer(worldPosition, continentLayer);
            float mountainValue = SampleLayer(worldPosition, mountainLayer);
            float detailValue = SampleLayer(worldPosition, detailLayer);
            float ridgeValue = SampleRidgedLayer(worldPosition, ridgeLayer);

            float mountainMask = math.saturate((mountainValue - mountainBlendStart) * mountainBlendSharpness);
            mountainMask *= mountainMask;

            float flatlands = continentValue * flatlandsHeightMultiplier;
            float mountainBase = (ridgeValue + mountainValue * mountainValue) * mountainHeightMultiplier;

            float blended = math.lerp(flatlands, mountainBase, mountainMask);
            blended += detailValue * (0.5f + 0.5f * mountainMask);

            return math.saturate(blended);
        }

        static float SampleLayer(float2 worldPosition, NoiseLayer layer)
        {
            float2 sample = (worldPosition + layer.offset) * layer.frequency;
            float noiseValue = noise.snoise(sample);
            noiseValue = noiseValue * 0.5f + 0.5f;

            if (math.abs(layer.redistribution - 1f) > 0.0001f)
            {
                noiseValue = math.pow(noiseValue, layer.redistribution);
            }

            return noiseValue * layer.amplitude;
        }

        static float SampleRidgedLayer(float2 worldPosition, NoiseLayer layer)
        {
            float2 sample = (worldPosition + layer.offset) * layer.frequency;
            float noiseValue = noise.snoise(sample);
            noiseValue = 1f - math.abs(noiseValue);
            noiseValue *= noiseValue;

            if (math.abs(layer.redistribution - 1f) > 0.0001f)
            {
                noiseValue = math.pow(noiseValue, layer.redistribution);
            }

            return noiseValue * layer.amplitude;
        }
    }
}