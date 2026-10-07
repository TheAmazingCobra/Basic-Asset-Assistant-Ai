#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    internal sealed class ScannedTexture
    {
        public string Name;
        public int Width;
        public int Height;
        public string Format;
        public long Bytes;
    }

    // Everything the Perf Scanner tab shows for one outfit. Pieces that start
    // switched off are counted too, the same way VRChat counts them
    internal sealed class OutfitScan
    {
        public int Triangles;
        public int SkinnedMeshes;
        public int StaticMeshes;
        public int MaterialSlots;
        public int Blendshapes;
        public int MeshesWithBlendshapes;
        public string MostBlendshapesMesh = "";
        public int MostBlendshapes;

        public int Bones;
        public bool LinkedToAvatar;
        public int BonesAdded;
        public int BonesMerged;
        public int PhysBones;
        public int PhysBoneTransforms;

        public long TextureBytes;
        public long MeshBytes;
        public readonly List<ScannedTexture> Textures = new List<ScannedTexture>();
        public readonly List<KeyValuePair<string, int>> Shaders = new List<KeyValuePair<string, int>>();

        public bool MenuRead;
        public int Toggles;
        public int OtherSliders;
        public readonly List<string> ColorChangers = new List<string>();
        public readonly List<string> Parts = new List<string>();
        public readonly List<string> Extras = new List<string>();
    }

    internal sealed class BoneMatch
    {
        public int Body;
        public int Matched;
    }

    internal static class OutfitScanner
    {
        private const string ContactReceiverType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";
        private const string ContactSenderType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender";
        private const string VrcConstraintNamespace = "VRC.SDK3.Dynamics.Constraint.Components";

        public static OutfitScan Scan(Outfit outfit)
        {
            var scan = new OutfitScan();
            if (outfit == null || outfit.Root == null) return scan;
            GameObject root = outfit.Root;

            ScanMeshes(root, scan);
            ScanMaterials(root, scan);
            ScanBones(outfit, scan);
            ScanMenu(root, scan);
            ScanExtras(root, scan);
            return scan;
        }

        // -------------------------------------------------------------------
        // Meshes, triangles, blendshapes and their memory
        // -------------------------------------------------------------------

        private static void ScanMeshes(GameObject root, OutfitScan scan)
        {
            var counted = new HashSet<Mesh>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (AssistantScope.InsideEditorOnly(r.transform)) continue;
                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null)
                {
                    scan.SkinnedMeshes++;
                    mesh = smr.sharedMesh;
                }
                else if (r is MeshRenderer)
                {
                    scan.StaticMeshes++;
                    var filter = r.GetComponent<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                }
                else continue;

                scan.MaterialSlots += r.sharedMaterials.Length;
                if (mesh == null) continue;
                scan.Triangles += Triangles(mesh);

                if (mesh.blendShapeCount > 0)
                {
                    scan.Blendshapes += mesh.blendShapeCount;
                    scan.MeshesWithBlendshapes++;
                    if (mesh.blendShapeCount > scan.MostBlendshapes)
                    {
                        scan.MostBlendshapes = mesh.blendShapeCount;
                        scan.MostBlendshapesMesh = r.name;
                    }
                }

                // Each skinned renderer also gets its own buffer for the skinned result:
                // position, normal and tangent per vertex
                if (smr != null) scan.MeshBytes += mesh.vertexCount * 40L;
                if (counted.Add(mesh)) scan.MeshBytes += MeshBytes(mesh);
            }
        }

        internal static int Triangles(Mesh mesh)
        {
            long tris = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                MeshTopology topology = mesh.GetTopology(i);
                long indices = mesh.GetIndexCount(i);
                if (topology == MeshTopology.Triangles) tris += indices / 3;
                else if (topology == MeshTopology.Quads) tris += indices / 4 * 2;
            }
            return (int)tris;
        }

        // Vertex and index buffers, plus blendshape deltas, which the graphics
        // card keeps only for the vertices each shape moves
        private static long MeshBytes(Mesh mesh)
        {
            long bytes = 0;
            for (int s = 0; s < mesh.vertexBufferCount; s++)
                bytes += (long)mesh.GetVertexBufferStride(s) * mesh.vertexCount;
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += (long)mesh.GetIndexCount(i);
            bytes += indices * (mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16 ? 2 : 4);

            if (mesh.blendShapeCount == 0) return bytes;
            var dv = new Vector3[mesh.vertexCount];
            var dn = new Vector3[mesh.vertexCount];
            var dt = new Vector3[mesh.vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                    long moved = 0;
                    for (int v = 0; v < dv.Length; v++)
                        if (dv[v] != Vector3.zero || dn[v] != Vector3.zero || dt[v] != Vector3.zero) moved++;
                    bytes += moved * 40L;
                }
            }
            return bytes;
        }

        // -------------------------------------------------------------------
        // textures and shaders
        // -------------------------------------------------------------------

        private static void ScanMaterials(GameObject root, OutfitScan scan)
        {
            var materials = new HashSet<Material>();
            var textures = new HashSet<Texture>();
            var shaders = new Dictionary<string, int>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (AssistantScope.InsideEditorOnly(r.transform)) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !materials.Add(m)) continue;
                    string shader = ShaderName(m);
                    int n;
                    shaders.TryGetValue(shader, out n);
                    shaders[shader] = n + 1;

                    // only what the material really draws with, a feature thats off doesnt count
                    foreach (Texture tex in TextureUse.Of(m)) textures.Add(tex);
                }
            }

            foreach (var tex in textures)
            {
                long bytes = AssistantScope.GpuBytes(tex);
                scan.TextureBytes += bytes;
                scan.Textures.Add(new ScannedTexture
                {
                    Name = tex.name,
                    Width = tex.width,
                    Height = tex.height,
                    Format = FormatName(tex),
                    Bytes = bytes,
                });
            }
            scan.Textures.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            foreach (var pair in shaders) scan.Shaders.Add(pair);
            scan.Shaders.Sort((a, b) => b.Value.CompareTo(a.Value));
        }

        private static string ShaderName(Material m)
        {
            if (m.shader == null || m.shader.name == "Hidden/InternalErrorShader") return Lang.T("missing shader (shows pink)");
            if (Poiyomi.IsLocked(m))
            {
                string original = m.GetTag("OriginalShader", false, "");
                return Lang.T("{0} (locked)", original.Length > 0 ? original : "Poiyomi");
            }
            return m.shader.name;
        }

        private static string FormatName(Texture tex)
        {
            var t2 = tex as Texture2D;
            if (t2 == null) return tex.dimension.ToString();
            string f = t2.format.ToString();
            if (f.EndsWith("Crunched")) f = f.Substring(0, f.Length - "Crunched".Length) + " crunched";
            if (f == "RGBA32" || f == "RGB24" || f == "ARGB32") f += " (uncompressed)";
            return f.Replace('_', ' ');
        }

        // -------------------------------------------------------------------
        // Bones, before and after VRCFury's Armature Link
        // -------------------------------------------------------------------

        private static void ScanBones(Outfit outfit, OutfitScan scan)
        {
            GameObject root = outfit.Root;
            var used = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (AssistantScope.InsideEditorOnly(smr.transform)) continue;
                foreach (var b in smr.bones)
                    if (b != null) used.Add(b);
            }
            scan.Bones = used.Count;

            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().FullName != AssistantScope.PhysBoneType || AssistantScope.InsideEditorOnly(c.transform)) continue;
                scan.PhysBones++;
                scan.PhysBoneTransforms += Moves(c, null);
            }

            if (outfit.Avatar == null || !AssistantScope.VrcFuryInstalled) return;
            Dictionary<Transform, Transform> merged = MergeMap(outfit.Root, outfit.Avatar);
            if (merged == null) return;
            scan.LinkedToAvatar = true;
            foreach (var b in used)
            {
                if (merged.ContainsKey(b)) scan.BonesMerged++;
                else scan.BonesAdded++;
            }
        }

        // the bones one physbone moves: its root and everything under it, minus the ignored ones.
        // into collects them when its not null
        internal static int Moves(Component physBone, HashSet<Transform> into)
        {
            var so = new SerializedObject(physBone);
            var rootProp = so.FindProperty("rootTransform");
            Transform start = rootProp != null ? rootProp.objectReferenceValue as Transform : null;
            if (start == null) start = physBone.transform;
            var ignore = new HashSet<Transform>();
            var ignoreProp = so.FindProperty("ignoreTransforms");
            if (ignoreProp != null && ignoreProp.isArray)
            {
                for (int i = 0; i < ignoreProp.arraySize; i++)
                {
                    var t = ignoreProp.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                    if (t != null) ignore.Add(t);
                }
            }
            return CountMoved(start, ignore, into);
        }

        private static int CountMoved(Transform t, HashSet<Transform> ignore, HashSet<Transform> into)
        {
            if (ignore.Contains(t)) return 0;
            if (into != null) into.Add(t);
            int n = 1;
            foreach (Transform child in t) n += CountMoved(child, ignore, into);
            return n;
        }

        // outfit bone -> the avatar bone VRCFury's Armature Link merges it into, following its rule:
        // from the linked root down, a child merges when the avatar has a bone of the same name in the
        // same place (ignoring a name suffix like "_Outfit"). Everything else stays and adds to the avatar.
        // null when theres no Armature Link the avatar can take. root can be the avatar itself, then
        // every link on it counts. links gets each link's own root bone
        internal static Dictionary<Transform, Transform> MergeMap(GameObject root, GameObject avatar, List<Transform> links = null)
        {
            Dictionary<Transform, Transform> map = null;
            foreach (VfFeature f in VrcFuryData.Features(root))
            {
                if (f.Type != "ArmatureLink") continue;
                var propBoneProp = f.Prop.FindPropertyRelative("propBone");
                Transform propBone = AsTransform(propBoneProp != null ? propBoneProp.objectReferenceValue : null);
                if (propBone == null) continue;
                Transform skip = root != avatar ? root.transform : f.Component.transform;
                Transform avatarBone = LinkTarget(avatar, skip, f.Prop, propBone);
                if (avatarBone == null) continue;

                if (map == null) map = new Dictionary<Transform, Transform>();
                if (links != null) links.Add(propBone);
                string suffix = VrcFuryData.Text(f.Prop, "removeBoneSuffix");
                if (string.IsNullOrWhiteSpace(suffix) && propBone.name.Contains(avatarBone.name) && propBone.name != avatarBone.name)
                    suffix = propBone.name.Replace(avatarBone.name, "");

                map[propBone] = avatarBone;
                if (!VrcFuryData.Bool(f.Prop, "recursive", true)) continue;
                var stack = new Stack<KeyValuePair<Transform, Transform>>();
                stack.Push(new KeyValuePair<Transform, Transform>(propBone, avatarBone));
                while (stack.Count > 0)
                {
                    var pair = stack.Pop();
                    foreach (Transform child in pair.Key)
                    {
                        string search = string.IsNullOrWhiteSpace(suffix) ? child.name : child.name.Replace(suffix, "");
                        Transform match = pair.Value.Find(search);
                        if (match == null) continue;
                        map[child] = match;
                        stack.Push(new KeyValuePair<Transform, Transform>(child, match));
                    }
                }
            }
            return map;
        }

        // how many of the outfits body bones VRCFury finds on the avatar. on the base its made for
        // thats all of them. bones a physbone moves dont count, skirts and tails are meant to be extra.
        // null when it cant tell
        internal static BoneMatch MatchBones(Outfit outfit)
        {
            if (outfit == null || outfit.Root == null || outfit.Avatar == null || !AssistantScope.VrcFuryInstalled) return null;
            var links = new List<Transform>();
            Dictionary<Transform, Transform> map = MergeMap(outfit.Root, outfit.Avatar, links);
            if (map == null) return null;

            var moved = new HashSet<Transform>();
            foreach (var c in outfit.Root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == AssistantScope.PhysBoneType && !AssistantScope.InsideEditorOnly(c.transform)) Moves(c, moved);

            var match = new BoneMatch();
            var seen = new HashSet<Transform>();
            foreach (var smr in outfit.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (AssistantScope.InsideEditorOnly(smr.transform)) continue;
                foreach (Transform b in smr.bones)
                {
                    if (b == null || !seen.Add(b) || moved.Contains(b) || !UnderLink(b, links)) continue;
                    match.Body++;
                    if (map.ContainsKey(b)) match.Matched++;
                }
            }
            return match;
        }

        private static bool UnderLink(Transform bone, List<Transform> links)
        {
            foreach (Transform l in links)
                if (bone != l && bone.IsChildOf(l)) return true;
            return false;
        }

        private static Transform LinkTarget(GameObject avatar, Transform skip, SerializedProperty link, Transform propBone)
        {
            var linkTo = link.FindPropertyRelative("linkTo");
            var animator = avatar.GetComponent<Animator>();
            if (linkTo != null && linkTo.isArray)
            {
                for (int i = 0; i < linkTo.arraySize; i++)
                {
                    var to = linkTo.GetArrayElementAtIndex(i);
                    if (VrcFuryData.Bool(to, "useBone") && animator != null && animator.isHuman)
                    {
                        var boneProp = to.FindPropertyRelative("bone");
                        if (boneProp != null)
                        {
                            Transform t = animator.GetBoneTransform((HumanBodyBones)boneProp.intValue);
                            if (t != null) return t;
                        }
                    }
                    if (VrcFuryData.Bool(to, "useObj"))
                    {
                        var objProp = to.FindPropertyRelative("obj");
                        Transform t = AsTransform(objProp != null ? objProp.objectReferenceValue : null);
                        if (t != null) return t;
                    }
                }
            }
            // no humanoid rig to ask: use the avatar's bone with the same name.
            foreach (var t in avatar.GetComponentsInChildren<Transform>(true))
                if (t.name == propBone.name && !t.IsChildOf(skip)) return t;
            return null;
        }

        private static Transform AsTransform(Object o)
        {
            var t = o as Transform;
            if (t != null) return t;
            var go = o as GameObject;
            return go != null ? go.transform : null;
        }

        // -------------------------------------------------------------------
        // Menu: toggles, color changers and the parts they switch
        // -------------------------------------------------------------------

        private static void ScanMenu(GameObject root, OutfitScan scan)
        {
            if (!AssistantScope.VrcFuryInstalled) return;
            scan.MenuRead = true;
            foreach (VfFeature f in VrcFuryData.Features(root))
            {
                if (f.Type != "Toggle") continue;
                string name = VrcFuryData.MenuName(VrcFuryData.Text(f.Prop, "name"));
                bool changesMaterial = false;
                bool switchesPart = false;
                foreach (SerializedProperty action in VrcFuryData.Actions(f.Prop.FindPropertyRelative("state")))
                {
                    string type = VrcFuryData.ShortType(action.managedReferenceFullTypename);
                    if (type == "MaterialPropertyAction") changesMaterial = true;
                    if (type != "ObjectToggleAction") continue;
                    var objProp = action.FindPropertyRelative("obj");
                    var obj = objProp != null ? objProp.objectReferenceValue as GameObject : null;
                    if (obj != null && obj.GetComponentInChildren<Renderer>(true) != null) switchesPart = true;
                }

                if (VrcFuryData.Bool(f.Prop, "slider"))
                {
                    if (changesMaterial) scan.ColorChangers.Add(name);
                    else scan.OtherSliders++;
                }
                else
                {
                    scan.Toggles++;
                    if (switchesPart) scan.Parts.Add(PartName(name));
                }
            }
        }

        private static string PartName(string toggleName)
        {
            return toggleName.StartsWith("Toggle ", System.StringComparison.OrdinalIgnoreCase)
                ? toggleName.Substring("Toggle ".Length)
                : toggleName;
        }

        // -------------------------------------------------------------------
        // anything else VRChat counts
        // -------------------------------------------------------------------

        private static void ScanExtras(GameObject root, OutfitScan scan)
        {
            int contacts = 0, constraints = 0, particles = 0, lights = 0, audio = 0, cloth = 0, trails = 0;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || AssistantScope.InsideEditorOnly(c.transform)) continue;
                string type = c.GetType().FullName;
                if (type == ContactReceiverType || type == ContactSenderType) contacts++;
                else if (c is UnityEngine.Animations.IConstraint || (c.GetType().Namespace ?? "") == VrcConstraintNamespace) constraints++;
                else if (c is ParticleSystem) particles++;
                else if (c is Light) lights++;
                else if (c is AudioSource) audio++;
                else if (c is Cloth) cloth++;
                else if (c is TrailRenderer || c is LineRenderer) trails++;
            }
            AddExtra(scan, contacts, Lang.N("{0} contact"), Lang.N("{0} contacts"));
            AddExtra(scan, constraints, Lang.N("{0} constraint"), Lang.N("{0} constraints"));
            AddExtra(scan, particles, Lang.N("{0} particle system"), Lang.N("{0} particle systems"));
            AddExtra(scan, lights, Lang.N("{0} light"), Lang.N("{0} lights"));
            AddExtra(scan, audio, Lang.N("{0} audio source"), Lang.N("{0} audio sources"));
            AddExtra(scan, cloth, Lang.N("{0} cloth"), Lang.N("{0} cloths"));
            AddExtra(scan, trails, Lang.N("{0} trail or line renderer"), Lang.N("{0} trail or line renderers"));
        }

        private static void AddExtra(OutfitScan scan, int n, string one, string many)
        {
            if (n > 0) scan.Extras.Add(Lang.T(n == 1 ? one : many, n.ToString("N0", Lang.Culture)));
        }
    }
}
#endif
