#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace BAAA
{
    internal enum FeatureKind { Hue, Saturation, Brightness, OtherColor, PhysBone, Collider, Contact }

    internal sealed class OutfitFeature
    {
        public FeatureKind Kind;
        public string Name;
        // Physbones: the pieces they move, like "Skirt". Empty when no piece uses them
        public string Group = "";
        public bool On;
        public Component Live;
        public RemovedComponent Removed;
        public Transform Parked;
    }

    // stuff you can turn off on an outfit: color sliders (hue/saturation/brightness), physbones,
    // colliders and contacts. works like dropping a piece, on a prefab copy its a removed override and
    // on an unpacked outfit it gets parked in the hidden EditorOnly holder. whatever a dropped piece took stays with it
    internal static class OutfitFeatures
    {
        internal const string FeaturePrefix = "feature|";
        private const string ColliderType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";
        private const string ContactReceiverType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";
        private const string ContactSenderType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender";

        public static List<OutfitFeature> Find(Outfit o)
        {
            var list = new List<OutfitFeature>();
            if (o == null || o.Root == null) return list;
            GameObject root = o.Root;
            Transform rootT = root.transform;
            List<Piece> pieces = OutfitPieces.Find(o);

            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || AssistantScope.InsideEditorOnly(c.transform)) continue;
                FeatureKind? kind = KindOf(c);
                if (kind == null) continue;
                var f = new OutfitFeature { Kind = kind.Value, Name = NameOf(c, kind.Value, c.name), On = true, Live = c };
                if (f.Kind == FeatureKind.PhysBone) f.Group = GroupOf(OutfitPieces.Moved(new SerializedObject(c), c.transform, null), pieces);
                list.Add(f);
            }

            if (o.Place == OutfitPlace.Scene && PrefabUtility.IsPartOfPrefabInstance(root))
            {
                var heldByPieces = new HashSet<Component>();
                foreach (Piece p in pieces)
                    foreach (RemovedComponent r in p.Removed)
                        if (r.assetComponent != null) heldByPieces.Add(r.assetComponent);

                Dictionary<UnityEngine.Object, GameObject> map = OutfitPieces.InstanceMap(root);
                GameObject instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(root);
                foreach (RemovedComponent removed in PrefabUtility.GetRemovedComponents(instanceRoot))
                {
                    Component asset = removed.assetComponent;
                    if (asset == null || heldByPieces.Contains(asset)) continue;
                    if (removed.containingInstanceGameObject == null || !removed.containingInstanceGameObject.transform.IsChildOf(rootT)) continue;
                    FeatureKind? kind = KindOf(asset);
                    if (kind == null) continue;
                    var f = new OutfitFeature { Kind = kind.Value, Name = NameOf(asset, kind.Value, asset.name), On = false, Removed = removed };
                    GameObject host;
                    if (f.Kind == FeatureKind.PhysBone && map.TryGetValue(asset.gameObject, out host))
                        f.Group = GroupOf(OutfitPieces.Moved(new SerializedObject(asset), host.transform, map), pieces);
                    list.Add(f);
                }
            }

            Transform holder = rootT.Find(OutfitPieces.HolderName);
            if (holder != null)
            {
                foreach (Transform record in holder)
                {
                    if (!record.name.StartsWith(FeaturePrefix, System.StringComparison.Ordinal)) continue;
                    string path = record.name.Substring(FeaturePrefix.Length);
                    Transform host = path.Length == 0 ? rootT : rootT.Find(path);
                    // named after the object it came off, not the record keeping it
                    string hostName = host != null ? host.name : path.Substring(path.LastIndexOf('/') + 1);
                    foreach (var c in record.GetComponents<Component>())
                    {
                        if (c == null || c is Transform) continue;
                        FeatureKind? kind = KindOf(c);
                        if (kind == null) continue;
                        var f = new OutfitFeature { Kind = kind.Value, Name = NameOf(c, kind.Value, hostName), On = false, Parked = record };
                        if (f.Kind == FeatureKind.PhysBone) f.Group = GroupOf(OutfitPieces.Moved(new SerializedObject(c), host != null ? host : record, null), pieces);
                        list.Add(f);
                    }
                }
            }

            list.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        // A physbone belongs to the pieces whose meshes use the bones it moves
        private static string GroupOf(HashSet<Transform> moved, List<Piece> pieces)
        {
            var names = new List<string>();
            foreach (Piece p in pieces)
                if (p.Bones.Overlaps(moved) && !names.Contains(p.Name)) names.Add(p.Name);
            if (names.Count == 0) return "";
            names.Sort(string.CompareOrdinal);
            if (names.Count == 1) return names[0];
            return Lang.T("{0} and {1}", string.Join(Lang.List, names.GetRange(0, names.Count - 1).ToArray()), names[names.Count - 1]);
        }

        // color sliders are sorted by the Poiyomi setting they change, then by
        // their name: hue, saturation, brightness, or something else (gamma...).
        private static FeatureKind? KindOf(Component c)
        {
            string type = c.GetType().FullName;
            if (type == AssistantScope.PhysBoneType) return FeatureKind.PhysBone;
            if (type == ColliderType) return FeatureKind.Collider;
            if (type == ContactReceiverType || type == ContactSenderType) return FeatureKind.Contact;
            if (!AssistantScope.IsVrcFury(c)) return null;

            var content = new SerializedObject(c).FindProperty("content");
            if (!VrcFuryData.IsManaged(content) || VrcFuryData.ShortType(content.managedReferenceFullTypename) != "Toggle") return null;
            if (!VrcFuryData.Bool(content, "slider")) return null;
            bool changesMaterial = false;
            foreach (SerializedProperty action in VrcFuryData.Actions(content.FindPropertyRelative("state")))
                if (VrcFuryData.ShortType(action.managedReferenceFullTypename) == "MaterialPropertyAction") changesMaterial = true;
            if (!changesMaterial) return null;
            return ColorKind(content);
        }

        // what a color slider changes, from the Poiyomi settings it moves and its name
        internal static FeatureKind ColorKind(SerializedProperty toggle)
        {
            string words = "";
            foreach (SerializedProperty action in VrcFuryData.Actions(toggle.FindPropertyRelative("state")))
                if (VrcFuryData.ShortType(action.managedReferenceFullTypename) == "MaterialPropertyAction")
                    words += " " + VrcFuryData.Text(action, "propertyName");
            words = (words + " " + VrcFuryData.Text(toggle, "name")).ToLowerInvariant();
            if (words.Contains("hue")) return FeatureKind.Hue;
            if (words.Contains("saturation")) return FeatureKind.Saturation;
            if (words.Contains("bright")) return FeatureKind.Brightness;
            return FeatureKind.OtherColor;
        }

        // hostName: the object the component sits on, or sat on before it was parked
        private static string NameOf(Component c, FeatureKind kind, string hostName)
        {
            switch (kind)
            {
                case FeatureKind.PhysBone:
                    var root = new SerializedObject(c).FindProperty("rootTransform");
                    var t = root != null ? root.objectReferenceValue as Transform : null;
                    return t != null ? t.name : hostName;
                case FeatureKind.Collider:
                case FeatureKind.Contact:
                    return hostName;
                default:
                    var content = new SerializedObject(c).FindProperty("content");
                    return VrcFuryData.MenuName(content != null ? VrcFuryData.Text(content, "name") : "");
            }
        }

        // turns these features off or back on, as one Ctrl+Z
        public static void Set(Outfit o, List<OutfitFeature> features, bool on)
        {
            if (!OutfitPieces.CanEdit(o)) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(on ? Lang.T("Turn features back on") : Lang.T("Turn features off"));
            int group = Undo.GetCurrentGroup();
            Transform rootT = o.Root.transform;
            bool instance = PrefabUtility.IsPartOfPrefabInstance(o.Root);
            foreach (OutfitFeature f in features)
            {
                if (f.On == on) continue;
                if (!on)
                {
                    if (f.Live == null) continue;
                    if (!instance) Park(rootT, f.Live);
                    Undo.DestroyObjectImmediate(f.Live);
                }
                else if (f.Removed != null)
                {
                    f.Removed.Revert(InteractionMode.UserAction);
                }
                else if (f.Parked != null)
                {
                    Unpark(rootT, f.Parked);
                }
            }
            Undo.CollapseUndoOperations(group);
        }

        private static void Park(Transform rootT, Component c)
        {
            string undo = Lang.T("Turn features off");
            Transform holder = OutfitPieces.EnsureHolder(rootT, undo);
            string path = c.transform == rootT ? "" : AnimationUtility.CalculateTransformPath(c.transform, rootT);
            var record = new GameObject(FeaturePrefix + path);
            Undo.RegisterCreatedObjectUndo(record, undo);
            Undo.SetTransformParent(record.transform, holder, undo);
            ComponentUtility.CopyComponent(c);
            ComponentUtility.PasteComponentAsNew(record);
        }

        private static void Unpark(Transform rootT, Transform record)
        {
            string path = record.name.Substring(FeaturePrefix.Length);
            Transform host = path.Length == 0 ? rootT : rootT.Find(path);
            if (host != null)
            {
                foreach (var c in record.GetComponents<Component>())
                {
                    if (c == null || c is Transform) continue;
                    ComponentUtility.CopyComponent(c);
                    ComponentUtility.PasteComponentAsNew(host.gameObject);
                }
            }
            Transform holder = record.parent;
            Undo.DestroyObjectImmediate(record.gameObject);
            if (holder != null && holder.childCount == 0) Undo.DestroyObjectImmediate(holder.gameObject);
        }
    }
}
#endif
