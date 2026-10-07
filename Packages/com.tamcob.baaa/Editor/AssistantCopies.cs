#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace BAAA
{
    // new kinds go at the end, records keep the number
    internal enum CopyKind { Extreme, Quest, Decimated, Fused }

    // lives in the copy prefabs importer userData, thats how we know what a copy is and where it came from
    [Serializable]
    internal sealed class CopyRecord
    {
        public string Marker = OutfitCopies.Marker;
        public CopyKind Kind;
        // the pack prefab the original is a copy of, when it is one.
        public string Source = "";
        public string SourceHash = "";
        public string Pack = "";
        public string OriginalName = "";
        public string OriginalTag = "Untagged";
        public bool OriginalActive = true;
        // scene and place of the original, so a remake reuses the same files
        public string MadeFrom = "";
        public int AtlasSize;
        // Extreme and Quest: the fused mesh, and the full-detail one the decimator starts from
        public string Mesh = "";
        public string FullMesh = "";
        // the share of triangles the decimator keeps
        public float Keep = 1f;
        // the outfit's triangles the decimator aims for, counted per renderer. 0 is full detail
        public int Target;
        // Extreme and Quest: the textures were baked again for the reduced mesh
        public bool Rebaked;
        // Quest: every physbone the outfit had, and the ones picked to keep
        public List<CopyPhysBone> PhysBones = new List<CopyPhysBone>();
        public List<string> Kept = new List<string>();
        // Decimated: each of the outfit's meshes, and the copy of it the decimator reduces
        public List<CopyMesh> Meshes = new List<CopyMesh>();
        // Fused, Extreme and Quest: the color sliders, and where they sat when their color got baked in
        public List<CopyColor> Colors = new List<CopyColor>();
    }

    [Serializable]
    internal sealed class CopyColor
    {
        public string Name;
        public float Value;
    }

    [Serializable]
    internal sealed class CopyMesh
    {
        public string Name;
        // the pack's mesh, as "guid:local id".
        public string Source;
        // the copy's own mesh file
        public string Mesh;
        // follows the slider, stays at full detail, or keeps its own share
        public MeshMode Mode;
        public float Keep = 1f;
    }

    // new ones go at the end, records keep the number
    internal enum MeshMode { Auto, Full, Own }

    [Serializable]
    internal sealed class CopyPhysBone
    {
        public string Id;
        public string Name;
        public string Group;
    }

    internal sealed class QuestInfo
    {
        public HashSet<string> On = new HashSet<string>();
        public int Triangles;
        public int FullTriangles;
        public int SkinnedMeshes;
        public int MaterialSlots;
        public int Colliders;
        public int Contacts;
        public long TextureBytes;
    }

    // writes a copys files into its own folder. if a file is already there it gets updated in place
    // so the GUID stays the same and nothing pointing at it breaks
    internal sealed class GeneratedFolder
    {
        public readonly string Path;
        public readonly HashSet<string> Written = new HashSet<string>();

        public GeneratedFolder(string path)
        {
            Path = path;
        }

        public T Save<T>(T asset, string fileName) where T : UnityEngine.Object
        {
            string path = Path + "/" + fileName;
            Written.Add(path);
            string name = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) as T;
            if (existing != null && existing.GetType() == asset.GetType())
            {
                var mesh = existing as Mesh;
                if (mesh != null) OutfitCopies.Overwrite(mesh, asset as Mesh);
                else EditorUtility.CopySerialized(asset, existing);
                existing.name = name;
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(asset);
                return existing;
            }
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
            asset.name = name;
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        public Texture2D SavePng(Color32[] pixels, int side, bool linear, string fileName, Action<TextureImporter> setup)
        {
            string path = Path + "/" + fileName;
            Written.Add(path);
            var tex = new Texture2D(side, side, TextureFormat.RGBA32, false, linear);
            tex.SetPixels32(pixels);
            tex.Apply(false);
            byte[] png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            File.WriteAllBytes(OutfitCopies.FullPath(path), png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                setup(importer);
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // removes files from an earlier version of this copy that weren't made again
        public void DeleteRest()
        {
            foreach (string guid in AssetDatabase.FindAssets("", new[] { Path }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(p) || Written.Contains(p)) continue;
                if (System.IO.Path.GetDirectoryName(p).Replace('\\', '/') != Path) continue;
                AssetDatabase.DeleteAsset(p);
            }
        }
    }

    // the copies (extreme, quest and decimated). we build them off the outfit thats in the scene,
    // save a prefab under Assets/BAAA Copies, outside every creators folder, and swap it in.
    // the original just sits there hidden next to it til you hit go back
    internal static partial class OutfitCopies
    {
        public const string Marker = "BAAACopy";
        public const string GeneratedRoot = "Assets/BAAA Copies";
        private const string ApplyDuringUploadType = "VF.Model.Feature.ApplyDuringUpload";
        private const string BlendShapeActionType = "VF.Model.StateAction.BlendShapeAction";
        private const string VrcFuryType = "VF.Model.VRCFury";

        private struct Carried
        {
            public string BlendShape;
            public float Value;
            public Renderer Renderer;
            public bool AllRenderers;
        }

        // the copy's name in its folder, always english. a folder that changed name with the
        // language would leave "make it again" looking in the wrong place
        public static string Label(CopyKind kind)
        {
            switch (kind)
            {
                case CopyKind.Extreme: return "Extreme";
                case CopyKind.Quest: return "Quest";
                case CopyKind.Fused: return "Fused";
                default: return "Decimated";
            }
        }

        // the same name, for people to read
        public static string Shown(CopyKind kind)
        {
            if (kind == CopyKind.Decimated) return Lang.T("Decimated");
            return kind == CopyKind.Fused ? Lang.T("Fused") : Label(kind);
        }

        // -------------------------------------------------------------------
        // records
        // -------------------------------------------------------------------

        public static CopyRecord RecordAt(string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.StartsWith(GeneratedRoot + "/", StringComparison.Ordinal)) return null;
            var importer = AssetImporter.GetAtPath(prefabPath);
            if (importer == null || string.IsNullOrEmpty(importer.userData) || !importer.userData.Contains(Marker)) return null;
            try
            {
                var record = JsonUtility.FromJson<CopyRecord>(importer.userData);
                return record != null && record.Marker == Marker ? record : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteRecord(string prefabPath, CopyRecord record)
        {
            var importer = AssetImporter.GetAtPath(prefabPath);
            if (importer == null) return;
            string json = JsonUtility.ToJson(record);
            if (importer.userData == json) return;
            importer.userData = json;
            importer.SaveAndReimport();
        }

        // the original waits next to its copy, hidden and tagged EditorOnly.
        public static GameObject FindOriginal(GameObject copy, CopyRecord record)
        {
            var siblings = new List<GameObject>();
            Transform parent = copy.transform.parent;
            if (parent != null) foreach (Transform t in parent) siblings.Add(t.gameObject);
            else siblings.AddRange(copy.scene.GetRootGameObjects());
            int at = siblings.IndexOf(copy);
            GameObject best = null;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < siblings.Count; i++)
            {
                GameObject s = siblings[i];
                if (s == copy || s.name != record.OriginalName || s.activeSelf || !s.CompareTag("EditorOnly")) continue;
                int d = Math.Abs(i - at);
                if (d < bestDistance)
                {
                    best = s;
                    bestDistance = d;
                }
            }
            return best;
        }

        // A pack update changes the prefab the copy was made from
        public static bool OutOfDate(CopyRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Source) || string.IsNullOrEmpty(record.SourceHash)) return false;
            if (AssetDatabase.LoadMainAssetAtPath(record.Source) == null) return false;
            return AssetDatabase.GetAssetDependencyHash(record.Source).ToString() != record.SourceHash;
        }

        // -------------------------------------------------------------------
        // making a copy
        // -------------------------------------------------------------------

        // returns null when it worked, or what went wrong.
        public static string Make(Outfit o, CopyKind kind, int atlasSize, CopyRecord previous, out GameObject made)
        {
            made = null;
            if (!OutfitPieces.CanEdit(o)) return Lang.T("Put the outfit in your scene first.");
            if (o.Copy != null) return Lang.T("This is already a copy. Go back to the original first.");
            if (kind != CopyKind.Decimated && !AssistantScope.VrcFuryInstalled)
                return Lang.T("VRCFury isn't installed, so this outfit's pieces can't be read. The Check tab shows how to fix that.");
            if (kind == CopyKind.Quest && AtlasBuilder.QuestShader() == null)
                return Lang.T("VRChat's Quest shaders aren't in this project. Add the VRChat SDK with the Creator Companion first.");

            GameObject original = o.Root;
            string label = Label(kind);
            string pack = o.PackFolder != null ? Path.GetFileName(o.PackFolder) : "Other outfits";
            string madeFrom = MadeFrom(original);
            string folderPath = FolderFor(pack, original.name + " (" + label + ")", madeFrom);
            string copyName = Path.GetFileName(folderPath);

            // What the original wears now, read before anything changes
            var pieceToggles = new HashSet<string>();
            if (kind != CopyKind.Decimated)
                foreach (Piece p in OutfitPieces.Find(o))
                    if (!p.Dropped && p.Toggle != null) pieceToggles.Add(ComponentKey(p.Toggle, original.transform));
            List<CopyPhysBone> physBones = kind == CopyKind.Quest ? PhysBonesOf(o) : new List<CopyPhysBone>();

            GameObject copy = null;
            try
            {
                Progress(Lang.T("Copying the outfit..."), 0.05f);
                EnsureFolder(folderPath);
                var folder = new GeneratedFolder(folderPath);
                copy = UnityEngine.Object.Instantiate(original, original.transform.parent);
                copy.name = copyName;
                copy.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1);
                if (copy.CompareTag("EditorOnly")) copy.tag = "Untagged";
                copy.SetActive(true);
                Transform root = copy.transform;

                var meshes = new List<CopyMesh>();
                var colors = new List<CopyColor>();
                Mesh mesh = null;
                Mesh full = null;
                if (kind == CopyKind.Decimated)
                {
                    // Everything stays as it is, toggles and dropped pieces included;
                    // each mesh gets a copy the decimator can reduce
                    Progress(Lang.T("Copying the meshes..."), 0.4f);
                    CopyMeshes(root, folder, copyName, meshes);
                }
                else
                {
                    MakeFused(o, kind, atlasSize, root, pieceToggles, folder, copyName, previous, colors, out mesh, out full);
                }

                Progress(Lang.T("Saving the {0} version...", Shown(kind)), 0.85f);
                string prefabPath = folderPath + "/" + copyName + ".prefab";
                folder.Written.Add(prefabPath);
                bool saved;
                PrefabUtility.SaveAsPrefabAssetAndConnect(copy, prefabPath, InteractionMode.AutomatedAction, out saved);
                if (!saved) throw new InvalidOperationException(Lang.T("Unity couldn't save {0}.", prefabPath));

                string source = PrefabUtility.IsAnyPrefabInstanceRoot(original) ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(original) : "";
                var record = new CopyRecord
                {
                    Kind = kind,
                    Source = source,
                    SourceHash = source.Length > 0 ? AssetDatabase.GetAssetDependencyHash(source).ToString() : "",
                    Pack = o.PackFolder ?? "",
                    OriginalName = original.name,
                    OriginalTag = original.tag,
                    OriginalActive = original.activeSelf,
                    MadeFrom = madeFrom,
                    AtlasSize = atlasSize,
                    Mesh = mesh != null ? GuidOf(mesh) : "",
                    FullMesh = full != null ? GuidOf(full) : "",
                    Keep = 1f,
                    PhysBones = physBones,
                    Kept = previous != null ? new List<string>(previous.Kept) : new List<string>(),
                    Meshes = meshes,
                    Colors = colors,
                };
                record.Kept.RemoveAll(id => !physBones.Exists(p => p.Id == id));
                // each mesh keeps what it was set to in the decimator
                if (previous != null)
                {
                    foreach (CopyMesh m in meshes)
                    {
                        CopyMesh was = previous.Meshes.Find(p => p.Source == m.Source) ?? previous.Meshes.Find(p => p.Name == m.Name);
                        if (was == null) continue;
                        m.Mode = was.Mode;
                        m.Keep = was.Keep;
                    }
                }
                WriteRecord(prefabPath, record);
                folder.DeleteRest();
                AssetDatabase.SaveAssets();

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName(Lang.T("Make {0} version", Shown(kind)));
                int group = Undo.GetCurrentGroup();
                Undo.RegisterCreatedObjectUndo(copy, Lang.T("Make {0} version", Shown(kind)));
                Undo.RecordObject(original, Lang.T("Make {0} version", Shown(kind)));
                original.tag = "EditorOnly";
                original.SetActive(false);
                if (PrefabUtility.IsPartOfPrefabInstance(original)) PrefabUtility.RecordPrefabInstancePropertyModifications(original);
                Undo.CollapseUndoOperations(group);

                if (previous != null && (previous.Target > 0 || previous.Keep < 0.999f || meshes.Exists(m => m.Mode != MeshMode.Auto)))
                {
                    Progress(Lang.T("Reducing triangles..."), 0.95f);
                    string reduced = previous.Target > 0 ? SetTarget(prefabPath, copy, previous.Target) : SetKeep(prefabPath, copy, previous.Keep);
                    if (reduced != null) Debug.LogWarning("Asset Assistant: " + reduced);
                    else if (previous.Rebaked && kind != CopyKind.Decimated) Rebake(prefabPath, copy);
                }
                made = copy;
                return null;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                return e.Message;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // Fused, Extreme and Quest: the picked colors and what the toggles do at rest,
        // then every piece that shows fused into one mesh
        private static void MakeFused(Outfit o, CopyKind kind, int atlasSize, Transform root, HashSet<string> pieceToggles,
            GeneratedFolder folder, string copyName, CopyRecord previous, List<CopyColor> colors, out Mesh mesh, out Mesh full)
        {
            // before ApplyToggles takes the sliders away
            colors.AddRange(BakeColors(root, folder, copyName));
            var carried = new List<Carried>();
            ApplyToggles(root, pieceToggles, carried);
            RemoveEditorOnly(root);
            if (kind == CopyKind.Quest) StripForQuest(root);

            var fuse = new List<Renderer>();
            var gone = new List<Renderer>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                bool shown = r.enabled && r.gameObject.activeInHierarchy && MeshOf(r) != null;
                // cloth needs a mesh of its own.
                if (r.GetComponent<Cloth>() != null) continue;
                if (shown) fuse.Add(r);
                else gone.Add(r);
            }
            if (fuse.Count == 0) throw new InvalidOperationException(Lang.T("Nothing on this outfit is showing, so there's nothing to fuse. Put some pieces back on in the Pieces tab."));

            Progress(Lang.T("Packing the textures..."), 0.25f);
            MaterialPlan plan = kind == CopyKind.Extreme
                ? AtlasBuilder.PlanExtreme(fuse, atlasSize, folder, copyName)
                : kind == CopyKind.Quest
                    ? AtlasBuilder.PlanQuest(fuse, atlasSize, folder, copyName)
                    : AtlasBuilder.PlanFused(fuse);

            Progress(Lang.T("Fusing the meshes..."), 0.6f);
            FusedMesh fused = MeshFuser.Fuse(fuse, m =>
            {
                SlotPlan sp;
                return plan.Of.TryGetValue(m, out sp) ? sp : null;
            }, plan.Slots.Count, copyName);
            mesh = folder.Save(fused.Mesh, copyName + " mesh.asset");
            // The decimator always starts from this one, so going back up restores detail
            full = folder.Save(UnityEngine.Object.Instantiate(mesh), copyName + " full mesh.asset");

            var fusedGo = new GameObject(o.Root.name + " (fused)");
            fusedGo.transform.SetParent(root, false);
            var smr = fusedGo.AddComponent<SkinnedMeshRenderer>();
            CopySettings(fused.Template, smr);
            smr.sharedMesh = mesh;
            smr.bones = fused.Bones;
            smr.rootBone = fused.RootBone;
            smr.sharedMaterials = plan.Slots.ToArray();
            smr.localBounds = fused.LocalBounds;
            smr.updateWhenOffscreen = false;
            for (int i = 0; i < fused.Weights.Length; i++) smr.SetBlendShapeWeight(i, fused.Weights[i]);

            var fusedFrom = new HashSet<Renderer>(fuse);
            var removed = new HashSet<Renderer>(fuse);
            removed.UnionWith(gone);
            RetargetLinks(root, fusedFrom, removed, smr);
            for (int i = 0; i < carried.Count; i++)
            {
                Carried c = carried[i];
                if (c.Renderer != null && removed.Contains(c.Renderer)) c.Renderer = smr;
                carried[i] = c;
            }
            RemoveRenderers(root, removed, fused.Bones);
            if (carried.Count > 0) AddApplyDuringUpload(root, carried);
            if (kind == CopyKind.Quest) ParkPhysBones(root, previous != null ? previous.Kept : null);
            foreach (UnityEngine.Object temp in plan.Temporary)
                if (temp != null && !EditorUtility.IsPersistent(temp)) UnityEngine.Object.DestroyImmediate(temp);
        }

        // A copy has no color sliders, so each one's color gets written into copies of the materials
        // it changes. A slider at 25% shows the material's own value moved 25% toward the slider's
        // end value, the same blend VRCFury plays and the colors tab previews. Pieces that share a
        // material but not a slider get their own copy, so the color stays where it was
        private static List<CopyColor> BakeColors(Transform root, GeneratedFolder folder, string copyName)
        {
            var record = new List<CopyColor>();
            List<ColorChanger> changers = OutfitColors.Find(new Outfit { Root = root.gameObject, Place = OutfitPlace.Scene });
            var edits = new Dictionary<KeyValuePair<Renderer, int>, SortedDictionary<string, KeyValuePair<int, Vector4>>>();
            foreach (ColorChanger c in changers)
            {
                record.Add(new CopyColor { Name = c.Name, Value = c.Value });
                if (c.Value <= 0f) continue;
                foreach (ColorTarget t in c.Targets)
                {
                    var key = new KeyValuePair<Renderer, int>(t.Renderer, t.Slot);
                    SortedDictionary<string, KeyValuePair<int, Vector4>> list;
                    if (!edits.TryGetValue(key, out list))
                    {
                        list = new SortedDictionary<string, KeyValuePair<int, Vector4>>(StringComparer.Ordinal);
                        edits[key] = list;
                    }
                    list[t.Property] = new KeyValuePair<int, Vector4>(t.Type, Vector4.Lerp(t.From, t.To, Mathf.Clamp01(c.Value)));
                }
            }

            var variants = new Dictionary<string, Material>();
            var names = new HashSet<string>();
            foreach (var pair in edits)
            {
                Renderer r = pair.Key.Key;
                int slot = pair.Key.Value;
                Material[] mats = r.sharedMaterials;
                Material m = slot < mats.Length ? mats[slot] : null;
                if (m == null) continue;
                var sig = new System.Text.StringBuilder().Append(m.GetInstanceID());
                foreach (var e in pair.Value) sig.Append('|').Append(e.Key).Append('=').Append(e.Value.Value.ToString("R"));
                Material variant;
                if (!variants.TryGetValue(sig.ToString(), out variant))
                {
                    variant = new Material(m);
                    AtlasBuilder.CopyTags(m, variant);
                    foreach (var e in pair.Value)
                    {
                        Vector4 v = e.Value.Value;
                        if (e.Value.Key == 0) variant.SetFloat(e.Key, v.x);
                        else if (e.Value.Key == 1) variant.SetColor(e.Key, v);
                        else variant.SetVector(e.Key, v);
                    }
                    string name = Clean(m.name) + " colors";
                    for (int n = 2; !names.Add(name); n++) name = Clean(m.name) + " colors " + n;
                    variant = folder.Save(variant, copyName + " " + name + ".mat");
                    variants[sig.ToString()] = variant;
                }
                mats[slot] = variant;
                r.sharedMaterials = mats;
            }
            return record;
        }

        // Decimated: every mesh of the outfit, pieces that start off included,
        // gets its own copy in the copy's folder. Dropped pieces never upload,
        // so they keep the packs. The packs meshes stay untouched and are what
        // the decimator starts from
        private static void CopyMeshes(Transform root, GeneratedFolder folder, string copyName, List<CopyMesh> meshes)
        {
            var copies = new Dictionary<Mesh, Mesh>();
            var names = new HashSet<string>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (AssistantScope.InsideEditorOnly(r.transform)) continue;
                Mesh source = MeshOf(r);
                if (source == null) continue;
                Mesh own;
                if (!copies.TryGetValue(source, out own))
                {
                    string name = Clean(source.name);
                    for (int n = 2; !names.Add(name); n++) name = Clean(source.name) + " " + n;
                    string sourceId = AssetId(source);
                    // A mesh that isn't a file of its own keeps a full-detail copy too
                    if (sourceId.Length == 0) sourceId = AssetId(folder.Save(UnityEngine.Object.Instantiate(source), copyName + " " + name + " full.asset"));
                    own = folder.Save(UnityEngine.Object.Instantiate(source), copyName + " " + name + ".asset");
                    copies[source] = own;
                    meshes.Add(new CopyMesh { Name = source.name, Source = sourceId, Mesh = GuidOf(own) });
                }
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) smr.sharedMesh = own;
                else r.GetComponent<MeshFilter>().sharedMesh = own;
            }
            if (meshes.Count == 0) throw new InvalidOperationException(Lang.T("This outfit has no meshes to decimate."));
        }

        private static string AssetId(UnityEngine.Object asset)
        {
            string guid;
            long id;
            return asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out id) && !string.IsNullOrEmpty(guid)
                ? guid + ":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        // finds a mesh by "guid:local id", including meshes inside a model file
        private static Mesh MeshById(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;
            int colon = assetId.IndexOf(':');
            long id;
            if (colon < 0 || !long.TryParse(assetId.Substring(colon + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id)) return null;
            string path = AssetDatabase.GUIDToAssetPath(assetId.Substring(0, colon));
            if (string.IsNullOrEmpty(path)) return null;
            foreach (UnityEngine.Object a in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var m = a as Mesh;
                string g;
                long l;
                if (m != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out g, out l) && l == id) return m;
            }
            return null;
        }

        // puts the original back and takes the copy out of the scene. Its files stay
        public static void GoBack(Outfit copy)
        {
            if (copy == null || copy.Root == null || copy.Copy == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Go back to the original"));
            int group = Undo.GetCurrentGroup();
            GameObject original = copy.Original;
            if (original != null)
            {
                Undo.RecordObject(original, Lang.T("Go back to the original"));
                original.tag = string.IsNullOrEmpty(copy.Copy.OriginalTag) ? "Untagged" : copy.Copy.OriginalTag;
                original.SetActive(copy.Copy.OriginalActive);
                if (PrefabUtility.IsPartOfPrefabInstance(original))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(original);
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(original);
                    if (source != null && source.tag == original.tag) RevertIfOverridden(original, "m_TagString");
                    if (source != null && source.activeSelf == original.activeSelf) RevertIfOverridden(original, "m_IsActive");
                }
            }
            Undo.DestroyObjectImmediate(copy.Root);
            Undo.CollapseUndoOperations(group);
        }

        // makes the copy again from the original, with the same choices
        public static string Rebuild(Outfit copy, out GameObject made)
        {
            made = null;
            CopyRecord record = copy.Copy;
            GameObject original = copy.Original;
            if (record == null || original == null) return Lang.T("The original isn't next to this copy anymore, so it can't be made again.");
            GoBack(copy);
            var o = new Outfit
            {
                Root = original,
                Place = OutfitPlace.Scene,
                PrefabPath = PrefabUtility.IsAnyPrefabInstanceRoot(original) ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(original) : null,
                PackFolder = string.IsNullOrEmpty(record.Pack) ? null : record.Pack,
                Avatar = copy.Avatar,
                Label = original.name,
            };
            return Make(o, record.Kind, record.AtlasSize, record, out made);
        }

        private static void Progress(string what, float amount)
        {
            EditorUtility.DisplayProgressBar("Asset Assistant", what, amount);
        }

        private static string MadeFrom(GameObject go)
        {
            return go.scene.path + "|" + HierarchyPath(go.transform);
        }

        private static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        // one folder per original; a remake of the same original reuses it.
        private static string FolderFor(string pack, string name, string madeFrom)
        {
            string parent = GeneratedRoot + "/" + Clean(pack);
            string clean = Clean(name);
            for (int n = 1; ; n++)
            {
                string folder = parent + "/" + clean + (n > 1 ? " " + n : "");
                if (!AssetDatabase.IsValidFolder(folder)) return folder;
                CopyRecord r = RecordAt(folder + "/" + Path.GetFileName(folder) + ".prefab");
                if (r == null || r.MadeFrom == madeFrom) return folder;
            }
        }

        private static string Clean(string name)
        {
            var sb = new System.Text.StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim().TrimEnd('.');
            return s.Length > 0 ? s : "Outfit";
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        internal static string FullPath(string assetPath)
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
        }

        private static string GuidOf(UnityEngine.Object asset)
        {
            return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
        }

        private static string Rel(Transform t, Transform root)
        {
            return t == root ? "" : AnimationUtility.CalculateTransformPath(t, root);
        }

        // A component by where it sits and how many of its kind come before it there.
        private static string ComponentKey(Component c, Transform root)
        {
            int index = 0;
            foreach (var other in c.GetComponents(c.GetType()))
            {
                if (other == c) break;
                index++;
            }
            return Rel(c.transform, root) + "#" + index;
        }

        private static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var filter = r.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // -------------------------------------------------------------------
        // toggles
        // -------------------------------------------------------------------

        // the copy has no toggles, so it wears what they do at rest: every kept
        // piece is on, and so is every toggle that starts on. What those do to
        // the body, like shrinking it under a top, keeps happening through an
        // Apply During Upload. Color changers go, and other sliders keep the
        // value they start at
        private static void ApplyToggles(Transform root, HashSet<string> pieceToggles, List<Carried> carried)
        {
            var toggles = new List<VfFeature>();
            foreach (VfFeature f in VrcFuryData.Features(root.gameObject))
                if (f.Type == "Toggle" && f.Prop.propertyPath == "content") toggles.Add(f);

            // Toggles that start on first, then the pieces, so the pieces win.
            foreach (bool pieces in new[] { false, true })
            {
                foreach (VfFeature f in toggles)
                {
                    SerializedProperty t = f.Prop;
                    bool piece = pieceToggles.Contains(ComponentKey(f.Component, root));
                    if (piece != pieces) continue;
                    SerializedProperty state = t.FindPropertyRelative("state");
                    if (VrcFuryData.Bool(t, "slider"))
                    {
                        var d = t.FindPropertyRelative("defaultSliderValue");
                        float start = d != null ? d.floatValue : 0f;
                        if (start > 0f) CarryShapes(state, start, carried);
                        continue;
                    }
                    if (!piece && !VrcFuryData.Bool(t, "defaultOn")) continue;
                    foreach (SerializedProperty action in VrcFuryData.Actions(state))
                    {
                        if (VrcFuryData.ShortType(action.managedReferenceFullTypename) != "ObjectToggleAction") continue;
                        var objProp = action.FindPropertyRelative("obj");
                        var obj = objProp != null ? objProp.objectReferenceValue as GameObject : null;
                        if (obj == null || !obj.transform.IsChildOf(root)) continue;
                        var modeProp = action.FindPropertyRelative("mode");
                        int mode = modeProp != null ? modeProp.intValue : 0;
                        obj.SetActive(mode == 0 ? true : mode == 1 ? false : !obj.activeSelf);
                    }
                    CarryShapes(state, 1f, carried);
                }
            }
            foreach (VfFeature f in toggles)
                if (f.Component != null) UnityEngine.Object.DestroyImmediate(f.Component);
        }

        private static void CarryShapes(SerializedProperty state, float scale, List<Carried> carried)
        {
            foreach (SerializedProperty action in VrcFuryData.Actions(state))
            {
                if (VrcFuryData.ShortType(action.managedReferenceFullTypename) != "BlendShapeAction") continue;
                string shape = VrcFuryData.Text(action, "blendShape");
                if (shape.Length == 0) continue;
                var value = action.FindPropertyRelative("blendShapeValue");
                var renderer = action.FindPropertyRelative("renderer");
                carried.Add(new Carried
                {
                    BlendShape = shape,
                    Value = (value != null ? value.floatValue : 100f) * scale,
                    Renderer = renderer != null ? renderer.objectReferenceValue as Renderer : null,
                    AllRenderers = VrcFuryData.Bool(action, "allRenderers", true),
                });
            }
        }

        private static void AddApplyDuringUpload(Transform root, List<Carried> carried)
        {
            Type vf = AssistantScope.FindType(VrcFuryType);
            Type apply = AssistantScope.FindType(ApplyDuringUploadType);
            Type shapeAction = AssistantScope.FindType(BlendShapeActionType);
            if (vf == null || apply == null || shapeAction == null) return;

            object feature = Activator.CreateInstance(apply);
            var actionField = apply.GetField("action");
            if (actionField == null) return;
            object state = actionField.GetValue(feature);
            if (state == null)
            {
                state = Activator.CreateInstance(actionField.FieldType);
                actionField.SetValue(feature, state);
            }
            var list = state.GetType().GetField("actions") != null ? state.GetType().GetField("actions").GetValue(state) as IList : null;
            if (list == null) return;
            foreach (Carried c in carried)
            {
                object a = Activator.CreateInstance(shapeAction);
                Set(a, "blendShape", c.BlendShape);
                Set(a, "blendShapeValue", c.Value);
                Set(a, "renderer", c.Renderer);
                Set(a, "allRenderers", c.AllRenderers);
                list.Add(a);
            }

            var component = root.gameObject.AddComponent(vf);
            var so = new SerializedObject(component);
            so.FindProperty("content").managedReferenceValue = feature;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Set(object target, string field, object value)
        {
            var f = target.GetType().GetField(field);
            if (f != null) f.SetValue(target, value);
        }

        private static void RemoveEditorOnly(Transform root)
        {
            var doomed = new List<GameObject>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && t.CompareTag("EditorOnly")) doomed.Add(t.gameObject);
            foreach (GameObject go in doomed)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }

        // Quest avatars can't have cloth, lights, cameras, audio, rigidbodies
        // or scripts other than VRChat's and VRCFury's
        private static void StripForQuest(Transform root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            foreach (Type type in new[] { typeof(Cloth), typeof(Joint), typeof(Rigidbody), typeof(Collider), typeof(Light), typeof(Camera), typeof(AudioSource) })
                foreach (Component c in root.GetComponentsInChildren(type, true))
                    if (c != null) UnityEngine.Object.DestroyImmediate(c);
            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string ns = mb.GetType().Namespace ?? "";
                if (!ns.StartsWith("VRC", StringComparison.Ordinal) && !ns.StartsWith("VF", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(mb);
            }
        }

        // -------------------------------------------------------------------
        // the fused mesh
        // -------------------------------------------------------------------

        private static void CopySettings(Renderer from, SkinnedMeshRenderer to)
        {
            if (from == null) return;
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage;
            to.reflectionProbeUsage = from.reflectionProbeUsage;
            to.probeAnchor = from.probeAnchor;
            to.allowOcclusionWhenDynamic = from.allowOcclusionWhenDynamic;
            var smr = from as SkinnedMeshRenderer;
            if (smr != null)
            {
                to.quality = smr.quality;
                to.skinnedMotionVectors = smr.skinnedMotionVectors;
            }
        }

        // Blendshape links that kept the fused pieces in shape now keep the
        // fused mesh in shape; links with the same settings share one entry
        // for it. A link left with nothing to keep in shape, like a dropped
        // piece's, goes.
        private static void RetargetLinks(Transform root, HashSet<Renderer> fusedFrom, HashSet<Renderer> removed, SkinnedMeshRenderer fused)
        {
            var claimed = new HashSet<string>();
            var doomed = new List<Component>();
            foreach (VfFeature f in VrcFuryData.Features(root.gameObject))
            {
                if (f.Type != "BlendShapeLink") continue;
                SerializedProperty link = f.Prop;
                bool hadFused = false;
                bool changed = false;
                var keep = new List<SkinnedMeshRenderer>();
                var skins = link.FindPropertyRelative("linkSkins");
                if (skins != null && skins.isArray)
                {
                    for (int i = 0; i < skins.arraySize; i++)
                    {
                        var r = skins.GetArrayElementAtIndex(i).FindPropertyRelative("renderer");
                        var smr = r != null ? r.objectReferenceValue as SkinnedMeshRenderer : null;
                        if (smr != null && fusedFrom.Contains(smr)) hadFused = true;
                        else if (smr != null && !removed.Contains(smr)) keep.Add(smr);
                        if (smr == null || removed.Contains(smr)) changed = true;
                    }
                }
                int objsLeft = 0;
                var objs = link.FindPropertyRelative("objs");
                if (objs != null && objs.isArray)
                {
                    for (int i = objs.arraySize - 1; i >= 0; i--)
                    {
                        var go = objs.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                        var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
                        if (smr != null && !removed.Contains(smr))
                        {
                            objsLeft++;
                            continue;
                        }
                        if (smr != null && fusedFrom.Contains(smr)) hadFused = true;
                        objs.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        objs.DeleteArrayElementAtIndex(i);
                        changed = true;
                    }
                }
                if (hadFused && claimed.Add(Signature(link))) keep.Add(fused);
                if (keep.Count == 0 && objsLeft == 0)
                {
                    doomed.Add(f.Component);
                    continue;
                }
                if (!changed) continue;
                if (skins != null && skins.isArray)
                {
                    skins.arraySize = keep.Count;
                    for (int i = 0; i < keep.Count; i++)
                        skins.GetArrayElementAtIndex(i).FindPropertyRelative("renderer").objectReferenceValue = keep[i];
                }
                link.serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (Component c in doomed)
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        // Everything about a blendshape link except the meshes it holds
        private static string Signature(SerializedProperty link)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(VrcFuryData.Text(link, "baseObj")).Append('|')
                .Append(VrcFuryData.Bool(link, "includeAll", true)).Append('|')
                .Append(VrcFuryData.Bool(link, "exactMatch")).Append('|');
            foreach (string list in new[] { "excludes", "includes" })
            {
                var p = link.FindPropertyRelative(list);
                if (p == null || !p.isArray) continue;
                sb.Append(list).Append(':');
                for (int i = 0; i < p.arraySize; i++)
                {
                    SerializedProperty e = p.GetArrayElementAtIndex(i);
                    SerializedProperty it = e.Copy();
                    SerializedProperty end = e.GetEndProperty();
                    while (it.NextVisible(true) && !SerializedProperty.EqualContents(it, end))
                        if (it.propertyType == SerializedPropertyType.String) sb.Append(it.stringValue).Append(',');
                    sb.Append(';');
                }
            }
            return sb.ToString();
        }

        private static void RemoveRenderers(Transform root, HashSet<Renderer> renderers, Transform[] bones)
        {
            var used = new HashSet<Transform>(bones);
            foreach (Renderer r in renderers)
            {
                if (r == null) continue;
                GameObject go = r.gameObject;
                MeshFilter filter = r is MeshRenderer ? go.GetComponent<MeshFilter>() : null;
                UnityEngine.Object.DestroyImmediate(r);
                if (filter != null) UnityEngine.Object.DestroyImmediate(filter);
                // an object that only held the mesh goes too
                if (go.transform != root && go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1 && !used.Contains(go.transform))
                    UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // -------------------------------------------------------------------
        // Quest physbones
        // -------------------------------------------------------------------

        // Every physbone of the original, with the piece it moves.
        private static List<CopyPhysBone> PhysBonesOf(Outfit o)
        {
            var groups = new Dictionary<Component, OutfitFeature>();
            foreach (OutfitFeature f in OutfitFeatures.Find(o))
                if (f.Kind == FeatureKind.PhysBone && f.Live != null) groups[f.Live] = f;
            var list = new List<CopyPhysBone>();
            foreach (var pair in PhysBoneIds(o.Root.transform, false))
            {
                OutfitFeature f;
                groups.TryGetValue(pair.Key, out f);
                list.Add(new CopyPhysBone
                {
                    Id = pair.Value,
                    Name = f != null ? f.Name : pair.Key.name,
                    Group = f != null ? f.Group : "",
                });
            }
            return list;
        }

        // ids that stay the same on the original, the copy and its prefab, live or parked:
        // where the physbone sits and which bone it starts from
        private static List<KeyValuePair<Component, string>> PhysBoneIds(Transform root, bool withParked)
        {
            var result = new List<KeyValuePair<Component, string>>();
            var seen = new Dictionary<string, int>();
            Action<Component, Transform> add = (c, host) =>
            {
                var rootProp = new SerializedObject(c).FindProperty("rootTransform");
                var start = rootProp != null ? rootProp.objectReferenceValue as Transform : null;
                string id = Rel(host, root) + "|" + (start != null && start.IsChildOf(root) ? Rel(start, root) : "");
                int n;
                seen.TryGetValue(id, out n);
                seen[id] = n + 1;
                result.Add(new KeyValuePair<Component, string>(c, n == 0 ? id : id + "#" + n));
            };
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == AssistantScope.PhysBoneType && !AssistantScope.InsideEditorOnly(c.transform)) add(c, c.transform);
            if (!withParked) return result;
            Transform holder = root.Find(OutfitPieces.HolderName);
            if (holder == null) return result;
            foreach (Transform record in holder)
            {
                if (!record.name.StartsWith(OutfitFeatures.FeaturePrefix, StringComparison.Ordinal)) continue;
                string path = record.name.Substring(OutfitFeatures.FeaturePrefix.Length);
                Transform host = path.Length == 0 ? root : root.Find(path);
                foreach (Component c in record.GetComponents<Component>())
                    if (c != null && c.GetType().FullName == AssistantScope.PhysBoneType) add(c, host != null ? host : record);
            }
            return result;
        }

        private static void ParkPhysBones(Transform root, List<string> keep)
        {
            foreach (var pair in PhysBoneIds(root, false))
                if (keep == null || !keep.Contains(pair.Value)) Park(root, pair.Key);
        }

        // moves a component into the hidden EditorOnly holder, the same way the
        // Features tab parks them, so it never uploads but can come back
        private static void Park(Transform root, Component c)
        {
            Transform holder = root.Find(OutfitPieces.HolderName);
            if (holder == null)
            {
                var h = new GameObject(OutfitPieces.HolderName) { tag = "EditorOnly" };
                h.SetActive(false);
                h.transform.SetParent(root, false);
                holder = h.transform;
            }
            var record = new GameObject(OutfitFeatures.FeaturePrefix + Rel(c.transform, root));
            record.transform.SetParent(holder, false);
            ComponentUtility.CopyComponent(c);
            ComponentUtility.PasteComponentAsNew(record);
            UnityEngine.Object.DestroyImmediate(c);
        }

        private static void Unpark(Transform root, Component parked)
        {
            Transform record = parked.transform;
            string path = record.name.Substring(OutfitFeatures.FeaturePrefix.Length);
            Transform host = path.Length == 0 ? root : root.Find(path);
            if (host == null) return;
            ComponentUtility.CopyComponent(parked);
            ComponentUtility.PasteComponentAsNew(host.gameObject);
            UnityEngine.Object.DestroyImmediate(parked);
            Transform holder = record.parent;
            if (record.GetComponents<Component>().Length == 1) UnityEngine.Object.DestroyImmediate(record.gameObject);
            if (holder != null && holder.childCount == 0) UnityEngine.Object.DestroyImmediate(holder.gameObject);
        }

        // keeps or drops one physbone on the Quest version's prefab
        public static void SetPhysBone(Outfit copy, string id, bool on)
        {
            if (copy == null || copy.Copy == null || copy.Copy.Kind != CopyKind.Quest) return;
            string path = copy.PrefabPath;
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (var pair in PhysBoneIds(root.transform, true))
                {
                    if (pair.Value != id) continue;
                    bool live = !AssistantScope.InsideEditorOnly(pair.Key.transform);
                    if (on && !live) Unpark(root.transform, pair.Key);
                    else if (!on && live) Park(root.transform, pair.Key);
                    break;
                }
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            CopyRecord record = RecordAt(path);
            if (record == null) return;
            record.Kept.Remove(id);
            if (on) record.Kept.Add(id);
            WriteRecord(path, record);
        }

        private static Mesh MeshAt(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return path.Length > 0 ? AssetDatabase.LoadAssetAtPath<Mesh>(path) : null;
        }

        // what the Quest version weighs now.
        public static QuestInfo Quest(Outfit copy)
        {
            var info = new QuestInfo();
            if (copy == null || copy.Copy == null) return info;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(copy.PrefabPath);
            if (asset != null)
                foreach (var pair in PhysBoneIds(asset.transform, false)) info.On.Add(pair.Value);
            Mesh working = MeshAt(copy.Copy.Mesh);
            Mesh full = MeshAt(copy.Copy.FullMesh);
            info.Triangles = working != null ? OutfitScanner.Triangles(working) : 0;
            info.FullTriangles = full != null ? OutfitScanner.Triangles(full) : info.Triangles;

            var textures = new HashSet<Texture>();
            foreach (Renderer r in copy.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (AssistantScope.InsideEditorOnly(r.transform)) continue;
                if (r is SkinnedMeshRenderer) info.SkinnedMeshes++;
                foreach (Material m in r.sharedMaterials)
                {
                    info.MaterialSlots++;
                    foreach (Texture t in TextureUse.Of(m)) textures.Add(t);
                }
            }
            foreach (Texture t in textures)
            {
                // ASTC 6x6, as the Quest version is imported for Android: 16 bytes
                // per 6x6 block, a third more for mip maps
                long blocks = (long)((t.width + 5) / 6) * ((t.height + 5) / 6);
                info.TextureBytes += blocks * 16 * 4 / 3;
            }
            foreach (Component c in copy.Root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || AssistantScope.InsideEditorOnly(c.transform)) continue;
                string type = c.GetType().FullName;
                if (type == "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider") info.Colliders++;
                else if (type == "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver" || type == "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender") info.Contacts++;
            }
            return info;
        }

        private static void RevertIfOverridden(GameObject go, string property)
        {
            var prop = new SerializedObject(go).FindProperty(property);
            if (prop != null && prop.prefabOverride) PrefabUtility.RevertPropertyOverride(prop, InteractionMode.UserAction);
        }
    }
}
#endif
