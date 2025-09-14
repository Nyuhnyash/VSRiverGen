using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;


namespace RiverGen;

public class NewGenTerra : ModStdWorldGen {
    public ICoreServerAPI sapi;

    // River fields.
    public RiverGenerator riverGenerator;
    public RiverSample[,] samples = new RiverSample[32, 32];

    public int chunksInPlate;
    public int chunksInZone;
    public int aboveSeaLevel;
    public int baseSeaLevel;
    public int heightBoost;
    public float topFactor;
    public Noise valleyNoise = new(0, 0.0008f, 2);
    public Noise floorNoise = new(0, 0.0008f, 1);
    public double maxValleyWidth;
    public int riverFloorBase;
    public double riverFloorVariation;

    public const double terrainDistortionMultiplier = 4.0;
    public const double terrainDistortionThreshold = 40.0;
    public const double geoDistortionMultiplier = 10.0;
    public const double geoDistortionThreshold = 10.0;
    public const double maxDistortionAmount = (55 + 40 + 30 + 10) * SimplexNoiseOctave.MAX_VALUE_2D_WARP;

    public int maxThreads;

    public LandformsWorldProperty landforms;
    public float[][] terrainYThresholds;
    public Dictionary<int, LerpedWeightedIndex2DMap> LandformMapByRegion = new(10);
    public int regionMapSize;
    public float noiseScale;
    public int terrainGenOctaves = 9;

    public NewNormalizedSimplexFractalNoise terrainNoise;
    public SimplexNoise distort2dx;
    public SimplexNoise distort2dz;
    public NormalizedSimplexNoise geoUpheavalNoise;
    public WeightedTaper[] taperMap;

    public struct ThreadLocalTempData {
        public double[] LerpedAmplitudes;
        public double[] LerpedThresholds;
        public float[] landformWeights;
    }

    public ThreadLocal<ThreadLocalTempData> tempDataThreadLocal;

    public struct WeightedTaper {
        public float TerrainYPos;
        public float Weight;
    }

    public struct ColumnResult {
        public BitArray ColumnBlockSolidities;
        public int WaterBlockID;
    }

    public ColumnResult[] columnResults;

    public bool[]
        layerFullySolid; // We can't use BitArrays for these because code which writes to them is heavily multi-threaded; but anyhow they are only mapSizeY x 4 bytes.

    public bool[] layerFullyEmpty;
    public int[] borderIndicesByCardinal;

    public override bool ShouldLoad(EnumAppSide side) {
        return side == EnumAppSide.Server;
    }

    public override double ExecuteOrder() {
        return 0;
    }

    public override void StartServerSide(ICoreServerAPI api) {
        sapi = api;
        
        if (api.World.Config.GetString("landcover").ToFloat() >= 1)
        {
            Mod.Logger.Warning("This world has Landcover set to 100%. There will be no oceans and rivers generated at all.");
        }
        api.Event.InitWorldGenerator(InitWorldGen, "standard");
        api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Terrain, "standard");
        
        // Remove vanilla GenTerra
        var worldGenHandler = (api.World as ServerMain).ModEventManager
            .GetWorldGenHandler("standard");
        foreach (var chunkGens in worldGenHandler.OnChunkColumnGen)
        {
            var i = chunkGens?.Find(x => x.Method.DeclaringType == typeof(GenTerra));
            chunkGens?.Remove(i);
        }

        var action = worldGenHandler.OnInitWorldGen.FirstOrDefault(a => a.Method.DeclaringType == typeof(GenTerra));
        worldGenHandler.OnInitWorldGen.Remove(action);
        
        InitWorldGen();
    }

    public bool initialized;

    public void InitWorldGen() {
        LoadGlobalConfig(sapi);
        LandformMapByRegion.Clear();

        // River config.
        aboveSeaLevel = sapi.WorldManager.MapSizeY - TerraGenConfig.seaLevel;
        baseSeaLevel = TerraGenConfig.seaLevel;
        chunksInPlate = RiverConfig.Loaded.zonesInPlate * RiverConfig.Loaded.zoneSize / 32;
        chunksInZone = RiverConfig.Loaded.zoneSize / 32;
        heightBoost = RiverConfig.Loaded.heightBoost;
        topFactor = RiverConfig.Loaded.topFactor;
        maxValleyWidth = RiverConfig.Loaded.maxValleyWidth;

        riverGenerator = new RiverGenerator(sapi);

        maxThreads = Math.Clamp(Environment.ProcessorCount - (sapi.Server.IsDedicated ? 4 : 6),
            1,
            sapi.Server.Config.HostedMode
                ? 4
                : 10); // We leave at least 4-6 threads free to avoid lag spikes due to CPU unavailability.

        regionMapSize = (int)Math.Ceiling((double)sapi.WorldManager.MapSizeX / sapi.WorldManager.RegionSize);
        noiseScale = Math.Max(1, sapi.WorldManager.MapSizeY / 256f);
        terrainGenOctaves = TerraGenConfig.GetTerrainOctaveCount(sapi.WorldManager.MapSizeY);

        terrainNoise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(
            terrainGenOctaves,
            0.0005 * NewSimplexNoiseLayer.OldToNewFrequency / noiseScale,
            0.9,
            sapi.WorldManager.Seed
        );

        distort2dx = new SimplexNoise(
            new double[] { 55, 40, 30, 10 },
            ScaleAdjustedFreqs(new double[] { 1 / 5.0, 1 / 2.50, 1 / 1.250, 1 / 0.65 }, noiseScale),
            sapi.World.Seed + 9876 + 0
        );

        distort2dz = new SimplexNoise(
            new double[] { 55, 40, 30, 10 },
            ScaleAdjustedFreqs(new double[] { 1 / 5.0, 1 / 2.50, 1 / 1.250, 1 / 0.65 }, noiseScale),
            sapi.World.Seed + 9876 + 2
        );

        geoUpheavalNoise = new NormalizedSimplexNoise(
            new double[] { 55, 40, 30, 15, 7, 4 },
            ScaleAdjustedFreqs(new double[]
                {
                    1.0 / 5.5,
                    1.1 / 2.75,
                    1.2 / 1.375,
                    1.2 / 0.715,
                    1.2 / 0.45,
                    1.2 / 0.25
                },
                noiseScale),
            sapi.World.Seed + 9876 + 1
        );

        if (!initialized) {
            tempDataThreadLocal = new ThreadLocal<ThreadLocalTempData>(() => {
                return new ThreadLocalTempData
                {
                    LerpedAmplitudes = new double[terrainGenOctaves],
                    LerpedThresholds = new double[terrainGenOctaves],
                    landformWeights = new float[landforms.LandFormsByIndex.Length]
                };
            });
        }

        columnResults = new ColumnResult[chunksize * chunksize];
        layerFullyEmpty = new bool[sapi.WorldManager.MapSizeY];
        layerFullySolid = new bool[sapi.WorldManager.MapSizeY];
        taperMap = new WeightedTaper[chunksize * chunksize];

        for (var i = 0; i < chunksize * chunksize; i++)
            columnResults[i].ColumnBlockSolidities = new BitArray(sapi.WorldManager.MapSizeY);

        borderIndicesByCardinal = new int[8];
        borderIndicesByCardinal[Cardinal.NorthEast.Index] = ((chunksize - 1) * chunksize) + 0;
        borderIndicesByCardinal[Cardinal.SouthEast.Index] = 0 + 0;
        borderIndicesByCardinal[Cardinal.SouthWest.Index] = 0 + chunksize - 1;
        borderIndicesByCardinal[Cardinal.NorthWest.Index] = ((chunksize - 1) * chunksize) + chunksize - 1;

        initialized = true;
    }

    private static double[] ScaleAdjustedFreqs(double[] vs, float horizontalScale) {
        for (var i = 0; i < vs.Length; i++) {
            vs[i] /= horizontalScale;
        }

        return vs;
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request) {
        if (request.RequiresChunkBorderSmoothing) {
            var neighHeightMaps = request.NeighbourTerrainHeight;

            // Ignore diagonals if direct adjacent faces are available, otherwise the corners get weighted too strongly.
            if (neighHeightMaps[Cardinal.North.Index] != null) {
                neighHeightMaps[Cardinal.NorthEast.Index] = null;
                neighHeightMaps[Cardinal.NorthWest.Index] = null;
            }

            if (neighHeightMaps[Cardinal.East.Index] != null) {
                neighHeightMaps[Cardinal.NorthEast.Index] = null;
                neighHeightMaps[Cardinal.SouthEast.Index] = null;
            }

            if (neighHeightMaps[Cardinal.South.Index] != null) {
                neighHeightMaps[Cardinal.SouthWest.Index] = null;
                neighHeightMaps[Cardinal.SouthEast.Index] = null;
            }

            if (neighHeightMaps[Cardinal.West.Index] != null) {
                neighHeightMaps[Cardinal.SouthWest.Index] = null;
                neighHeightMaps[Cardinal.NorthWest.Index] = null;
            }

            var sides = "";
            for (var i = 0; i < Cardinal.ALL.Length; i++) {
                var neibMap = neighHeightMaps[i];
                if (neibMap == null) continue;

                sides += Cardinal.ALL[i].Code + "_";
            }

            for (var dx = 0; dx < chunksize; dx++) {
                borderIndicesByCardinal[Cardinal.North.Index] = ((chunksize - 1) * chunksize) + dx;
                borderIndicesByCardinal[Cardinal.South.Index] = 0 + dx;

                for (var dz = 0; dz < chunksize; dz++) {
                    double sumWeight = 0;
                    double yPos = 0;
                    float maxWeight = 0;

                    borderIndicesByCardinal[Cardinal.East.Index] = (dz * chunksize) + 0;
                    borderIndicesByCardinal[Cardinal.West.Index] = (dz * chunksize) + chunksize - 1;

                    for (var i = 0; i < Cardinal.ALL.Length; i++) {
                        var neibMap = neighHeightMaps[i];
                        if (neibMap == null) continue;

                        float distToEdge = 0;

                        switch (i) {
                            case 0: // N: Negative Z.
                                distToEdge = (float)dz / chunksize;
                                break;
                            case 1: // NE: Positive X, negative Z.
                                distToEdge = 1 - ((float)dx / chunksize) + ((float)dz / chunksize);
                                break;
                            case 2: // E: Positive X
                                distToEdge = 1 - ((float)dx / chunksize);
                                break;
                            case 3: // SE: Positive X, positive Z.
                                distToEdge = 1 - ((float)dx / chunksize) + (1 - ((float)dz / chunksize));
                                break;
                            case 4: // S: Positive Z.
                                distToEdge = 1 - ((float)dz / chunksize);
                                break;
                            case 5: // SW: Negative X, positive Z.
                                distToEdge = ((float)dx / chunksize) + 1 - ((float)dz / chunksize);
                                break;
                            case 6: // W: Negative X.
                                distToEdge = (float)dx / chunksize;
                                break;
                            case 7: // Negative X, negative Z.
                                distToEdge = ((float)dx / chunksize) + ((float)dz / chunksize);
                                break;
                        }

                        var cardinalWeight = (float)Math.Pow((float)(1 - GameMath.Clamp(distToEdge, 0, 1)), 2);
                        var neibYPos = neibMap[borderIndicesByCardinal[i]] + 0.5f;

                        yPos += neibYPos * Math.Max(0.0001, cardinalWeight);
                        sumWeight += cardinalWeight;
                        maxWeight = Math.Max(maxWeight, cardinalWeight);
                    }

                    taperMap[(dz * chunksize) + dx] = new WeightedTaper()
                        { TerrainYPos = (float)(yPos / Math.Max(0.0001, sumWeight)), Weight = maxWeight };
                }
            }
        }
        if (landforms == null) {
            var type = typeof(NoiseBase).Assembly.GetType("Vintagestory.ServerMods.NoiseLandforms");
            landforms = type
                .GetStaticField<LandformsWorldProperty>("landforms");
            
            terrainYThresholds = new float[landforms.LandFormsByIndex.Length][];
            for (var i = 0; i < landforms.LandFormsByIndex.Length; i++) {
                // Get river landform and adjust it to new world height.
                if (landforms.LandFormsByIndex[i].Code.ToString() == "game:riverlandform") {
                    var riverLandform = landforms.LandFormsByIndex[i];

                    riverIndex = i;
                    riverVariant = riverLandform;

                    var modifier = 256f / sapi.WorldManager.MapSizeY;

                    var seaLevelThreshold = 0.4313725490196078f;

                    var blockThreshold = 0.4313725490196078f / 110 * modifier;

                    riverLandform.TerrainYKeyPositions[0] = seaLevelThreshold; // 100% chance to be atleast sea level.
                    riverLandform.TerrainYKeyPositions[1] =
                        seaLevelThreshold + (blockThreshold * 4); // 50% chance to be atleast 4 blocks above sea level.

                    riverLandform.TerrainYKeyPositions[2] =
                        seaLevelThreshold + (blockThreshold * 6); // 25% chance to be atleast 6 blocks above sea level.

                    riverLandform.TerrainYKeyPositions[3] =
                        seaLevelThreshold + (blockThreshold * 12); // 0% chance to be astleast 10 blocks above sea level.

                    // Re-lerp with adjusted heights.
                    riverLandform.CallMethod("LerpThresholds", sapi.WorldManager.MapSizeY);
                }

                terrainYThresholds[i] = landforms.LandFormsByIndex[i].TerrainYThresholds;
            }
        }

        Generate(request.Chunks, request.ChunkX, request.ChunkZ, request.RequiresChunkBorderSmoothing);
    }

    public int riverIndex;
    public LandformVariant riverVariant;

    private void Generate(IServerChunk[] chunks, int chunkX, int chunkZ, bool requiresChunkBorderSmoothing) {
        var mapChunk = chunks[0].MapChunk;
        const int chunkSize = GlobalConstants.ChunkSize;

        var regionChunkSize = sapi.WorldManager.RegionSize / chunkSize;
        var rlX = chunkX % regionChunkSize;
        var rlZ = chunkZ % regionChunkSize;
        var rockId = GlobalConfig.defaultRockId;

        // Get climate data.
        var climateMap = chunks[0].MapChunk.MapRegion.ClimateMap;
        var cFac = (float)climateMap.InnerSize / regionChunkSize;
        var climateUpLeft = climateMap.GetUnpaddedInt((int)(rlX * cFac), (int)(rlZ * cFac));
        var climateUpRight = climateMap.GetUnpaddedInt((int)((rlX * cFac) + cFac), (int)(rlZ * cFac));
        var climateBotLeft = climateMap.GetUnpaddedInt((int)(rlX * cFac), (int)((rlZ * cFac) + cFac));
        var climateBotRight = climateMap.GetUnpaddedInt((int)((rlX * cFac) + cFac), (int)((rlZ * cFac) + cFac));

        // Get ocean data.
        var oceanMap = chunks[0].MapChunk.MapRegion.OceanMap;
        var oceanUpLeft = 0;
        var oceanUpRight = 0;
        var oceanBotLeft = 0;
        var oceanBotRight = 0;
        if (oceanMap != null && oceanMap.Data.Length > 0) {
            var oFac = (float)oceanMap.InnerSize / regionChunkSize;
            oceanUpLeft = oceanMap.GetUnpaddedInt((int)(rlX * oFac), (int)(rlZ * oFac));
            oceanUpRight = oceanMap.GetUnpaddedInt((int)((rlX * oFac) + oFac), (int)(rlZ * oFac));
            oceanBotLeft = oceanMap.GetUnpaddedInt((int)(rlX * oFac), (int)((rlZ * oFac) + oFac));
            oceanBotRight = oceanMap.GetUnpaddedInt((int)((rlX * oFac) + oFac), (int)((rlZ * oFac) + oFac));
        }

        // Get upheaval data.
        var upheavalMap = chunks[0].MapChunk.MapRegion.UpheavelMap;
        var upheavalMapUpLeft = 0;
        var upheavalMapUpRight = 0;
        var upheavalMapBotLeft = 0;
        var upheavalMapBotRight = 0;
        if (upheavalMap != null) {
            var uFac = (float)upheavalMap.InnerSize / regionChunkSize;
            upheavalMapUpLeft = upheavalMap.GetUnpaddedInt((int)(rlX * uFac), (int)(rlZ * uFac));
            upheavalMapUpRight = upheavalMap.GetUnpaddedInt((int)((rlX * uFac) + uFac), (int)(rlZ * uFac));
            upheavalMapBotLeft = upheavalMap.GetUnpaddedInt((int)(rlX * uFac), (int)((rlZ * uFac) + uFac));
            upheavalMapBotRight = upheavalMap.GetUnpaddedInt((int)((rlX * uFac) + uFac), (int)((rlZ * uFac) + uFac));
        }

        var oceanicityFac =
            sapi.WorldManager.MapSizeY / 256 * 0.33333f; // At a map height of 255, submerge land by up to 85 blocks.

        var landformMap = mapChunk.MapRegion.LandformMap;

        // # of pixels for each chunk (probably 1, 2, or 4) in the landform map.

        float chunkPixelSize = landformMap.InnerSize / regionChunkSize;

        // Start coordinates for the chunk in the region map.
        var baseX = chunkX % regionChunkSize * chunkPixelSize;
        var baseZ = chunkZ % regionChunkSize * chunkPixelSize;

        var landLerpMap =
            GetOrLoadLerpedLandformMap(chunks[0].MapChunk, chunkX / regionChunkSize, chunkZ / regionChunkSize);

        // Terrain octaves.
        var landformWeights = tempDataThreadLocal.Value.landformWeights;
        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX, baseZ, landformWeights),
            out var octNoiseX0,
            out var octThX0);

        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX + chunkPixelSize, baseZ, landformWeights),
            out var octNoiseX1,
            out var octThX1);

        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX, baseZ + chunkPixelSize, landformWeights),
            out var octNoiseX2,
            out var octThX2);

        GetInterpolatedOctaves(landLerpMap.WeightsAt(baseX + chunkPixelSize, baseZ + chunkPixelSize, landformWeights),
            out var octNoiseX3,
            out var octThX3);

        var terrainYThresholds = this.terrainYThresholds;

        // Store heightmap in the map chunk.
        var rainHeightMap = chunks[0].MapChunk.RainHeightMap;
        var terrainHeightMap = chunks[0].MapChunk.WorldGenTerrainHeightMap;

        var mapSizeY = sapi.WorldManager.MapSizeY;
        var mapSizeYm2 = sapi.WorldManager.MapSizeY - 2;
        var taperThreshold = (int)(mapSizeY * 0.9f);
        double geoUpheavalAmplitude = 255;

        const float chunkBlockDelta = 1.0f / chunkSize;
        var chunkPixelBlockStep = chunkPixelSize * chunkBlockDelta;
        var verticalNoiseRelativeFrequency = 0.5 / TerraGenConfig.terrainNoiseVerticalScale;
        for (var y = 0; y < layerFullySolid.Length; y++)
            layerFullySolid[y] =
                true; // Fill with true; later if any block in the layer is non-solid we will set it to false.

        for (var y = 0; y < layerFullyEmpty.Length; y++)
            layerFullyEmpty[y] =
                true; // Fill with true; later if any block in the layer is non-solid we will set it to false.

        layerFullyEmpty[mapSizeY - 1] =
            false; // The top block is always empty (air), leaving space for grass, snow layer etc.

        // RIVERSTUFF.

        // Get cached plate.
        var plateX = chunkX / chunksInPlate;
        var plateZ = chunkZ / chunksInPlate;

        var plate = ObjectCacheUtil.GetOrCreate(sapi,
            plateX.ToString() + "+" + plateZ.ToString(),
            () => { return new TectonicPlate(sapi, plateX, plateZ); });

        // Get chunk relative to plate.
        var localChunkX = chunkX % chunksInPlate;
        var localChunkZ = chunkZ % chunksInPlate;

        List<RiverSegment> validSegments = new();
        Vec2d localStart = new(localChunkX * chunksize, localChunkZ * chunksize);

        localStart += 16;

        foreach (var river in plate.rivers) {
            if (localStart.DistanceTo(river.startPos) > river.radius) continue;

            foreach (var node in river.nodes) {
                if (RiverMath.DistanceToLine(localStart, node.startPos, node.endPos) <
                    maxValleyWidth + 512) // Consider a river of 100 size, 100 distortion.
                {
                    foreach (var segment in node.segments) {
                        if (RiverMath.DistanceToLine(localStart, segment.startPos, segment.endPos) <
                            maxValleyWidth + segment.riverNode.startSize + 128) {
                            validSegments
                                .Add(segment); // Later check for duplicates. If the distance to another segment is too great it shouldn't have to be here.
                        }
                    }
                }
            }
        }

        localStart -= 16;

        List<RiverSegment> valid = new();
        riverGenerator.ValidateSegments(validSegments.ToArray(), maxValleyWidth, localStart.X, localStart.Y, valid);
        riverGenerator.ValidateSegments(validSegments.ToArray(),
            maxValleyWidth,
            localStart.X + 31,
            localStart.Y,
            valid);

        riverGenerator.ValidateSegments(validSegments.ToArray(),
            maxValleyWidth,
            localStart.X,
            localStart.Y + 31,
            valid);

        riverGenerator.ValidateSegments(validSegments.ToArray(),
            maxValleyWidth,
            localStart.X + 31,
            localStart.Y + 31,
            valid);

        var validArray = valid.ToArray();

        var flowVectors = new float[32 * 32 * 2];
        var riverDistance = new ushort[32 * 32];
        var riverBank = false;

        Parallel.For(0,
            chunkSize * chunkSize,
            new ParallelOptions() { MaxDegreeOfParallelism = maxThreads },
            chunkIndex2d => {
                var localX = chunkIndex2d % chunkSize;
                var localZ = chunkIndex2d / chunkSize;

                var worldX = (chunkX * chunkSize) + localX;
                var worldZ = (chunkZ * chunkSize) + localZ;

                // Sample river.
                samples[localX, localZ] =
                    riverGenerator.SampleRiver(validArray, localStart.X + localX, localStart.Y + localZ);

                var sample = samples[localX, localZ];

                // Determine if water is flowing there and add it.
                if (sample.flowVectorX > -100) {
                    flowVectors[chunkIndex2d] = sample.flowVectorX;
                    flowVectors[chunkIndex2d + 1024] = sample.flowVectorZ;
                    riverBank = true;
                }

                // Log river distance in chunk data.
                riverDistance[chunkIndex2d] = (ushort)sample.riverDistance;

                float riverLerp = 1;

                // 1 - edge of valley, 0 - edge of river.
                if (sample.riverDistance < maxValleyWidth) {
                    // Get raw perlin noise.
                    double valley = valleyNoise.GetNoise(worldX, worldZ);

                    // *2 gain for faster transitions.
                    valley = Math.Clamp(valley * 2, -1, 1);

                    // Convert to positive number.
                    valley += 1;
                    valley /= 2;

                    // Before this was -1 to 3? This should be correct weighting.

                    // Clamp to bounds.
                    if (valley < 0.02) valley = 0.02;

                    if (valley < 1) {
                        //riverLerp = (float)Math.Clamp(RiverMath.InverseLerp(samples[localX, localZ].riverDistance, 0, maxValleyWidth), valley, 1);

                        // Smooth lerp instead.
                        riverLerp = (float)GameMath.Lerp(valley,
                            1,
                            RiverMath.InverseLerp(samples[localX, localZ].riverDistance, 0, maxValleyWidth));

                        riverLerp *= riverLerp;
                    }
                }

                var columnBlockSolidities = columnResults[chunkIndex2d].ColumnBlockSolidities;
                columnBlockSolidities.SetAll(false);

                var lerpedAmps = tempDataThreadLocal.Value.LerpedAmplitudes;
                var lerpedThresh = tempDataThreadLocal.Value.LerpedThresholds;

                var columnLandformIndexedWeights = tempDataThreadLocal.Value.landformWeights;
                landLerpMap.WeightsAt(baseX + (localX * chunkPixelBlockStep),
                    baseZ + (localZ * chunkPixelBlockStep),
                    columnLandformIndexedWeights);

                // Weight landform to river.
                if (riverLerp < 1) {
                    // Multiply by river value.
                    for (var i = 0; i < columnLandformIndexedWeights.Length; i++) {
                        columnLandformIndexedWeights[i] *= riverLerp;
                    }

                    // Multiply river landform by inverse.
                    columnLandformIndexedWeights[riverIndex] += 1 - riverLerp;
                }

                for (var i = 0; i < lerpedAmps.Length; i++) {
                    lerpedAmps[i] = GameMath.BiLerp(octNoiseX0[i],
                        octNoiseX1[i],
                        octNoiseX2[i],
                        octNoiseX3[i],
                        localX * chunkBlockDelta,
                        localZ * chunkBlockDelta);

                    lerpedThresh[i] = GameMath.BiLerp(octThX0[i],
                        octThX1[i],
                        octThX2[i],
                        octThX3[i],
                        localX * chunkBlockDelta,
                        localZ * chunkBlockDelta);

                    // Weight octaves to river.
                    if (riverLerp < 1) {
                        lerpedAmps[i] *= riverLerp;
                        lerpedThresh[i] *= riverLerp;

                        lerpedAmps[i] += riverVariant.TerrainOctaves[i] * (1 - riverLerp);
                        lerpedThresh[i] += riverVariant.TerrainOctaveThresholds[i] * (1 - riverLerp);
                    }
                }

                // Create a directional compression effect.
                var dist = NewDistortionNoise(worldX, worldZ);
                var distTerrain = ApplyIsotropicDistortionThreshold(dist * terrainDistortionMultiplier,
                    terrainDistortionThreshold,
                    terrainDistortionMultiplier * maxDistortionAmount);

                // Get Y distortion from oceanicity and upheaval.
                var upheavalStrength = GameMath.BiLerp(upheavalMapUpLeft,
                    upheavalMapUpRight,
                    upheavalMapBotLeft,
                    upheavalMapBotRight,
                    localX * chunkBlockDelta,
                    localZ * chunkBlockDelta);

                // Weight upheaval to river.
                upheavalStrength *= riverLerp;

                var oceanicity = GameMath.BiLerp(oceanUpLeft,
                    oceanUpRight,
                    oceanBotLeft,
                    oceanBotRight,
                    localX * chunkBlockDelta,
                    localZ * chunkBlockDelta) * oceanicityFac;

                var distGeo = ApplyIsotropicDistortionThreshold(dist * geoDistortionMultiplier,
                    geoDistortionThreshold,
                    geoDistortionMultiplier * maxDistortionAmount);

                var distY = oceanicity + ComputeOceanAndUpheavalDistY(upheavalStrength, worldX, worldZ, distGeo);

                columnResults[chunkIndex2d].WaterBlockID =
                    oceanicity > 1 ? GlobalConfig.saltWaterBlockId : GlobalConfig.waterBlockId;

                // Prepare the noise for the entire column.
                var columnNoise =
                    terrainNoise.ForColumn(verticalNoiseRelativeFrequency,
                        lerpedAmps,
                        lerpedThresh,
                        worldX + distTerrain.X,
                        worldZ + distTerrain.Z);

                var noiseBoundMin = columnNoise.BoundMin;
                var noiseBoundMax = columnNoise.BoundMax;

                var wTaper = taperMap[chunkIndex2d];

                var distortedPosYSlide =
                    distY - (int)Math.Floor(distY); // This value will be unchanged throughout the posY loop.

                for (var posY = 1; posY <= mapSizeYm2; posY++) {
                    // Setup a lerp between threshold values, so that distortY can be applied continuously there.
                    StartSampleDisplacedYThreshold(posY + distY, mapSizeYm2, out var distortedPosYBase);

                    // Value starts as the landform Y threshold.
                    double threshold = 0;

                    for (var i = 0; i < columnLandformIndexedWeights.Length; i++) {
                        var weight = columnLandformIndexedWeights[i];
                        if (weight == 0) continue;

                        // Sample the two values to lerp between. The value of distortedPosYBase is clamped in such a way that this always works.
                        // Underflow and overflow of distortedPosY result in linear extrapolation.

                        threshold += weight * ContinueSampleDisplacedYThreshold(distortedPosYBase,
                            distortedPosYSlide,
                            terrainYThresholds[i]);
                    }

                    // Geo upheaval modifier for threshold.
                    ComputeGeoUpheavalTaper(posY, distY, taperThreshold, geoUpheavalAmplitude, mapSizeY, ref threshold);

                    if (requiresChunkBorderSmoothing) {
                        double th = posY > wTaper.TerrainYPos ? 1 : -1;

                        var yDiff = Math.Abs(posY - wTaper.TerrainYPos);
                        var noise = yDiff > 10
                            ? 0
                            : distort2dx.Noise(-((chunkX * chunkSize) + localX) / 10.0,
                                posY / 10.0,
                                -((chunkZ * chunkSize) + localZ) / 10.0) / Math.Max(1, yDiff / 2.0);

                        noise *= GameMath.Clamp(2 * (1 - wTaper.Weight), 0, 1) * 0.1;

                        threshold = GameMath.Lerp(threshold, th + noise, wTaper.Weight);
                    }

                    // Often we don't need to calculate the noise.
                    if (threshold <= noiseBoundMin) {
                        columnBlockSolidities[posY] = true; // Yes terrain block, fill with stone.
                        layerFullyEmpty[posY] = false; // (Thread safe even when this is parallel).
                    }
                    else if (!(threshold < noiseBoundMax)) // Second case also catches NaN if it were to ever happen.
                    {
                        layerFullySolid[posY] = false; // No terrain block (thread safe even when this is parallel).

                        // We can now exit the loop early, because empirical testing shows that once the threshold has exceeded the max noise bound, it never returns to a negative noise value at any higher y value in the same blocks column. This represents air well above the "interesting" part of the terrain. Tested for all world heights in the range 256-1536, tested with arches, overhangs, etc.
                        for (var yAbove = posY + 1; yAbove <= mapSizeYm2; yAbove++) layerFullySolid[yAbove] = false;
                        break;
                    }
                    else // But sometimes we do.
                    {
                        var noiseSign = -NormalizedSimplexNoise.NoiseValueCurveInverse(threshold);
                        noiseSign = columnNoise.NoiseSign(posY, noiseSign);

                        if (noiseSign > 0) // Solid.
                        {
                            columnBlockSolidities[posY] = true; // Yes, terrain block.
                            layerFullyEmpty[posY] = false; // Thread safe even when this is parallel.
                        }
                        else {
                            layerFullySolid[posY] = false; // Thread safe even when this is parallel.
                        }
                    }
                }

                // Don't do this optimization where rivers exist.
                if (sample.riverDistance <= 1) {
                    for (var posY = 1; posY <= mapSizeYm2; posY++) {
                        layerFullyEmpty[posY] = false;
                        layerFullySolid[posY] = false;
                    }
                }
            });

        var chunkBlockData = chunks[0].Data;

        // First set all the fully solid layers in bulk, as much as possible.
        chunkBlockData.SetBlockBulk(0, chunkSize, chunkSize, GlobalConfig.mantleBlockId);
        var yBase = 1;
        for (; yBase < mapSizeY - 1; yBase++) {
            if (layerFullySolid[yBase]) {
                if (yBase % chunkSize == 0) {
                    chunkBlockData = chunks[yBase / chunkSize].Data;
                }

                chunkBlockData.SetBlockBulk(yBase % chunkSize * chunkSize * chunkSize, chunkSize, chunkSize, rockId);
            }
            else break;
        }

        // Now figure out the top of the mixed layers (above yTop we have fully empty layers, i.e. air).
        var seaLevel = TerraGenConfig.seaLevel;

        var surfaceWaterId = 0;

        // yTop never more than (mapSizeY - 1), but leave the top block layer on the map always as air / for grass.
        var yTop = mapSizeY - 2;

        while (yTop >= yBase && layerFullyEmpty[yTop])
            yTop--; // Decrease yTop, we don't need to generate anything for fully empty (air layers).

        if (yTop < seaLevel) yTop = seaLevel;
        yTop++; // Add back one because this is going to be the loop until limit.

        // Then for the rest place blocks column by column (from yBase to yTop only; outside that range layers were already placed below, or are fully air above).
        for (var localZ = 0; localZ < chunkSize; localZ++) {
            var worldZ = (chunkZ * chunkSize) + localZ;
            var mapIndex = ChunkIndex2d(0, localZ);
            for (var localX = 0; localX < chunkSize; localX++) {
                var columnResult = columnResults[mapIndex];
                var waterId = columnResult.WaterBlockID;
                surfaceWaterId = waterId;

                if (yBase < seaLevel && waterId != GlobalConfig.saltWaterBlockId &&
                    !columnResult
                        .ColumnBlockSolidities
                        [seaLevel -
                         1]) // Should surface water be lake ice? Relevant only for fresh water and only if this particular XZ column has a non-solid block at sea-level.
                {
                    var temp = (GameMath.BiLerpRgbColor(localX * chunkBlockDelta,
                        localZ * chunkBlockDelta,
                        climateUpLeft,
                        climateUpRight,
                        climateBotLeft,
                        climateBotRight) >> 16) & 0xFF;

                    var distort = (float)distort2dx.Noise((chunkX * chunkSize) + localX, worldZ) / 20f;
                    var tempF = CompatUtil.GetScaledAdjustedTemperatureFloat(temp, 0) + distort;
                    if (tempF < TerraGenConfig.WaterFreezingTempOnGen) surfaceWaterId = GlobalConfig.lakeIceBlockId;
                }

                terrainHeightMap[mapIndex] =
                    (ushort)(yBase -
                             1); // Initially set the height maps to values reflecting the top of the fully solid layers.

                rainHeightMap[mapIndex] = (ushort)(yBase - 1);

                chunkBlockData = chunks[yBase / chunkSize].Data;

                var sample = samples[localX, localZ];

                // Carver.
                if (sample.riverDistance <= 0) {
                    var bankFactorBlocks = (int)(sample.bankFactor * aboveSeaLevel);
                    var baseline = baseSeaLevel + heightBoost;

                    for (var posY = yBase; posY < yTop; posY++) {
                        var localY = posY % chunkSize;

                        // For every single block in the chunk, the cost is checking one of these.
                        // This is really laggy and bad Lol.

                        if (columnResult.ColumnBlockSolidities[posY] && (posY <= baseline - bankFactorBlocks ||
                                                                         posY >= baseline +
                                                                         (bankFactorBlocks * topFactor))) // If isSolid.
                        {
                            terrainHeightMap[mapIndex] = (ushort)posY;
                            rainHeightMap[mapIndex] = (ushort)posY;
                            chunkBlockData[ChunkIndex3d(localX, localY, localZ)] = rockId;
                        }
                        else if (posY < seaLevel) {
                            int blockId;
                            if (posY == seaLevel - 1) {
                                rainHeightMap[mapIndex] =
                                    (ushort)posY; // We only need to set the rainHeightMap on the top water block, i.e. seaLevel - 1.

                                blockId = surfaceWaterId;
                            }
                            else {
                                blockId = waterId;
                            }

                            chunkBlockData.SetFluid(ChunkIndex3d(localX, localY, localZ), blockId);
                        }

                        if (localY == chunkSize - 1) {
                            chunkBlockData =
                                chunks[(posY + 1) / chunkSize].Data; // Set up the next chunksBlockData value.
                        }
                    }
                }
                else {
                    for (var posY = yBase; posY < yTop; posY++) {
                        var localY = posY % chunkSize;

                        if (columnResult.ColumnBlockSolidities[posY]) // If isSolid.
                        {
                            terrainHeightMap[mapIndex] = (ushort)posY;
                            rainHeightMap[mapIndex] = (ushort)posY;
                            chunkBlockData[ChunkIndex3d(localX, localY, localZ)] = rockId;
                        }
                        else if (posY < seaLevel) {
                            int blockId;
                            if (posY == seaLevel - 1) {
                                rainHeightMap[mapIndex] =
                                    (ushort)posY; // We only need to set the rainHeightMap on the top water block, i.e. seaLevel - 1.

                                blockId = surfaceWaterId;
                            }
                            else {
                                blockId = waterId;
                            }

                            chunkBlockData.SetFluid(ChunkIndex3d(localX, localY, localZ), blockId);
                        }

                        if (localY == chunkSize - 1) {
                            chunkBlockData =
                                chunks[(posY + 1) / chunkSize].Data; // Set up the next chunksBlockData value.
                        }
                    }
                }

                mapIndex++;
            }
        }

        if (riverBank) {
            chunks[0].SetModdata("flowVectors", flowVectors);
        }

        chunks[0].MapChunk.SetModdata("riverDistance", riverDistance);

        ushort yMax = 0;
        for (var i = 0; i < rainHeightMap.Length; i++) {
            yMax = Math.Max(yMax, rainHeightMap[i]);
        }

        chunks[0].MapChunk.YMax = yMax;
    }

    public LerpedWeightedIndex2DMap GetOrLoadLerpedLandformMap(IMapChunk mapchunk, int regionX, int regionZ) {
        // 1. Load?
        LandformMapByRegion.TryGetValue((regionZ * regionMapSize) + regionX, out var map);
        if (map != null) return map;

        var lMap = mapchunk.MapRegion.LandformMap;

        // 2. Create
        map = LandformMapByRegion[(regionZ * regionMapSize) + regionX]
            = new LerpedWeightedIndex2DMap(lMap.Data,
                lMap.Size,
                TerraGenConfig.landFormSmoothingRadius,
                lMap.TopLeftPadding,
                lMap.BottomRightPadding);

        return map;
    }

    public void GetInterpolatedOctaves(float[] indices, out double[] amps, out double[] thresholds) {
        amps = new double[terrainGenOctaves];
        thresholds = new double[terrainGenOctaves];

        for (var octave = 0; octave < terrainGenOctaves; octave++) {
            double amplitude = 0;
            double threshold = 0;
            for (var i = 0; i < indices.Length; i++) {
                var weight = indices[i];
                if (weight == 0) continue;

                var l = landforms.LandFormsByIndex[i];
                amplitude += l.TerrainOctaves[octave] * weight;
                threshold += l.TerrainOctaveThresholds[octave] * weight;
            }

            amps[octave] = amplitude;
            thresholds[octave] = threshold;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StartSampleDisplacedYThreshold(float distortedPosY, int mapSizeYm2, out int yBase) {
        yBase = GameMath.Clamp((int)Math.Floor(distortedPosY), 0, mapSizeYm2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ContinueSampleDisplacedYThreshold(int yBase, float ySlide, float[] thresholds) {
        return GameMath.Lerp(thresholds[yBase], thresholds[yBase + 1], ySlide);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ComputeOceanAndUpheavalDistY(float upheavalStrength, double worldX, double worldZ, VectorXZ distGeo) {
        var upheavalNoiseValue =
            (float)geoUpheavalNoise.Noise((worldX + distGeo.X) / 400.0, (worldZ + distGeo.Z) / 400.0) * 0.9f;

        var upheavalMultiplier = Math.Min(0, 0.5f - upheavalNoiseValue);
        return upheavalStrength * upheavalMultiplier;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ComputeGeoUpheavalTaper(double posY, double distY, double taperThreshold,
        double geoUpheavalAmplitude, double mapSizeY, ref double threshold) {
        const double AMPLITUDE_MODIFIER = 40.0;
        if (posY > taperThreshold && distY < -2) {
            var upheavalAmount = GameMath.Clamp(-distY, posY - mapSizeY, posY);
            var ceilingDelta = posY - taperThreshold;
            threshold += ceilingDelta * upheavalAmount / (AMPLITUDE_MODIFIER * geoUpheavalAmplitude);
        }
    }

    // Closely matches the old two-noise distortion in a given seed, but is more fair to all angles.
    public VectorXZ NewDistortionNoise(double worldX, double worldZ) {
        var noiseX = worldX / 400.0;
        var noiseZ = worldZ / 400.0;
        SimplexNoise.NoiseFairWarpVector(distort2dx, distort2dz, noiseX, noiseZ, out var distX, out var distZ);
        return new VectorXZ { X = distX, Z = distZ };
    }

    // Cuts off the distortion in a circle rather than a square.
    // Between this and the new distortion noise, this makes the bigger difference.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VectorXZ ApplyIsotropicDistortionThreshold(VectorXZ dist, double threshold, double maximum) {
        var distMagnitudeSquared = (dist.X * dist.X) + (dist.Z * dist.Z);
        var thresholdSquared = threshold * threshold;
        if (distMagnitudeSquared <= thresholdSquared) dist.X = dist.Z = 0;
        else {
            // `slide` is 0 to 1 between `threshold` and `maximum` (input vector magnitude).
            var baseCurve = (distMagnitudeSquared - thresholdSquared) / distMagnitudeSquared;
            var maximumSquared = maximum * maximum;
            var baseCurveReciprocalAtMaximum = maximumSquared / (maximumSquared - thresholdSquared);
            var slide = baseCurve * baseCurveReciprocalAtMaximum;

            // Let `slide` be smooth to start.
            slide *= slide;

            // `forceDown` needs to make `dist` zero at `threshold`
            // and `expectedOutputMaximum` at `maximum`.
            var expectedOutputMaximum = maximum - threshold;
            var forceDown = slide * (expectedOutputMaximum / maximum);

            dist *= forceDown;
        }

        return dist;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChunkIndex3d(int x, int y, int z) {
        return (((y * chunksize) + z) * chunksize) + x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChunkIndex2d(int x, int z) {
        return (z * chunksize) + x;
    }

    public struct VectorXZ {
        public double X, Z;

        public static VectorXZ operator *(VectorXZ a, double b) {
            return new() { X = a.X * b, Z = a.Z * b };
        }
    }
}
