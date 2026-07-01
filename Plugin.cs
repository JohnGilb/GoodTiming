using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ModSaveBackups;

namespace GoodTiming
{
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "com.johng.goodtiming";
        public const string PluginName = "Good Timing";
        public const string PluginVersion = "1.0.0";

        private Harmony _harmony;
        internal static Plugin Instance { get; private set; }
        public new BepInEx.Logging.ManualLogSource Logger { get { return base.Logger; } }

        // Tracks cooldown: Island Name -> Day on which the cooldown expires (i.e. day + 14)
        public Dictionary<string, int> Cooldowns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            Instance = this;
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginId);
            Logger.LogInfo("Good Timing plugin loaded!");
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
            }
        }
    }

    [Serializable]
    public class CooldownEntry
    {
        public string islandName;
        public int cooldownUntilDay;
    }

    [Serializable]
    public class GoodTimingSaveData
    {
        public List<CooldownEntry> cooldowns = new List<CooldownEntry>();
    }

    [HarmonyPatch(typeof(SaveLoadManager))]
    internal static class SaveLoadPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("SaveModData")]
        private static void PostfixSave()
        {
            if (Plugin.Instance == null) return;

            var container = new GoodTimingSaveData();
            foreach (var kv in Plugin.Instance.Cooldowns)
            {
                container.cooldowns.Add(new CooldownEntry
                {
                    islandName = kv.Key,
                    cooldownUntilDay = kv.Value
                });
            }

            try
            {
                ModSave.Save(Plugin.Instance.Info, container);
                Plugin.Instance.Logger.LogInfo("Saved Good Timing cooldowns.");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogError("Error saving Good Timing data: " + ex);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("LoadModData")]
        private static void PostfixLoad()
        {
            if (Plugin.Instance == null) return;

            Plugin.Instance.Cooldowns.Clear();

            try
            {
                GoodTimingSaveData data;
                if (ModSave.Load(Plugin.Instance.Info, out data))
                {
                    if (data != null && data.cooldowns != null)
                    {
                        foreach (var entry in data.cooldowns)
                        {
                            if (!string.IsNullOrEmpty(entry.islandName))
                            {
                                Plugin.Instance.Cooldowns[entry.islandName] = entry.cooldownUntilDay;
                            }
                        }
                        Plugin.Instance.Logger.LogInfo(string.Format("Loaded {0} Good Timing cooldowns.", Plugin.Instance.Cooldowns.Count));
                    }
                }
                else
                {
                    Plugin.Instance.Logger.LogInfo("No Good Timing save file found (normal for first load).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.LogError("Error loading Good Timing data: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(PortDude), "OnTriggerEnter")]
    internal static class PortDudeEnterPatch
    {
        private static void Postfix(PortDude __instance, Collider other)
        {
            if (other != null && other.CompareTag("Player"))
            {
                try
                {
                    CheckAndReplenishPort(__instance);
                }
                catch (Exception ex)
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Instance.Logger.LogError("Error in OnTriggerEnter CheckAndReplenishPort: " + ex);
                    }
                }
            }
        }

        private static void CheckAndReplenishPort(PortDude dude)
        {
            if (dude == null) return;
            Port port = dude.GetPort();
            if (port == null) return;

            IslandMarket market = port.GetComponent<IslandMarket>();
            if (market == null) return;

            if (market.production == null || market.currentSupply == null) return;

            string islandName = port.GetPortName();
            if (string.IsNullOrEmpty(islandName)) return;

            int currentDay = GameState.day;

            // Check if island is on cooldown
            int cooldownUntilDay;
            if (Plugin.Instance != null && Plugin.Instance.Cooldowns.TryGetValue(islandName, out cooldownUntilDay))
            {
                if (currentDay < cooldownUntilDay)
                {
                    Plugin.Instance.Logger.LogInfo(string.Format("Player arrived at {0}, but the replenishment effect is on cooldown until day {1} (Current day: {2}).", islandName, cooldownUntilDay, currentDay));
                    return;
                }
            }

            bool boostedAny = false;
            int count = Mathf.Min(market.production.Length, market.currentSupply.Length);
            for (int i = 0; i < count; i++)
            {
                float prod = market.production[i];
                if (prod > 0f)
                {
                    float minSupply = 3f * prod;
                    if (market.currentSupply[i] < minSupply)
                    {
                        Plugin.Instance.Logger.LogInfo(string.Format("Replenishing good index {0} on {1} from {2} to {3}.", i, islandName, market.currentSupply[i], minSupply));
                        market.currentSupply[i] = minSupply;
                        boostedAny = true;
                    }
                }
            }

            if (boostedAny)
            {
                // Put on cooldown for 14 in-game days
                int nextAllowedDay = currentDay + 14;
                if (Plugin.Instance != null)
                {
                    Plugin.Instance.Cooldowns[islandName] = nextAllowedDay;
                    Plugin.Instance.Logger.LogInfo(string.Format("Port supplies replenished for {0}. Cooldown active until day {1}.", islandName, nextAllowedDay));
                }

                // Update price reports & refresh UI
                market.UpdateSelfPriceReportForPlayer();
                if (EconomyUI.instance != null && EconomyUI.instance.uiActive)
                {
                    EconomyUI.instance.RefreshPage();
                }
            }
            else
            {
                if (Plugin.Instance != null)
                {
                    Plugin.Instance.Logger.LogInfo(string.Format("Player arrived at {0}, but all produced goods are already at or above 3x production. No replenishment needed.", islandName));
                }
            }
        }
    }
}
