using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace RiverGen;

public class GravityBlocksPass : ModStdWorldGen
{
    public ICoreServerAPI sapi;

    public HashSet<int> unstableBlockIds = new();

    public override bool ShouldLoad(EnumAppSide side)
    {
        return side == EnumAppSide.Server;
    }

    public override double ExecuteOrder()
    {
        // Execute after block layers have been placed.
        return 0.5;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        if (RiverConfig.Loaded.fixGravityBlocks)
        {
            api.Event.InitWorldGenerator(InitWorldGen, "standard");
            api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Terrain, "standard");
        }
    }

    public void InitWorldGen()
    {
        // Find all blocks that can fall once. Soil only has this if gravity is enabled.
        foreach (var block in sapi.World.Blocks)
        {
            if (block.HasBehavior<BlockBehaviorUnstableFalling>())
            {
                unstableBlockIds.Add(block.Id);
            }
        }
    }

    public void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        var chunks = request.Chunks;
        var terrainHeightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;
        var topRockIdMap = request.Chunks[0].MapChunk.TopRockIdMap;

        for (var localZ = 0; localZ < 32; localZ++)
        {
            for (var localX = 0; localX < 32; localX++)
            {
                int surfaceLevel = terrainHeightMap[RiverMath.ChunkIndex2d(localX, localZ)];

                var chunkY = surfaceLevel / 32;

                if (unstableBlockIds.Contains(chunks[chunkY].Data[RiverMath.ChunkIndex3d(localX, surfaceLevel % 32, localZ)]))
                {
                    var illegal = false;

                    var index = 1;

                    int newChunkY;

                    while (true)
                    {
                        newChunkY = (surfaceLevel - index) / 32;

                        var id = chunks[newChunkY].Data[RiverMath.ChunkIndex3d(localX, (surfaceLevel - index) % 32, localZ)];

                        if (unstableBlockIds.Contains(id))
                        {
                            index++;
                            continue;
                        }

                        if (id == 0) illegal = true;

                        break;
                    }

                    // 99% of the time not called.
                    if (illegal)
                    {

                        // Set the air block below to rock.
                        newChunkY = (surfaceLevel - index) / 32;
                        chunks[newChunkY].Data[RiverMath.ChunkIndex3d(localX, (surfaceLevel - index) % 32, localZ)] = topRockIdMap[RiverMath.ChunkIndex2d(localX, localZ)];
                    }
                }
            }
        }
    }
}
