using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace RiverGen;

public class GravelGen : ModStdWorldGen
{
    public Noise gravelNoise = new(0, 0.01f, 2);

    public Dictionary<int, int> gravelMappings = new();

    public ICoreServerAPI sapi;

    public IBlockAccessor blockAccessor;

    public override double ExecuteOrder()
    {
        return 0.45;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);

        sapi = api;

        if (RiverConfig.Loaded.gravel)
        {
            sapi.Event.InitWorldGenerator(InitWorldGen, "standard");
            sapi.Event.GetWorldgenBlockAccessor(OnWorldGenBlockAccessor);
            sapi.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Terrain, "standard");
        }
    }

    private void OnWorldGenBlockAccessor(IChunkProviderThread chunkProvider)
    {
        blockAccessor = chunkProvider.GetBlockAccessor(false);
    }

    public int baseSeaLevel;

    public void InitWorldGen()
    {
        var rockStrata = sapi.Assets.Get<RockStrataConfig>(new AssetLocation("worldgen/rockstrata.json"));

        foreach (var stratum in rockStrata.Variants)
        {
            var stratumId = sapi.World.GetBlock(stratum.BlockCode).BlockId;

            if (gravelMappings.ContainsKey(stratumId)) continue;

            // No gravel for kimberlite/phyllite.
            var gravelId = sapi.World.GetBlock(new AssetLocation("gravel-" + stratum.BlockCode.ToString().Split('-')[1]))?.BlockId ?? stratumId;

            gravelMappings.Add(stratumId, gravelId);
        }

        gravelMappings.Add(0, 0);

        baseSeaLevel = TerraGenConfig.seaLevel - 1;
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        var mapChunk = request.Chunks[0].MapChunk;

        var chunkX = request.ChunkX;
        var chunkZ = request.ChunkZ;

        var startX = chunkX * 32;
        var startZ = chunkZ * 32;

        var riverDistance = mapChunk.GetModdata<ushort[]>("riverDistance");
        var topRocks = mapChunk.TopRockIdMap;
        var heightMap = mapChunk.WorldGenTerrainHeightMap;

        for (var x = 0; x < 32; x++)
        {
            for (var z = 0; z < 32; z++)
            {
                var dist = riverDistance[(z * 32) + x];
                if (dist > 10) continue;

                int height = heightMap[(z * 32) + x];
                if (height < baseSeaLevel) continue;
                var diff = height - baseSeaLevel;

                double maxDist = gravelNoise.GetNoise(x + startX, z + startZ);
                maxDist *= 10;
                maxDist -= diff;

                if (dist < maxDist)
                {
                    var topRock = topRocks[(z * 32) + x];
                    var gravelId = gravelMappings[topRock];

                    BlockPos pos = new(startX + x, height + 1, startZ + z, 0);

                    blockAccessor.SetBlock(0, pos);
                    pos.Y--;
                    blockAccessor.SetBlock(gravelId, pos);
                    pos.Y--;
                    blockAccessor.SetBlock(gravelId, pos);
                    pos.Y--;
                    blockAccessor.SetBlock(gravelId, pos);
                }
            }
        }
    }
}
