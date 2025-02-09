using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace RiverGen;

// public class MuddyGravelBlock : BlockBehavior {
//
//     public int[] types;
//
//     public MuddyGravelBlock(Block block) : base(block) {
//     }
//
//     public void OnServerGameTick(IWorldAccessor world, BlockPos pos, float _)
//     {
//         // base.OnServerGameTick(world, pos, extra);
//     
//         // Only below sea level and if close to the river.
//         IWorldChunk chunk = world.BlockAccessor.GetChunk(pos.X / 32, 0, pos.Z / 32);
//     
//         ushort[] riverDistance = chunk.MapChunk.GetModdata<ushort[]>("riverDistance");
//         if (riverDistance == null) return;
//     
//         bool valid = riverDistance[(pos.Z % 32 * 32) + (pos.X % 32)] < 5;
//     
//         ushort[] wGenHeight = chunk.MapChunk.WorldGenTerrainHeightMap;
//         if (pos.Y != wGenHeight[(pos.Z % 32 * 32) + (pos.X % 32)] || pos.Y > TerraGenConfig.seaLevel - 1) return;
//         if (!valid || world.BlockAccessor.GetBlock(pos.Copy().Add(0, 1, 0)).LiquidLevel != 7) return;
//     
//         float deposit = depositNoise.GetPosNoise(pos.X, pos.Z);
//     
//         if (deposit > threshold)
//         {
//             float type = typeNoise.GetPosNoise(pos.X, pos.Z);
//             world.BlockAccessor.ExchangeBlock(clayBlockIds[Math.Min((int)(type * clayBlockIds.Length), clayBlockIds.Length - 1)], pos);
//         }
//     }
//     
//     public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom, out object extra)
//     {
//         extra = null;
//     
//         if (offThreadRandom.NextDouble() > growthChanceOnTick)
//         {
//             return false;
//         }
//     
//         return true;
//     }
// }

public class InitialClay : ModStdWorldGen
{
    private IBlockAccessor blockAccessor;

    
    public Noise depositNoise;
    public Noise typeNoise;

    public float growthChanceOnTick = 0.05f;
    public float threshold;

    public int[] clayBlockIds;

    private int muddyGravelId;

    public override double ExecuteOrder()
    {
        return 0.92;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        if (TerraGenConfig.DoDecorationPass && RiverConfig.Loaded.riverDeposits)
        {
            depositNoise = new Noise(api.World.Seed, 0.02f, 2);
            typeNoise = new Noise(api.World.Seed + 1, 0.001f, 1);
            threshold = 1 - RiverConfig.Loaded.clayDepositFrequency;

            clayBlockIds = api.World
                .SearchBlocks(new AssetLocation("game:rawclay-*-none"))
                .Select(clay => clay.Id)
                .ToArray();

            muddyGravelId = api.World.GetBlock(new AssetLocation("game:muddygravel")).Id;
            
            api.Event.ChunkColumnGeneration(ChunkColumnGeneration, EnumWorldGenPass.Vegetation, "standard");
            api.Event.GetWorldgenBlockAccessor(x =>
            {
                blockAccessor = x.GetBlockAccessor(true);
            });
        }
    }

    public void ChunkColumnGeneration(IChunkColumnGenerateRequest request)
    {
        var heightMap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;

        var chunks = request.Chunks;

        var riverDistance = chunks[0].MapChunk.GetModdata<ushort[]>("riverDistance");

        if (riverDistance == null) return;

        for (var x = 0; x < 32; x++)
        {
            for (var z = 0; z < 32; z++)
            {
                int height = heightMap[(z * 32) + x];

                if (chunks[height / 32].Data[ChunkIndex3d(x, height % 32, z)] != muddyGravelId) continue;

                TickBlock(new BlockPos((32 * request.ChunkX) + x, height, (32 * request.ChunkZ) + z, 0), riverDistance);
            }
        }
    }

    public void TickBlock(BlockPos pos, ushort[] riverDistance)
    {
        var valid = riverDistance[(pos.Z % 32 * 32) + (pos.X % 32)] < 5;
        if (pos.Y > TerraGenConfig.seaLevel - 1 || !valid || blockAccessor.GetBlock(pos.Copy().Add(0, 1, 0)).LiquidLevel != 7) return;
        var deposit = depositNoise.GetPosNoise(pos.X, pos.Z);

        if (deposit > threshold)
        {
            var type = typeNoise.GetPosNoise(pos.X, pos.Z);
            blockAccessor.SetBlock(clayBlockIds[(int)(type * clayBlockIds.Length)], pos); // Set instead of exchange.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChunkIndex3d(int x, int y, int z)
    {
        return (((y * chunksize) + z) * chunksize) + x;
    }
}
