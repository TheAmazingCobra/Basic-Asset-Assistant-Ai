#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    internal sealed class Optimizer
    {
        public string Title;
        public string Why;
        public string Type;
        // Older names VRCFury still turns into this one.
        public string[] SameAs;
        public bool OnAvatar;
        public bool Installed;
    }

    // vrcfurys own optimizers in one click, they only do anything on upload.
    // vrcfury only allows one of each per avatar, on the root or on a child that has nothing but vrcfury
    // on it, so they get their own child object. no parameter compressor, vrcfury does that by itself now
    internal static class VrcFuryOptimizers
    {
        public const string HolderName = "VRCFury Optimizations";
        private const string VrcFuryType = "VF.Model.VRCFury";

        public static List<Optimizer> For(GameObject avatar)
        {
            var list = new List<Optimizer>
            {
                new Optimizer
                {
                    Title = "Blendshape Optimizer",
                    Why = Lang.N("Bakes in blendshapes that never move. Saves memory."),
                    Type = "VF.Model.Feature.BlendshapeOptimizer",
                    SameAs = new string[0],
                },
                new Optimizer
                {
                    Title = "Direct Tree Optimizer",
                    Why = Lang.N("Merges toggle layers into one blend tree, so the animator does less work each frame."),
                    Type = "VF.Model.Feature.DirectTreeOptimizer",
                    SameAs = new string[0],
                },
                new Optimizer
                {
                    Title = "Anchor Override Fix",
                    Why = Lang.N("Gives every mesh the same light anchor, so your whole avatar is lit evenly."),
                    Type = "VF.Model.Feature.AnchorOverrideFix2",
                    SameAs = new[] { "VF.Model.Feature.AnchorOverrideFix" },
                },
            };
            var present = new HashSet<string>();
            if (avatar != null)
                foreach (VfFeature f in VrcFuryData.Features(avatar)) present.Add(f.Type);
            foreach (Optimizer o in list)
            {
                o.Installed = AssistantScope.FindType(o.Type) != null;
                o.OnAvatar = present.Contains(Short(o.Type));
                foreach (string other in o.SameAs) o.OnAvatar |= present.Contains(Short(other));
            }
            return list;
        }

        private static string Short(string type)
        {
            return type.Substring(type.LastIndexOf('.') + 1);
        }

        // adds the ones the avatar doesnt have yet, as one Ctrl+Z. Returns how many
        public static int Apply(GameObject avatar)
        {
            Type vf = AssistantScope.FindType(VrcFuryType);
            if (avatar == null || vf == null) return 0;
            var missing = For(avatar).FindAll(o => o.Installed && !o.OnAvatar);
            if (missing.Count == 0) return 0;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Apply VRCFury optimizations"));
            int group = Undo.GetCurrentGroup();
            GameObject holder = Holder(avatar);
            foreach (Optimizer o in missing)
            {
                var component = Undo.AddComponent(holder, vf);
                var so = new SerializedObject(component);
                so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(AssistantScope.FindType(o.Type));
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Undo.CollapseUndoOperations(group);
            return missing.Count;
        }

        // A child of the avatar root that holds only VRCFury components, made
        // the first time. If something else ended up in it, the avatar root
        // itself is used.
        private static GameObject Holder(GameObject avatar)
        {
            Transform existing = avatar.transform.Find(HolderName);
            if (existing != null) return OnlyVrcFury(existing) ? existing.gameObject : avatar;
            var go = new GameObject(HolderName);
            Undo.RegisterCreatedObjectUndo(go, Lang.T("Apply VRCFury optimizations"));
            Undo.SetTransformParent(go.transform, avatar.transform, Lang.T("Apply VRCFury optimizations"));
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        private static bool OnlyVrcFury(Transform t)
        {
            foreach (Component c in t.GetComponentsInChildren<Component>(true))
                if (!(c is Transform) && !AssistantScope.IsVrcFury(c)) return false;
            return true;
        }
    }
}
#endif
