using Vintagestory.API.Common;
using Vintagestory.ServerMods;


namespace RiverGen;

public static class CompatUtil {
    public static float GetScaledAdjustedTemperatureFloat(int unscaledTemp, int distToSealevel) {
        var type = typeof(ICoreAPI).Assembly.GetType("Vintagestory.API.Common.Climate", false) // 1.20+ 
                   ?? typeof(TerraGenConfig);

        return type.CallStaticMethod<float>("GetScaledAdjustedTemperatureFloat", unscaledTemp, distToSealevel);
    }
}
