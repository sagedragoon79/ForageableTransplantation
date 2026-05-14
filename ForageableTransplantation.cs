using MelonLoader;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System;

[assembly: MelonInfo(typeof(ForageableTransplantation.Relocator), "Forageable Transplantation", "1.1.7", "SageDragoon")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace ForageableTransplantation
{
    public class Relocator : MelonMod
    {
        // ── Config ───────────────────────────────────────────────────────────
        public static MelonPreferences_Entry<bool> ModEnabled;
        public static MelonPreferences_Entry<bool> RelocateHerbs;
        public static MelonPreferences_Entry<bool> RelocateMushrooms;
        public static MelonPreferences_Entry<bool> RelocateGreens;
        public static MelonPreferences_Entry<bool> RelocateRoots;
        public static MelonPreferences_Entry<bool> RelocateNuts;
        public static MelonPreferences_Entry<bool> RelocateWillow;
        public static MelonPreferences_Entry<bool> RelocateBerries;
        public static MelonPreferences_Entry<int>  GoldCostToRelocate;

        public static Dictionary<int, PendingRelocation> PendingRelocations
            = new Dictionary<int, PendingRelocation>();

        public class PendingRelocation
        {
            public int instanceId;
            public string baseName;
            public Vector3 destination;
            public GameObject nativeConstructSite;
            public System.Collections.IDictionary replenishRates;
            public System.Collections.IDictionary maxReplenishRates;
            public List<int[]> seasonWindows;
        }

        public static Dictionary<string, GameObject> ForageablePrefabs = new Dictionary<string, GameObject>();
        public static GameManager gameManager;

        private static int lastKnownYear = -1;
        private static int lastKnownDayOfYear = -1;

        public override void OnInitializeMelon()
        {
            // Kill switch FIRST — if TW is loaded, bail before creating prefs.
            // TW is a superset of FT; running both stacks duplicate Harmony
            // patches. Bailing pre-prefs also keeps FT out of mod settings UIs
            // (Keep Clarity, MelonPrefManager, etc.) where its dormant entries
            // would otherwise show as live controls that do nothing.
            foreach (var melon in MelonBase.RegisteredMelons)
            {
                if (melon == this) continue;
                string detName = melon.Info?.Name ?? "";
                if (detName.IndexOf("Tended Wilds", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LoggerInstance.Warning("Tended Wilds detected — Forageable Transplantation is auto-disabling. " +
                        "TW already includes all FT functionality. Remove ForageableTransplantation.dll to suppress this message.");
                    return;
                }
            }

            // ── Config setup ────────────────────────────────────────────────
            var cat = MelonPreferences.CreateCategory("ForageableTransplantation");

            ModEnabled = cat.CreateEntry("ModEnabled", true,
                display_name: "Mod Enabled",
                description: "Master switch to enable/disable the mod. Requires game restart to take effect.");

            RelocateHerbs = cat.CreateEntry("RelocateHerbs", true,
                display_name: "Relocate Herbs", description: "Allow relocating herb patches.");
            RelocateMushrooms = cat.CreateEntry("RelocateMushrooms", true,
                display_name: "Relocate Mushrooms", description: "Allow relocating mushroom clusters.");
            RelocateGreens = cat.CreateEntry("RelocateGreens", true,
                display_name: "Relocate Greens", description: "Allow relocating greens patches.");
            RelocateRoots = cat.CreateEntry("RelocateRoots", true,
                display_name: "Relocate Roots", description: "Allow relocating root concentrations.");
            RelocateNuts = cat.CreateEntry("RelocateNuts", true,
                display_name: "Relocate Nuts", description: "Allow relocating hazelnut bushes.");
            RelocateWillow = cat.CreateEntry("RelocateWillow", true,
                display_name: "Relocate Willow", description: "Allow relocating willow bushes.");
            RelocateBerries = cat.CreateEntry("RelocateBerries", true,
                display_name: "Relocate Berries", description: "Allow relocating berry bushes (hawthorn, sumac).");
            GoldCostToRelocate = cat.CreateEntry("GoldCostToRelocate", 0,
                display_name: "Gold Cost to Relocate",
                description: "Gold required per relocation (0 = free, just labor). Applied to all forageable types.");

            if (!ModEnabled.Value)
            {
                LoggerInstance.Msg("Forageable Transplantation is DISABLED via config.");
                return;
            }

            try
            {
                var harmony = new HarmonyLib.Harmony("com.sagedragoon.forageabletransplantation");

                System.Type buildManagerType = null;
                System.Type buildSiteType = null;
                System.Type terrainBuildSiteType = null;

                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (buildManagerType == null) buildManagerType = asm.GetType("BuildManager");
                    if (buildSiteType == null) buildSiteType = asm.GetType("BuildSite");
                    if (terrainBuildSiteType == null) terrainBuildSiteType = asm.GetType("TerrainObjectBuildsite");
                    if (buildManagerType != null && buildSiteType != null && terrainBuildSiteType != null) break;
                }

                if (buildManagerType != null)
                {
                    var relocate = buildManagerType.GetMethod("Relocate",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (relocate != null)
                    {
                        harmony.Patch(relocate, prefix: new HarmonyLib.HarmonyMethod(
                            typeof(RelocatePatches).GetMethod("RelocatePrefix", BindingFlags.Public | BindingFlags.Static)));
                        MelonLogger.Msg("Patched BuildManager.Relocate");
                    }
                }

                if (buildSiteType != null)
                {
                    var initialize = buildSiteType.GetMethod("Initialize",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (initialize != null)
                    {
                        harmony.Patch(initialize, postfix: new HarmonyLib.HarmonyMethod(
                            typeof(RelocatePatches).GetMethod("BuildSiteInitializePostfix", BindingFlags.Public | BindingFlags.Static)));
                        MelonLogger.Msg("Patched BuildSite.Initialize");
                    }
                }

                if (terrainBuildSiteType != null)
                {
                    var onBuilt = terrainBuildSiteType.GetMethod("OnBuiltPrefabInstantiated",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (onBuilt != null)
                    {
                        harmony.Patch(onBuilt, prefix: new HarmonyLib.HarmonyMethod(
                            typeof(RelocatePatches).GetMethod("OnBuiltPrefabInstantiatedTerrain", BindingFlags.Public | BindingFlags.Static)));
                        MelonLogger.Msg("Patched TerrainObjectBuildsite.OnBuiltPrefabInstantiated");
                    }
                    else
                    {
                        var onBuiltBase = buildSiteType?.GetMethod("OnBuiltPrefabInstantiated",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (onBuiltBase != null)
                        {
                            harmony.Patch(onBuiltBase, prefix: new HarmonyLib.HarmonyMethod(
                                typeof(RelocatePatches).GetMethod("OnBuiltPrefabInstantiatedBase", BindingFlags.Public | BindingFlags.Static)));
                            MelonLogger.Msg("Patched BuildSite.OnBuiltPrefabInstantiated (base fallback)");
                        }
                    }
                }

                MelonLogger.Msg("Forageable Transplantation v1.1.4: Init complete.");

                // Note: Keep Clarity registration intentionally happens in
                // OnSceneWasLoaded("Map"), not here. FT loads alphabetically
                // before KeepClarity, so its types aren't resolvable yet at
                // OnInitializeMelon time. By the time any scene loads, every
                // mod has finished init and KC's API is reachable.
            }
            catch (System.Exception ex)
            {
                MelonLogger.Error($"OnInitializeMelon error: {ex}");
            }
        }

        private bool _kcRegistered;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Optional: register with Keep Clarity's settings panel once it's
            // loaded. Done here rather than in OnInitializeMelon because of
            // alphabetical mod load order — KC isn't reachable from FT's init.
            if (!_kcRegistered)
            {
                KeepClarityIntegration.TryRegisterAll();
                _kcRegistered = true;
            }

            if (buildIndex > 0)
            {
                lastKnownYear = -1;
                lastKnownDayOfYear = -1;
                gameManager = null;
                MelonCoroutines.Start(ScoutForageablePrefabs());
                MelonCoroutines.Start(ApplyBuildingDataChain());
                MelonCoroutines.Start(InitializeGameManagerDelayed());
                MelonCoroutines.Start(YearChangeWatcher());
            }
        }

        // Tracks the result of the most recent ApplyBuildingData run so the
        // chain coroutine can decide whether to escalate to a safety-net pass.
        // > 0 means the pass enabled at least one forageable (save was loaded
        // in time) and no further passes are needed.
        private static int _lastApplyCount = -1;

        // Chain-on-failure: only escalates to a safety-net pass if the prior
        // pass found zero forageables. On a normal load, pass 1 succeeds and
        // the coroutine exits — no idle sleeps or wasted scans. Replaces the
        // earlier "schedule three passes unconditionally" approach which paid
        // the full Resources.FindObjectsOfTypeAll cost three times per load.
        private IEnumerator ApplyBuildingDataChain()
        {
            // Pass 1 — initial scan after GlobalAssets is ready (10s wait
            // lives inside ApplyBuildingData itself).
            _lastApplyCount = -1;
            yield return ApplyBuildingData();
            if (_lastApplyCount > 0) yield break;

            // Pass 2 — safety net for slow saves that hadn't spawned all
            // forageables when pass 1 ran.
            yield return new WaitForSeconds(30f);
            MelonLogger.Msg("ApplyBuildingData: Pass 1 found 0 forageables — running safety-net pass after +30s.");
            _lastApplyCount = -1;
            yield return ApplyBuildingData();
            if (_lastApplyCount > 0) yield break;

            // Pass 3 — last resort for very slow loads.
            yield return new WaitForSeconds(60f);
            MelonLogger.Msg("ApplyBuildingData: Pass 2 still found 0 — running last-resort pass.");
            yield return ApplyBuildingData();
        }

        private IEnumerator InitializeGameManagerDelayed()
        {
            while (gameManager == null)
            {
                yield return new WaitForSeconds(2f);
                gameManager = GameObject.FindObjectOfType<GameManager>();
                if (gameManager == null)
                    gameManager = GameObject.Find("GameManager")?.GetComponent<GameManager>();
                if (gameManager != null)
                    MelonLogger.Msg("GameManager found!");
            }
        }

        private IEnumerator YearChangeWatcher()
        {
            yield return new WaitForSeconds(10f);
            MelonLogger.Msg("YearChangeWatcher: Started.");

            while (true)
            {
                yield return new WaitForSeconds(5f);

                if (gameManager == null) continue;

                try
                {
                    var tm = gameManager.timeManager;
                    if (tm == null) continue;

                    var dateObj = tm.GetType()
                        .GetProperty("currentDate",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(tm);

                    if (dateObj == null) continue;

                    var dateType = dateObj.GetType();

                    int currentYear = -1;
                    var yearProp = dateType.GetProperty("year", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var yearField = dateType.GetField("year", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (yearProp != null) currentYear = (int)yearProp.GetValue(dateObj);
                    else if (yearField != null) currentYear = (int)yearField.GetValue(dateObj);

                    int currentDayOfYear = -1;
                    var dayProp = dateType.GetProperty("dayOfYear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var dayField = dateType.GetField("dayOfYear", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (dayProp != null) currentDayOfYear = (int)dayProp.GetValue(dateObj);
                    else if (dayField != null) currentDayOfYear = (int)dayField.GetValue(dateObj);

                    bool yearChanged = false;

                    if (currentYear != -1 && lastKnownYear != -1 && currentYear != lastKnownYear)
                    {
                        yearChanged = true;
                        MelonLogger.Msg($"YearChangeWatcher: Year changed {lastKnownYear} -> {currentYear}.");
                    }
                    else if (currentDayOfYear != -1 && lastKnownDayOfYear != -1
                             && currentDayOfYear < lastKnownDayOfYear
                             && lastKnownDayOfYear > 300)
                    {
                        yearChanged = true;
                        MelonLogger.Msg($"YearChangeWatcher: Year rollover detected via dayOfYear ({lastKnownDayOfYear} -> {currentDayOfYear}).");
                    }

                    if (currentYear != -1) lastKnownYear = currentYear;
                    if (currentDayOfYear != -1) lastKnownDayOfYear = currentDayOfYear;

                    if (yearChanged)
                    {
                        MelonLogger.Msg("YearChangeWatcher: Reapplying building data...");
                        MelonCoroutines.Start(ApplyBuildingData());
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Error($"YearChangeWatcher error: {ex.Message}");
                }
            }
        }

        private IEnumerator ScoutForageablePrefabs()
        {
            yield return new WaitForSeconds(15f);
            MelonLogger.Msg("PrefabScout: Starting...");

            // Single ForageableResource scan replaces the previous three-pass
            // approach (asset GameObject scan + scene GameObject scan +
            // ForagerShack-fields fallback).
            //
            // The old ForagerShack-fields path returned nothing on maps where
            // the player hadn't built or opened the build menu for a Forager
            // Shack — the shack prefab isn't in memory until something
            // references it. On those maps the cache stayed empty and every
            // relocation failed with "No prefab found", landing the destination
            // as a blueberry placeholder.
            //
            // ForageableResource is the component the relocation path itself
            // checks on each candidate — anything we cache here is by
            // definition a valid relocation target. FindObjectsOfTypeAll<T>
            // returns both prefab assets and scene instances, so we get the
            // asset prefabs FF loads with the map even when nothing's been
            // built yet. Dedupe by lowercased gameObject name to match the
            // SpawnForageableAtDestination lookup format.
            try
            {
                // Two-pass scan. Prefab assets (scene.IsValid() == false) are
                // stable across the entire session; scene instances can be
                // destroyed when the player relocates/harvests them, leaving
                // the cache holding a Unity-null reference and breaking the
                // next relocation of the same variant. Pass 1 grabs every
                // prefab asset; pass 2 fills any remaining variants from
                // scene instances as a fallback.
                int loadedPrefabs = 0, loadedInstances = 0;
                var all = Resources.FindObjectsOfTypeAll<ForageableResource>();
                foreach (var fr in all)
                {
                    if (fr == null) continue;
                    var go = fr.gameObject;
                    if (go == null || go.scene.IsValid()) continue; // pass 1: prefabs only
                    string baseName = go.name.Replace("(Clone)", "").Trim().ToLower();
                    if (string.IsNullOrEmpty(baseName)) continue;
                    if (baseName.Contains("blueberry")) continue;
                    if (baseName.Contains("deco")) continue;
                    if (ForageablePrefabs.ContainsKey(baseName)) continue;
                    ForageablePrefabs[baseName] = go;
                    loadedPrefabs++;
                }
                foreach (var fr in all)
                {
                    if (fr == null) continue;
                    var go = fr.gameObject;
                    if (go == null || !go.scene.IsValid()) continue; // pass 2: scene instances
                    string baseName = go.name.Replace("(Clone)", "").Trim().ToLower();
                    if (string.IsNullOrEmpty(baseName)) continue;
                    if (baseName.Contains("blueberry")) continue;
                    if (baseName.Contains("deco")) continue;
                    if (ForageablePrefabs.ContainsKey(baseName)) continue;
                    ForageablePrefabs[baseName] = go;
                    loadedInstances++;
                }
                MelonLogger.Msg($"PrefabScout: Loaded {loadedPrefabs} prefab asset(s) + {loadedInstances} scene-instance fallback(s).");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"PrefabScout: ForageableResource scan failed: {ex.Message}");
            }

            MelonLogger.Msg($"PrefabScout: Found {ForageablePrefabs.Count} prefabs.");
        }

        // seasons is a PROPERTY (public getter, private setter) — must use GetProperty not GetField
        // Pair<int,int> has public readonly fields: first, second
        public static List<int[]> CopySeasonWindows(Component seasonalComp)
        {
            var result = new List<int[]>();
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                var seasonsProp = seasonalComp.GetType().GetProperty("seasons", flags);
                if (seasonsProp == null)
                {
                    MelonLogger.Warning("  CopySeasonWindows: 'seasons' property not found.");
                    return result;
                }

                var seasonsList = seasonsProp.GetValue(seasonalComp) as System.Collections.IList;
                if (seasonsList == null || seasonsList.Count == 0)
                {
                    MelonLogger.Warning("  CopySeasonWindows: seasons list is null or empty.");
                    return result;
                }

                var pairType = seasonsList[0].GetType();
                var firstField = pairType.GetField("first", flags);
                var secondField = pairType.GetField("second", flags);

                if (firstField == null || secondField == null)
                {
                    MelonLogger.Warning("  CopySeasonWindows: Pair first/second fields not found.");
                    return result;
                }

                foreach (var pair in seasonsList)
                {
                    int start = (int)firstField.GetValue(pair);
                    int end = (int)secondField.GetValue(pair);
                    result.Add(new int[] { start, end });
                    MelonLogger.Msg($"  Season window: {start}-{end}");
                }

                MelonLogger.Msg($"  Copied {result.Count} season window(s) from SeasonalComponentBase.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"  CopySeasonWindows failed: {ex.Message}");
            }
            return result;
        }

        public static void ApplySeasonWindows(Component seasonalComp, List<int[]> windows)
        {
            if (windows == null || windows.Count == 0) return;
            try
            {
                var addSeason = seasonalComp.GetType().GetMethod("AddSeason",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (addSeason == null)
                {
                    MelonLogger.Warning("  ApplySeasonWindows: AddSeason method not found.");
                    return;
                }
                foreach (var window in windows)
                    addSeason.Invoke(seasonalComp, new object[] { window[0], window[1] });
                MelonLogger.Msg($"  Applied {windows.Count} season window(s) to SeasonalComponentBase.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"  ApplySeasonWindows failed: {ex.Message}");
            }
        }

        public static void SpawnForageableAtDestination(string baseName, PendingRelocation pending, GameObject blueberryToDestroy = null)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            MelonLogger.Msg($"SpawnForageableAtDestination: '{baseName}' at {pending.destination}");

            GameObject prefab;
            ForageablePrefabs.TryGetValue(baseName, out prefab);

            // Cached entry might be Unity-null if it was a scene instance that
            // got destroyed by a previous relocation/harvest. Or there might be
            // no entry at all. Either way, re-scan for a fresh prefab asset
            // before giving up — eliminates the "permanently broken after first
            // failure" mode the prior cache had.
            if (prefab == null || !prefab)
            {
                foreach (var fr in Resources.FindObjectsOfTypeAll<ForageableResource>())
                {
                    if (fr == null) continue;
                    var go = fr.gameObject;
                    if (go == null) continue;
                    if (go.name.Replace("(Clone)", "").Trim().ToLower() != baseName) continue;
                    if (!go.scene.IsValid()) { prefab = go; break; } // prefer prefab asset
                    if (prefab == null) prefab = go;                 // scene-instance fallback
                }
                if (prefab != null)
                {
                    ForageablePrefabs[baseName] = prefab;
                    MelonLogger.Msg($"SpawnForageableAtDestination: Re-resolved prefab for '{baseName}' (cache was stale).");
                }
                else
                {
                    MelonLogger.Error($"No prefab found for '{baseName}' after re-scan! Blueberry placeholder will remain.");
                    ForageablePrefabs.Remove(baseName);
                    return;
                }
            }

            GameObject spawned = GameObject.Instantiate(prefab, pending.destination, Quaternion.identity);
            spawned.name = prefab.name.Replace("(Clone)", "").Trim();
            MelonLogger.Msg($"  Spawned '{spawned.name}' (inactive)");

            var forageComp = spawned.GetComponent("ForageableResource");
            if (forageComp != null)
            {
                var fType = (forageComp as Component).GetType();

                // Step 1: Initialize replenish dict structure
                var setRandom = fType.GetMethod("SetRandomReplenishRateOnSpawn", flags);
                if (setRandom != null)
                    try { setRandom.Invoke(forageComp, null); MelonLogger.Msg("  Called SetRandomReplenishRateOnSpawn."); }
                    catch (System.Exception ex) { MelonLogger.Warning($"  SetRandomReplenishRateOnSpawn failed: {ex.Message}"); }

                // Step 2: Restore saved replenish rates from original
                if (pending.replenishRates != null && pending.replenishRates.Count > 0)
                {
                    var setAmount = fType.GetMethod("SetAmountToReplenish", flags, null,
                        new Type[] { typeof(Item), typeof(uint) }, null);
                    if (setAmount != null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in pending.replenishRates)
                            try { setAmount.Invoke(forageComp, new object[] { entry.Key, entry.Value }); } catch { }
                        MelonLogger.Msg($"  Applied {pending.replenishRates.Count} rate(s).");
                    }
                    else
                    {
                        var rf = fType.GetField("itemToReplenishRateDict", flags);
                        if (rf != null) rf.SetValue(forageComp, pending.replenishRates);
                        var mrf = fType.GetField("itemToMaxReplenishRateDict", flags);
                        if (mrf != null && pending.maxReplenishRates != null)
                            mrf.SetValue(forageComp, pending.maxReplenishRates);
                    }
                }
                else MelonLogger.Warning("  No saved replenish rates.");

                // Step 3: Reset flags so Unity's Start() -> PostInit() runs fully
                var itemsAddedField = fType.GetField("itemsAddedForSeason", flags);
                var initializedField = fType.GetField("initialized", flags);
                if (itemsAddedField != null) itemsAddedField.SetValue(forageComp, false);
                if (initializedField != null) initializedField.SetValue(forageComp, false);
                MelonLogger.Msg("  Reset initialized and itemsAddedForSeason flags.");
            }
            else MelonLogger.Warning("  No ForageableResource component on spawned object!");

            // Step 4: Restore season windows to SeasonalComponentBase BEFORE SetActive
            // seasons is populated at world gen and not serialized into prefabs —
            // freshly instantiated objects have an empty seasons list.
            var seasonalComp = spawned.GetComponent("SeasonalComponentBase");
            if (seasonalComp != null)
            {
                if (pending.seasonWindows != null && pending.seasonWindows.Count > 0)
                    ApplySeasonWindows(seasonalComp as Component, pending.seasonWindows);
                else
                    MelonLogger.Warning("  No season windows to apply — seasonal replenishment may not work.");
            }

            // Step 5: Activate — triggers Awake() then Start() next frame
            // Start() sees initialized=false and seasons list already populated
            // -> PostInit() runs -> HandleDayChanged() evaluates correctly
            spawned.SetActive(true);
            MelonLogger.Msg($"SpawnForageableAtDestination: SUCCESS - '{baseName}' at {pending.destination}");

            // Directly destroy the blueberry clone that the game built at the
            // destination. We have a direct reference from OnBuiltPrefabInstantiated.
            if (blueberryToDestroy != null && blueberryToDestroy != spawned)
            {
                MelonLogger.Msg($"Destroying intermediate blueberry: {blueberryToDestroy.name}");
                GameObject.Destroy(blueberryToDestroy);
            }
        }

        private IEnumerator ApplyBuildingData()
        {
            yield return new WaitForSeconds(10f);

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // Load Bush_Blueberry_Small BuildingData directly from GlobalAssets.
            // This is the canonical serialized asset — available as soon as core game
            // assets load, regardless of what's spawned on the current map. Works on
            // any map type (including ones without natural blueberry spawns) and is
            // compatible with slow settlement creation (map preview mods).
            object templateBD = null;
            System.Type buildingDataType = null;

            int attempts = 0;
            const int maxAttempts = 60;  // 60 × 2s = 2 minutes — plenty for GlobalAssets init
            while (attempts < maxAttempts)
            {
                attempts++;
                try
                {
                    var bd = GlobalAssets.buildingSetupData?.GetBuildingData("Bush_Blueberry_Small");
                    if (bd != null)
                    {
                        templateBD = bd;
                        buildingDataType = bd.GetType();
                        MelonLogger.Msg($"ApplyBuildingData: Loaded 'Bush_Blueberry_Small' from GlobalAssets (attempt {attempts}).");
                        break;
                    }
                }
                catch (System.Exception ex)
                {
                    if (attempts <= 3)
                        MelonLogger.Warning($"ApplyBuildingData: GlobalAssets access error: {ex.Message}");
                }

                if (attempts <= 3 || attempts % 20 == 0)
                    MelonLogger.Warning($"ApplyBuildingData: GlobalAssets not ready yet (attempt {attempts}/{maxAttempts}), retrying...");
                yield return new WaitForSeconds(2f);
            }

            if (templateBD == null)
            {
                MelonLogger.Error("ApplyBuildingData: Could not load Bush_Blueberry_Small from GlobalAssets after 2 minutes.");
                yield break;
            }

            MelonLogger.Msg("Forageable Transplantation: Applying BuildingData...");

            var f_identifier = buildingDataType.GetField("identifier", flags);
            var f_placeablePrefab = buildingDataType.GetField("placeablePrefab", flags);
            var f_buildSitePrefab = buildingDataType.GetField("buildSitePrefab", flags);
            var f_deconstructSitePrefab = buildingDataType.GetField("deconstructSitePrefab", flags);
            var f_destinationPrefab = buildingDataType.GetField("destinationPrefab", flags);
            var f_gridSize = buildingDataType.GetField("gridSize", flags);
            var f_placementGridSettings = buildingDataType.GetField("placementGridSettings", flags);
            var f_goldToRelocate = buildingDataType.GetField("goldRequiredToRelocate", flags);
            var f_workToConstruct = buildingDataType.GetField("workRequiredToConstruct", flags);
            var f_workToDeconstruct = buildingDataType.GetField("workRequiredToDeconstruct", flags);
            var f_defaultBuilders = buildingDataType.GetField("defaultBuilders", flags);
            var f_maxBuilders = buildingDataType.GetField("maxBuilders", flags);
            var f_buildGroup = buildingDataType.GetField("buildGroup", flags);
            var f_buildsiteClearingMode = buildingDataType.GetField("buildsiteClearingMode", flags);
            var f_clearDetailsBorder = buildingDataType.GetField("clearDetailsBorderWidth", flags);
            var f_prefabEntries = buildingDataType.GetField("prefabEntries", flags);
            var f_diagPrefabEntries = buildingDataType.GetField("diagPrefabEntries", flags);

            var templateEntries = f_prefabEntries?.GetValue(templateBD) as System.Collections.IList;
            if (templateEntries == null || templateEntries.Count == 0) { MelonLogger.Error("No prefabEntries found."); yield break; }

            object blueberryEntry = templateEntries[0];
            var entryType = blueberryEntry.GetType();

            object val_diagPrefabEntries = f_diagPrefabEntries?.GetValue(templateBD);

            object val_placeablePrefab = f_placeablePrefab?.GetValue(templateBD);
            object val_buildSitePrefab = f_buildSitePrefab?.GetValue(templateBD);
            object val_deconstructSitePrefab = f_deconstructSitePrefab?.GetValue(templateBD);
            object val_destinationPrefab = f_destinationPrefab?.GetValue(templateBD);
            object val_gridSize = f_gridSize?.GetValue(templateBD);
            object val_placementGridSettings = f_placementGridSettings?.GetValue(templateBD);
            object val_goldToRelocate = f_goldToRelocate?.GetValue(templateBD);
            object val_workToConstruct = f_workToConstruct?.GetValue(templateBD);
            object val_workToDeconstruct = f_workToDeconstruct?.GetValue(templateBD);
            object val_defaultBuilders = f_defaultBuilders?.GetValue(templateBD);
            object val_maxBuilders = f_maxBuilders?.GetValue(templateBD);
            object val_buildGroup = f_buildGroup?.GetValue(templateBD);
            object val_clearingMode = f_buildsiteClearingMode?.GetValue(templateBD);
            object val_clearBorder = f_clearDetailsBorder?.GetValue(templateBD);

            int goldCost = GoldCostToRelocate.Value;

            int count = 0;
            // Iterate ForageableResource components directly. This skips the
            // ~30k-object scene-wide GameObject scan (the dominant cost in
            // this method) and the per-object string-based GetComponent —
            // returns only the ~100-500 ForageableResource components, an
            // O(60-300×) speedup that turns a ~1-second freeze into a few ms
            // on populated maps. Same source TW switched to.
            foreach (var comp in Resources.FindObjectsOfTypeAll<ForageableResource>())
            {
                if (comp == null) continue;
                var obj = comp.gameObject;
                if (obj == null) continue;
                if (obj.name.ToLower().Contains("blueberry")) continue;
                if (obj.name.ToLower().Contains("deco")) continue;

                // Per-type config filter
                string nameLower = obj.name.ToLower();
                if (nameLower.Contains("herb") && !RelocateHerbs.Value) continue;
                if (nameLower.Contains("mushroom") && !RelocateMushrooms.Value) continue;
                if (nameLower.Contains("greens") && !RelocateGreens.Value) continue;
                if (nameLower.Contains("roots") && !RelocateRoots.Value) continue;
                if (nameLower.Contains("hazelnut") && !RelocateNuts.Value) continue;
                if (nameLower.Contains("willow") && !RelocateWillow.Value) continue;
                if ((nameLower.Contains("hawthorn") || nameLower.Contains("sumac")) && !RelocateBerries.Value) continue;

                var bdField = comp.GetType().GetField("_buildingData", flags);
                if (bdField == null) continue;
                if (bdField.GetValue(comp) != null) continue;

                var newBD = System.Activator.CreateInstance(buildingDataType);
                f_placeablePrefab?.SetValue(newBD, val_placeablePrefab);
                f_buildSitePrefab?.SetValue(newBD, val_buildSitePrefab);
                f_deconstructSitePrefab?.SetValue(newBD, val_deconstructSitePrefab);
                f_destinationPrefab?.SetValue(newBD, val_destinationPrefab);
                f_gridSize?.SetValue(newBD, val_gridSize);
                f_placementGridSettings?.SetValue(newBD, val_placementGridSettings);
                f_goldToRelocate?.SetValue(newBD, goldCost > 0 ? goldCost : val_goldToRelocate);
                f_workToConstruct?.SetValue(newBD, val_workToConstruct);
                f_workToDeconstruct?.SetValue(newBD, val_workToDeconstruct);
                f_defaultBuilders?.SetValue(newBD, val_defaultBuilders);
                f_maxBuilders?.SetValue(newBD, val_maxBuilders);
                f_buildGroup?.SetValue(newBD, val_buildGroup);
                f_buildsiteClearingMode?.SetValue(newBD, val_clearingMode);
                f_clearDetailsBorder?.SetValue(newBD, val_clearBorder);
                f_identifier?.SetValue(newBD, obj.name.Replace("(Clone)", "").Trim());

                var listType = typeof(List<>).MakeGenericType(entryType);
                var newList = (System.Collections.IList)System.Activator.CreateInstance(listType);
                newList.Add(blueberryEntry);
                f_prefabEntries?.SetValue(newBD, newList);

                if (f_diagPrefabEntries != null)
                {
                    if (val_diagPrefabEntries != null)
                        f_diagPrefabEntries.SetValue(newBD, val_diagPrefabEntries);
                    else
                        f_diagPrefabEntries.SetValue(newBD, System.Activator.CreateInstance(listType));
                }

                bdField.SetValue(comp, newBD);

                var f_bdIdentifier = comp.GetType().GetField("buildingDataIdentifier", flags);
                if (f_bdIdentifier != null)
                {
                    string templateId = f_identifier?.GetValue(templateBD) as string;
                    if (templateId != null)
                        f_bdIdentifier.SetValue(comp, templateId);
                }
                count++;
                if (count <= 5) MelonLogger.Msg($"Enabled transplantation for {obj.name}");
            }

            _lastApplyCount = count;
            if (count > 0)
                MelonLogger.Msg($"Done! Enabled {count} forageables for transplantation.");
        }
    }

    public static class RelocatePatches
    {
        public static readonly BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                                  | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        public static void RelocatePrefix(object __instance, object deconstructionData, object constructionData)
        {
            try
            {
                if (constructionData == null || deconstructionData == null) return;
                var f_sceneObject = deconstructionData.GetType().GetField("sceneObject", flags);
                if (f_sceneObject == null) return;
                var sceneObj = f_sceneObject.GetValue(deconstructionData) as GameObject;
                if (sceneObj == null) return;
                var forageComp = sceneObj.GetComponent("ForageableResource");
                if (forageComp == null) return;
                if (sceneObj.name.ToLower().Contains("blueberry")) return;
                if (sceneObj.name.ToLower().Contains("deco")) return;

                int instanceId = sceneObj.GetInstanceID();
                if (!Relocator.PendingRelocations.ContainsKey(instanceId))
                {
                    var baseName = sceneObj.name.Replace("(Clone)", "").Trim().ToLower();

                    // Self-healing prefab cache. By the time RelocatePrefix
                    // fires we have a direct reference to a valid instance, but
                    // sceneObj is the GO the player is about to delete —
                    // caching it leaves the next relocation of the same variant
                    // looking at a Unity-null reference. Prefer a prefab asset
                    // (scene-less) version, fall back to sceneObj only if no
                    // asset exists.
                    if (!Relocator.ForageablePrefabs.TryGetValue(baseName, out var existing) || existing == null)
                    {
                        GameObject prefabAsset = null;
                        foreach (var fr in Resources.FindObjectsOfTypeAll<ForageableResource>())
                        {
                            if (fr == null) continue;
                            var go = fr.gameObject;
                            if (go == null || go.scene.IsValid()) continue;
                            if (go.name.Replace("(Clone)", "").Trim().ToLower() == baseName)
                            { prefabAsset = go; break; }
                        }
                        Relocator.ForageablePrefabs[baseName] = prefabAsset != null ? prefabAsset : sceneObj;
                    }

                    var f_position = constructionData.GetType().GetField("position", flags);
                    var destPos = f_position != null ? (Vector3)f_position.GetValue(constructionData) : Vector3.zero;

                    if (Vector3.Distance(sceneObj.transform.position, destPos) < 5f)
                    {
                        MelonLogger.Msg($"RelocatePrefix: Destination too close to origin for '{baseName}' — ignoring.");
                        return;
                    }

                    System.Collections.IDictionary copiedRates = null;
                    System.Collections.IDictionary copiedMaxRates = null;
                    List<int[]> copiedSeasonWindows = null;

                    var fType = (forageComp as Component).GetType();
                    var replenishF = fType.GetField("itemToReplenishRateDict", flags);
                    var maxReplenishF = fType.GetField("itemToMaxReplenishRateDict", flags);

                    if (replenishF != null)
                    {
                        var src = replenishF.GetValue(forageComp) as System.Collections.IDictionary;
                        if (src != null && src.Count > 0)
                        {
                            var cloned = (System.Collections.IDictionary)System.Activator.CreateInstance(src.GetType());
                            foreach (System.Collections.DictionaryEntry e in src) cloned[e.Key] = e.Value;
                            copiedRates = cloned;
                            MelonLogger.Msg($"RelocatePrefix: Copied {src.Count} rate(s) for '{baseName}' (id={instanceId}).");
                        }
                        else MelonLogger.Warning($"RelocatePrefix: replenishRateDict empty for '{baseName}'!");
                    }

                    if (maxReplenishF != null)
                    {
                        var src = maxReplenishF.GetValue(forageComp) as System.Collections.IDictionary;
                        if (src != null && src.Count > 0)
                        {
                            var cloned = (System.Collections.IDictionary)System.Activator.CreateInstance(src.GetType());
                            foreach (System.Collections.DictionaryEntry e in src) cloned[e.Key] = e.Value;
                            copiedMaxRates = cloned;
                        }
                    }

                    // Copy season windows from SeasonalComponentBase before original is destroyed
                    var seasonalComp = sceneObj.GetComponent("SeasonalComponentBase");
                    if (seasonalComp != null)
                    {
                        copiedSeasonWindows = Relocator.CopySeasonWindows(seasonalComp as Component);
                        if (copiedSeasonWindows.Count > 0)
                            MelonLogger.Msg($"RelocatePrefix: Copied {copiedSeasonWindows.Count} season window(s) for '{baseName}'.");
                        else
                            MelonLogger.Warning($"RelocatePrefix: No season windows found on '{baseName}'.");
                    }

                    Relocator.PendingRelocations[instanceId] = new Relocator.PendingRelocation
                    {
                        instanceId = instanceId,
                        baseName = baseName,
                        destination = destPos,
                        replenishRates = copiedRates,
                        maxReplenishRates = copiedMaxRates,
                        seasonWindows = copiedSeasonWindows
                    };

                    MelonLogger.Msg($"RelocatePrefix: Recorded '{baseName}' (id={instanceId}) -> {destPos}");
                }
            }
            catch (System.Exception ex) { MelonLogger.Error($"RelocatePrefix error: {ex}"); }
        }

        public static void BuildSiteInitializePostfix(object __instance, object __0)
        {
            try
            {
                Component buildSiteComp = __instance as Component;
                if (buildSiteComp == null || __0 == null) return;
                if (Relocator.PendingRelocations.Count == 0) return;

                var f_position = __0.GetType().GetField("position", flags);
                if (f_position == null) return;
                var position = (Vector3)f_position.GetValue(__0);

                foreach (var kvp in new Dictionary<int, Relocator.PendingRelocation>(Relocator.PendingRelocations))
                {
                    var pending = kvp.Value;
                    if (pending.nativeConstructSite == null
                        && Vector3.Distance(position, pending.destination) < 2f)
                    {
                        pending.nativeConstructSite = buildSiteComp.gameObject;
                        MelonLogger.Msg($"BuildSiteInitializePostfix: Linked construct site for '{pending.baseName}' (id={kvp.Key}) at {position}");
                        return;
                    }
                }
            }
            catch (System.Exception ex) { MelonLogger.Error($"BuildSiteInitializePostfix error: {ex}"); }
        }

        public static void OnBuiltPrefabInstantiatedTerrain(object __instance, object __0)
        {
            try
            {
                if (Relocator.PendingRelocations.Count == 0) return;
                Component buildSiteComp = __instance as Component;
                if (buildSiteComp == null) return;
                // __0 is the built instance (blueberry clone) — cast to GameObject
                GameObject builtObj = __0 as GameObject;
                MelonLogger.Msg($"OnBuiltPrefabInstantiated (terrain) at {buildSiteComp.transform.position}");
                HandleCompletion(buildSiteComp, builtObj);
            }
            catch (System.Exception ex) { MelonLogger.Error($"OnBuiltPrefabInstantiatedTerrain error: {ex}"); }
        }

        public static void OnBuiltPrefabInstantiatedBase(object __instance, GameObject builtInstance)
        {
            try
            {
                if (Relocator.PendingRelocations.Count == 0) return;
                Component buildSiteComp = __instance as Component;
                if (buildSiteComp == null) return;
                MelonLogger.Msg($"OnBuiltPrefabInstantiated (base) at {buildSiteComp.transform.position}");
                HandleCompletion(buildSiteComp, builtInstance);
            }
            catch (System.Exception ex) { MelonLogger.Error($"OnBuiltPrefabInstantiatedBase error: {ex}"); }
        }

        private static void HandleCompletion(Component buildSiteComp, GameObject blueberryToDestroy)
        {
            foreach (var kvp in new Dictionary<int, Relocator.PendingRelocation>(Relocator.PendingRelocations))
            {
                var pending = kvp.Value;
                if (pending.nativeConstructSite != null
                    && pending.nativeConstructSite == buildSiteComp.gameObject)
                {
                    MelonLogger.Msg($"HandleCompletion: Matched '{pending.baseName}' (id={kvp.Key}). Spawning.");
                    Relocator.PendingRelocations.Remove(kvp.Key);
                    Relocator.SpawnForageableAtDestination(pending.baseName, pending, blueberryToDestroy);
                    return;
                }
            }
        }
    }
}