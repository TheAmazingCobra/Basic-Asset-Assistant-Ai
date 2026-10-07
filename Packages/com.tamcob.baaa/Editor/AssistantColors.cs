#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // one color slider and the value the avatar loads in with
    internal sealed class ColorChanger
    {
        public Component Component;
        // where the toggle sits in the vrcfury component, usually "content"
        public string Path;
        public string Name;
        public FeatureKind Kind;
        public float Value;
        // the packs own value, -1 when theres no pack to ask (unpacked outfits)
        public float Shipped = -1f;
        public readonly List<ColorTarget> Targets = new List<ColorTarget>();
    }

    // one material setting a slider moves, from what the material has to the slider's end value
    internal sealed class ColorTarget
    {
        public Renderer Renderer;
        public int Slot;
        public string Property;
        // 0 a number, 1 a color, 2 a vector
        public int Type;
        public Vector4 From;
        public Vector4 To;
    }

    // default colors. VRCFury plays a slider as a blend between the material's own value (0%) and the
    // value in its material property action (100%), and the avatar loads in at defaultSliderValue.
    // the preview does that same blend with property blocks, so no material ever changes
    internal static class OutfitColors
    {
        private static readonly List<KeyValuePair<Renderer, int>> Previewed = new List<KeyValuePair<Renderer, int>>();

        public static bool Previewing
        {
            get { return Previewed.Count > 0; }
        }

        public static List<ColorChanger> Find(Outfit o)
        {
            var list = new List<ColorChanger>();
            if (o == null || o.Root == null || !AssistantScope.VrcFuryInstalled) return list;
            GameObject scope = o.Avatar != null ? o.Avatar : o.Root;
            foreach (VfFeature f in VrcFuryData.Features(o.Root))
            {
                if (f.Type != "Toggle" || !VrcFuryData.Bool(f.Prop, "slider")) continue;
                var c = new ColorChanger
                {
                    Component = f.Component,
                    Path = f.Prop.propertyPath,
                    Name = VrcFuryData.MenuName(VrcFuryData.Text(f.Prop, "name")),
                };
                bool material = false;
                foreach (SerializedProperty action in VrcFuryData.Actions(f.Prop.FindPropertyRelative("state")))
                {
                    if (VrcFuryData.ShortType(action.managedReferenceFullTypename) != "MaterialPropertyAction") continue;
                    material = true;
                    AddTargets(c, action, scope);
                }
                if (!material) continue;
                c.Kind = OutfitFeatures.ColorKind(f.Prop);
                var value = f.Prop.FindPropertyRelative("defaultSliderValue");
                c.Value = value != null ? value.floatValue : 0f;
                c.Shipped = Shipped(f.Component, c.Path);
                list.Add(c);
            }
            return list;
        }

        private static void AddTargets(ColorChanger c, SerializedProperty action, GameObject scope)
        {
            string prop = VrcFuryData.Text(action, "propertyName");
            // vrcfury skips names with a dot (one part of a vector), so do we
            if (string.IsNullOrEmpty(prop) || prop.Contains(".")) return;

            var renderers = new List<Renderer>();
            if (VrcFuryData.Bool(action, "affectAllMeshes"))
            {
                renderers.AddRange(scope.GetComponentsInChildren<Renderer>(true));
            }
            else
            {
                var r2 = action.FindPropertyRelative("renderer2");
                var r1 = action.FindPropertyRelative("renderer");
                var go = r2 != null ? r2.objectReferenceValue as GameObject : null;
                Renderer single = go != null ? go.GetComponent<Renderer>() : (r1 != null ? r1.objectReferenceValue as Renderer : null);
                if (single != null) renderers.Add(single);
            }

            var typeProp = action.FindPropertyRelative("propertyType");
            // Float, Color, Vector, St, LegacyAuto
            int kind = typeProp != null ? typeProp.enumValueIndex : 4;
            var valueProp = action.FindPropertyRelative("value");
            var colorProp = action.FindPropertyRelative("valueColor");
            var vectorProp = action.FindPropertyRelative("valueVector");
            float value = valueProp != null ? valueProp.floatValue : 0f;
            Vector4 color = colorProp != null ? (Vector4)colorProp.colorValue : Vector4.one;
            Vector4 vector = vectorProp != null ? vectorProp.vector4Value : Vector4.zero;

            foreach (Renderer r in renderers)
            {
                if (r == null || AssistantScope.InsideEditorOnly(r.transform)) continue;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    string name = PreviewName(mats[i], prop);
                    if (name == null) continue;
                    int type = kind == 1 ? 1 : kind == 2 || kind == 3 ? 2 : kind == 0 ? 0 : AutoType(mats[i], name);
                    var t = new ColorTarget { Renderer = r, Slot = i, Property = name, Type = type };
                    if (type == 0)
                    {
                        t.From = new Vector4(mats[i].GetFloat(name), 0f, 0f, 0f);
                        t.To = new Vector4(value, 0f, 0f, 0f);
                    }
                    else if (type == 1)
                    {
                        t.From = mats[i].GetColor(name);
                        t.To = color;
                    }
                    else
                    {
                        t.From = mats[i].GetVector(name);
                        t.To = vector;
                    }
                    c.Targets.Add(t);
                }
            }
        }

        // the name the material has the setting under. a Poiyomi material that isnt locked yet only
        // has the original name, the renamed one shows up once its locked
        private static string PreviewName(Material m, string prop)
        {
            if (m == null || m.shader == null) return null;
            if (m.HasProperty(prop)) return prop;
            if (!Poiyomi.IsUnlocked(m)) return null;
            string suffix = Poiyomi.RenameSuffix(m);
            if (string.IsNullOrEmpty(suffix) || !prop.EndsWith("_" + suffix, StringComparison.Ordinal)) return null;
            string original = prop.Substring(0, prop.Length - suffix.Length - 1);
            return m.HasProperty(original) && m.GetTag(original + "Animated", false, "") == "2" ? original : null;
        }

        private static int AutoType(Material m, string name)
        {
            int i = m.shader.FindPropertyIndex(name);
            if (i < 0) return 0;
            switch (m.shader.GetPropertyType(i))
            {
                case UnityEngine.Rendering.ShaderPropertyType.Color: return 1;
                case UnityEngine.Rendering.ShaderPropertyType.Vector: return 2;
                default: return 0;
            }
        }

        private static float Shipped(Component c, string path)
        {
            UnityEngine.Object source = EditorUtility.IsPersistent(c) ? c : PrefabUtility.IsPartOfPrefabInstance(c) ? PrefabUtility.GetCorrespondingObjectFromSource(c) : null;
            if (source == null) return -1f;
            var p = new SerializedObject(source).FindProperty(path + ".defaultSliderValue");
            return p != null ? p.floatValue : -1f;
        }

        // shows every slider at its value in the scene. valueOf can swap in the one being dragged
        public static void Preview(List<ColorChanger> changers, Func<ColorChanger, float> valueOf)
        {
            // starts clean, so a renderer that left the list (another outfit, a swapped material) goes back to normal
            ClearBlocks();
            var block = new MaterialPropertyBlock();
            foreach (ColorChanger c in changers)
            {
                float v = Mathf.Clamp01(valueOf(c));
                foreach (ColorTarget t in c.Targets)
                {
                    if (t.Renderer == null) continue;
                    t.Renderer.GetPropertyBlock(block, t.Slot);
                    Vector4 now = Vector4.Lerp(t.From, t.To, v);
                    if (t.Type == 0) block.SetFloat(t.Property, now.x);
                    else if (t.Type == 1) block.SetColor(t.Property, now);
                    else block.SetVector(t.Property, now);
                    t.Renderer.SetPropertyBlock(block, t.Slot);
                    var key = new KeyValuePair<Renderer, int>(t.Renderer, t.Slot);
                    if (!Previewed.Contains(key)) Previewed.Add(key);
                }
            }
            SceneView.RepaintAll();
        }

        public static void ClearPreview()
        {
            if (Previewed.Count == 0) return;
            ClearBlocks();
            SceneView.RepaintAll();
        }

        private static void ClearBlocks()
        {
            foreach (var pair in Previewed)
            {
                if (pair.Key == null) continue;
                try { pair.Key.SetPropertyBlock(null, pair.Value); }
                catch (Exception) { pair.Key.SetPropertyBlock(new MaterialPropertyBlock(), pair.Value); }
            }
            Previewed.Clear();
        }

        // one Ctrl+Z. on a pack prefab copy its an override, so the pack itself never changes
        public static void SetDefault(ColorChanger c, float value)
        {
            if (c.Component == null) return;
            var so = new SerializedObject(c.Component);
            var p = so.FindProperty(c.Path + ".defaultSliderValue");
            if (p == null) return;
            Undo.IncrementCurrentGroup();
            p.floatValue = Mathf.Clamp01(value);
            so.ApplyModifiedProperties();
            Undo.SetCurrentGroupName(Lang.T("Change the default color"));
        }

        public static void Reset(ColorChanger c)
        {
            if (c.Component == null) return;
            var p = new SerializedObject(c.Component).FindProperty(c.Path + ".defaultSliderValue");
            if (p != null && p.prefabOverride)
            {
                Undo.IncrementCurrentGroup();
                OutfitCheck.RevertProperty(c.Component, c.Path + ".defaultSliderValue");
                Undo.SetCurrentGroupName(Lang.T("Change the default color"));
            }
            else if (c.Shipped >= 0f)
            {
                SetDefault(c, c.Shipped);
            }
        }
    }
}
#endif
