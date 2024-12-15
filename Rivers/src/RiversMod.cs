using HarmonyLib;
using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.Server;
using Vintagestory.ServerMods;


namespace Rivers;

public class RiversMod : ModSystem
{
    // public static float RiverSpeed { get; set; } = 1;

    // public IClientNetworkChannel clientChannel;
    // public IServerNetworkChannel serverChannel;

    public override double ExecuteOrder()
    {
        return 0;
    }

    // public override void Start(ICoreAPI api)
    // {
        // api.RegisterBlockBehaviorClass("riverblock", typeof(RiverBlockBehavior));
        //
        // api.RegisterBlockClass("fullalluvialblock", typeof(FullAlluvialBlock));
        //
        
        // if (RiverConfig.Loaded.riverDeposits) api.RegisterBlockBehaviorClass("muddygravel", typeof(MuddyGravelBlock));
    // }

    public override void StartServerSide(ICoreServerAPI api)
    {
        var worldGenHandler = (api.World as ServerMain).ModEventManager
            .GetWorldGenHandler("standard");
        
        foreach (var chunkGens in worldGenHandler.OnChunkColumnGen)
        {
            var i = chunkGens?.Find(x => x.Method.DeclaringType == typeof(GenTerra));
            chunkGens?.Remove(i);
        }

        var action = worldGenHandler.OnInitWorldGen.FirstOrDefault(a => a.Method.DeclaringType == typeof(GenTerra));
        worldGenHandler.OnInitWorldGen.Remove(action);

        string cfgFileName = "rivers.json";
        try
        {
            RiverConfig fromDisk;
            if ((fromDisk = api.LoadModConfig<RiverConfig>(cfgFileName)) == null)
            {
                api.StoreModConfig(RiverConfig.Loaded, cfgFileName);
            }
            else
            {
                RiverConfig.Loaded = fromDisk;
            }
        }
        catch
        {
            api.StoreModConfig(RiverConfig.Loaded, cfgFileName);
        }
        
        // serverChannel = api.Network.RegisterChannel("rivers")
        //     .RegisterMessageType(typeof(SpeedMessage));
        
        api.RegisterCommand(new RiverDebugCommand(api));

        // RiverSpeed = RiverConfig.Loaded.riverSpeed;
        // api.Event.PlayerJoin += Event_PlayerJoin;
    }

    // private void Event_PlayerJoin(IServerPlayer byPlayer)
    // {
    //     serverChannel.SendPacket(new SpeedMessage() { riverSpeed = RiverConfig.Loaded.riverSpeed }, byPlayer);
    // }

    // public static void OnSpeedMessage(SpeedMessage message)
    // {
    //     RiverSpeed = message.riverSpeed;
    // }

    // public override void Dispose()
    // {
    //     ChunkTesselatorManagerPatch.BottomChunk = null;
    //     BlockLayersPatches.Distances = null;
    //     if (patchedLocal)
    //     {
    //         harmony.UnpatchAll("rivers");
    //         Patched = false;
    //         patchedLocal = false;
    //     }
    //     SeaPatch.Multiplier = 0;
    // }
}

// [ProtoContract(ImplicitFields = ImplicitFields.AllPublic)]
// public class SpeedMessage
// {
//     public float riverSpeed = 1;
// }

public class RiverDebugCommand : ServerChatCommand
{
    public ICoreServerAPI sapi;

    public RiverDebugCommand(ICoreServerAPI sapi)
    {
        this.sapi = sapi;

        Command = "riverdebug";
        Description = "Debug command for rivers";
        Syntax = "/riverdebug";

        RequiredPrivilege = Privilege.ban;
    }

    public override void CallHandler(IPlayer player, int groupId, CmdArgs args)
    {
        try
        {
            WaypointMapLayer wp = sapi.ModLoader.GetModSystem<WorldMapManager>().MapLayers.FirstOrDefault((MapLayer ml) => ml is WaypointMapLayer) as WaypointMapLayer;

            int worldX = (int)player.Entity.Pos.X;
            int worldZ = (int)player.Entity.Pos.Z;
            int chunkX = worldX / 32;
            int chunkZ = worldZ / 32;

            int chunksInPlate = RiverConfig.Loaded.zonesInPlate * RiverConfig.Loaded.zoneSize / 32;

            int plateX = chunkX / chunksInPlate;
            int plateZ = chunkZ / chunksInPlate;

            TectonicPlate plate = ObjectCacheUtil.GetOrCreate(sapi, plateX.ToString() + "+" + plateZ.ToString(), () =>
            {
                return new TectonicPlate(sapi, plateX, plateZ);
            });

            Vec2d plateStart = plate.globalPlateStart;

            if (args[0] == "starts")
            {
                foreach (RiverSegment segment in plate.riverStarts)
                {
                    int r = sapi.World.Rand.Next(255);
                    int g = sapi.World.Rand.Next(255);
                    int b = sapi.World.Rand.Next(255);
                    MapRiver(wp, segment, r, g, b, player, plateStart);
                }

                sapi.SendMessage(player, 0, $"{riversMapped} rivers, {biggestRiver} biggest. {biggestX - sapi.World.DefaultSpawnPosition.X}, {biggestZ - sapi.World.DefaultSpawnPosition.Z}.", EnumChatType.Notification);
                riversMapped = 0;
                biggestRiver = 0;
                biggestX = 0;
                biggestZ = 0;

                wp.CallMethod("ResendWaypoints", player);
            }

            if (args[0] == "full")
            {
                foreach (River river in plate.rivers)
                {
                    int r = sapi.World.Rand.Next(255);
                    int g = sapi.World.Rand.Next(255);
                    int b = sapi.World.Rand.Next(255);

                    foreach (RiverNode node in river.nodes)
                    {
                        AddWaypoint(wp, "x", new Vec3d(node.startPos.X + plateStart.X, 0, node.startPos.Y + plateStart.Y), player.PlayerUID, r, g, b, node.startSize.ToString(), false);
                    }
                }

                wp.CallMethod("ResendWaypoints", player);
            }

            if (args[0] == "land")
            {
                foreach (TectonicZone zone in plate.zones)
                {
                    if (zone.ocean)
                    {
                        AddWaypoint(wp, "x", new Vec3d(plateStart.X + zone.localZoneCenterPosition.X, 0, plateStart.Y + zone.localZoneCenterPosition.Y), player.PlayerUID, 0, 100, 255, "Ocean", false);
                    }
                    else
                    {
                        AddWaypoint(wp, "x", new Vec3d(plateStart.X + zone.localZoneCenterPosition.X, 0, plateStart.Y + zone.localZoneCenterPosition.Y), player.PlayerUID, 255, 150, 150, "Land", false);
                    }
                }

                wp.CallMethod("ResendWaypoints", player);
            }

            if (args[0] == "ocean")
            {
                int oceanTiles = 0;

                foreach (TectonicZone zone in plate.zones)
                {
                    if (zone.ocean)
                    {
                        oceanTiles++;
                        AddWaypoint(wp, "x", new Vec3d(plateStart.X + zone.localZoneCenterPosition.X, 0, plateStart.Y + zone.localZoneCenterPosition.Y), player.PlayerUID, 0, 100, 255, "Ocean", false);
                    }
                }

                wp.CallMethod("ResendWaypoints", player);

                sapi.SendMessage(player, 0, $"{oceanTiles} ocean tiles.", EnumChatType.Notification);
            }

            if (args[0] == "coastal")
            {
                int coastalTiles = 0;

                foreach (TectonicZone zone in plate.zones)
                {
                    if (zone.coastal)
                    {
                        coastalTiles++;
                        AddWaypoint(wp, "x", new Vec3d(plateStart.X + zone.localZoneCenterPosition.X, 0, plateStart.Y + zone.localZoneCenterPosition.Y), player.PlayerUID, 255, 100, 255, "Ocean", false);
                    }
                }

                wp.CallMethod("ResendWaypoints", player);

                sapi.SendMessage(player, 0, $"{coastalTiles} ocean tiles.", EnumChatType.Notification);
            }

            if (args[0] == "clear")
            {
                wp.Waypoints.Clear();

                wp.CallMethod("ResendWaypoints", player);
            }
        }
        catch (Exception e)
        {
            sapi.SendMessage(player, 0, $"Error, {e.Message}", EnumChatType.Notification);
        }
    }

    public int riversMapped = 0;
    public int biggestRiver = 0;
    public int biggestX = 0;
    public int biggestZ = 0;

    public void MapRiver(WaypointMapLayer wp, RiverSegment segment, int r, int g, int b, IPlayer player, Vec2d plateStart)
    {
        AddWaypoint(wp, "x", new Vec3d(segment.startPos.X + plateStart.X, 0, segment.startPos.Y + plateStart.Y), player.PlayerUID, r, g, b, $"{segment.riverNode.startSize}");

        if (segment.riverNode.startSize > biggestRiver)
        {
            biggestRiver = (int)segment.riverNode.startSize;
            biggestX = (int)(segment.startPos.X + plateStart.X);
            biggestZ = (int)(segment.startPos.Y + plateStart.Y);
        }

        riversMapped++;
    }

    public static void AddWaypoint(WaypointMapLayer wp, string type, Vec3d worldPos, string playerUid, int r, int g, int b, string name, bool pin = true)
    {
        wp.Waypoints.Add(new Waypoint
        {
            Color = ColorUtil.ColorFromRgba(r, g, b, 255),
            Icon = type,
            Pinned = pin,
            Position = worldPos,
            OwningPlayerUid = playerUid,
            Title = name
        });
    }
}