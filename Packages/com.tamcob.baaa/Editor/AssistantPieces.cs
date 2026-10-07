#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace BAAA
{
    internal enum BodyRegion { Head, Neck, Torso, Arms, Hands, Hips, Legs, Feet, Tail, Extras }

    internal struct PieceCost
    {
        public int SkinnedMeshes;
        public int Meshes;
        public int Triangles;
        public int MaterialSlots;
        public int PhysBones;
    }

    internal sealed class Piece
    {
        public string Name;
        public Texture Icon;
        public Mesh PreviewMesh;
        public BodyRegion Region;
        public bool Dropped;
        public PieceCost Cost;
        public readonly List<GameObject> Objects = new List<GameObject>();
        // bones its meshes actually move.
        public readonly HashSet<Transform> Bones = new HashSet<Transform>();
        // while kept: its toggle and the physbones that only move it.
        public Component Toggle;
        public readonly List<Component> PhysBones = new List<Component>();
        // While dropped: what to put back.
        public readonly List<RemovedComponent> Removed = new List<RemovedComponent>();
        public Transform Parked;
    }

    // the outfits pieces, aka the clothes its toggles turn on and off. taking one off tags it EditorOnly
    // (vrcfury and vrchat skip those on upload), hides it and removes its toggle + the physbones only it uses.
    // on a prefab copy those are all just overrides so putting it back gives you the packs version again,
    // on an unpacked outfit they wait in a hidden EditorOnly holder
    internal static class OutfitPieces
    {
        public const string HolderName = "Dropped pieces (Asset Assistant)";
        private const string PiecePrefix = "piece|";
        private const string ObjectPrefix = "object|";
        private const string ComponentPrefix = "component|";

        private sealed class Candidate
        {
            public SerializedProperty Feature;
            public Component Live;
            public RemovedComponent Removed;
            public Transform Parked;
            public Dictionary<UnityEngine.Object, GameObject> Map;
        }

        public static bool CanEdit(Outfit o)
        {
            return o != null && o.Root != null && o.Place == OutfitPlace.Scene;
        }

        // -------------------------------------------------------------------
        // finding the pieces
        // -------------------------------------------------------------------

        public static List<Piece> Find(Outfit o)
        {
            var pieces = new List<Piece>();
            if (o == null || o.Root == null || !AssistantScope.VrcFuryInstalled) return pieces;
            GameObject root = o.Root;
            bool instance = o.Place == OutfitPlace.Scene && PrefabUtility.IsPartOfPrefabInstance(root);

            var candidates = new List<Candidate>();
            foreach (VfFeature f in VrcFuryData.Features(root))
                if (f.Type == "Toggle") candidates.Add(new Candidate { Feature = f.Prop, Live = f.Component });

            // toggles taken off this copy of the outfit, kept by Unity as overrides
            var removedPhysBones = new List<RemovedComponent>();
            Dictionary<UnityEngine.Object, GameObject> map = null;
            if (instance)
            {
                map = InstanceMap(root);
                GameObject instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(root);
                foreach (RemovedComponent removed in PrefabUtility.GetRemovedComponents(instanceRoot))
                {
                    if (removed.containingInstanceGameObject == null || !removed.containingInstanceGameObject.transform.IsChildOf(root.transform)) continue;
                    Component asset = removed.assetComponent;
                    if (asset == null) continue;
                    if (asset.GetType().FullName == AssistantScope.PhysBoneType)
                    {
                        removedPhysBones.Add(removed);
                    }
                    else if (AssistantScope.IsVrcFury(asset) && VrcFuryData.FeatureType(asset) == "Toggle")
                    {
                        var content = new SerializedObject(asset).FindProperty("content");
                        candidates.Add(new Candidate { Feature = content, Removed = removed, Map = map });
                    }
                }
            }

            // toggles parked by an earlier drop on an unpacked outfit
            Transform holder = root.transform.Find(HolderName);
            if (holder != null)
            {
                foreach (Transform record in holder)
                    foreach (Transform part in record)
                        foreach (var mb in part.GetComponents<MonoBehaviour>())
                            if (AssistantScope.IsVrcFury(mb) && VrcFuryData.FeatureType(mb) == "Toggle")
                                candidates.Add(new Candidate { Feature = new SerializedObject(mb).FindProperty("content"), Parked = record });
            }

            foreach (Candidate c in candidates)
            {
                Piece p = ToPiece(c);
                if (p != null) pieces.Add(p);
            }
            DropGroupToggles(pieces);
            SortByPlace(pieces, root);
            WorkOutPhysBones(root, pieces, removedPhysBones, map);
            foreach (Piece p in pieces)
            {
                // where people would say its worn comes first: a skirt is weighted
                // mostly to the thighs, but it belongs on the hips
                p.Region = RegionFromName(p.Name) ?? RegionOf(p.Objects, root.transform);
                p.Cost = CostOf(p);
            }
            return pieces;
        }

        // pieces go in the order their clothes sit in the outfit. taking one off moves its
        // toggle around but never the clothes, so the tiles dont jump around in the pieces tab
        private static void SortByPlace(List<Piece> pieces, GameObject root)
        {
            var place = new Dictionary<Transform, int>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) place[t] = place.Count;
            Func<Piece, int> first = p =>
            {
                int best = int.MaxValue;
                foreach (GameObject go in p.Objects)
                {
                    int i;
                    if (go != null && place.TryGetValue(go.transform, out i) && i < best) best = i;
                }
                return best;
            };
            pieces.Sort((a, b) =>
            {
                int pa = first(a), pb = first(b);
                return pa != pb ? pa.CompareTo(pb) : string.CompareOrdinal(a.Name, b.Name);
            });
        }

        private const BodyRegion Head = BodyRegion.Head, Neck = BodyRegion.Neck, Torso = BodyRegion.Torso, Arms = BodyRegion.Arms,
            Hands = BodyRegion.Hands, Hips = BodyRegion.Hips, Legs = BodyRegion.Legs, Feet = BodyRegion.Feet, Extras = BodyRegion.Extras;

        // Clothing words in toggle names, and where that clothing is worn.
        private static readonly Dictionary<string, BodyRegion> Garments = new Dictionary<string, BodyRegion>
        {
            { "hat", Head }, { "cap", Head }, { "beanie", Head }, { "crown", Head }, { "hood", Head }, { "ear", Head }, { "horn", Head },
            { "glasses", Head }, { "goggle", Head }, { "mask", Head }, { "earring", Head }, { "hair", Head }, { "hairpin", Head },
            { "headband", Head }, { "halo", Head }, { "helmet", Head }, { "visor", Head }, { "eye", Head }, { "ponytail", Head }, { "wig", Head },
            { "choker", Neck }, { "choke", Neck }, { "collar", Neck }, { "necklace", Neck }, { "scarf", Neck }, { "tie", Neck }, { "bowtie", Neck },
            { "top", Torso }, { "shirt", Torso }, { "tshirt", Torso }, { "corset", Torso }, { "jacket", Torso }, { "coat", Torso },
            { "overcoat", Torso }, { "sweater", Torso }, { "hoodie", Torso }, { "vest", Torso }, { "bra", Torso }, { "cape", Torso },
            { "cloak", Torso }, { "blouse", Torso }, { "tuxedo", Torso }, { "overall", Torso }, { "dress", Torso }, { "pasties", Torso },
            { "backpack", Torso }, { "bodysuit", Torso }, { "leotard", Torso }, { "kimono", Torso }, { "robe", Torso }, { "apron", Torso },
            { "suit", Torso }, { "pajama", Torso }, { "pijama", Torso },
            { "arm", Arms }, { "sleeve", Arms }, { "armband", Arms },
            { "glove", Hands }, { "mitten", Hands }, { "bracelet", Hands }, { "ring", Hands }, { "watch", Hands }, { "wristband", Hands },
            { "gauntlet", Hands }, { "nail", Hands },
            { "skirt", Hips }, { "panties", Hips }, { "panty", Hips }, { "underwear", Hips }, { "brief", Hips }, { "short", Hips },
            { "belt", Hips }, { "loincloth", Hips }, { "thong", Hips }, { "garter", Hips }, { "tutu", Hips },
            { "pant", Legs }, { "trouser", Legs }, { "jean", Legs }, { "legging", Legs }, { "sock", Legs }, { "stocking", Legs },
            { "tight", Legs }, { "leg", Legs }, { "thighhigh", Legs },
            { "boot", Feet }, { "shoe", Feet }, { "slipper", Feet }, { "heel", Feet }, { "sandal", Feet }, { "sneaker", Feet }, { "anklet", Feet },
            { "katana", Extras }, { "sword", Extras }, { "gun", Extras }, { "phone", Extras }, { "antiphone", Extras }, { "plushie", Extras },
            { "plush", Extras }, { "pillow", Extras }, { "staff", Extras }, { "wand", Extras }, { "weapon", Extras }, { "extra", Extras }, { "prop", Extras },
        };

        private static BodyRegion? RegionFromName(string name)
        {
            List<string> words = Words(name);
            // "Tail Bowtie" is on the tail, whatever else its called
            foreach (string w in words) if (w == "tail" || w == "tails") return BodyRegion.Tail;
            foreach (string w in words)
            {
                BodyRegion region;
                if (Garments.TryGetValue(w, out region)) return region;
                if (w.Length > 3 && w.EndsWith("s") && Garments.TryGetValue(w.Substring(0, w.Length - 1), out region)) return region;
            }
            return null;
        }

        private static Piece ToPiece(Candidate c)
        {
            SerializedProperty t = c.Feature;
            if (t == null || VrcFuryData.Bool(t, "slider")) return null;
            string name = VrcFuryData.MenuName(VrcFuryData.Text(t, "name"));
            // fix toggles like "Naked Harness Fix" adjust the body, they aren't clothing
            if (name.IndexOf("fix", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            var piece = new Piece { Name = PartName(name), Toggle = c.Live, Parked = c.Parked };
            foreach (SerializedProperty action in VrcFuryData.Actions(t.FindPropertyRelative("state")))
            {
                if (VrcFuryData.ShortType(action.managedReferenceFullTypename) != "ObjectToggleAction") continue;
                var objProp = action.FindPropertyRelative("obj");
                var obj = objProp != null ? objProp.objectReferenceValue as GameObject : null;
                if (obj == null) continue;
                if (c.Map != null && !c.Map.TryGetValue(obj, out obj)) continue;
                if (obj.GetComponentInChildren<Renderer>(true) == null || piece.Objects.Contains(obj)) continue;
                piece.Objects.Add(obj);
            }
            if (piece.Objects.Count == 0) return null;

            if (c.Removed != null)
            {
                piece.Dropped = true;
                piece.Removed.Add(c.Removed);
            }
            else if (c.Parked != null)
            {
                piece.Dropped = true;
            }
            else
            {
                // its toggle is still there but its clothing is EditorOnly: dropped halfway.
                piece.Dropped = piece.Objects.TrueForAll(go => AssistantScope.InsideEditorOnly(go.transform));
            }
            piece.Icon = IconOf(t);
            piece.PreviewMesh = BiggestMesh(piece.Objects);
            return piece;
        }

        // A toggle that only switches things other toggles already switch is a
        // group toggle ("whole outfit"), not a piece of its own.
        private static void DropGroupToggles(List<Piece> pieces)
        {
            var group = new List<Piece>();
            foreach (Piece p in pieces)
            {
                if (p.Objects.Count < 2) continue;
                bool allShared = p.Objects.TrueForAll(go => pieces.Exists(other => other != p && other.Objects.Contains(go)));
                if (allShared) group.Add(p);
            }
            foreach (Piece p in group) pieces.Remove(p);
        }

        // maps each object of the pack prefab to its copy in this outfit.
        internal static Dictionary<UnityEngine.Object, GameObject> InstanceMap(GameObject root)
        {
            var map = new Dictionary<UnityEngine.Object, GameObject>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
                if (source != null && !map.ContainsKey(source)) map[source] = t.gameObject;
            }
            return map;
        }

        private static string PartName(string toggleName)
        {
            return toggleName.StartsWith("Toggle ", StringComparison.OrdinalIgnoreCase) ? toggleName.Substring("Toggle ".Length) : toggleName;
        }

        private static Texture IconOf(SerializedProperty toggle)
        {
            if (!VrcFuryData.Bool(toggle, "enableIcon")) return null;
            var slot = toggle.FindPropertyRelative("icon");
            if (slot == null) return null;
            var objRef = slot.FindPropertyRelative("objRef");
            var tex = objRef != null ? objRef.objectReferenceValue as Texture : null;
            if (tex != null) return tex;
            string id = VrcFuryData.Text(slot, "id");
            if (id.Length == 0) return null;
            string path = AssetDatabase.GUIDToAssetPath(id.Split('|', ':')[0]);
            return path.Length > 0 ? AssetDatabase.LoadAssetAtPath<Texture>(path) : null;
        }

        private static Mesh BiggestMesh(List<GameObject> objects)
        {
            Mesh best = null;
            foreach (GameObject go in objects)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh m = MeshOf(r);
                    if (m != null && (best == null || m.vertexCount > best.vertexCount)) best = m;
                }
            }
            return best;
        }

        private static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var filter = r.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // -------------------------------------------------------------------
        // physbones that only move one piece
        // -------------------------------------------------------------------

        private static void WorkOutPhysBones(GameObject root, List<Piece> pieces, List<RemovedComponent> removedPhysBones, Dictionary<UnityEngine.Object, GameObject> map)
        {
            var pieceBones = new Dictionary<Piece, HashSet<Transform>>();
            var pieceMeshes = new HashSet<SkinnedMeshRenderer>();
            foreach (Piece p in pieces)
            {
                var bones = new HashSet<Transform>();
                foreach (GameObject go in p.Objects)
                {
                    foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        AddWeightedBones(smr, bones);
                        pieceMeshes.Add(smr);
                    }
                }
                pieceBones[p] = bones;
                p.Bones.UnionWith(bones);
            }

            // bones the meshes of every kept piece, and every mesh outside the pieces, move.
            var keptBonesOutside = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (!pieceMeshes.Contains(smr) && !AssistantScope.InsideEditorOnly(smr.transform)) AddWeightedBones(smr, keptBonesOutside);

            var physBones = new List<Component>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == AssistantScope.PhysBoneType && !AssistantScope.InsideEditorOnly(c.transform)) physBones.Add(c);

            foreach (Piece p in pieces)
            {
                // Everything other than this piece that stays on the avatar
                var others = new HashSet<Transform>(keptBonesOutside);
                foreach (Piece other in pieces)
                    if (other != p && !other.Dropped) others.UnionWith(pieceBones[other]);

                if (!p.Dropped)
                {
                    foreach (Component pb in physBones)
                    {
                        HashSet<Transform> moved = Moved(new SerializedObject(pb), pb.transform, null);
                        if (moved.Overlaps(pieceBones[p]) && !moved.Overlaps(others)) p.PhysBones.Add(pb);
                    }
                }
                else if (map != null)
                {
                    foreach (RemovedComponent removed in removedPhysBones)
                    {
                        if (p.Removed.Contains(removed)) continue;
                        GameObject host;
                        if (!map.TryGetValue(removed.assetComponent.gameObject, out host)) continue;
                        HashSet<Transform> moved = Moved(new SerializedObject(removed.assetComponent), host.transform, map);
                        if (moved.Overlaps(pieceBones[p]) && !moved.Overlaps(others)) p.Removed.Add(removed);
                    }
                }
            }
        }

        private static void AddWeightedBones(SkinnedMeshRenderer smr, HashSet<Transform> into)
        {
            Mesh mesh = smr.sharedMesh;
            if (mesh == null) return;
            Transform[] bones = smr.bones;
            foreach (BoneWeight w in mesh.boneWeights)
            {
                AddBone(bones, w.boneIndex0, w.weight0, into);
                AddBone(bones, w.boneIndex1, w.weight1, into);
                AddBone(bones, w.boneIndex2, w.weight2, into);
                AddBone(bones, w.boneIndex3, w.weight3, into);
            }
        }

        private static void AddBone(Transform[] bones, int index, float weight, HashSet<Transform> into)
        {
            if (weight > 0f && index >= 0 && index < bones.Length && bones[index] != null) into.Add(bones[index]);
        }

        // The bones a physbone moves: its root and everything under it, minus
        // the ones it ignores. Data from the pack prefab is mapped onto this copy
        internal static HashSet<Transform> Moved(SerializedObject pb, Transform host, Dictionary<UnityEngine.Object, GameObject> map)
        {
            var moved = new HashSet<Transform>();
            var rootProp = pb.FindProperty("rootTransform");
            Transform start = MapTransform(rootProp != null ? rootProp.objectReferenceValue as Transform : null, map) ?? host;
            var ignore = new HashSet<Transform>();
            var ignoreProp = pb.FindProperty("ignoreTransforms");
            if (ignoreProp != null && ignoreProp.isArray)
            {
                for (int i = 0; i < ignoreProp.arraySize; i++)
                {
                    Transform t = MapTransform(ignoreProp.GetArrayElementAtIndex(i).objectReferenceValue as Transform, map);
                    if (t != null) ignore.Add(t);
                }
            }
            AddMoved(start, ignore, moved);
            return moved;
        }

        private static Transform MapTransform(Transform t, Dictionary<UnityEngine.Object, GameObject> map)
        {
            if (t == null || map == null) return t;
            GameObject copy;
            return map.TryGetValue(t.gameObject, out copy) ? copy.transform : null;
        }

        private static void AddMoved(Transform t, HashSet<Transform> ignore, HashSet<Transform> moved)
        {
            if (ignore.Contains(t)) return;
            moved.Add(t);
            foreach (Transform child in t) AddMoved(child, ignore, moved);
        }

        // -------------------------------------------------------------------
        // where a piece is worn, and what it costs
        // -------------------------------------------------------------------

        // The bone a piece's meshes lean on most, named the way VRChat rigs name
        // them ("Head", "UpperLeg.L", "Tail_1"); a custom chain like a skirt's
        // counts as the bone it hangs from
        private static BodyRegion RegionOf(List<GameObject> objects, Transform stop)
        {
            var weights = new Dictionary<Transform, float>();
            foreach (GameObject go in objects)
            {
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh mesh = smr.sharedMesh;
                    if (mesh == null) continue;
                    Transform[] bones = smr.bones;
                    foreach (BoneWeight w in mesh.boneWeights)
                    {
                        AddWeight(bones, w.boneIndex0, w.weight0, weights);
                        AddWeight(bones, w.boneIndex1, w.weight1, weights);
                        AddWeight(bones, w.boneIndex2, w.weight2, weights);
                        AddWeight(bones, w.boneIndex3, w.weight3, weights);
                    }
                }
            }

            Transform dominant = null;
            float best = 0f;
            foreach (var pair in weights)
            {
                if (pair.Value > best)
                {
                    best = pair.Value;
                    dominant = pair.Key;
                }
            }
            // props without a skinned mesh: the bone they hang from
            if (dominant == null && objects.Count > 0) dominant = objects[0].transform.parent;
            return Classify(dominant, stop);
        }

        private static void AddWeight(Transform[] bones, int index, float weight, Dictionary<Transform, float> weights)
        {
            if (weight <= 0f || index < 0 || index >= bones.Length || bones[index] == null) return;
            float w;
            weights.TryGetValue(bones[index], out w);
            weights[bones[index]] = w + weight;
        }

        private static BodyRegion Classify(Transform bone, Transform stop)
        {
            bool tail = false;
            for (Transform t = bone; t != null && t != stop; t = t.parent)
            {
                // whole words only: "LeftForeArm" is left, fore, arm, and must not
                // count as an ear; "Armature" is not an arm.
                List<string> words = Words(t.name);
                if (Has(words, "tail"))
                {
                    tail = true;
                    continue;
                }
                // only a chain hanging from the hips or lower back is a real tail;
                // "Hood_Tail" bones under the head are a hood's floppy ends
                if (tail && Has(words, "hip", "pelvis", "spine")) return BodyRegion.Tail;
                if (Has(words, "ear", "head", "jaw", "eye", "hair")) return BodyRegion.Head;
                if (Has(words, "neck")) return BodyRegion.Neck;
                if (Has(words, "finger", "thumb", "index", "middle", "ring", "little", "pinky", "hand", "wrist", "palm")) return BodyRegion.Hands;
                if (Has(words, "toe", "foot", "feet", "ankle")) return BodyRegion.Feet;
                if (Has(words, "leg", "thigh", "knee", "calf", "shin")) return BodyRegion.Legs;
                if (Has(words, "arm", "forearm", "elbow", "shoulder")) return BodyRegion.Arms;
                if (Has(words, "hip", "pelvis", "butt", "crotch")) return BodyRegion.Hips;
                if (Has(words, "chest", "spine", "breast", "torso", "belly", "stomach")) return BodyRegion.Torso;
            }
            return tail ? BodyRegion.Tail : BodyRegion.Extras;
        }

        // "UpperLeg.L" -> upper, leg, l
        private static List<string> Words(string name)
        {
            var words = new List<string>();
            var word = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool split = !char.IsLetter(c) || (char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1]));
                if (split && word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Length = 0;
                }
                if (char.IsLetter(c)) word.Append(char.ToLowerInvariant(c));
            }
            if (word.Length > 0) words.Add(word.ToString());
            return words;
        }

        private static bool Has(List<string> words, params string[] keys)
        {
            foreach (string w in words)
                foreach (string k in keys)
                    if (w == k || w == k + "s") return true;
            return false;
        }

        private static PieceCost CostOf(Piece p)
        {
            var cost = new PieceCost();
            foreach (GameObject go in p.Objects)
            {
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is SkinnedMeshRenderer) cost.SkinnedMeshes++;
                    else if (r is MeshRenderer) cost.Meshes++;
                    else continue;
                    cost.MaterialSlots += r.sharedMaterials.Length;
                    Mesh m = MeshOf(r);
                    if (m != null) cost.Triangles += OutfitScanner.Triangles(m);
                }
            }
            if (!p.Dropped) cost.PhysBones = p.PhysBones.Count;
            else if (p.Parked != null) cost.PhysBones = ParkedPhysBones(p.Parked);
            else foreach (RemovedComponent r in p.Removed) if (r.assetComponent != null && r.assetComponent.GetType().FullName == AssistantScope.PhysBoneType) cost.PhysBones++;
            return cost;
        }

        private static int ParkedPhysBones(Transform record)
        {
            int n = 0;
            foreach (Transform part in record)
                foreach (var c in part.GetComponents<Component>())
                    if (c != null && c.GetType().FullName == AssistantScope.PhysBoneType) n++;
            return n;
        }

        // -------------------------------------------------------------------
        // Dropping and bringing back. Each is one Ctrl+Z.
        // -------------------------------------------------------------------

        public static void Drop(Outfit o, Piece p)
        {
            if (!CanEdit(o) || p.Dropped) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Take off {0}", p.Name));
            int group = Undo.GetCurrentGroup();
            Transform rootT = o.Root.transform;
            bool instance = PrefabUtility.IsPartOfPrefabInstance(o.Root);
            Transform record = instance ? null : NewRecord(rootT, p.Name);

            foreach (GameObject go in p.Objects)
            {
                if (go == null) continue;
                if (record != null) AddRecord(record, ObjectPrefix + Path(go.transform, rootT) + "|" + (go.activeSelf ? "1" : "0") + "|" + go.tag);
                Undo.RecordObject(go, Lang.T("Take off {0}", p.Name));
                go.tag = "EditorOnly";
                go.SetActive(false);
                if (instance) PrefabUtility.RecordPrefabInstancePropertyModifications(go);
            }

            var parts = new List<Component>();
            if (p.Toggle != null) parts.Add(p.Toggle);
            parts.AddRange(p.PhysBones);
            foreach (Component c in parts)
            {
                if (c == null) continue;
                if (record != null)
                {
                    Transform part = AddRecord(record, ComponentPrefix + Path(c.transform, rootT));
                    ComponentUtility.CopyComponent(c);
                    ComponentUtility.PasteComponentAsNew(part.gameObject);
                }
                Undo.DestroyObjectImmediate(c);
            }
            Undo.CollapseUndoOperations(group);
        }

        public static void Restore(Outfit o, Piece p)
        {
            if (!CanEdit(o) || !p.Dropped) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Put back {0}", p.Name));
            int group = Undo.GetCurrentGroup();
            RestoreOne(o, p);
            Undo.CollapseUndoOperations(group);
        }

        public static void RestoreAll(Outfit o, List<Piece> pieces)
        {
            if (!CanEdit(o)) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Put everything back"));
            int group = Undo.GetCurrentGroup();
            foreach (Piece p in pieces) if (p.Dropped) RestoreOne(o, p);
            Undo.CollapseUndoOperations(group);
        }

        private static void RestoreOne(Outfit o, Piece p)
        {
            Transform rootT = o.Root.transform;
            if (p.Parked != null)
            {
                var parts = new List<Transform>();
                foreach (Transform part in p.Parked) parts.Add(part);
                foreach (Transform part in parts)
                {
                    if (part.name.StartsWith(ObjectPrefix, StringComparison.Ordinal))
                    {
                        // object|<path>|<active>|<tag>
                        string[] bits = part.name.Substring(ObjectPrefix.Length).Split('|');
                        if (bits.Length < 3) continue;
                        string path = string.Join("|", bits, 0, bits.Length - 2);
                        Transform t = path.Length == 0 ? rootT : rootT.Find(path);
                        if (t == null) continue;
                        Undo.RecordObject(t.gameObject, Lang.T("Put back {0}", p.Name));
                        t.gameObject.tag = bits[bits.Length - 1];
                        t.gameObject.SetActive(bits[bits.Length - 2] == "1");
                    }
                    else if (part.name.StartsWith(ComponentPrefix, StringComparison.Ordinal))
                    {
                        string path = part.name.Substring(ComponentPrefix.Length);
                        Transform host = path.Length == 0 ? rootT : rootT.Find(path);
                        if (host == null) continue;
                        foreach (var c in part.GetComponents<Component>())
                        {
                            if (c is Transform) continue;
                            ComponentUtility.CopyComponent(c);
                            ComponentUtility.PasteComponentAsNew(host.gameObject);
                        }
                    }
                }
                Transform holder = p.Parked.parent;
                Undo.DestroyObjectImmediate(p.Parked.gameObject);
                if (holder != null && holder.childCount == 0) Undo.DestroyObjectImmediate(holder.gameObject);
                return;
            }

            foreach (GameObject go in p.Objects)
            {
                if (go == null) continue;
                if (PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    RevertIfOverridden(go, "m_TagString");
                    RevertIfOverridden(go, "m_IsActive");
                }
                else
                {
                    Undo.RecordObject(go, Lang.T("Put back {0}", p.Name));
                    go.tag = "Untagged";
                }
            }
            foreach (RemovedComponent r in p.Removed) r.Revert(InteractionMode.UserAction);
        }

        private static void RevertIfOverridden(GameObject go, string property)
        {
            var prop = new SerializedObject(go).FindProperty(property);
            if (prop != null && prop.prefabOverride) PrefabUtility.RevertPropertyOverride(prop, InteractionMode.UserAction);
        }

        private static Transform NewRecord(Transform rootT, string pieceName)
        {
            return AddRecord(EnsureHolder(rootT, "Drop " + pieceName), PiecePrefix + pieceName);
        }

        // the hidden EditorOnly object that keeps what was taken off an unpacked outfit.
        internal static Transform EnsureHolder(Transform rootT, string undoName)
        {
            Transform holder = rootT.Find(HolderName);
            if (holder != null) return holder;
            var h = new GameObject(HolderName) { tag = "EditorOnly" };
            h.SetActive(false);
            Undo.RegisterCreatedObjectUndo(h, undoName);
            Undo.SetTransformParent(h.transform, rootT, undoName);
            return h.transform;
        }

        private static Transform AddRecord(Transform parent, string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            Undo.SetTransformParent(go.transform, parent, name);
            return go.transform;
        }

        private static string Path(Transform t, Transform rootT)
        {
            return t == rootT ? "" : AnimationUtility.CalculateTransformPath(t, rootT);
        }
    }
}
#endif
