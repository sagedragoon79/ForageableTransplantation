using System;
using System.Reflection;
using MelonLoader;

namespace ForageableTransplantation
{
    /// <summary>Optional integration with Keep Clarity's settings panel. No-op when KeepClarity.dll is absent.</summary>
    internal static class KeepClarityIntegration
    {
        private static bool _resolved, _present;
        private static MethodInfo _registerMod;
        private static MethodInfo _registerEntry;
        private static Type _settingsMetaType;

        private const string ModId = "ForageableTransplantation";
        private const string ModDisplayName = "Forageable Transplantation";

        public static void TryRegisterAll()
        {
            // FT may have auto-disabled itself (e.g. when TW is loaded) before
            // creating any MelonPreferences entries. Skip registration in that
            // case — there's nothing to expose.
            if (Relocator.ModEnabled == null) return;
            if (!ResolveApi()) return;
            try
            {
                RegisterMod();
                RegisterEntries();
                MelonLogger.Msg("[FT] Registered with Keep Clarity settings panel");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FT] Keep Clarity registration failed: {ex.Message}");
            }
        }

        private static bool ResolveApi()
        {
            if (_resolved) return _present;
            _resolved = true;
            var apiType = Type.GetType("FFUIOverhaul.Settings.SettingsAPI, KeepClarity");
            if (apiType == null) { _present = false; return false; }
            _settingsMetaType = Type.GetType("FFUIOverhaul.Settings.SettingsMeta, KeepClarity");
            if (_settingsMetaType == null) { _present = false; return false; }
            _registerMod = apiType.GetMethod("RegisterMod", BindingFlags.Public | BindingFlags.Static);
            foreach (var m in apiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "Register" && m.IsGenericMethodDefinition) { _registerEntry = m; break; }
            _present = _registerMod != null && _registerEntry != null;
            return _present;
        }

        private static void RegisterMod()
        {
            _registerMod.Invoke(null, new object[] {
                ModId, ModDisplayName,
                "Standalone forageable relocation for all wild plant types",
                /*version*/ null,
                /*iconResourcePath*/ null,
                /*accentRgb — sage green*/ new[] { 0.45f, 0.60f, 0.40f, 1f },
                /*order*/ 40
            });
        }

        private static object NewMeta(string label = null, string tooltip = null,
            object min = null, object max = null, bool restartRequired = false,
            int order = 0, Func<bool> visibleWhen = null)
        {
            var m = Activator.CreateInstance(_settingsMetaType);
            void Set(string field, object value)
            {
                var f = _settingsMetaType.GetField(field);
                if (f != null) f.SetValue(m, value);
            }
            Set("Label", label);
            Set("Tooltip", tooltip);
            Set("Min", min);
            Set("Max", max);
            Set("RestartRequired", restartRequired);
            Set("Order", order);
            Set("VisibleWhen", visibleWhen);
            return m;
        }

        private static void Reg<T>(string category, MelonPreferences_Entry<T> entry, object meta)
        {
            var closed = _registerEntry.MakeGenericMethod(typeof(T));
            closed.Invoke(null, new object[] { ModId, ModDisplayName, category, entry, meta });
        }

        private static void RegisterEntries()
        {
            Reg("Master", Relocator.ModEnabled,
                NewMeta("Mod Enabled", "Disable to fall back to vanilla foraging", restartRequired: true));

            Func<bool> on = () => Relocator.ModEnabled.Value;
            Reg("Relocate by Type", Relocator.RelocateHerbs,     NewMeta("Herbs", visibleWhen: on));
            Reg("Relocate by Type", Relocator.RelocateMushrooms, NewMeta("Mushrooms", visibleWhen: on));
            Reg("Relocate by Type", Relocator.RelocateGreens,    NewMeta("Greens", visibleWhen: on));
            Reg("Relocate by Type", Relocator.RelocateRoots,     NewMeta("Roots", visibleWhen: on));
            Reg("Relocate by Type", Relocator.RelocateNuts,      NewMeta("Hazelnuts", visibleWhen: on));
            Reg("Relocate by Type", Relocator.RelocateWillow,    NewMeta("Willow", visibleWhen: on));
            Reg("Relocate by Type", Relocator.RelocateBerries,
                NewMeta("Berry Bushes", "Hawthorn, sumac", visibleWhen: on));

            Reg("Cost", Relocator.GoldCostToRelocate,
                NewMeta("Gold Cost per Relocation", min: 0, max: 100, tooltip: "0 = free, just labor"));
        }
    }
}
