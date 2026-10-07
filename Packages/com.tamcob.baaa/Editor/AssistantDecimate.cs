#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BAAA
{
    // Triangles before and after the decimator, per mesh.
    internal sealed class DecimateInfo
    {
        // counted per renderer, like VRChat counts them
        public long Triangles;
        public long FullTriangles;
        // the slider's low end, and where it sits
        public long Floor;
        public long Target;
        // the rest of the avatar, -1 when the copy isn't on one
        public long Others = -1;
        public readonly List<string> Names = new List<string>();
        public readonly List<int> Full = new List<int>();
        public readonly List<int> Now = new List<int>();
        public readonly List<MeshMode> Modes = new List<MeshMode>();
        public readonly List<float> Keeps = new List<float>();
        public readonly List<bool> Small = new List<bool>();
        // Made before the decimator kept a full-detail mesh, so it cant be reduced
        public bool Missing;
        public bool Rebaked;
    }

    // the decimator side of the copies: the slider, each mesh's setting, and baking the textures
    // again for Extreme and Quest versions
    internal static partial class OutfitCopies
    {
        private const string RebakedSuffix = " rebaked";

        private sealed class Reducible
        {
            public string Name;
            public Mesh Full;
            public Mesh Own;
            public CopyMesh Record;
            public int Uses = 1;
            // how big one unit of the mesh is in the scene
            public float Scale = 1f;
        }

        // the copy's meshes the decimator works on, next to the full-detail ones they come from
        private static List<Reducible> ReduciblesOf(CopyRecord record, GameObject root)
        {
            var list = new List<Reducible>();
            if (record.Kind == CopyKind.Decimated)
            {
                foreach (CopyMesh m in record.Meshes)
                    list.Add(new Reducible { Name = m.Name, Full = MeshById(m.Source), Own = MeshAt(m.Mesh), Record = m });
            }
            else
            {
                Mesh own = MeshAt(record.Mesh);
                list.Add(new Reducible { Name = own != null ? own.name : Lang.T("Fused mesh"), Full = MeshAt(record.FullMesh), Own = own });
            }
            if (root == null) return list;
            var uses = new Dictionary<Mesh, int>();
            var scales = new Dictionary<Mesh, float>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (AssistantScope.InsideEditorOnly(r.transform)) continue;
                Mesh m = MeshOf(r);
                if (m == null) continue;
                int n;
                uses.TryGetValue(m, out n);
                uses[m] = n + 1;
                if (!scales.ContainsKey(m)) scales[m] = SceneScale(r, m);
            }
            foreach (Reducible r in list)
            {
                int n;
                float s;
                if (r.Own != null && uses.TryGetValue(r.Own, out n)) r.Uses = n;
                if (r.Own != null && scales.TryGetValue(r.Own, out s)) r.Scale = s;
            }
            return list;
        }

        // a skinned mesh goes through its bones, a 1/100 import keeps its scale on the armature.
        // works on hidden pieces too, unlike the renderer's bounds
        private static float SceneScale(Renderer r, Mesh mesh)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null)
            {
                Transform[] bones = smr.bones;
                Matrix4x4[] binds = mesh.bindposes;
                for (int b = 0; b < bones.Length && b < binds.Length; b++)
                {
                    if (bones[b] == null) continue;
                    Vector3 s = (bones[b].localToWorldMatrix * binds[b]).lossyScale;
                    float f = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
                    if (f > 1e-9f && !float.IsNaN(f) && !float.IsInfinity(f)) return f;
                }
            }
            Vector3 l = r.transform.lossyScale;
            float plain = (Mathf.Abs(l.x) + Mathf.Abs(l.y) + Mathf.Abs(l.z)) / 3f;
            return plain > 1e-9f ? plain : 1f;
        }

        // measure: the error curves the split needs, a full run of the engine per mesh the first time
        private static List<BudgetMesh> BudgetOf(List<Reducible> list, bool measure)
        {
            var budget = new List<BudgetMesh>();
            foreach (Reducible r in list)
            {
                int full = r.Full != null ? OutfitScanner.Triangles(r.Full) : 0;
                var b = new BudgetMesh { Full = full, Uses = r.Uses };
                if (r.Record != null)
                {
                    if (full < MeshReducer.Small || r.Record.Mode == MeshMode.Full) b.Fixed = full;
                    else if (r.Record.Mode == MeshMode.Own) b.Fixed = Mathf.Clamp(Mathf.RoundToInt(full * r.Record.Keep), 1, full);
                }
                budget.Add(b);
            }
            if (!measure || budget.FindAll(b => b.Fixed < 0).Count < 2) return budget;
            for (int i = 0; i < list.Count; i++)
            {
                if (budget[i].Fixed >= 0) continue;
                Progress(Lang.T("Measuring {0}...", list[i].Name), (float)i / list.Count);
                float[] curve = MeshReducer.Curve(list[i].Full);
                if (curve == null) continue;
                // the engine measures every mesh at one unit across. at its size in the scene squared,
                // a millimeter off costs the same on a boot as on a jacket
                float size = list[i].Full.bounds.size.magnitude * list[i].Scale;
                float weight = size * size;
                var scaled = new float[curve.Length];
                for (int t = 0; t < curve.Length; t++) scaled[t] = curve[t] * weight;
                budget[i].Curve = scaled;
            }
            return budget;
        }

        // the decimator: the copy's meshes get rebuilt from full detail so the outfit has about
        // this many triangles. 0, or the full count, puts every triangle back
        public static string SetTarget(Outfit copy, long target)
        {
            if (copy == null || copy.Copy == null) return Lang.T("Make a decimated version first.");
            return SetTarget(copy.PrefabPath, copy.Root, target);
        }

        private static string SetTarget(string prefabPath, GameObject root, long target)
        {
            CopyRecord record = RecordAt(prefabPath);
            if (record == null) return Lang.T("Make a decimated version first.");
            List<Reducible> list = ReduciblesOf(record, root);
            if (list.Count == 0 || list.Exists(r => r.Full == null || r.Own == null))
                return Lang.T("This copy was made before the decimator kept its full-detail meshes. Make it again first.");

            long full = 0;
            long now = 0;
            // what was asked for gets saved, the floor only applies to the cut. a mesh set to keep
            // all raises the floor, and back on auto the slider should be where it was
            long wanted = target;
            try
            {
                List<BudgetMesh> budget = BudgetOf(list, true);
                full = TriangleBudget.Full(budget);
                wanted = target <= 0 ? full : Math.Min(target, full);
                target = Math.Max(wanted, TriangleBudget.Floor(budget));
                int[] counts = TriangleBudget.Split(budget, target);
                var got = new int[list.Count];
                for (int round = 0; round < 2; round++)
                {
                    now = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (round == 0 || budget[i].Fixed < 0)
                        {
                            Progress(Lang.T("Reducing {0}...", list[i].Name), (float)i / list.Count);
                            Mesh result = MeshReducer.Reduce(list[i].Full, counts[i]);
                            Overwrite(list[i].Own, result);
                            UnityEngine.Object.DestroyImmediate(result);
                            EditorUtility.SetDirty(list[i].Own);
                            got[i] = OutfitScanner.Triangles(list[i].Own);
                        }
                        now += (long)list[i].Uses * got[i];
                    }
                    // a mesh that couldn't get as low as asked (spots locked against spikes) leaves the
                    // outfit over, so the meshes that follow the slider make up for it, once
                    long over = now - target;
                    long auto = 0;
                    for (int i = 0; i < list.Count; i++) if (budget[i].Fixed < 0) auto += (long)list[i].Uses * got[i];
                    if (round > 0 || over <= 0 || auto <= over) break;
                    double scale = (double)(auto - over) / auto;
                    for (int i = 0; i < list.Count; i++)
                        if (budget[i].Fixed < 0) counts[i] = Math.Max(1, (int)Math.Floor(got[i] * scale));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            AssetDatabase.SaveAssets();
            record.Target = wanted >= full ? 0 : (int)wanted;
            record.Keep = full > 0 ? (float)now / full : 1f;
            // baked for another mesh, they'd show in the wrong places now
            if (record.Rebaked) Unbake(prefabPath, root, record);
            WriteRecord(prefabPath, record);
            return null;
        }

        // the same with a share of every triangle, like the old slider
        public static string SetKeep(Outfit copy, float keep)
        {
            if (copy == null || copy.Copy == null) return Lang.T("Make a decimated version first.");
            return SetKeep(copy.PrefabPath, copy.Root, keep);
        }

        private static string SetKeep(string prefabPath, GameObject root, float keep)
        {
            CopyRecord record = RecordAt(prefabPath);
            if (record == null) return Lang.T("Make a decimated version first.");
            long full = 0;
            foreach (Reducible r in ReduciblesOf(record, root))
                if (r.Full != null) full += (long)r.Uses * OutfitScanner.Triangles(r.Full);
            return SetTarget(prefabPath, root, keep >= 0.999f ? 0 : (long)Math.Round(full * Mathf.Clamp(keep, 0.05f, 1f)));
        }

        // one mesh of a decimated copy: follows the slider, stays at full detail, or keeps its own share
        public static string SetMeshMode(Outfit copy, int index, MeshMode mode, float keep)
        {
            if (copy == null || copy.Copy == null || copy.Copy.Kind != CopyKind.Decimated) return Lang.T("Make a decimated version first.");
            CopyRecord record = RecordAt(copy.PrefabPath);
            if (record == null || index < 0 || index >= record.Meshes.Count) return Lang.T("Make a decimated version first.");
            record.Meshes[index].Mode = mode;
            record.Meshes[index].Keep = Mathf.Clamp(keep, 0.05f, 1f);
            WriteRecord(copy.PrefabPath, record);
            return SetTarget(copy.PrefabPath, copy.Root, record.Target);
        }

        // triangles now and at full detail, per mesh
        public static DecimateInfo Decimation(Outfit copy)
        {
            var info = new DecimateInfo();
            if (copy == null || copy.Copy == null) return info;
            CopyRecord r = copy.Copy;
            info.Rebaked = r.Rebaked;
            List<Reducible> list = ReduciblesOf(r, copy.Root);
            foreach (Reducible m in list)
            {
                if (m.Full == null || m.Own == null)
                {
                    info.Missing = true;
                    if (m.Own == null) continue;
                }
                int now = OutfitScanner.Triangles(m.Own);
                int full = m.Full != null ? OutfitScanner.Triangles(m.Full) : now;
                info.Names.Add(m.Name);
                info.Full.Add(full);
                info.Now.Add(now);
                info.Modes.Add(m.Record != null ? m.Record.Mode : MeshMode.Auto);
                info.Keeps.Add(m.Record != null ? m.Record.Keep : 1f);
                info.Small.Add(m.Record != null && full < MeshReducer.Small);
                info.Triangles += (long)m.Uses * now;
                info.FullTriangles += (long)m.Uses * full;
            }
            info.Floor = info.Missing ? info.Triangles : TriangleBudget.Floor(BudgetOf(list, false));
            info.Target = r.Target > 0 ? Math.Min(r.Target, info.FullTriangles) : r.Keep < 0.999f ? info.Triangles : info.FullTriangles;
            if (copy.Avatar != null && copy.Root != null)
                info.Others = Ranks.Measure(copy.Avatar, null, false).Triangles - Ranks.Measure(copy.Root, null, false).Triangles;
            return info;
        }

        // a share of one mesh, for the bare project's test
        internal static Mesh Reduce(Mesh full, float keep)
        {
            return MeshReducer.Reduce(full, Mathf.RoundToInt(OutfitScanner.Triangles(full) * keep));
        }

        // Replaces a meshs data through Unity's mesh API, then reconnects every
        // renderer that shows it. Copying the saved data over it instead leaves
        // skinned renderers on the old layout, and Unity stops drawing them
        internal static void Overwrite(Mesh target, Mesh source)
        {
            string name = target.name;
            target.Clear();
            target.indexFormat = source.indexFormat;
            target.vertices = source.vertices;
            if (source.HasVertexAttribute(VertexAttribute.Normal)) target.normals = source.normals;
            if (source.HasVertexAttribute(VertexAttribute.Tangent)) target.tangents = source.tangents;
            if (source.HasVertexAttribute(VertexAttribute.Color)) target.colors32 = source.colors32;
            var uvs = new List<Vector4>();
            for (int k = 0; k < 8; k++)
            {
                var attr = VertexAttribute.TexCoord0 + k;
                if (!source.HasVertexAttribute(attr)) continue;
                uvs.Clear();
                source.GetUVs(k, uvs);
                int dim = source.GetVertexAttributeDimension(attr);
                if (dim <= 2) target.SetUVs(k, uvs.ConvertAll(u => new Vector2(u.x, u.y)));
                else if (dim == 3) target.SetUVs(k, uvs.ConvertAll(u => new Vector3(u.x, u.y, u.z)));
                else target.SetUVs(k, uvs);
            }
            target.bindposes = source.bindposes;
            // the full bone list, so meshes with one bone per vertex (no weight channel at all, like the
            // watch and the katana) and ones with more than four keep theirs. going by the weight channel
            // skipped the one-bone ones and left the old bones behind, every vertex on the wrong one
            var perVertex = source.GetBonesPerVertex();
            if (perVertex.Length > 0 && perVertex.Length == source.vertexCount) target.SetBoneWeights(perVertex, source.GetAllBoneWeights());
            else if (target.HasVertexAttribute(VertexAttribute.BlendIndices)) target.boneWeights = new BoneWeight[0];
            target.subMeshCount = source.subMeshCount;
            for (int i = 0; i < source.subMeshCount; i++) target.SetTriangles(source.GetTriangles(i), i, false);
            int n = source.vertexCount;
            var dv = new Vector3[n];
            var dn = new Vector3[n];
            var dt = new Vector3[n];
            for (int s = 0; s < source.blendShapeCount; s++)
            {
                string shape = source.GetBlendShapeName(s);
                for (int f = 0; f < source.GetBlendShapeFrameCount(s); f++)
                {
                    source.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    target.AddBlendShapeFrame(shape, source.GetBlendShapeFrameWeight(s, f), dv, dn, dt);
                }
            }
            target.RecalculateBounds();
            target.name = name;
            Rebind(target);
        }

        private static void Rebind(Mesh mesh)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject go in scene.GetRootGameObjects())
                {
                    foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr.sharedMesh != mesh) continue;
                        smr.sharedMesh = null;
                        smr.sharedMesh = mesh;
                    }
                    foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (filter.sharedMesh != mesh) continue;
                        filter.sharedMesh = null;
                        filter.sharedMesh = mesh;
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // baking the textures again
        // -------------------------------------------------------------------

        // Extreme and Quest: what the full-detail mesh shows gets baked onto the reduced one, into
        // textures next to the copy's own. textures from the pack (materials that kept their own) stay
        public static string Rebake(Outfit copy)
        {
            if (copy == null || copy.Copy == null) return Lang.T("Make a decimated version first.");
            return Rebake(copy.PrefabPath, copy.Root);
        }

        private static string Rebake(string prefabPath, GameObject root)
        {
            CopyRecord record = RecordAt(prefabPath);
            if (record == null || (record.Kind != CopyKind.Extreme && record.Kind != CopyKind.Quest))
                return Lang.T("Only Extreme and Quest versions have textures of their own to bake.");
            Mesh full = MeshAt(record.FullMesh);
            Mesh now = MeshAt(record.Mesh);
            if (full == null || now == null) return Lang.T("This copy was made before the decimator kept its full-detail meshes. Make it again first.");
            if (OutfitScanner.Triangles(now) >= OutfitScanner.Triangles(full)) return Lang.T("Cut some triangles first.");
            Renderer shown = null;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                if (MeshOf(r) == now) shown = r;
            if (shown == null) return Lang.T("The fused mesh isn't in this copy anymore. Make it again first.");

            string folderPath = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var folder = new GeneratedFolder(folderPath);
            Material[] mats = shown.sharedMaterials;
            // each of the copy's own textures, where it's used, and on which parts of the mesh
            var users = new Dictionary<Texture2D, List<KeyValuePair<Material, string>>>();
            var parts = new Dictionary<Texture2D, List<int>>();
            for (int s = 0; s < mats.Length && s < now.subMeshCount && s < full.subMeshCount; s++)
            {
                Material m = mats[s];
                if (m == null || !InFolder(AssetDatabase.GetAssetPath(m), folderPath)) continue;
                foreach (string p in m.GetTexturePropertyNames())
                {
                    Texture2D plain = PlainTexture(m.GetTexture(p) as Texture2D, folderPath);
                    if (plain == null) continue;
                    if (!users.ContainsKey(plain))
                    {
                        users[plain] = new List<KeyValuePair<Material, string>>();
                        parts[plain] = new List<int>();
                    }
                    users[plain].Add(new KeyValuePair<Material, string>(m, p));
                    if (!parts[plain].Contains(s)) parts[plain].Add(s);
                }
            }
            if (users.Count == 0) return Lang.T("This version has no textures of its own to bake.");

            var maps = new Dictionary<string, AtlasBuilder.RebakeMap>();
            int done = 0;
            try
            {
                foreach (var pair in users)
                {
                    Progress(Lang.T("Baking {0}...", pair.Key.name), (float)done++ / users.Count);
                    string key = string.Join(",", parts[pair.Key]);
                    AtlasBuilder.RebakeMap map;
                    if (!maps.TryGetValue(key, out map)) maps[key] = map = AtlasBuilder.MapForRebake(full, now, parts[pair.Key]);
                    Texture2D baked = RebakeTexture(map, pair.Key, folder);
                    if (baked == null) continue;
                    foreach (var use in pair.Value)
                    {
                        use.Key.SetTexture(use.Value, baked);
                        EditorUtility.SetDirty(use.Key);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            AssetDatabase.SaveAssets();
            record.Rebaked = true;
            WriteRecord(prefabPath, record);
            return null;
        }

        // back to the plain textures, and the baked ones get deleted
        public static string UsePlainTextures(Outfit copy)
        {
            if (copy == null || copy.Copy == null) return Lang.T("Make a decimated version first.");
            CopyRecord record = RecordAt(copy.PrefabPath);
            if (record == null) return Lang.T("Make a decimated version first.");
            Unbake(copy.PrefabPath, copy.Root, record);
            WriteRecord(copy.PrefabPath, record);
            return null;
        }

        private static void Unbake(string prefabPath, GameObject root, CopyRecord record)
        {
            string folderPath = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var baked = new HashSet<string>();
            if (root != null)
            {
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null || !InFolder(AssetDatabase.GetAssetPath(m), folderPath)) continue;
                        foreach (string p in m.GetTexturePropertyNames())
                        {
                            var t = m.GetTexture(p) as Texture2D;
                            string path = t != null ? AssetDatabase.GetAssetPath(t) : "";
                            if (!path.EndsWith(RebakedSuffix + ".png", StringComparison.Ordinal)) continue;
                            m.SetTexture(p, PlainTexture(t, folderPath));
                            EditorUtility.SetDirty(m);
                        }
                    }
                }
            }
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folderPath }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (InFolder(path, folderPath) && path.EndsWith(RebakedSuffix + ".png", StringComparison.Ordinal)) baked.Add(path);
            }
            AssetDatabase.SaveAssets();
            foreach (string path in baked) AssetDatabase.DeleteAsset(path);
            record.Rebaked = false;
        }

        // the copy's own texture behind tex, baked again or not. null for the pack's textures
        private static Texture2D PlainTexture(Texture2D tex, string folderPath)
        {
            if (tex == null) return null;
            string path = AssetDatabase.GetAssetPath(tex);
            if (!InFolder(path, folderPath) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            if (!path.EndsWith(RebakedSuffix + ".png", StringComparison.Ordinal)) return tex;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path.Substring(0, path.Length - RebakedSuffix.Length - 4) + ".png");
        }

        private static bool InFolder(string path, string folderPath)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(folderPath + "/", StringComparison.Ordinal)
                && Path.GetDirectoryName(path).Replace('\\', '/') == folderPath;
        }

        // reads the png itself, so it doesn't matter how unity imported it
        private static Texture2D RebakeTexture(AtlasBuilder.RebakeMap map, Texture2D plain, GeneratedFolder folder)
        {
            string path = AssetDatabase.GetAssetPath(plain);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            bool linear = importer != null && (!importer.sRGBTexture || importer.textureType == TextureImporterType.NormalMap);
            var read = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
            try
            {
                if (!read.LoadImage(File.ReadAllBytes(FullPath(path))) || read.width != read.height) return null;
                Color32[] pixels = AtlasBuilder.Rebake(map, read.GetPixels32(), read.width);
                string file = Path.GetFileNameWithoutExtension(path) + RebakedSuffix + ".png";
                return folder.SavePng(pixels, read.width, linear, file, to => CopyImport(importer, to));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(read);
            }
        }

        private static void CopyImport(TextureImporter from, TextureImporter to)
        {
            if (from == null) return;
            var settings = new TextureImporterSettings();
            from.ReadTextureSettings(settings);
            to.SetTextureSettings(settings);
            to.maxTextureSize = from.maxTextureSize;
            to.textureCompression = from.textureCompression;
            to.crunchedCompression = from.crunchedCompression;
            to.compressionQuality = from.compressionQuality;
            foreach (string platform in new[] { "Standalone", "Android", "iPhone" })
            {
                TextureImporterPlatformSettings p = from.GetPlatformTextureSettings(platform);
                if (p != null && p.overridden) to.SetPlatformTextureSettings(p);
            }
        }
    }
}
#endif
