using System.Collections.Generic;
using System.IO;
using System.Xml;
using BepInEx;
using BepInEx.Logging;
using RiskOfOptions;
using RiskOfOptions.Options;
using RoR2;
using RoR2.UI;
using UnityEngine;

namespace MarkAllSeen
{
    /// <summary>
    /// Adds a "Mark all as seen" button to the Risk of Options menu. It marks what is showing "New!" (logbook entries,
    /// and survivors, skills and skins in character select) as seen on the local players' profiles and saves them.
    /// Client-side: only this game's profiles change.
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(RiskOfOptionsGuid)]
    public sealed class MarkAllSeenPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "revor.MarkAllSeen";
        public const string PluginName = "MarkAllSeen";
        public const string PluginVersion = "1.0.2"; // tools/package.py checks this against the manifest

        // A hard dependency: the button is the whole mod.
        private const string RiskOfOptionsGuid = "com.rune580.riskofoptions";

        // The store icon, embedded from thunderstore/MarkAllSeen/icon.png by Directory.Build.targets.
        private const string IconResource = "MarkAllSeen.icon.png";

        // The button's name, and the title of the popup it opens.
        private const string Title = "Mark all as seen";

        // Viewable names as the game builds them (SurvivorCatalog.SetSurvivorDefs, Loadout.GenerateViewables):
        // /Survivors/<survivor>, and a /Loadout/Bodies/<body>/ folder holding <skill family>.<skill> and Skins/<skin>.
        private const string SurvivorsFolder = "/Survivors/";
        private const string LoadoutBodiesFolder = "/Loadout/Bodies/";

        internal static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Log = Logger;
            ModSettingsManager.SetModDescription("Clears the \"New!\" markers on your profile with one button.");
            ModSettingsManager.AddOption(new GenericButtonOption(
                Title,
                "General",
                "Marks what is showing \"New!\" as seen: logbook entries, and survivors, skills and skins in character "
                + "select. Anything you unlock or discover later still shows as new, and so do the skills and skins "
                + "of survivors you haven't unlocked or whose DLC you don't own. There is no undo.",
                "Mark all",
                OnMarkAllPressed));
            ModSettingsManager.SetModIcon(LoadIcon());
        }

        /// <summary>The store icon, shown next to the mod in Risk of Options instead of a question mark.</summary>
        private static Sprite LoadIcon()
        {
            byte[] png;
            using (Stream stream = typeof(MarkAllSeenPlugin).Assembly.GetManifestResourceStream(IconResource))
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                png = buffer.ToArray();
            }
            // LoadImage replaces the placeholder size and format with the image's.
            Texture2D texture = new Texture2D(2, 2);
            texture.LoadImage(png);
            // With the default Repeat, the edges of the scaled-down icon would blend with the opposite edges.
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Marks what is showing "New!" for every local player, then says how many entries that was.</summary>
        private static void OnMarkAllPressed()
        {
            int marked = 0;
            foreach (LocalUser user in LocalUserManager.readOnlyLocalUsersList)
            {
                marked += MarkAllFor(user);
            }
            Log.LogInfo($"Marked {marked} entries as seen.");

            SimpleDialogBox dialog = SimpleDialogBox.Create();
            dialog.headerToken = new SimpleDialogBox.TokenParamsPair(Title);
            dialog.descriptionToken = new SimpleDialogBox.TokenParamsPair(marked == 0
                ? "Nothing to mark."
                : $"Marked {marked} {(marked == 1 ? "entry" : "entries")} as seen.");
            dialog.AddCancelButton(CommonLanguageTokens.ok);
        }

        /// <summary>
        /// Marks the entries showing "New!" for this user, except those of survivors they can't play yet and names the
        /// profile can't save, requests a save and returns how many were marked. Each entry decides whether it is new
        /// (for example only once it is unlocked or discovered), so what comes later still gets its marker.
        /// </summary>
        private static int MarkAllFor(LocalUser user)
        {
            UserProfile profile = user.userProfile;
            HashSet<string> leftAlone = GetEntriesOfUnavailableSurvivors(user);
            List<string> toMark = new List<string>();
            List<string> unsavable = new List<string>();
            // Every viewable (anything that can show "New!"): the catalog has no public way to list them.
            foreach (ViewablesCatalog.Node node in ViewablesCatalog.fullNameToNodeMap.Values)
            {
                if (node.isFolder || leftAlone.Contains(node.fullName) || !node.shouldShowUnviewed(profile))
                {
                    continue;
                }
                if (CanBeSaved(node.fullName))
                {
                    toMark.Add(node.fullName);
                }
                else
                {
                    unsavable.Add(node.fullName);
                }
            }
            if (unsavable.Count > 0)
            {
                Log.LogWarning($"Left {unsavable.Count} entries showing \"New!\": the game can't save names with a "
                    + $"space, a carriage return or a character XML can't hold. {string.Join(", ", unsavable)}");
            }

            // Marked after the loop: marking raises onUserProfileViewedViewablesChanged, whose handlers shouldn't run
            // while the catalog's map is being enumerated. The game batches the "New!" tag refresh to once per frame.
            foreach (string name in toMark)
            {
                profile.MarkViewableAsViewed(name);
            }
            if (toMark.Count > 0)
            {
                // MarkViewableAsViewed only flags the profile as changed, so the marks would reach the disk with the
                // next save (when the logbook closes, a run ends, the game quits, ...) and a crash before that would
                // lose them. A requested save starts on the next frame.
                profile.RequestEventualSave();
            }
            return toMark.Count;
        }

        /// <summary>
        /// The entries of survivors this user can't play yet (not unlocked, or from a DLC they don't own): the
        /// survivor and its skills and skins. The game counts skills that need no unlock as unlocked, so those entries
        /// report "new" before the player can use them, and marking them would take away the "New!" markers the
        /// survivor's loadout should show once it is available.
        /// </summary>
        private static HashSet<string> GetEntriesOfUnavailableSurvivors(LocalUser user)
        {
            HashSet<string> names = new HashSet<string>();
            foreach (SurvivorDef survivor in SurvivorCatalog.allSurvivorDefs)
            {
                if (user.userProfile.HasSurvivorUnlocked(survivor.survivorIndex)
                    && survivor.CheckUserHasRequiredEntitlement(user))
                {
                    continue;
                }
                names.Add(SurvivorsFolder + survivor.cachedName);
                BodyIndex body = SurvivorCatalog.GetBodyIndexFromSurvivorIndex(survivor.survivorIndex);
                ViewablesCatalog.Node loadout =
                    ViewablesCatalog.FindNode(LoadoutBodiesFolder + BodyCatalog.GetBodyName(body) + "/");
                if (loadout == null)
                {
                    continue;
                }
                foreach (ViewablesCatalog.Node node in loadout.Descendants())
                {
                    names.Add(node.fullName);
                }
            }
            return names;
        }

        /// <summary>
        /// Whether the profile can store the name and load it back unchanged. It saves the viewed names as one
        /// space-separated text in an XML file: a space would split the name, a carriage return comes back as a line
        /// feed, and a character XML can't hold stops the whole profile from saving or loading.
        /// </summary>
        private static bool CanBeSaved(string name)
        {
            if (name.Contains(' ') || name.Contains('\r'))
            {
                return false;
            }
            try
            {
                XmlConvert.VerifyXmlChars(name);
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }
    }
}
