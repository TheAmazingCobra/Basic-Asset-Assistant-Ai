#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace BAAA
{
    internal enum OutfitPlace { Scene, PrefabMode, PackFile }

    internal enum TextureRole { None, Quality, Icon }

    internal sealed class Outfit
    {
        public GameObject Root;
        public OutfitPlace Place;
        public string PrefabPath;
        public string PackFolder;
        public GameObject Avatar;
        public string Label;
        // set on an Extreme or Quest copy the assistant made.
        public CopyRecord Copy;
        // for a copy: the outfit it stands in for, hidden next to it. Null when its gone.
        public GameObject Original;
    }

    internal struct OutfitStats
    {
        public int SkinnedMeshes;
        public int Meshes;
        public int MaterialSlots;
        public int PhysBones;
        public long TextureBytes;
    }

    // what BAAA is allowed to touch: pack files in a supported creators folders, and outfits in the
    // open scenes. an outfit from anyone else counts once its worn, through its VRCFury armature link
    internal static class AssistantScope
    {
        private const string VrcFuryType = "VF.Model.VRCFury";
        private const string AvatarDescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        private const string AvatarDescriptorBaseType = "VRC.SDKBase.VRC_AvatarDescriptor";
        internal const string PhysBoneType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";

        private static readonly HashSet<string> IconFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Icon", "Icons",
        };

        // poiyomi's locked shader folders, never resized for anyone
        private static readonly HashSet<string> NoQuality = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "OptimizedShaders",
        };

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        public static bool IsInsideRoot(string path)
        {
            Creator creator;
            return Creators.RootOf(path, out creator) != null;
        }

        // "<root>/Santa 2025/Icons/Main.png" -> ["Santa 2025", "Icons"]
        private static string[] FoldersBelow(string root, string path)
        {
            string[] parts = path.Substring(root.Length + 1).Split('/');
            var folders = new string[parts.Length - 1];
            Array.Copy(parts, folders, folders.Length);
            return folders;
        }

        private static bool IsPackName(Creator creator, string name)
        {
            return !Creators.Has(creator.notPacks, name) && !name.StartsWith("_") && !name.StartsWith(".");
        }

        public static string PackFolderOf(string path)
        {
            Creator creator;
            string root = Creators.RootOf(path, out creator);
            if (root == null) return null;
            string[] folders = FoldersBelow(root, path);
            if (folders.Length == 0 || !IsPackName(creator, folders[0])) return null;
            return root + "/" + folders[0];
        }

        // never anything in Packages or in our own copies. in a creators folder their untouched
        // and noQuality lists count too
        public static TextureRole RoleOf(string path, ICollection<string> menuIcons)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) return TextureRole.None;
            if (path.StartsWith(OutfitCopies.GeneratedRoot + "/", StringComparison.Ordinal)) return TextureRole.None;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".hdr" || ext == ".exr") return TextureRole.None;

            Creator creator;
            string root = Creators.RootOf(path, out creator) ?? "Assets";
            bool inIconFolder = false;
            bool noQuality = false;
            foreach (string folder in FoldersBelow(root, path))
            {
                if (creator != null && Creators.Has(creator.untouched, folder)) return TextureRole.None;
                if (IconFolders.Contains(folder)) inIconFolder = true;
                if (NoQuality.Contains(folder) || (creator != null && Creators.Has(creator.noQuality, folder))) noQuality = true;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return TextureRole.None;
            // menu icons first, one kept in a shared folder is still an icon
            if (inIconFolder || menuIcons.Contains(path)) return TextureRole.Icon;
            if (noQuality) return TextureRole.None;
            if (importer.textureType != TextureImporterType.Default && importer.textureType != TextureImporterType.NormalMap)
                return TextureRole.None;
            return TextureRole.Quality;
        }

        // textures a prefab points at directly are its VRCFury menu icons:
        // everything else it uses goes through a material or a mesh.
        public static HashSet<string> MenuIconsOf(IEnumerable<string> prefabPaths)
        {
            var icons = new HashSet<string>();
            foreach (string prefab in prefabPaths)
            {
                foreach (string dep in AssetDatabase.GetDependencies(prefab, false))
                {
                    if (dep.StartsWith("Assets/", StringComparison.Ordinal) && typeof(Texture).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(dep)))
                        icons.Add(dep);
                }
            }
            return icons;
        }

        // the same for an outfit that only lives in the scene: textures its components point at
        public static HashSet<string> MenuIconsOn(GameObject root)
        {
            var icons = new HashSet<string>();
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                SerializedProperty it = new SerializedObject(mb).GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var tex = it.objectReferenceValue as Texture;
                    if (tex == null) continue;
                    string p = AssetDatabase.GetAssetPath(tex);
                    if (p.StartsWith("Assets/", StringComparison.Ordinal)) icons.Add(p);
                }
            }
            return icons;
        }

        public static List<string> PackPrefabs()
        {
            var result = new List<string>();
            List<string> roots = Creators.Roots();
            if (roots.Count == 0) return result;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", roots.ToArray()))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (PackFolderOf(path) == null || result.Contains(path)) continue;
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null && HasOutfitParts(go)) result.Add(path);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // A pack prefab worth listing has VRCFury on it, or scripts that failed
        // to load because VRCFury is not installed.
        private static bool HasOutfitParts(GameObject go)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || IsVrcFury(mb)) return true;
            }
            return false;
        }

        public static List<Outfit> FindOutfits()
        {
            var outfits = new List<Outfit>();
            List<string> packPrefabs = PackPrefabs();
            var known = new HashSet<string>(packPrefabs);

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject go in scene.GetRootGameObjects())
                    CollectSceneOutfits(go.transform, known, outfits);
            }

            // an outfit swapped out for its Extreme or Quest copy waits hidden
            // next to it, and only the copy is listed
            var swapped = new HashSet<GameObject>();
            foreach (Outfit o in outfits)
                if (o.Original != null) swapped.Add(o.Original);
            outfits.RemoveAll(o => swapped.Contains(o.Root));

            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null && known.Contains(stage.assetPath))
                outfits.Add(MakeOutfit(stage.prefabContentsRoot, OutfitPlace.PrefabMode, stage.assetPath, null));

            foreach (string path in packPrefabs)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null) outfits.Add(MakeOutfit(go, OutfitPlace.PackFile, path, null));
            }
            return outfits;
        }

        private static void CollectSceneOutfits(Transform t, HashSet<string> known, List<Outfit> outfits)
        {
            GameObject go = t.gameObject;
            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (known.Contains(source))
                {
                    outfits.Add(MakeOutfit(go, OutfitPlace.Scene, source, FindAvatar(t)));
                    return;
                }
                CopyRecord copy = OutfitCopies.RecordAt(source);
                if (copy != null)
                {
                    Outfit o = MakeOutfit(go, OutfitPlace.Scene, source, FindAvatar(t));
                    o.Copy = copy;
                    o.PackFolder = string.IsNullOrEmpty(copy.Pack) ? null : copy.Pack;
                    o.Original = OutfitCopies.FindOriginal(go, copy);
                    outfits.Add(o);
                    return;
                }
            }
            if (IsLooseOutfit(go))
            {
                // a prefab from a creator we have no file for still keeps its prefab, the fixes use it
                string source = PrefabUtility.IsAnyPrefabInstanceRoot(go) ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) : null;
                outfits.Add(MakeOutfit(go, OutfitPlace.Scene, source, FindAvatar(t)));
                return;
            }
            foreach (Transform child in t)
                CollectSceneOutfits(child, known, outfits);
        }

        // an outfit that isnt a known pack prefab: one that got unpacked, or one from a creator without
        // a file. it has a VRCFury Armature Link, and its either worn or made of a packs meshes
        private static bool IsLooseOutfit(GameObject go)
        {
            bool armatureLink = false;
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (IsVrcFury(mb) && VrcFuryData.FeatureType(mb) == "ArmatureLink")
                {
                    armatureLink = true;
                    break;
                }
            }
            if (!armatureLink) return false;
            return FindAvatar(go.transform) != null || PackFolderOfMeshes(go) != null;
        }

        private static string PackFolderOfMeshes(GameObject root)
        {
            string pack = null;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                string meshPack = PackFolderOf(AssetDatabase.GetAssetPath(smr.sharedMesh));
                if (meshPack == null) return null;
                if (pack == null) pack = meshPack;
            }
            return pack;
        }

        private static Outfit MakeOutfit(GameObject root, OutfitPlace place, string prefabPath, GameObject avatar)
        {
            string label;
            if (place == OutfitPlace.Scene)
                label = avatar != null ? Lang.T("{0} on {1}", root.name, avatar.name) : Lang.T("{0} (not on an avatar)", root.name);
            else if (place == OutfitPlace.PrefabMode)
                label = Lang.T("{0} (editing the prefab)", root.name);
            else
                label = Lang.T("{0} (pack file)", root.name);

            return new Outfit
            {
                Root = root,
                Place = place,
                PrefabPath = prefabPath,
                PackFolder = PackFolderOf(prefabPath) ?? PackFolderOfMeshes(root),
                Avatar = avatar,
                Label = label,
            };
        }

        public static GameObject FindAvatar(Transform t)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                foreach (var c in p.GetComponents<Component>())
                {
                    if (c != null && Inherits(c.GetType(), AvatarDescriptorType, AvatarDescriptorBaseType))
                        return p.gameObject;
                }
            }
            return null;
        }

        private static bool Inherits(Type type, string name, string otherName)
        {
            for (Type x = type; x != null; x = x.BaseType)
            {
                if (x.FullName == name || x.FullName == otherName) return true;
            }
            return false;
        }

        public static bool IsVrcFury(Component c)
        {
            return c != null && c.GetType().FullName == VrcFuryType;
        }

        // VRCFury and VRChat leave EditorOnly objects, and everything under them,
        // out of the upload.
        public static bool InsideEditorOnly(Transform t)
        {
            for (Transform x = t; x != null; x = x.parent)
                if (x.CompareTag("EditorOnly")) return true;
            return false;
        }

        public static bool VrcFuryInstalled
        {
            get { return FindType(VrcFuryType) != null; }
        }

        // looks a type up by name, so this code never depends on VRCFury, Poiyomi or
        // the VRChat SDK being installed. A missing package must never become a
        // compile error in a customer's project
        public static Type FindType(string fullName)
        {
            Type found;
            if (TypeCache.TryGetValue(fullName, out found)) return found;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = asm.GetType(fullName, false);
                if (found != null) break;
            }
            TypeCache[fullName] = found;
            return found;
        }

        public static OutfitStats Measure(GameObject root)
        {
            var stats = new OutfitStats();
            if (root == null) return stats;

            var textures = new HashSet<Texture>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (InsideEditorOnly(r.transform)) continue;
                if (r is SkinnedMeshRenderer) stats.SkinnedMeshes++;
                else if (r is MeshRenderer) stats.Meshes++;
                else continue;

                foreach (var m in r.sharedMaterials)
                {
                    stats.MaterialSlots++;
                    foreach (Texture tex in TextureUse.Of(m)) textures.Add(tex);
                }
            }

            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c != null && c.GetType().FullName == PhysBoneType && !InsideEditorOnly(c.transform)) stats.PhysBones++;
            }

            foreach (var tex in textures)
                stats.TextureBytes += GpuBytes(tex);
            return stats;
        }

        // Memory the texture takes on the graphics card, mip maps included
        internal static long GpuBytes(Texture tex)
        {
            var t2 = tex as Texture2D;
            if (t2 != null)
            {
                GraphicsFormat format = t2.graphicsFormat;
                long block = GraphicsFormatUtility.GetBlockSize(format);
                long bw = GraphicsFormatUtility.GetBlockWidth(format);
                long bh = GraphicsFormatUtility.GetBlockHeight(format);
                if (block > 0 && bw > 0 && bh > 0)
                {
                    long total = 0;
                    int w = t2.width;
                    int h = t2.height;
                    for (int i = 0; i < Math.Max(1, t2.mipmapCount); i++)
                    {
                        total += ((w + bw - 1) / bw) * ((h + bh - 1) / bh) * block;
                        w = Math.Max(1, w / 2);
                        h = Math.Max(1, h / 2);
                    }
                    return total;
                }
            }
            return UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
        }
    }
}
#endif
