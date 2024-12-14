using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace Rivers;

public class DisableGenTerra
{
    [HarmonyPatch(typeof(GenTerra))]
    [HarmonyPatch("StartServerSide")]
    public static class GenTerraDisable
    {
        [HarmonyPrefix]
        public static bool Prefix(GenTerra __instance, ICoreServerAPI api)
        {
            __instance.SetField("api", api);
            return false;
        }
    }

    [HarmonyPatch(typeof(GenTerra))]
    [HarmonyPatch("initWorldGen")]
    public static class RegenChunksDisable
    {
        [HarmonyPrefix]
        public static bool Prefix(GenTerra __instance)
        {
            ICoreServerAPI api = __instance.GetField<ICoreServerAPI>("api");
            NewGenTerra system = api.ModLoader.GetModSystem<NewGenTerra>();
            system.InitWorldGen();
            return false;
        }
    }
}