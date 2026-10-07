#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    internal enum SizeLevel { Default, Low, Medium, High }

    internal enum CompressionChoice { Default, Low, Normal, High }

    internal enum TextureKind { Texture, NormalMap, Icon, Roughness, Metallic }

    internal struct TextureChoice
    {
        public SizeLevel Size;
        public SizeLevel NormalSize;
        public SizeLevel IconSize;
        public SizeLevel RoughnessSize;
        public SizeLevel MetallicSize;
        public CompressionChoice Compression;
        public bool Crunch;

        public SizeLevel SizeFor(TextureKind kind)
        {
            switch (kind)
            {
                case TextureKind.Icon: return IconSize;
                case TextureKind.NormalMap: return NormalSize;
                case TextureKind.Roughness: return RoughnessSize;
                case TextureKind.Metallic: return MetallicSize;
                default: return Size;
            }
        }
    }

    internal sealed class PackTextures
    {
        // the pack folder, or null for an outfit from a creator without a file
        public string Folder;
        // what the window calls it: the pack folders name or the outfits
        public string Name = "";
        public readonly List<string> Textures = new List<string>();
        public readonly List<string> NormalMaps = new List<string>();
        public readonly List<string> Roughness = new List<string>();
        public readonly List<string> Metallic = new List<string>();
        public readonly List<string> Icons = new List<string>();
        public int OtherTool; // textures another tool keeps its own data on; left alone

        public IEnumerable<KeyValuePair<string, TextureKind>> All()
        {
            foreach (string p in Textures) yield return new KeyValuePair<string, TextureKind>(p, TextureKind.Texture);
            foreach (string p in NormalMaps) yield return new KeyValuePair<string, TextureKind>(p, TextureKind.NormalMap);
            foreach (string p in Roughness) yield return new KeyValuePair<string, TextureKind>(p, TextureKind.Roughness);
            foreach (string p in Metallic) yield return new KeyValuePair<string, TextureKind>(p, TextureKind.Metallic);
            foreach (string p in Icons) yield return new KeyValuePair<string, TextureKind>(p, TextureKind.Icon);
        }

        public bool Empty
        {
            get { return Textures.Count + NormalMaps.Count + Roughness.Count + Metallic.Count + Icons.Count == 0; }
        }
    }

    // texture size and compression for one pack folder. The first time a
    // texture is changed, its shipped settings are written into its own
    // .meta file (importer userData), so "Restore as shipped" always knows
    // what to put back. A pack update replaces the .meta and resets it
    internal static class AssistantTextures
    {
        private const string Tool = "BAAA";
        // crunch at Unity's default quality (50) makes shading blotchy, and
        // normal maps blocky, so it runs higher here.
        private const int CrunchQuality = 75;
        private const int CrunchQualityNormalMap = 100;
        private static readonly string[] Platforms = { "Standalone", "Android", "iPhone" };

        [Serializable]
        private sealed class PlatformRecord
        {
            public string platform;
            public int maxTextureSize;
            public int textureCompression;
            public bool crunchedCompression;
            public int compressionQuality;
            public int format;
        }

        [Serializable]
        private sealed class Record
        {
            public string tool = Tool;
            public int maxTextureSize;
            public int textureCompression;
            public bool crunchedCompression;
            public int compressionQuality;
            public List<PlatformRecord> platforms = new List<PlatformRecord>();
            public string size = "";
            public string compression = "";
            public bool crunch;
        }

        private struct Settings
        {
            public int Max;
            public TextureImporterCompression Compression;
            public bool Crunch;
            public int Quality;
            public TextureImporterFormat Format;
        }

        // Unity's size limits come in powers of two, so High icons are 512
        public static int MaxSize(SizeLevel level, TextureKind kind)
        {
            if (kind == TextureKind.Icon)
                return level == SizeLevel.Low ? 64 : level == SizeLevel.Medium ? 256 : 512;
            return level == SizeLevel.Low ? 1024 : level == SizeLevel.Medium ? 2048 : 4096;
        }

        public static string SizeName(SizeLevel level, TextureKind kind)
        {
            if (level == SizeLevel.Default) return Lang.T("as shipped");
            int max = MaxSize(level, kind);
            if (kind == TextureKind.Icon) return max + "x" + max;
            if (level == SizeLevel.Low) return Lang.T("Low ({0}K)", max / 1024);
            return level == SizeLevel.Medium ? Lang.T("Medium ({0}K)", max / 1024) : Lang.T("High ({0}K)", max / 1024);
        }

        public static PackTextures Collect(string packFolder)
        {
            var pack = new PackTextures { Folder = packFolder, Name = Path.GetFileName(packFolder ?? "") };
            if (string.IsNullOrEmpty(packFolder) || !AssetDatabase.IsValidFolder(packFolder)) return pack;

            var prefabs = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { packFolder }))
                prefabs.Add(AssetDatabase.GUIDToAssetPath(guid));
            HashSet<string> menuIcons = AssistantScope.MenuIconsOf(prefabs);
            var slots = new Slots(packFolder);

            var seen = new HashSet<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture", new[] { packFolder }))
                Sort(AssetDatabase.GUIDToAssetPath(guid), menuIcons, slots, pack, seen);
            // icons kept outside the pack folder, like a creators logo in a shared folder
            foreach (string icon in menuIcons)
                Sort(icon, menuIcons, slots, pack, seen);
            Order(pack);
            return pack;
        }

        // an outfit from a creator without a file has no pack folder, so its the textures its own
        // materials use (the slots poiyomi really reads) and its menu icons
        public static PackTextures CollectFor(Outfit o)
        {
            var pack = new PackTextures { Name = o.Root != null ? o.Root.name : "" };
            if (o.Root == null) return pack;
            var materials = new List<Material>();
            foreach (Renderer r in o.Root.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                    if (m != null && !materials.Contains(m)) materials.Add(m);
            HashSet<string> menuIcons = !string.IsNullOrEmpty(o.PrefabPath)
                ? AssistantScope.MenuIconsOf(new[] { o.PrefabPath })
                : AssistantScope.MenuIconsOn(o.Root);
            var slots = new Slots(materials);
            var seen = new HashSet<string>();
            foreach (Material m in materials)
                foreach (Texture t in TextureUse.Of(m))
                    Sort(AssetDatabase.GetAssetPath(t), menuIcons, slots, pack, seen);
            foreach (string icon in menuIcons)
                Sort(icon, menuIcons, slots, pack, seen);
            Order(pack);
            return pack;
        }

        private static void Order(PackTextures pack)
        {
            pack.Textures.Sort(StringComparer.OrdinalIgnoreCase);
            pack.NormalMaps.Sort(StringComparer.OrdinalIgnoreCase);
            pack.Roughness.Sort(StringComparer.OrdinalIgnoreCase);
            pack.Metallic.Sort(StringComparer.OrdinalIgnoreCase);
            pack.Icons.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static void Sort(string path, HashSet<string> menuIcons, Slots slots, PackTextures pack, HashSet<string> seen)
        {
            if (!seen.Add(path)) return;
            TextureRole role = AssistantScope.RoleOf(path, menuIcons);
            if (role == TextureRole.None) return;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;
            if (OtherToolOwns(imp))
            {
                pack.OtherTool++;
                return;
            }
            if (role == TextureRole.Icon) pack.Icons.Add(path);
            else if (IsNormalMap(path, imp, slots.Normals)) pack.NormalMaps.Add(path);
            else
            {
                // the name says it first (koboC_R, Sport2_M), the material slot it sits in second
                TextureKind kind = NamedKind(path);
                if (kind == TextureKind.Texture && slots.Metallic.Contains(path)) kind = TextureKind.Metallic;
                if (kind == TextureKind.Texture && slots.Roughness.Contains(path)) kind = TextureKind.Roughness;
                if (kind == TextureKind.Roughness) pack.Roughness.Add(path);
                else if (kind == TextureKind.Metallic) pack.Metallic.Add(path);
                else pack.Textures.Add(path);
            }
        }

        // roughness and metallic maps by name: "_R" / "_M" at the end (or "-R", " R", any case), a word like
        // Rough or Metallic there, or a capital R or M stuck right on a name like ShirtR or Sport2M. a variant
        // tail like " - Alt" or " (1)" comes off first. a lower case r or m on a word ("Hair") doesnt count
        private static readonly Regex VariantTail = new Regex(@"(\s*[-_ ]\s*(alt|alternative|variant|copy|v\d+|\d+|\(\d+\)))+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RoughnessEnd = new Regex(@"[-_ .](r|rough|roughness|smooth|smoothness|gloss|glossiness)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex MetallicEnd = new Regex(@"[-_ .](m|metal|metallic|metalness|ms)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CapitalR = new Regex(@"[a-z0-9]R$", RegexOptions.CultureInvariant);
        private static readonly Regex CapitalM = new Regex(@"[a-z0-9]M$", RegexOptions.CultureInvariant);
        private static readonly Regex RightSide = new Regex(@"^(.*[-_ .])r$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static TextureKind NamedKind(string path)
        {
            string name = VariantTail.Replace(Path.GetFileNameWithoutExtension(path), "");
            if (RoughnessEnd.IsMatch(name) || CapitalR.IsMatch(name))
            {
                // Glove_R next to a Glove_L is the right hand, not a roughness map
                Match side = RightSide.Match(name);
                return side.Success && HasSibling(path, side.Groups[1].Value + "L") ? TextureKind.Texture : TextureKind.Roughness;
            }
            if (MetallicEnd.IsMatch(name) || CapitalM.IsMatch(name)) return TextureKind.Metallic;
            return TextureKind.Texture;
        }

        private static bool HasSibling(string path, string stem)
        {
            string folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
            foreach (string file in Directory.GetFiles(folder))
                if (!file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // A normal map is marked as one in Unity, sits in a material's normal or
        // bump slot, or is named like one ("_N", "_Nrm", "_Normal")
        private static bool IsNormalMap(string path, TextureImporter imp, HashSet<string> materialNormals)
        {
            if (imp.textureType == TextureImporterType.NormalMap || materialNormals.Contains(path)) return true;
            string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            return name.EndsWith("_n") || name.EndsWith("_nrm") || name.EndsWith("_nm") || name.EndsWith("_norm") || name.Contains("normal");
        }

        // what sits in the pack materials' slots: normal or bump, metallic (poiyomi's packed metallic maps
        // too), roughness or smoothness or gloss
        private sealed class Slots
        {
            public readonly HashSet<string> Normals = new HashSet<string>();
            public readonly HashSet<string> Metallic = new HashSet<string>();
            public readonly HashSet<string> Roughness = new HashSet<string>();

            public Slots(string packFolder)
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { packFolder }))
                    Add(AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid)));
            }

            public Slots(IEnumerable<Material> materials)
            {
                foreach (Material m in materials) Add(m);
            }

            private void Add(Material m)
            {
                if (m == null || m.shader == null) return;
                int count = m.shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    if (m.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                    var tex = m.GetTexture(m.shader.GetPropertyNameId(i));
                    if (tex == null) continue;
                    string prop = m.shader.GetPropertyName(i).ToLowerInvariant();
                    string path = AssetDatabase.GetAssetPath(tex);
                    if (prop.Contains("normal") || prop.Contains("bump")) Normals.Add(path);
                    else if (prop.Contains("metal")) Metallic.Add(path);
                    else if (prop.Contains("rough") || prop.Contains("smooth") || prop.Contains("gloss")) Roughness.Add(path);
                }
            }
        }

        // ---------------------------------------------------------------
        // reading where a pack stands
        // ---------------------------------------------------------------

        // "textures Low (1K), normal maps as shipped, menu icons 256x256, Normal
        // compression". consistent is true when every changed texture of a kind
        // agrees, so the window can show the choices that made it so. A pack
        // nobody touched starts with icons on Medium, the size we recommend
        public static string Describe(PackTextures pack, out bool consistent, out TextureChoice choice)
        {
            consistent = true;
            choice = new TextureChoice
            {
                Size = SizeLevel.Default,
                NormalSize = SizeLevel.Default,
                RoughnessSize = SizeLevel.Default,
                MetallicSize = SizeLevel.Default,
                IconSize = SizeLevel.Medium,
                Compression = CompressionChoice.Default,
            };
            var parts = new List<string>();
            parts.Add(Lang.T("textures {0}", KindState(pack.Textures, TextureKind.Texture, ref consistent, ref choice.Size)));
            parts.Add(Lang.T("normal maps {0}", KindState(pack.NormalMaps, TextureKind.NormalMap, ref consistent, ref choice.NormalSize)));
            // only the kinds a pack has, most don't come with roughness or metallic maps
            if (pack.Roughness.Count > 0)
                parts.Add(Lang.T("roughness maps {0}", KindState(pack.Roughness, TextureKind.Roughness, ref consistent, ref choice.RoughnessSize)));
            if (pack.Metallic.Count > 0)
                parts.Add(Lang.T("metallic maps {0}", KindState(pack.Metallic, TextureKind.Metallic, ref consistent, ref choice.MetallicSize)));
            parts.Add(Lang.T("menu icons {0}", KindState(pack.Icons, TextureKind.Icon, ref consistent, ref choice.IconSize)));

            Record first = null;
            bool mixed = false;
            var compressed = new List<string>(pack.Textures);
            compressed.AddRange(pack.NormalMaps);
            compressed.AddRange(pack.Roughness);
            compressed.AddRange(pack.Metallic);
            foreach (string path in compressed)
            {
                Record r = RecordAt(path);
                if (r == null) continue;
                if (first == null) first = r;
                else if (first.compression != r.compression || first.crunch != r.crunch) mixed = true;
            }
            CompressionChoice compression;
            if (mixed || (first != null && !Enum.TryParse(first.compression, out compression)))
            {
                consistent = false;
                parts.Add(Lang.T("mixed compression"));
            }
            else if (first != null && Enum.TryParse(first.compression, out compression))
            {
                choice.Compression = compression;
                choice.Crunch = first.crunch;
                parts.Add(Lang.T("{0} compression", compression == CompressionChoice.Default ? Lang.T("default") : Lang.T(compression.ToString())) + (first.crunch ? Lang.List + "Crunch" : ""));
            }
            return string.Join(Lang.List, parts.ToArray());
        }

        private static string KindState(List<string> paths, TextureKind kind, ref bool consistent, ref SizeLevel level)
        {
            string seen = null;
            foreach (string path in paths)
            {
                Record r = RecordAt(path);
                if (r == null) continue;
                if (seen == null) seen = r.size;
                else if (seen != r.size)
                {
                    consistent = false;
                    return Lang.T("mixed");
                }
            }
            if (seen == null) return Lang.T("as shipped");
            SizeLevel parsed;
            if (!Enum.TryParse(seen, out parsed))
            {
                consistent = false;
                return Lang.T("changed");
            }
            level = parsed;
            return SizeName(parsed, kind);
        }

        public static int Pending(PackTextures pack, TextureChoice choice)
        {
            int n = 0;
            foreach (var t in pack.All()) if (Change(t.Key, t.Value, choice, false)) n++;
            return n;
        }

        public static int Restorable(PackTextures pack)
        {
            int n = 0;
            foreach (var t in pack.All()) if (RestoreOne(t.Key, false)) n++;
            return n;
        }

        // ---------------------------------------------------------------
        // Changing and restoring
        // ---------------------------------------------------------------

        // returns how many textures changed; fewer than planned if cancelled.
        public static int Apply(PackTextures pack, TextureChoice choice, out bool cancelled)
        {
            var work = new List<KeyValuePair<string, TextureKind>>();
            foreach (var t in pack.All()) if (Change(t.Key, t.Value, choice, false)) work.Add(t);
            return RunWithProgress(Lang.T("Changing texture quality"), work, w => Change(w.Key, w.Value, choice, true), out cancelled);
        }

        public static int Restore(PackTextures pack, out bool cancelled)
        {
            var work = new List<KeyValuePair<string, TextureKind>>();
            foreach (var t in pack.All()) if (RestoreOne(t.Key, false)) work.Add(t);
            return RunWithProgress(Lang.T("Restoring textures as shipped"), work, w => RestoreOne(w.Key, true), out cancelled);
        }

        private static int RunWithProgress(string title, List<KeyValuePair<string, TextureKind>> work, Func<KeyValuePair<string, TextureKind>, bool> step, out bool cancelled)
        {
            cancelled = false;
            int done = 0;
            try
            {
                for (int i = 0; i < work.Count; i++)
                {
                    string file = Path.GetFileName(work[i].Key);
                    if (EditorUtility.DisplayCancelableProgressBar(title, Lang.T("{0}  ({1} of {2})", file, i + 1, work.Count), (float)i / work.Count))
                    {
                        cancelled = true;
                        break;
                    }
                    if (step(work[i])) done++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return done;
        }

        // works out what a texture should become. With write false it only
        // says whether anything would change
        private static bool Change(string path, TextureKind kind, TextureChoice choice, bool write)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null || OtherToolOwns(imp)) return false;

            Record shipped = ReadRecord(imp) ?? Capture(imp);
            SizeLevel level = choice.SizeFor(kind);
            // Icons keep their own compression; the compression choice is for textures.
            CompressionChoice compression = kind == TextureKind.Icon ? CompressionChoice.Default : choice.Compression;
            bool crunch = kind != TextureKind.Icon && choice.Crunch;
            bool normalMap = kind == TextureKind.NormalMap;
            // the size picked, as far as the source image goes. Unity never scales up
            int source = SourceLimit(imp);

            int max = level == SizeLevel.Default ? shipped.maxTextureSize : Math.Min(MaxSize(level, kind), source);
            Settings want = Want(Shipped(shipped), max, compression, crunch, normalMap);
            var wantPlatforms = new List<KeyValuePair<string, Settings>>();
            bool backToShipped = Same(want, Shipped(shipped));
            foreach (var p in shipped.platforms)
            {
                int platformMax = level == SizeLevel.Default ? p.maxTextureSize : Math.Min(MaxSize(level, kind), source);
                Settings ps = Want(Shipped(p), platformMax, compression, crunch, normalMap);
                wantPlatforms.Add(new KeyValuePair<string, Settings>(p.platform, ps));
                if (!Same(ps, Shipped(p))) backToShipped = false;
            }

            string data = "";
            if (!backToShipped)
            {
                shipped.size = level.ToString();
                shipped.compression = compression.ToString();
                shipped.crunch = crunch;
                data = JsonUtility.ToJson(shipped);
            }

            bool settingsMatch = Same(Current(imp), want);
            foreach (var p in wantPlatforms)
                if (!Same(Current(imp.GetPlatformTextureSettings(p.Key)), p.Value)) settingsMatch = false;
            if (settingsMatch && (imp.userData ?? "") == data) return false;
            if (!write) return true;

            Set(imp, want);
            foreach (var p in wantPlatforms)
            {
                var s = imp.GetPlatformTextureSettings(p.Key);
                Set(s, p.Value);
                imp.SetPlatformTextureSettings(s);
            }
            imp.userData = data;
            imp.SaveAndReimport();
            return true;
        }

        private static bool RestoreOne(string path, bool write)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return false;
            Record r = ReadRecord(imp);
            if (r == null) return false;
            if (!write) return true;

            Set(imp, Shipped(r));
            foreach (var p in r.platforms)
            {
                var s = imp.GetPlatformTextureSettings(p.platform);
                Set(s, Shipped(p));
                imp.SetPlatformTextureSettings(s);
            }
            imp.userData = "";
            imp.SaveAndReimport();
            return true;
        }

        // the largest size worth asking for: the source image's own size, as the
        // power of two Unity imports it at. Asking for more changes nothing
        private static int SourceLimit(TextureImporter imp)
        {
            int width, height;
            imp.GetSourceTextureWidthAndHeight(out width, out height);
            int side = Math.Max(width, height);
            if (side <= 0) return int.MaxValue;
            int limit = 32;
            while (limit < side && limit < 16384) limit *= 2;
            return limit;
        }

        private static Settings Want(Settings shipped, int max, CompressionChoice compression, bool crunch, bool normalMap)
        {
            Settings s = shipped;
            s.Max = max;
            if (compression != CompressionChoice.Default)
            {
                s.Compression = ToImporter(compression);
                s.Format = TextureImporterFormat.Automatic;
            }
            if (crunch)
            {
                // Unity can only crunch normal and low quality compression.
                if (s.Compression == TextureImporterCompression.CompressedHQ || s.Compression == TextureImporterCompression.Uncompressed)
                    s.Compression = TextureImporterCompression.Compressed;
                s.Crunch = true;
                s.Quality = normalMap ? CrunchQualityNormalMap : CrunchQuality;
                s.Format = TextureImporterFormat.Automatic;
            }
            else if (compression != CompressionChoice.Default)
            {
                // Default keeps a texture's own crunch; any other choice sets it
                s.Crunch = false;
            }
            return s;
        }

        private static TextureImporterCompression ToImporter(CompressionChoice c)
        {
            switch (c)
            {
                case CompressionChoice.Low: return TextureImporterCompression.CompressedLQ;
                case CompressionChoice.High: return TextureImporterCompression.CompressedHQ;
                default: return TextureImporterCompression.Compressed;
            }
        }

        private static bool Same(Settings a, Settings b)
        {
            return a.Max == b.Max && a.Compression == b.Compression && a.Crunch == b.Crunch
                && a.Format == b.Format && (!a.Crunch || a.Quality == b.Quality);
        }

        private static Settings Shipped(Record r)
        {
            return new Settings
            {
                Max = r.maxTextureSize,
                Compression = (TextureImporterCompression)r.textureCompression,
                Crunch = r.crunchedCompression,
                Quality = r.compressionQuality,
                Format = TextureImporterFormat.Automatic,
            };
        }

        private static Settings Shipped(PlatformRecord p)
        {
            return new Settings
            {
                Max = p.maxTextureSize,
                Compression = (TextureImporterCompression)p.textureCompression,
                Crunch = p.crunchedCompression,
                Quality = p.compressionQuality,
                Format = (TextureImporterFormat)p.format,
            };
        }

        private static Settings Current(TextureImporter imp)
        {
            return new Settings
            {
                Max = imp.maxTextureSize,
                Compression = imp.textureCompression,
                Crunch = imp.crunchedCompression,
                Quality = imp.compressionQuality,
                Format = TextureImporterFormat.Automatic,
            };
        }

        private static Settings Current(TextureImporterPlatformSettings s)
        {
            return new Settings
            {
                Max = s.maxTextureSize,
                Compression = s.textureCompression,
                Crunch = s.crunchedCompression,
                Quality = s.compressionQuality,
                Format = s.format,
            };
        }

        private static void Set(TextureImporter imp, Settings s)
        {
            imp.maxTextureSize = s.Max;
            imp.textureCompression = s.Compression;
            imp.crunchedCompression = s.Crunch;
            imp.compressionQuality = s.Quality;
        }

        private static void Set(TextureImporterPlatformSettings target, Settings s)
        {
            target.overridden = true;
            target.maxTextureSize = s.Max;
            target.textureCompression = s.Compression;
            target.crunchedCompression = s.Crunch;
            target.compressionQuality = s.Quality;
            target.format = s.Format;
        }

        // ---------------------------------------------------------------
        // The record kept in each texture's .meta file
        // ---------------------------------------------------------------

        private static Record Capture(TextureImporter imp)
        {
            var r = new Record
            {
                maxTextureSize = imp.maxTextureSize,
                textureCompression = (int)imp.textureCompression,
                crunchedCompression = imp.crunchedCompression,
                compressionQuality = imp.compressionQuality,
            };
            // only per-platform settings that override the default can quietly
            // win over the choice, so only those are followed.
            foreach (string platform in Platforms)
            {
                var s = imp.GetPlatformTextureSettings(platform);
                if (!s.overridden) continue;
                r.platforms.Add(new PlatformRecord
                {
                    platform = platform,
                    maxTextureSize = s.maxTextureSize,
                    textureCompression = (int)s.textureCompression,
                    crunchedCompression = s.crunchedCompression,
                    compressionQuality = s.compressionQuality,
                    format = (int)s.format,
                });
            }
            return r;
        }

        private static Record RecordAt(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            return imp != null ? ReadRecord(imp) : null;
        }

        private static Record ReadRecord(TextureImporter imp)
        {
            string data = imp.userData;
            if (string.IsNullOrEmpty(data) || data.IndexOf(Tool, StringComparison.Ordinal) < 0) return null;
            try
            {
                var r = JsonUtility.FromJson<Record>(data);
                return r != null && r.tool == Tool ? r : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool OtherToolOwns(TextureImporter imp)
        {
            return !string.IsNullOrEmpty(imp.userData) && ReadRecord(imp) == null;
        }
    }
}
#endif
