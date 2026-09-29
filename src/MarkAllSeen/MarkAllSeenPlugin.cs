using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using RiskOfOptions;
using RiskOfOptions.Options;
using RoR2;
using RoR2.UI;

namespace MarkAllSeen
{
    /// <summary>
    /// Adds a "Mark all as seen" button to the Risk of Options menu that clears every "New!" marker
    /// (logbook, items, skills, skins, survivors, ...) on your profile.
    /// </summary>
    [BepInPlugin(PluginGUID, "MarkAllSeen", "1.0.1")]
    [BepInDependency("com.rune580.riskofoptions")]
    public class MarkAllSeenPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "revor.MarkAllSeen";

        private static readonly AccessTools.FieldRef<Dictionary<string, ViewablesCatalog.Node>> allNodes =
            AccessTools.StaticFieldRefAccess<Dictionary<string, ViewablesCatalog.Node>>(AccessTools.Field(typeof(ViewablesCatalog), "fullNameToNodeMap"));

        private static readonly AccessTools.FieldRef<UserProfile, List<string>> viewedViewables =
            AccessTools.FieldRefAccess<UserProfile, List<string>>("viewedViewables");

        private void Awake()
        {
            ModSettingsManager.SetModDescription("Clear every \"New!\" marker on your profile with one button.");
            ModSettingsManager.AddOption(new GenericButtonOption(
                "Mark all as seen",
                "General",
                "Marks everything currently showing \"New!\" as seen: logbook entries, items, skills, skins and survivors. Things you unlock later still show as new.",
                "Mark all",
                MarkAll));
        }

        private void MarkAll()
        {
            int marked = 0;
            foreach (LocalUser user in LocalUserManager.readOnlyLocalUsersList)
            {
                if (user?.userProfile != null)
                {
                    marked += MarkAll(user.userProfile);
                }
            }
            Logger.LogInfo($"Marked {marked} viewables as seen.");

            SimpleDialogBox dialog = SimpleDialogBox.Create();
            dialog.headerToken = new SimpleDialogBox.TokenParamsPair("Mark all as seen");
            dialog.descriptionToken = new SimpleDialogBox.TokenParamsPair(marked == 0
                ? "Nothing to mark: you've already seen everything."
                : $"Marked {marked} {(marked == 1 ? "entry" : "entries")} as seen.");
            dialog.AddCancelButton("OK");
        }

        /// <summary>
        /// Only marks entries that are currently showing as new (each entry decides that itself, e.g. only once unlocked),
        /// so content unlocked later is still flagged. Adds them in one go and raises the change/save once at the end.
        /// </summary>
        private static int MarkAll(UserProfile profile)
        {
            List<string> toMark = new List<string>();
            foreach (ViewablesCatalog.Node node in allNodes().Values)
            {
                if (node != null && !node.isFolder && node.shouldShowUnviewed != null && node.shouldShowUnviewed(profile))
                {
                    toMark.Add(node.fullName);
                }
            }
            if (toMark.Count == 0)
            {
                return 0;
            }

            List<string> viewed = viewedViewables(profile);
            for (int i = 0; i < toMark.Count - 1; i++)
            {
                if (!viewed.Contains(toMark[i]))
                {
                    viewed.Add(toMark[i]);
                }
            }
            // The last one goes through the normal path, which notifies the UI and saves the profile.
            profile.MarkViewableAsViewed(toMark[toMark.Count - 1]);
            return toMark.Count;
        }
    }
}
