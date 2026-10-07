#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // what BAAA knows about one creators packs. any VRCFury outfit on an avatar works without one,
    // a creator file adds where their packs live, so unplaced pack prefabs and whole pack folders show up
    [Serializable]
    internal sealed class Creator
    {
        public string name = "";
        // where the packs live. every folder right inside one of these is one pack
        public string[] folders = new string[0];
        // folders in there that arent packs, like shared files or tools
        public string[] notPacks = new string[0];
        // never touched, not even their icons
        public string[] untouched = new string[0];
        // left out of texture size and compression, at any depth
        public string[] noQuality = new string[0];
    }

    // one json file per creator, in the packages Supported Creators folder or in
    // Assets/BAAA/Supported Creators. the second one is yours, a BAAA update never replaces it
    internal static class Creators
    {
        // the packages own folder, same guid everywhere
        private const string FolderGuid = "5920821a6c32ea206762559076b6aa59";
        public const string ProjectFolder = "Assets/BAAA/Supported Creators";
        private static List<Creator> _all;

        public static List<Creator> All
        {
            get
            {
                if (_all == null) _all = Load();
                return _all;
            }
        }

        // after a creator file changed
        public static void Reload()
        {
            _all = null;
        }

        // for the tests, like Lang.UseForNow
        internal static void UseForNow(List<Creator> list)
        {
            _all = list;
        }

        private static List<Creator> Load()
        {
            var list = new List<Creator>();
            var folders = new List<string>();
            string own = AssetDatabase.GUIDToAssetPath(FolderGuid);
            if (!string.IsNullOrEmpty(own)) folders.Add(own);
            folders.Add(ProjectFolder);
            string project = Path.GetDirectoryName(Application.dataPath);
            foreach (string folder in folders)
            {
                string full = Path.Combine(project, folder);
                if (!Directory.Exists(full)) continue;
                foreach (string file in Directory.GetFiles(full, "*.json"))
                {
                    try
                    {
                        Creator c = JsonUtility.FromJson<Creator>(File.ReadAllText(file));
                        if (c == null || c.folders == null || c.folders.Length == 0) continue;
                        if (string.IsNullOrEmpty(c.name)) c.name = Path.GetFileNameWithoutExtension(file);
                        c.folders = Clean(c.folders);
                        c.notPacks = c.notPacks ?? new string[0];
                        c.untouched = c.untouched ?? new string[0];
                        c.noQuality = c.noQuality ?? new string[0];
                        list.Add(c);
                    }
                    catch (Exception)
                    {
                        // a broken file just isnt used
                    }
                }
            }
            return list;
        }

        private static string[] Clean(string[] folders)
        {
            var clean = new List<string>();
            foreach (string f in folders)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string p = f.Replace('\\', '/').Trim().TrimEnd('/');
                if (p.Length > 0 && !clean.Contains(p)) clean.Add(p);
            }
            return clean.ToArray();
        }

        // the creator folder a path sits in and whose it is, or null. the longest match wins,
        // so one creator can keep a folder inside another ones
        public static string RootOf(string path, out Creator creator)
        {
            creator = null;
            string best = null;
            if (string.IsNullOrEmpty(path)) return null;
            foreach (Creator c in All)
            {
                foreach (string root in c.folders)
                {
                    if (!path.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                    if (best != null && best.Length >= root.Length) continue;
                    best = root;
                    creator = c;
                }
            }
            return best;
        }

        public static List<string> Roots()
        {
            var roots = new List<string>();
            foreach (Creator c in All)
                foreach (string root in c.folders)
                    if (!roots.Contains(root) && AssetDatabase.IsValidFolder(root)) roots.Add(root);
            return roots;
        }

        public static bool Has(string[] list, string name)
        {
            foreach (string s in list)
                if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
#endif
