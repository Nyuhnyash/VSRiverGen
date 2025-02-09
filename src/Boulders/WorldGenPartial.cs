using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace RiverGen;

/// <summary>
/// Uses in things like deposits. Does not generate for an entire chunk column.
/// </summary>
public abstract class WorldGenPartial : WorldGenBase
{
    public ICoreServerAPI sapi;
    public LCGRandom chunkRand;

    public abstract int ChunkRange { get; }

    public virtual void ChunkColumnGeneration(IChunkColumnGenerateRequest request)
    {
        var chunks = request.Chunks;
        var chunkX = request.ChunkX;
        var chunkZ = request.ChunkZ;
        for (var i = -ChunkRange; i <= ChunkRange; i++)
        {
            for (var j = -ChunkRange; j <= ChunkRange; j++)
            {
                GeneratePartial(chunks, chunkX, chunkZ, chunkX + i, chunkZ + j);
            }
        }
    }

    public virtual void GeneratePartial(IServerChunk[] chunks, int mainChunkX, int mainChunkZ, int generatingChunkX, int generatingChunkZ)
    {
    }
}
