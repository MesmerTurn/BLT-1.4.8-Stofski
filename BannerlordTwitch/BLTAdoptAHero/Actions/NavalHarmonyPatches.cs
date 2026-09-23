using HarmonyLib;
using System;
using System.Reflection;
using BannerlordTwitch.Util;

namespace BLTAdoptAHero.Actions
{
    internal static class NavalHarmonyPatches
    {
        public static void ApplyIfAvailable(Harmony navalharmony)
        {
            try
            {
                var spawnLogicType = AccessTools.TypeByName(
                    "NavalDLC.Missions.MissionLogics.DefaultNavalMissionAgentSpawnLogic");
                var shipTradeType = AccessTools.TypeByName(
                    "NavalDLC.CampaignBehaviors.ShipTradeCampaignBehavior");

                if (spawnLogicType == null || shipTradeType == null)
                    return; // Naval DLC not loaded - nothing to patch

                var isAnyTeamsUnfilled = AccessTools.Method(spawnLogicType, "IsAnyTeamsUnfilled");
                if (isAnyTeamsUnfilled != null)
                {
                    navalharmony.Patch(isAnyTeamsUnfilled,
                        prefix: new HarmonyMethod(typeof(NavalHarmonyPatches), nameof(IsAnyTeamsUnfilledPrefix)));
                }

                var onShipOwnerChanged = AccessTools.Method(shipTradeType, "OnShipOwnerChanged");
                if (onShipOwnerChanged != null)
                {
                    navalharmony.Patch(onShipOwnerChanged,
                        finalizer: new HarmonyMethod(typeof(NavalHarmonyPatches), nameof(SuppressOnShipOwnerChangedFinalizer)));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[BLT] NavalHarmonyPatches.ApplyIfAvailable failed: {ex}");
            }
        }

        static bool IsAnyTeamsUnfilledPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        static Exception SuppressOnShipOwnerChangedFinalizer(Exception __exception) => null;
    }
}