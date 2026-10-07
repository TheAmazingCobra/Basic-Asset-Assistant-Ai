#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // our star next to every outfit in the hierarchy. green when the check is happy, red when it found
    // a problem or a warning (notes dont count), yellow on a copy we don't support (Fused, Extreme, Quest,
    // Decimated). red wins, a broken copy still shows red. clicking it opens the assistant on that outfit.
    // the checks only run while a hierarchy is on screen, and a moment after the last change
    [InitializeOnLoad]
    internal static class AssistantHierarchy
    {
        // our own plain white star next to the code, tinted per state
        private const string IconGuid = "3f4d801d99c0556e44865b1dd27ee21e";
        private static readonly Color Green = new Color(0.4f, 0.86f, 0.55f);
        // a bit stronger than the window's soft red, that one reads pink on a 16 px star
        private static readonly Color Red = new Color(0.94f, 0.33f, 0.33f);
        private static readonly Color Yellow = new Color(1f, 0.839f, 0f);

        internal enum State { Good, Wrong, Unsupported }

        private struct Mark
        {
            public State State;
            public string Tip;
            // unity draws its own arrow at the end of a prefab's row, the star sits left of it
            public bool Prefab;
        }

        private static readonly Dictionary<int, Mark> Marks = new Dictionary<int, Mark>();
        private static bool _dirty = true;
        private static double _dirtySince;
        private static double _drawn = -10.0;
        private static Texture2D _icon;
        private static bool _iconLooked;
        private static GUIStyle _fallback;

        static AssistantHierarchy()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnItem;
            EditorApplication.hierarchyChanged += MarkDirty;
            EditorApplication.projectChanged += MarkDirty;
            Undo.undoRedoPerformed += MarkDirty;
            // edits in the inspector (a toggle's object slot emptied, say) don't touch the hierarchy
            ObjectChangeEvents.changesPublished += Changed;
            EditorApplication.update += Tick;
        }

        private static void Changed(ref ObjectChangeEventStream stream)
        {
            MarkDirty();
        }

        internal static void MarkDirty()
        {
            if (!_dirty) _dirtySince = EditorApplication.timeSinceStartup;
            _dirty = true;
        }

        private static void Tick()
        {
            if (!_dirty) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - _drawn > 2.0 || now - _dirtySince < 1.0) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            _dirty = false;
            Rebuild();
            EditorApplication.RepaintHierarchyWindow();
        }

        internal static void Rebuild()
        {
            Marks.Clear();
            foreach (Outfit o in AssistantScope.FindOutfits())
            {
                if (o.Place != OutfitPlace.Scene || o.Root == null) continue;
                int open = 0;
                foreach (Issue i in OutfitCheck.Run(o))
                    if (i.Severity != Severity.Note) open++;
                State state = open > 0 ? State.Wrong : o.Copy != null ? State.Unsupported : State.Good;
                string tip;
                if (open > 1) tip = Lang.T("Asset Assistant: {0} things to look at", open);
                else if (open == 1) tip = Lang.T("Asset Assistant: 1 thing to look at");
                else if (o.Copy != null) tip = Lang.T("Asset Assistant: {0} version, no support", OutfitCopies.Shown(o.Copy.Kind));
                else tip = Lang.T("Asset Assistant: all good");
                Marks[o.Root.GetInstanceID()] = new Mark { State = state, Tip = tip, Prefab = PrefabUtility.IsAnyPrefabInstanceRoot(o.Root) };
            }
        }

        // for the tests
        internal static bool Marked(GameObject go, out bool open)
        {
            State state;
            bool marked = Marked(go, out state);
            open = marked && state == State.Wrong;
            return marked;
        }

        internal static bool Marked(GameObject go, out State state)
        {
            state = State.Good;
            Mark m;
            if (go == null || !Marks.TryGetValue(go.GetInstanceID(), out m)) return false;
            state = m.State;
            return true;
        }

        private static void OnItem(int id, Rect row)
        {
            _drawn = EditorApplication.timeSinceStartup;
            Mark mark;
            if (!Marks.TryGetValue(id, out mark)) return;
            var r = new Rect(row.xMax - (mark.Prefab ? 36f : 18f), row.y + (row.height - 16f) * 0.5f, 16f, 16f);
            if (Event.current.type == EventType.Repaint)
            {
                Color before = GUI.color;
                GUI.color = mark.State == State.Wrong ? Red : mark.State == State.Unsupported ? Yellow : Green;
                Texture2D icon = Icon();
                if (icon != null) GUI.DrawTexture(r, icon, ScaleMode.ScaleToFit, true);
                else GUI.Label(r, "★", Fallback());
                GUI.color = before;
            }
            GUI.Label(r, new GUIContent("", mark.Tip), GUIStyle.none);
            if (GUI.Button(r, GUIContent.none, GUIStyle.none))
            {
                var go = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (go != null) BaaaWindow.ShowOutfit(go);
            }
        }

        internal static Texture2D Icon()
        {
            if (_iconLooked) return _icon;
            _iconLooked = true;
            string path = AssetDatabase.GUIDToAssetPath(IconGuid);
            _icon = !string.IsNullOrEmpty(path) ? AssetDatabase.LoadAssetAtPath<Texture2D>(path) : null;
            return _icon;
        }

        private static GUIStyle Fallback()
        {
            if (_fallback == null) _fallback = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12, normal = { textColor = Color.white } };
            return _fallback;
        }
    }
}
#endif
