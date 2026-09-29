using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
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
        public const string PluginVersion = "1.0.1"; // tools/package.py checks this against the manifest

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

        private static readonly Vector2 centerPivot = new Vector2(0.5f, 0.5f);

        // Each name the profile can't save is logged once, not on every press.
        private static readonly HashSet<string> reportedUnsavableNames = new HashSet<string>();

        // Every viewable (anything that can show "New!") by full name: ViewablesCatalog's private fullNameToNodeMap,
        // the map FindNode looks names up in. The catalog has no public way to list them.
        private static AccessTools.FieldRef<Dictionary<string, ViewablesCatalog.Node>> viewablesByName;

        private static bool warningLogged;

        internal static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Log = Logger;
            if (!FindViewables())
            {
                return;
            }
            ModSettingsManager.SetModDescription("Clears the \"New!\" markers on your profile with one button.");
            ModSettingsManager.AddOption(new GenericButtonOption(
                Title,
                "General",
                "Marks what is showing \"New!\" as seen: logbook entries, and survivors, skills and skins in character "
                + "select. Anything you unlock or discover later still shows as new, and so do the skills and skins "
                + "of survivors you haven't unlocked or whose DLC you don't own. There is no undo.",
                "Mark all",
                OnMarkAllPressed));
            SetModIcon();
        }

        /// <summary>
        /// Looks up the catalog's map of viewables. The button can't work without it, so then it isn't added.
        /// </summary>
        private static bool FindViewables()
        {
            try
            {
                viewablesByName = AccessTools.StaticFieldRefAccess<Dictionary<string, ViewablesCatalog.Node>>(
                    AccessTools.Field(typeof(ViewablesCatalog), "fullNameToNodeMap"));
                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Couldn't find the game's list of \"New!\" markers (ViewablesCatalog.fullNameToNodeMap; "
                    + $"a game update may have changed it), so the Mark all as seen button isn't added. {e}");
                return false;
            }
        }

        /// <summary>Shows the store icon next to the mod in Risk of Options, instead of a question mark.</summary>
        private static void SetModIcon()
        {
            try
            {
                Sprite icon = LoadIcon();
                if (icon)
                {
                    ModSettingsManager.SetModIcon(icon);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"Couldn't give the mod its icon in Risk of Options. {e}");
            }
        }

        /// <summary>The embedded store icon, or null with a warning when it is missing or can't be decoded.</summary>
        private static Sprite LoadIcon()
        {
            byte[] png;
            using (Stream stream = typeof(MarkAllSeenPlugin).Assembly.GetManifestResourceStream(IconResource))
            {
                if (stream == null)
                {
                    Log.LogWarning($"The DLL has no {IconResource}, so Risk of Options shows no icon for the mod.");
                    return null;
                }
                using (MemoryStream buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    png = buffer.ToArray();
                }
            }
            // LoadImage replaces the placeholder size and format with the image's.
            Texture2D texture = new Texture2D(2, 2);
            if (!texture.LoadImage(png))
            {
                Destroy(texture);
                Log.LogWarning($"Couldn't decode {IconResource}, so Risk of Options shows no icon for the mod.");
                return null;
            }
            // With the default Repeat, the edges of the scaled-down icon would blend with the opposite edges.
            texture.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), centerPivot);
        }

        /// <summary>Marks what is showing "New!" for every local player, then says how many entries that was.</summary>
        private static void OnMarkAllPressed()
        {
            int marked = 0;
            foreach (LocalUser user in LocalUserManager.readOnlyLocalUsersList)
            {
                if (user?.userProfile != null)
                {
                    marked += MarkAllFor(user);
                }
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
            foreach (ViewablesCatalog.Node node in viewablesByName().Values)
            {
                if (node.isFolder || leftAlone.Contains(node.fullName) || !ShowsAsNew(node, profile))
                {
                    continue;
                }
                if (CanBeSaved(node.fullName))
                {
                    toMark.Add(node.fullName);
                }
                else if (reportedUnsavableNames.Add(node.fullName))
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
            // while the catalog's map is being enumerated.
            foreach (string name in toMark)
            {
                MarkAsViewed(profile, name);
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
                if (!survivor || HasSurvivor(user, survivor))
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
        /// Whether the user has unlocked the survivor and owns the DLC it needs. When that can't be checked, the
        /// survivor's entries are left alone.
        /// </summary>
        private static bool HasSurvivor(LocalUser user, SurvivorDef survivor)
        {
            try
            {
                return user.userProfile.HasSurvivorUnlocked(survivor.survivorIndex)
                    && survivor.CheckUserHasRequiredEntitlement(user);
            }
            catch (Exception e)
            {
                LogWarningOnce($"checking survivor {survivor.cachedName}", e);
                return false;
            }
        }

        /// <summary>
        /// The entry's own "New!" test. One that throws (a broken entry from another mod) counts as not new, so it
        /// can't stop the other entries from being marked.
        /// </summary>
        private static bool ShowsAsNew(ViewablesCatalog.Node node, UserProfile profile)
        {
            try
            {
                return node.shouldShowUnviewed != null && node.shouldShowUnviewed(profile);
            }
            catch (Exception e)
            {
                LogWarningOnce($"checking {node.fullName}", e);
                return false;
            }
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

        /// <summary>
        /// Marks one entry. An error in another mod's handler of the change event can't stop the other entries.
        /// </summary>
        private static void MarkAsViewed(UserProfile profile, string name)
        {
            try
            {
                // Adds the name and raises onUserProfileViewedViewablesChanged, which refreshes the "New!" tags (the
                // game batches that to once per frame).
                profile.MarkViewableAsViewed(name);
            }
            catch (Exception e)
            {
                // Only an event handler can throw here, after the name has been added.
                LogWarningOnce($"marking {name}", e);
            }
        }

        /// <summary>
        /// Logs a warning for the first error only: a broken entry or handler from another mod usually fails for each
        /// of its entries and on every press.
        /// </summary>
        private static void LogWarningOnce(string action, Exception e)
        {
            if (!warningLogged)
            {
                warningLogged = true;
                Log.LogWarning($"Error while {action}; skipped it (later errors aren't logged). {e}");
            }
        }
    }
}
