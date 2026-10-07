#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // the text the "copy project info" button puts on the clipboard. always english so whoever
    // helps can read it, whatever language the window is in. no full file paths, those have the
    // windows user name in them
    internal static class ProjectReport
    {
        public static string For(Outfit o, PackTextures pack)
        {
            var sb = new StringBuilder();
            using (Lang.English())
            {
                sb.AppendLine("BAAA project info");
                if (o == null || o.Root == null)
                {
                    sb.AppendLine("No outfit picked.");
                    return sb.ToString();
                }

                sb.AppendLine("Outfit: " + o.Root.name);
                sb.AppendLine("Pack: " + (o.PackFolder != null ? Path.GetFileName(o.PackFolder) : "not in a supported creator's folder"));
                sb.AppendLine("Where: " + Where(o));
                sb.AppendLine("Version: " + Kind(o));
                sb.AppendLine("Unity: " + Application.unityVersion + ", building for " + EditorUserBuildSettings.activeBuildTarget);
                VersionInfo v = Versions.For(o);
                sb.AppendLine("VRChat SDK: " + (Versions.PackageVersion("com.vrchat.avatars") ?? "not found"));
                sb.AppendLine("VRCFury: " + Installed(v.VrcFuryInstalled, v.VrcFuryPresent) + ", outfit saved with " + (v.VrcFuryOutfit ?? "unknown"));
                sb.AppendLine("Poiyomi: " + Installed(v.PoiyomiInstalled, v.PoiyomiPresent) + ", outfit made with " + (v.PoiyomiOutfit ?? "unknown"));
                sb.AppendLine("Copies of VRCFury / Poiyomi: " + ProjectCheck.VrcFuryCopies().Count + " / " + ProjectCheck.PoiyomiCopies().Count);
                sb.AppendLine("Script errors: " + (EditorUtility.scriptCompilationFailed ? "yes" : "no"));
                if (pack != null)
                {
                    bool consistent;
                    TextureChoice choice;
                    sb.AppendLine("Textures: " + AssistantTextures.Describe(pack, out consistent, out choice));
                }
                sb.AppendLine("Pieces off: " + PiecesOff(o));
                sb.AppendLine("Features off: " + FeaturesOff(o));

                GearStats s = Ranks.Measure(o.Root, null, false);
                sb.AppendLine("Size: " + N(s.Triangles) + " triangles, " + N(s.SkinnedMeshes) + " skinned meshes, " + N(s.MaterialSlots) + " material slots, "
                    + N(s.PhysBones) + " physbones, " + (s.TextureBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB of textures");
                if (o.Avatar != null)
                {
                    AvatarRank rank = Ranks.For(o.Avatar);
                    sb.AppendLine("Avatar rank (estimated): PC " + Ranks.Name(rank.Pc) + ", Quest " + Ranks.Name(rank.Quest));
                }

                List<Issue> issues = OutfitCheck.Run(o);
                sb.AppendLine();
                sb.AppendLine(issues.Count == 0 ? "Check: all good" : "Check found " + issues.Count + ":");
                foreach (Issue i in issues)
                {
                    sb.AppendLine("- " + i.Severity + ": " + i.Title);
                    if (!string.IsNullOrEmpty(i.Detail)) sb.AppendLine("  " + i.Detail);
                }
            }
            return sb.ToString();
        }

        private static string Where(Outfit o)
        {
            if (o.Place == OutfitPlace.PackFile) return "the pack's own prefab";
            if (o.Place == OutfitPlace.PrefabMode) return "open in prefab mode";
            return o.Avatar != null ? "in the scene, on \"" + o.Avatar.name + "\"" : "in the scene, not on an avatar";
        }

        private static string Kind(Outfit o)
        {
            if (o.Copy == null) return "the original";
            CopyRecord c = o.Copy;
            string text = OutfitCopies.Label(c.Kind) + " copy of \"" + c.OriginalName + "\"";
            if (c.AtlasSize > 0) text += ", " + c.AtlasSize / 1024 + "K atlas";
            text += ", keeps " + Mathf.RoundToInt(c.Keep * 100f) + "% of the triangles";
            if (o.Original == null) text += ", original missing";
            if (OutfitCopies.OutOfDate(c)) text += ", out of date";
            return text;
        }

        private static string Installed(string version, bool present)
        {
            return version != null ? version : present ? "installed, version unknown" : "not installed";
        }

        private static string PiecesOff(Outfit o)
        {
            if (!AssistantScope.VrcFuryInstalled) return "can't tell without VRCFury";
            try
            {
                var off = new List<string>();
                foreach (Piece p in OutfitPieces.Find(o))
                    if (p.Dropped) off.Add(p.Name);
                return off.Count == 0 ? "none" : string.Join(", ", off.ToArray());
            }
            catch (Exception e)
            {
                return "couldn't read them (" + e.GetType().Name + ")";
            }
        }

        private static string FeaturesOff(Outfit o)
        {
            try
            {
                var counts = new Dictionary<FeatureKind, int>();
                foreach (OutfitFeature f in OutfitFeatures.Find(o))
                {
                    if (f.On) continue;
                    int n;
                    counts.TryGetValue(f.Kind, out n);
                    counts[f.Kind] = n + 1;
                }
                if (counts.Count == 0) return "none";
                var parts = new List<string>();
                foreach (var pair in counts) parts.Add(pair.Value + " " + pair.Key);
                return string.Join(", ", parts.ToArray());
            }
            catch (Exception e)
            {
                return "couldn't read them (" + e.GetType().Name + ")";
            }
        }

        private static string N(long n)
        {
            return n.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
#endif
