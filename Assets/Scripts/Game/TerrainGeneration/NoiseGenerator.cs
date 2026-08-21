// GenerateTerrainHeightMapJob.cs
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace BenScr.MinecraftClone
{
    public struct NoiseLayer
    {
        public float Frequency;
        public float Amplitude;
        public float Redistribution;
        public float2 Offset;
    }

    [BurstCompile]
    public struct GenerateTerrainHeightMapJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<int> HeightMap;
        [WriteOnly] public NativeArray<byte> BiomeMap;
        [WriteOnly] public NativeArray<byte> SurfaceBiomeMap;
        [WriteOnly] public NativeArray<byte> BiomeBlendMap;
        [WriteOnly] public NativeArray<byte> DesertEdgeMap;
        [WriteOnly] public NativeArray<byte> RiverMap;
        [WriteOnly] public NativeArray<int> RiverSurfaceMap;

        [ReadOnly] public int ChunkSize;
        [ReadOnly] public float2 ChunkOrigin;
        [ReadOnly] public float2 SampleStep;

        [ReadOnly] public NoiseLayer ContinentLayer;
        [ReadOnly] public NoiseLayer MountainLayer;
        [ReadOnly] public NoiseLayer DetailLayer;
        [ReadOnly] public NoiseLayer RidgeLayer;
        [ReadOnly] public NoiseLayer TemperatureLayer;
        [ReadOnly] public NoiseLayer MoistureLayer;
        [ReadOnly] public NoiseLayer RedDesertLayer;
        [ReadOnly] public NoiseLayer ErosionLayer;
        [ReadOnly] public NoiseLayer LandformLayer;
        [ReadOnly] public NoiseLayer CliffLayer;
        [ReadOnly] public NoiseLayer RiverLayer;

        [ReadOnly] public float FlatlandsHeightMultiplier;
        [ReadOnly] public float MountainHeightMultiplier;
        [ReadOnly] public float MountainBlendStart;
        [ReadOnly] public float MountainBlendSharpness;

        [ReadOnly] public int GroundOffset;
        [ReadOnly] public int WaterLevel;
        [ReadOnly] public int BedrockLevel;
        [ReadOnly] public int BedrockThickness;
        [ReadOnly] public int Seed;
        [ReadOnly] public int OceanDepth;
        [ReadOnly] public int MinLandAboveWater;
        [ReadOnly] public float NoiseHeight;
        [ReadOnly] public float OceanThreshold;
        [ReadOnly] public float BeachThreshold;
        [ReadOnly] public float PlainsFlattening;
        [ReadOnly] public float LandBias;
        [ReadOnly] public int BiomeNoiseOctaves;
        [ReadOnly] public float BiomeContrast;
        [ReadOnly] public float BiomeTransitionWidth;
        [ReadOnly] public float LandformContrast;
        [ReadOnly] public float HillStrength;
        [ReadOnly] public float MountainRegionStrength;
        [ReadOnly] public float CliffStrength;
        [ReadOnly] public float CliffStepHeight;
        [ReadOnly] public float TallMountainExtraHeight;
        [ReadOnly] public float GiantMountainExtraHeight;
        [ReadOnly] public float MountainTypeVariation;
        [ReadOnly] public float PlateauMountainStrength;
        [ReadOnly] public float PlateauMountainFlatness;
        [ReadOnly] public float CoastLowlandWidth;
        [ReadOnly] public float CoastHeightScale;
        [ReadOnly] public float CoastMountainFade;
        [ReadOnly] public bool EnableRivers;
        [ReadOnly] public float RiverWidth;
        [ReadOnly] public float RiverBankWidth;
        [ReadOnly] public int RiverDepth;
        [ReadOnly] public float RiverMinLandDistance;
        [ReadOnly] public float RiverMaxMountainMask;
        [ReadOnly] public bool EnableLakes;
        [ReadOnly] public int LakeCellSize;
        [ReadOnly] public float LakeChance;
        [ReadOnly] public float LakeMinRadius;
        [ReadOnly] public float LakeMaxRadius;
        [ReadOnly] public int LakeDepth;
        [ReadOnly] public float LakeShoreWidth;
        [ReadOnly] public float LakeMinLandDistance;
        [ReadOnly] public float LakeMaxMountainMask;

        public void Execute(int index)
        {
            int x = index % ChunkSize;
            int z = index / ChunkSize;

            float sampleStepX = SampleStep.x > 0f ? SampleStep.x : 1f;
            float sampleStepZ = SampleStep.y > 0f ? SampleStep.y : 1f;
            float2 worldPosition = ChunkOrigin + new float2(x * sampleStepX, z * sampleStepZ);

            float continentalness = TerrainNoiseUtility.FbmUnit01(worldPosition, ContinentLayer, 4, 2.0f, 0.5f);
            continentalness = TerrainNoiseUtility.Redistribute01(continentalness, ContinentLayer.Redistribution);
            continentalness = math.saturate(continentalness + LandBias);

            int biomeOctaves = math.clamp(BiomeNoiseOctaves, 1, 4);
            float biomeContrast = math.max(0.5f, BiomeContrast);
            float2 biomePosition = TerrainNoiseUtility.WarpBiomePosition(worldPosition, ErosionLayer, DetailLayer);

            float temperature = TerrainNoiseUtility.FbmUnit01(biomePosition, TemperatureLayer, biomeOctaves, 2.0f, 0.45f);
            temperature = TerrainNoiseUtility.Redistribute01(temperature, TemperatureLayer.Redistribution);
            temperature = TerrainNoiseUtility.Contrast01(temperature, biomeContrast);

            float moisture = TerrainNoiseUtility.FbmUnit01(biomePosition, MoistureLayer, biomeOctaves, 2.0f, 0.45f);
            moisture = TerrainNoiseUtility.Redistribute01(moisture, MoistureLayer.Redistribution);
            moisture = TerrainNoiseUtility.Contrast01(moisture, biomeContrast);

            float redDesertRegion = TerrainNoiseUtility.FbmUnit01(
                biomePosition + new float2(573.7f, -811.4f),
                RedDesertLayer,
                math.max(1, biomeOctaves - 1),
                1.85f,
                0.52f);
            redDesertRegion = TerrainNoiseUtility.Redistribute01(redDesertRegion, RedDesertLayer.Redistribution);
            redDesertRegion = TerrainNoiseUtility.Contrast01(redDesertRegion, math.lerp(0.88f, 1.24f, math.saturate(biomeContrast - 0.5f)));

            float erosion = TerrainNoiseUtility.FbmUnit01(worldPosition, ErosionLayer, biomeOctaves, 2.0f, 0.5f);
            erosion = TerrainNoiseUtility.Redistribute01(erosion, ErosionLayer.Redistribution);

            float landform = TerrainNoiseUtility.FbmUnit01(worldPosition, LandformLayer, 3, 2.0f, 0.48f);
            landform = TerrainNoiseUtility.Redistribute01(landform, LandformLayer.Redistribution);
            landform = TerrainNoiseUtility.Contrast01(landform, math.max(0.5f, LandformContrast));

            float cliffNoise = TerrainNoiseUtility.RidgedUnit01(worldPosition, CliffLayer, 4, 2.05f, 0.50f);
            cliffNoise = TerrainNoiseUtility.Redistribute01(cliffNoise, CliffLayer.Redistribution);

            float weirdnessUnit = TerrainNoiseUtility.FbmUnit01(
                worldPosition + new float2(391.7f, -827.3f),
                MountainLayer,
                3,
                1.92f,
                0.52f);
            float peaksAndValleys = TerrainNoiseUtility.PeaksAndValleys(weirdnessUnit * 2f - 1f);
            float peakStrength = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(-0.15f, 0.78f, peaksAndValleys)));
            float ruggedness = 1f - TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.35f, 0.72f, erosion)));
            float provinceSignal = math.saturate(landform * 0.72f + ruggedness * 0.28f);
            float provinceStart = math.clamp(MountainBlendStart - 0.06f, 0.42f, 0.68f);
            float provinceWidth = math.lerp(0.28f, 0.14f, math.saturate(MountainBlendSharpness / 4f));
            float mountainProvince = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(
                provinceStart,
                provinceStart + provinceWidth,
                provinceSignal)));
            mountainProvince *= math.saturate(MountainRegionStrength);

            float oceanThreshold = math.min(OceanThreshold, BeachThreshold - 0.001f);
            float beachThreshold = math.max(BeachThreshold, oceanThreshold + 0.001f);
            float broadCoastWarp = TerrainNoiseUtility.FbmUnit01(
                worldPosition + new float2(-1186.3f, 421.7f),
                ErosionLayer,
                2,
                1.90f,
                0.55f);
            float fineCoastWarp = TerrainNoiseUtility.FbmUnit01(
                worldPosition + new float2(706.9f, -982.4f),
                DetailLayer,
                3,
                2.15f,
                0.52f);
            float coastlineOffset = (broadCoastWarp - 0.5f) * 0.085f +
                                    (fineCoastWarp - 0.5f) * 0.050f;
            float coastContinentalness = math.saturate(continentalness + coastlineOffset);
            float mountainInlandGate = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(
                beachThreshold + 0.08f,
                beachThreshold + 0.34f,
                coastContinentalness)));
            mountainProvince *= mountainInlandGate;
            float majorMountainMask = math.pow(math.saturate(mountainProvince * ruggedness), 1.35f) *
                                      math.pow(peakStrength, 1.5f);
            float mountainMask = math.saturate(mountainProvince * 0.58f + majorMountainMask * 0.82f);
            mountainMask = TerrainNoiseUtility.Smooth01(mountainMask);
            byte biome = TerrainNoiseUtility.SelectBiome(
                coastContinentalness,
                temperature,
                moisture,
                mountainMask,
                redDesertRegion,
                oceanThreshold,
                beachThreshold);
            byte landSurfaceBiome = TerrainNoiseUtility.SelectLandBiome(temperature, moisture, mountainMask, redDesertRegion);
            bool coldSurface = !TerrainNoiseUtility.IsDryDesertBiome(landSurfaceBiome) &&
                               temperature < TerrainNoiseUtility.SnowTemperatureThreshold + 0.055f;
            if (coldSurface)
                landSurfaceBiome = (byte)BiomeId.Snow;

            byte surfaceBiome = biome == (byte)BiomeId.Ocean || biome == (byte)BiomeId.Beach
                ? landSurfaceBiome
                : (coldSurface && !TerrainNoiseUtility.IsDryDesertBiome(biome) ? (byte)BiomeId.Snow : biome);

            int seaLevel = GroundOffset + WaterLevel;
            float detail = TerrainNoiseUtility.FbmUnit01(worldPosition + new float2(173.17f, -91.83f), DetailLayer, 3, 2.0f, 0.5f);
            float detailSigned = (detail - 0.5f) * 2.0f;

            float ground = seaLevel;
            float configuredMinLand = math.max(0f, MinLandAboveWater);
            float minLand = seaLevel + configuredMinLand;
            float riverStrength = 0f;
            float riverWaterStrength = 0f;
            float surfaceWaterDepthLimit = 0f;
            int riverSurfaceLevel = int.MinValue;

            if (biome == (byte)BiomeId.Ocean)
            {
                float deepRaw = 1.0f - math.saturate(coastContinentalness / math.max(0.0001f, oceanThreshold));
                float deep01 = TerrainNoiseUtility.Smooth01(deepRaw);
                float shelf01 = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.05f, 0.34f, deep01)));
                float abyss01 = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.42f, 0.92f, deep01)));
                float oceanDepth = math.max(1f, OceanDepth);

                float oceanFloorNoise = TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(-431.8f, 1189.6f),
                    ErosionLayer,
                    3,
                    1.85f,
                    0.56f);
                float basinNoise = TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(1394.2f, -822.7f),
                    LandformLayer,
                    3,
                    1.82f,
                    0.54f);
                float ridgeNoise = TerrainNoiseUtility.RidgedUnit01(
                    worldPosition + new float2(-1741.6f, -118.3f),
                    RidgeLayer,
                    4,
                    2.05f,
                    0.50f);
                float seamountNoise = TerrainNoiseUtility.RidgedUnit01(
                    worldPosition + new float2(812.9f, 1644.1f),
                    MountainLayer,
                    3,
                    2.0f,
                    0.48f);

                float basinDrop = oceanDepth * (
                    shelf01 * 0.28f +
                    deep01 * 0.70f +
                    abyss01 * basinNoise * 0.32f);

                float ridgeMask = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.52f, 0.86f, ridgeNoise))) * deep01;
                float ridgeLift = ridgeMask * oceanDepth * math.lerp(0.06f, 0.18f, basinNoise);

                float seamountMask = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.70f, 0.95f, seamountNoise))) * deep01;
                seamountMask *= 1.0f - ridgeMask * 0.45f;
                float seamountLift = seamountMask * oceanDepth * math.lerp(0.10f, 0.30f, oceanFloorNoise);

                float trenchLine = math.abs(noise.snoise((worldPosition + new float2(-2632.4f, 713.8f)) * 0.0065f));
                trenchLine = math.abs(trenchLine + (oceanFloorNoise - 0.5f) * 0.11f);
                float trenchMask = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.17f, 0.035f, trenchLine))) * abyss01;
                float trenchDrop = trenchMask * math.lerp(12f, oceanDepth * 0.34f, basinNoise);

                float floorRelief = (oceanFloorNoise - 0.5f) * math.lerp(3.0f, 13.0f, deep01);
                floorRelief += detailSigned * math.lerp(0.85f, 4.6f, deep01);
                floorRelief += (basinNoise - 0.5f) * math.lerp(2.0f, 11.5f, abyss01);

                ground = seaLevel - 2.0f - basinDrop + floorRelief + ridgeLift + seamountLift - trenchDrop;
                ground = math.min(ground, seaLevel - 3.0f - deep01 * 2.0f);

                float configuredDeepest = seaLevel - oceanDepth - math.lerp(10f, 26f, abyss01);
                float bedrockClearance = BedrockLevel + math.max(1, BedrockThickness) + 3f;
                float deepestAllowed = math.max(configuredDeepest, bedrockClearance);
                ground = math.max(ground, deepestAllowed);
            }
            else if (biome == (byte)BiomeId.Beach)
            {
                float beachHeightNoise = TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(156.9f, -1327.5f),
                    DetailLayer,
                    3,
                    2.12f,
                    0.52f);
                float beachLocalWarp = (beachHeightNoise - 0.5f) * 0.10f;
                float shore01 = math.unlerp(oceanThreshold, beachThreshold, math.saturate(coastContinentalness + beachLocalWarp));
                shore01 = TerrainNoiseUtility.Smooth01(math.saturate(shore01));
                float beachRelief = (broadCoastWarp - 0.5f) * 1.25f +
                                    (fineCoastWarp - 0.5f) * 1.65f;
                float beachHigh = seaLevel + math.lerp(0.8f, 2.6f, beachHeightNoise);
                ground = math.lerp(seaLevel - 1.2f, beachHigh, shore01);
                ground += detailSigned * 0.90f + beachRelief * math.lerp(0.95f, 0.45f, shore01);
            }
            else
            {
                float inland01 = math.unlerp(beachThreshold, 1.0f, coastContinentalness);
                inland01 = TerrainNoiseUtility.Smooth01(math.saturate(inland01));
                float coastWidth = math.max(0.001f, CoastLowlandWidth > 0f ? CoastLowlandWidth : 0.24f);
                float coastRampWarp = (TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(1421.6f, -617.3f),
                    ErosionLayer,
                    2,
                    1.85f,
                    0.55f) - 0.5f) * 0.125f;
                coastRampWarp += (TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(-914.2f, 1276.5f),
                    DetailLayer,
                    2,
                    2.20f,
                    0.52f) - 0.5f) * 0.075f;
                float coastHeightContinentalness = math.saturate(coastContinentalness + coastRampWarp);
                float coast01 = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(beachThreshold, beachThreshold + coastWidth, coastHeightContinentalness)));
                float coastProximity = 1f - coast01;

                float hillNoise = TerrainNoiseUtility.FbmUnit01(worldPosition + new float2(319.6f, -203.8f), MountainLayer, 4, 2.05f, 0.50f);
                float hillShape = TerrainNoiseUtility.Smooth01(hillNoise);
                float lowlandRipple = TerrainNoiseUtility.FbmUnit01(worldPosition + new float2(-581.2f, 903.5f), DetailLayer, 2, 2.0f, 0.52f);
                float broadRipple = TerrainNoiseUtility.FbmUnit01(worldPosition + new float2(246.8f, -744.1f), ErosionLayer, 2, 1.85f, 0.56f);
                float naturalRelief = (lowlandRipple - 0.5f) * math.min(4.5f, NoiseHeight * 0.0045f);
                naturalRelief += (broadRipple - 0.5f) * math.min(7.0f, NoiseHeight * 0.0070f);
                float coastFloor01 = TerrainNoiseUtility.Smooth01(coast01);
                float coastPatch = TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(-1727.3f, 392.6f),
                    DetailLayer,
                    3,
                    2.10f,
                    0.52f);
                float coastPatchSigned = (coastPatch - 0.5f) * 2.0f;
                float coastRelief = (broadCoastWarp - 0.5f) * 2.40f +
                                    (fineCoastWarp - 0.5f) * 1.60f +
                                    naturalRelief * 0.45f +
                                    coastPatchSigned * 1.35f * coastProximity;
                float coastalFloor = seaLevel + math.lerp(0.35f, configuredMinLand + 0.75f, coastFloor01);
                coastalFloor += coastRelief * math.lerp(0.85f, 0.45f, coastFloor01);
                coastalFloor = math.max(seaLevel + 0.05f, coastalFloor);

                // Continentalness sets only a modest inland baseline. Broad erosion and
                // landform fields decide whether that baseline stays flat, becomes hilly,
                // or enters a separate mountain province.
                float flatlandReliefScale = math.min(
                    48f,
                    math.max(4f, NoiseHeight * 0.030f * math.max(0.1f, FlatlandsHeightMultiplier)));
                float continentalRise = math.lerp(2f, flatlandReliefScale * 1.65f, inland01);
                float flatGround = minLand + continentalRise;
                flatGround += naturalRelief * 0.55f + detailSigned * 1.25f;

                float flatlandProtection = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.44f, 0.72f, erosion)));
                flatlandProtection *= (1f - mountainProvince) * math.saturate(PlainsFlattening);
                float hillWeight = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.52f, 0.70f, landform)));
                hillWeight *= (1f - mountainProvince * 0.90f) * (1f - flatlandProtection * 0.85f);
                float rollingWeight = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.68f, 0.86f, landform)));
                rollingWeight *= (1f - mountainProvince * 0.78f) * (1f - flatlandProtection);

                float smallHillGround = flatGround;
                smallHillGround += hillShape * NoiseHeight * 0.025f * math.saturate(HillStrength);
                smallHillGround += detailSigned * 1.8f;

                float rollingGround = flatGround;
                rollingGround += hillShape * NoiseHeight * 0.055f * math.saturate(HillStrength);
                rollingGround += (0.5f - erosion) * NoiseHeight * 0.010f + detailSigned * 3.2f;

                float lowlandGround = math.lerp(flatGround, smallHillGround, hillWeight);
                lowlandGround = math.lerp(lowlandGround, rollingGround, rollingWeight);

                // Climate selects surface character, not the macro elevation. Deserts add
                // dunes and rare mesas without forcing every desert or snow biome uphill.
                float dune = TerrainNoiseUtility.FbmUnit01(worldPosition + new float2(-233.4f, 157.8f), DetailLayer, 3, 2.1f, 0.55f);
                float duneShape = math.abs((dune - 0.5f) * 2.0f);
                float desertGround = lowlandGround + duneShape * math.min(9f, NoiseHeight * 0.009f) + detailSigned * 0.55f;
                bool redDesertBiome = biome == (byte)BiomeId.RedDesert;
                float redMesaMask = 0f;
                float redDesertGround = desertGround;
                if (redDesertBiome)
                {
                    // Broad low-frequency fields create mesa provinces and carved
                    // valleys. Quantizing only the lift gives the large horizontal
                    // shelves of a layered badlands biome without making its floor
                    // look like a field of small pyramids.
                    float redMesaMass = TerrainNoiseUtility.FbmUnit01(
                        worldPosition + new float2(684.8f, -1339.2f),
                        LandformLayer,
                        3,
                        1.72f,
                        0.52f);
                    float redPlateauNoise = TerrainNoiseUtility.FbmUnit01(
                        worldPosition + new float2(-928.4f, 714.6f),
                        LandformLayer,
                        2,
                        1.65f,
                        0.56f);
                    float canyonField = TerrainNoiseUtility.RidgedUnit01(
                        worldPosition + new float2(1217.3f, 386.2f),
                        RidgeLayer,
                        3,
                        1.86f,
                        0.52f);

                    redMesaMask = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.38f, 0.68f, redMesaMass)));
                    float plateau01 = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.34f, 0.78f, redPlateauNoise)));
                    float terraceCount = 7f;
                    float terracedLift = math.floor(plateau01 * terraceCount + 0.35f) / terraceCount;
                    float canyonCut = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.73f, 0.91f, canyonField)));
                    float mesaLift = redMesaMask * terracedLift * NoiseHeight * 0.145f;
                    mesaLift -= canyonCut * redMesaMask * NoiseHeight * 0.045f;
                    mesaLift *= 1f - mountainProvince * 0.50f;
                    redDesertGround = lowlandGround + mesaLift;
                    redDesertGround += duneShape * math.min(5f, NoiseHeight * 0.005f) + naturalRelief * 0.35f;
                }

                float desertWeight = math.min(
                    math.unlerp(0.54f, 0.72f, temperature),
                    math.unlerp(0.54f, 0.34f, moisture));
                desertWeight = TerrainNoiseUtility.Smooth01(math.saturate(desertWeight));
                lowlandGround = math.lerp(lowlandGround, desertGround, desertWeight * 0.72f);
                if (redDesertBiome)
                    lowlandGround = math.lerp(lowlandGround, redDesertGround, math.saturate(0.72f + redMesaMask * 0.28f));

                float mountainCoastGate = math.lerp(
                    1f - math.saturate(CoastMountainFade),
                    1f,
                    coast01);
                float routedProvince = mountainProvince * mountainCoastGate;
                float routedMajorMask = majorMountainMask * mountainCoastGate;
                float mountainWeight = math.saturate(routedProvince + routedMajorMask);

                float mountainHeightScale = math.clamp(MountainHeightMultiplier / 2.75f, 0.35f, 1.80f);
                float tallHeightScale = math.lerp(0.72f, 1.28f, math.saturate(TallMountainExtraHeight));
                float foothillLift = routedProvince * (0.025f + 0.045f * peakStrength) * NoiseHeight;
                float majorLift = routedMajorMask * 0.20f * NoiseHeight * mountainHeightScale * tallHeightScale;

                float extremeRidge = TerrainNoiseUtility.RidgedUnit01(
                    worldPosition + new float2(877.3f, 214.9f),
                    RidgeLayer,
                    4,
                    2.05f,
                    0.50f);
                float extremeSummitMask = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.74f, 0.92f, extremeRidge)));
                extremeSummitMask *= routedMajorMask * math.saturate(MountainTypeVariation);
                float giantHeightScale = math.lerp(0.70f, 1.35f, math.saturate(GiantMountainExtraHeight));
                float extremeLift = extremeSummitMask * 0.16f * NoiseHeight * giantHeightScale;

                float valleyMask = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.05f, -0.65f, peaksAndValleys)));
                valleyMask *= routedProvince * math.lerp(0.35f, 1f, ruggedness);
                float valleyCut = valleyMask * NoiseHeight * 0.035f;
                ground = lowlandGround + foothillLift + majorLift + extremeLift - valleyCut;
                ground += detailSigned * math.lerp(1.2f, 5.0f, mountainWeight);

                // Broad, less-eroded massifs occasionally form buildable high shoulders.
                float plateauShape = TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(-1241.8f, 629.4f),
                    LandformLayer,
                    2,
                    1.75f,
                    0.55f);
                float plateauMask = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.72f, 0.90f, plateauShape)));
                plateauMask *= routedProvince * (1f - ruggedness * 0.55f) * math.saturate(PlateauMountainStrength);
                if (plateauMask > 0.001f)
                {
                    float plateauTarget = lowlandGround + foothillLift + NoiseHeight * math.lerp(0.075f, 0.14f, plateauShape);
                    plateauTarget += detailSigned * math.lerp(1.5f, 0.35f, math.saturate(PlateauMountainFlatness));
                    float plateauBlend = plateauMask * math.saturate(PlateauMountainFlatness) * 0.62f;
                    ground = math.lerp(ground, math.max(ground, plateauTarget), plateauBlend);
                }

                float shoreContourBreakup = (TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(527.4f, 1498.2f),
                    DetailLayer,
                    3,
                    2.18f,
                    0.50f) - 0.5f) * 2.0f;
                shoreContourBreakup += (TerrainNoiseUtility.FbmUnit01(
                    worldPosition + new float2(-1194.7f, -358.6f),
                    ErosionLayer,
                    2,
                    1.92f,
                    0.55f) - 0.5f) * 1.35f;
                shoreContourBreakup += noise.snoise((worldPosition + new float2(641.8f, -1066.1f)) * 0.18f) * 0.75f;

                float coastGround = coastalFloor;
                coastGround += coast01 * NoiseHeight * math.max(0f, CoastHeightScale > 0f ? CoastHeightScale : 0.04f) * math.lerp(0.35f, 1.0f, coastPatch);
                coastGround += detailSigned * math.lerp(0.35f, 0.85f, coast01);
                coastGround += shoreContourBreakup * math.lerp(1.25f, 0.15f, coast01);
                float coastInfluence = coastProximity * coastProximity;
                coastInfluence *= math.lerp(1f, 0.55f, mountainWeight);
                ground = math.lerp(ground, coastGround, coastInfluence);

                // Cliffs are local mountain accents, not absolute-Y terraces. Avoiding
                // floor(height / step) removes the artificial horizontal bands.
                float cliffWeight = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.76f, 0.94f, cliffNoise)));
                cliffWeight *= routedMajorMask * math.saturate(CliffStrength) * coast01;
                ground += cliffWeight * NoiseHeight * 0.025f * math.lerp(0.35f, 1f, peakStrength);

                riverStrength = GetRiverStrength(
                    worldPosition,
                    coastContinentalness,
                    mountainMask,
                    oceanThreshold,
                    beachThreshold,
                    out riverWaterStrength);

                float lakeStrength = GetLakeStrength(
                    worldPosition,
                    biome,
                    coastContinentalness,
                    mountainMask,
                    mountainWeight,
                    landform,
                    cliffNoise,
                    beachThreshold,
                    out float lakeWaterStrength,
                    out float lakeDepthMultiplier);

                if (riverStrength > 0.001f)
                {
                    float preRiverGround = ground;
                    float riverSurface = preRiverGround - math.lerp(1.25f, 3.75f, riverWaterStrength);
                    float bankTarget = preRiverGround - math.lerp(0.20f, 1.85f, riverStrength);
                    float riverBankInfluence = riverStrength * math.lerp(0.035f, 0.30f, riverWaterStrength);
                    ground = math.lerp(ground, bankTarget + detailSigned * 0.18f, riverBankInfluence);

                    float riverBed = riverSurface - math.lerp(1.0f, math.max(1f, RiverDepth), riverWaterStrength);
                    ground = math.lerp(ground, riverBed, riverWaterStrength);

                    if (riverWaterStrength > 0.001f)
                    {
                        riverSurfaceLevel = (int)math.floor(riverSurface);
                        surfaceWaterDepthLimit = math.max(surfaceWaterDepthLimit, math.max(1f, RiverDepth));
                    }
                }

                if (lakeStrength > 0.001f)
                {
                    float preLakeGround = ground;
                    float lakeSurface = preLakeGround - math.lerp(0.75f, 2.25f, lakeWaterStrength);
                    float lakeBank = lakeSurface + math.lerp(0.75f, 2.5f, 1.0f - lakeWaterStrength);
                    float lakeBankInfluence = lakeStrength * math.lerp(0.06f, 0.42f, lakeWaterStrength);
                    ground = math.lerp(ground, math.min(ground, lakeBank + detailSigned * 0.10f + shoreContourBreakup * 0.20f), lakeBankInfluence);

                    float lakeBedRelief = (TerrainNoiseUtility.FbmUnit01(
                        worldPosition + new float2(-634.8f, 291.7f),
                        DetailLayer,
                        3,
                        2.12f,
                        0.52f) - 0.5f) * math.lerp(0.45f, 3.25f, lakeWaterStrength);
                    lakeBedRelief += noise.snoise((worldPosition + new float2(173.2f, -911.4f)) * 0.115f) * math.lerp(0.10f, 1.35f, lakeWaterStrength);
                    float lakeBed = lakeSurface - math.lerp(1.0f, math.max(1f, LakeDepth) * lakeDepthMultiplier, lakeWaterStrength);
                    lakeBed += lakeBedRelief;
                    ground = math.lerp(ground, lakeBed, lakeWaterStrength);

                    if (lakeWaterStrength > riverWaterStrength && lakeWaterStrength > 0.001f)
                    {
                        riverWaterStrength = lakeWaterStrength;
                        riverSurfaceLevel = (int)math.floor(lakeSurface);
                    }

                    if (lakeWaterStrength > 0.001f)
                        surfaceWaterDepthLimit = math.max(surfaceWaterDepthLimit, math.max(1f, LakeDepth) * lakeDepthMultiplier);

                    riverStrength = math.max(riverStrength, lakeStrength);
                }

                if (riverWaterStrength > 0.001f)
                {
                    ground = math.min(ground, riverSurfaceLevel - 1.0f);
                    ground = math.max(ground, riverSurfaceLevel - math.max(1f, surfaceWaterDepthLimit) - 1.0f);
                }
                else
                {
                    float dryFloor = seaLevel + 0.05f;
                    float coastFloorClamp = coastalFloor - 0.90f + shoreContourBreakup * 0.35f;
                    float clampInfluence = coastProximity * coastProximity;
                    float localClamp = math.lerp(dryFloor, coastFloorClamp, clampInfluence);
                    ground = math.max(ground, math.max(dryFloor, localClamp));
                }
            }

            int groundLevel = (int)math.floor(ground);
            float transitionStrength = TerrainNoiseUtility.GetBiomeTransitionStrength(
                temperature,
                moisture,
                mountainMask,
                BiomeTransitionWidth);
            float desertEdgeStrength = TerrainNoiseUtility.GetDesertInfluence(
                temperature,
                moisture,
                BiomeTransitionWidth * 0.65f) * math.saturate((transitionStrength - 0.35f) * 1.85f);

            HeightMap[index] = groundLevel;
            if (BiomeMap.IsCreated)
                BiomeMap[index] = biome;
            if (SurfaceBiomeMap.IsCreated)
                SurfaceBiomeMap[index] = surfaceBiome;
            if (BiomeBlendMap.IsCreated)
                BiomeBlendMap[index] = (byte)math.round(transitionStrength * 255f);
            if (DesertEdgeMap.IsCreated)
                DesertEdgeMap[index] = (byte)math.round(math.saturate(desertEdgeStrength) * 255f);
            if (RiverMap.IsCreated)
                RiverMap[index] = (byte)math.round(math.saturate(riverStrength) * 255f);
            if (RiverSurfaceMap.IsCreated)
                RiverSurfaceMap[index] = riverSurfaceLevel;
        }

        private float GetRiverStrength(
            float2 worldPosition,
            float continentalness,
            float mountainMask,
            float oceanThreshold,
            float beachThreshold,
            out float waterStrength)
        {
            waterStrength = 0f;

            if (!EnableRivers)
                return 0f;

            float riverWidth = math.max(0.001f, RiverWidth > 0f ? RiverWidth : 0.045f);
            float riverBankWidth = math.max(riverWidth + 0.001f, RiverBankWidth > 0f ? RiverBankWidth : 0.13f);
            float minLandDistance = RiverMinLandDistance > 0f ? RiverMinLandDistance : 0.075f;
            float maxMountainMask = RiverMaxMountainMask > 0f ? RiverMaxMountainMask : 0.78f;

            float landGate = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(
                beachThreshold + minLandDistance,
                beachThreshold + minLandDistance + 0.16f,
                continentalness)));
            float mountainGate = 1f - TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(
                maxMountainMask - 0.16f,
                maxMountainMask,
                mountainMask)));

            if (landGate <= 0f || mountainGate <= 0f)
                return 0f;

            float widthNoise = TerrainNoiseUtility.FbmUnit01(worldPosition + new float2(611.3f, -274.9f), RiverLayer, 2, 2.0f, 0.45f);
            widthNoise = TerrainNoiseUtility.Smooth01(widthNoise);
            riverWidth *= math.lerp(0.55f, 1.85f, widthNoise);
            riverBankWidth *= math.lerp(0.75f, 2.15f, widthNoise);

            float2 riverSample = (worldPosition + RiverLayer.Offset) * RiverLayer.Frequency;
            float riverLine = math.abs(noise.snoise(riverSample));
            float meander = TerrainNoiseUtility.FbmUnit01(worldPosition + new float2(-918.2f, 431.7f), RiverLayer, 2, 2.4f, 0.45f);
            riverLine = math.abs(riverLine + (meander - 0.5f) * 0.11f);

            float bankStrength = 1f - math.saturate(riverLine / riverBankWidth);
            waterStrength = 1f - math.saturate(riverLine / riverWidth);

            bankStrength = TerrainNoiseUtility.Smooth01(bankStrength) * landGate * mountainGate;
            waterStrength = TerrainNoiseUtility.Smooth01(waterStrength) * landGate * mountainGate;

            return math.max(bankStrength, waterStrength);
        }

        private float GetLakeStrength(
            float2 worldPosition,
            byte biome,
            float continentalness,
            float mountainMask,
            float mountainWeight,
            float landform,
            float cliffNoise,
            float beachThreshold,
            out float waterStrength,
            out float depthMultiplier)
        {
            waterStrength = 0f;
            depthMultiplier = 1f;

            if (!EnableLakes)
                return 0f;

            bool dryDesertBiome = TerrainNoiseUtility.IsDryDesertBiome(biome);
            float chance = math.saturate(LakeChance > 0f ? LakeChance : 0.34f);
            chance = math.saturate(chance * (dryDesertBiome ? 1.18f : 1f));
            if (chance <= 0f)
                return 0f;

            float minLandDistance = LakeMinLandDistance > 0f ? LakeMinLandDistance : 0.12f;
            float maxMountainMask = LakeMaxMountainMask > 0f ? LakeMaxMountainMask : 0.46f;
            if (dryDesertBiome)
                maxMountainMask = math.max(maxMountainMask, 0.54f);
            if (continentalness < beachThreshold + minLandDistance || mountainMask > maxMountainMask)
                return 0f;

            float mountainSlopeGate = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.34f, 0.62f, mountainWeight)));
            float cliffSlopeGate = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.58f, 0.78f, cliffNoise)));
            float highLandformGate = TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.76f, 0.91f, landform))) *
                                     TerrainNoiseUtility.Smooth01(math.saturate(math.unlerp(0.28f, 0.52f, mountainMask)));
            if (math.max(mountainSlopeGate, math.max(cliffSlopeGate, highLandformGate)) > 0.45f)
                return 0f;

            int cellSize = math.max(64, LakeCellSize > 0 ? LakeCellSize : 180);
            int cellX = FastFloorToInt(worldPosition.x / cellSize);
            int cellZ = FastFloorToInt(worldPosition.y / cellSize);

            float bestStrength = 0f;
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
                {
                    int candidateCellX = cellX + offsetX;
                    int candidateCellZ = cellZ + offsetZ;

                    if (Hash01(Hash(candidateCellX, candidateCellZ, 0x1A6E, Seed)) > chance)
                        continue;

                    float minRadius = math.max(3f, LakeMinRadius > 0f ? LakeMinRadius : 12f);
                    float maxRadius = math.max(minRadius + 1f, LakeMaxRadius > 0f ? LakeMaxRadius : 38f);
                    float maxAllowedRadius = math.min(maxRadius, cellSize * 0.44f);
                    float radius = math.lerp(minRadius, maxAllowedRadius, Hash01(Hash(candidateCellX, candidateCellZ, 0x93CD, Seed)));
                    radius *= dryDesertBiome ? math.lerp(0.82f, 1.18f, Hash01(Hash(candidateCellX, candidateCellZ, 0xD0E5, Seed))) : 1f;
                    float shoreWidth = math.max(1f, LakeShoreWidth > 0f ? LakeShoreWidth : 8f);
                    shoreWidth *= math.lerp(0.85f, 1.55f, Hash01(Hash(candidateCellX, candidateCellZ, 0xB02A, Seed)));
                    float margin = math.max(radius + shoreWidth + 2f, cellSize * 0.18f);

                    float centerX = candidateCellX * cellSize + math.lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0xB7C1, Seed)));
                    float centerZ = candidateCellZ * cellSize + math.lerp(margin, cellSize - margin, Hash01(Hash(candidateCellX, candidateCellZ, 0x2F31, Seed)));

                    float2 delta = new float2(worldPosition.x - centerX, worldPosition.y - centerZ);
                    float angle = Hash01(Hash(candidateCellX, candidateCellZ, 0xD8F1, Seed)) * math.PI * 2f;
                    float angleSin = math.sin(angle);
                    float angleCos = math.cos(angle);
                    float stretch = math.lerp(0.72f, 1.55f, Hash01(Hash(candidateCellX, candidateCellZ, 0x15E7, Seed)));
                    float squeeze = math.lerp(0.72f, 1.35f, Hash01(Hash(candidateCellX, candidateCellZ, 0x36A9, Seed)));
                    float shapedX = (delta.x * angleCos - delta.y * angleSin) / stretch;
                    float shapedZ = (delta.x * angleSin + delta.y * angleCos) / squeeze;
                    float outerRadius = radius + shoreWidth;

                    // The three shoreline terms can displace the contour inward by
                    // at most 46% of the radius (27% + 11% + 8%). Keep a small extra
                    // margin and reject points that cannot reach the noisy shoreline
                    // before evaluating its four simplex-noise samples.
                    const float MaximumInwardShoreDisplacement = 0.50f;
                    float maximumNoisyReach = outerRadius + radius * MaximumInwardShoreDisplacement;
                    if (math.lengthsq(new float2(shapedX, shapedZ)) > maximumNoisyReach * maximumNoisyReach)
                        continue;

                    float shorelineNoise = noise.snoise((worldPosition + new float2(candidateCellX * 37.1f, candidateCellZ * -19.8f)) * 0.055f) * radius * 0.27f;
                    shorelineNoise += (TerrainNoiseUtility.FbmUnit01(
                        worldPosition + new float2(candidateCellX * 89.7f, candidateCellZ * -53.4f),
                        DetailLayer,
                        2,
                        2.05f,
                        0.52f) - 0.5f) * radius * 0.22f;
                    shorelineNoise += noise.snoise((worldPosition + new float2(candidateCellX * -16.4f, candidateCellZ * 82.5f)) * 0.145f) * radius * 0.08f;
                    float distance = math.length(new float2(shapedX, shapedZ)) + shorelineNoise;

                    if (distance > outerRadius)
                        continue;

                    float candidateWater = 1f - math.saturate(distance / radius);
                    candidateWater = TerrainNoiseUtility.Smooth01(candidateWater);
                    float candidateStrength = 1f - math.saturate((distance - radius) / shoreWidth);
                    candidateStrength = TerrainNoiseUtility.Smooth01(candidateStrength);

                    if (candidateStrength > bestStrength)
                    {
                        bestStrength = candidateStrength;
                        waterStrength = candidateWater;
                        depthMultiplier = math.lerp(0.85f, dryDesertBiome ? 1.75f : 2.20f, Hash01(Hash(candidateCellX, candidateCellZ, 0x651B, Seed)));
                    }
                }
            }

            return bestStrength;
        }

        private static int FastFloorToInt(float value)
        {
            int integer = (int)value;
            return value < integer ? integer - 1 : integer;
        }

        private static uint Hash(int x, int z, int salt, int seed)
        {
            unchecked
            {
                uint h = (uint)seed;
                h ^= (uint)x * 0x9E3779B9u;
                h ^= (uint)z * 0x85EBCA6Bu;
                h ^= (uint)salt * 0xC2B2AE35u;
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return h;
            }
        }

        private static float Hash01(uint hash)
        {
            return (hash & 0x00FFFFFFu) / 16777215f;
        }

    }
}
