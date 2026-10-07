#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // features tab, one card per kind with a switch for all of it. physbones get grouped by the piece they move
    public partial class BaaaWindow
    {
        private const float SwitchWidth = 34f;
        private const float SwitchHeight = 18f;
        private static readonly Color ColSwitchOff = new Color(0.22f, 0.21f, 0.17f);
        private static readonly Color ColSwitchOffHover = new Color(0.29f, 0.28f, 0.23f);

        private List<OutfitFeature> _features;
        // Physbone groups opened to list their physbones one by one
        private readonly HashSet<string> _openGroups = new HashSet<string>();

        private GUIStyle _styleCardTitle;
        private GUIStyle _styleCardMeta;
        private GUIStyle _styleCardAll;
        private GUIStyle _styleItem;
        private GUIStyle _styleItemDetail;
        private GUIStyle _styleSubItem;

        private void DrawFeatures()
        {
            PageHeader(T("Features"), T("Switch things *off*"), T("Turn off what you don't need."));
            Notice(Tone.Warn, T("Heads up"), T("Turning things off can break parts of the outfit. Only turn off what you understand. Ctrl+Z undoes it."));

            Outfit o = Selected;
            bool canEdit = OutfitPieces.CanEdit(o);
            if (!canEdit)
            {
                GUILayout.Space(4);
                GUILayout.Label(o.Place == OutfitPlace.PackFile
                    ? T("This is the pack's own prefab. Put the outfit in your scene to switch its features.")
                    : T("Features are switched on outfits in your scene, not on the pack's own prefab."), _styleWarn);
            }
            if (_features == null)
            {
                GUILayout.Space(8);
                GUILayout.Label(T("Looking at the features..."), _styleBody);
                return;
            }
            if (_features.Count == 0)
            {
                GUILayout.Space(8);
                GUILayout.Label(T("Nothing to switch off on this outfit."), _styleBody);
                return;
            }

            List<OutfitFeature> colors = _features.FindAll(f => IsColor(f.Kind));
            if (colors.Count > 0)
            {
                BeginCard(T("Color changers"), colors, N("{0} slider"), N("{0} sliders"), T("Sliders you turn off leave your menu and keep the shipped color."), canEdit);
                bool first = true;
                ColorRow(T("Hue"), FeatureKind.Hue, canEdit, ref first);
                ColorRow(T("Saturation"), FeatureKind.Saturation, canEdit, ref first);
                ColorRow(T("Brightness"), FeatureKind.Brightness, canEdit, ref first);
                ColorRow(T("Other colors"), FeatureKind.OtherColor, canEdit, ref first);
                EndCard();
            }

            List<OutfitFeature> physBones = Of(FeatureKind.PhysBone);
            if (o.Copy != null && o.Copy.Kind == CopyKind.Quest)
            {
                GUILayout.Space(4);
                Notice(Tone.Neutral, null, T("The Quest version's physbones are picked in the Quest tab."));
            }
            else if (physBones.Count > 0)
            {
                BeginCard(T("PhysBones"), physBones, N("{0} physbone"), N("{0} physbones"), T("Without them, skirts, hair and tails stop moving. Open a group to switch single ones."), canEdit);
                bool first = true;
                foreach (string group in GroupNames(physBones))
                {
                    List<OutfitFeature> items = physBones.FindAll(f => f.Group == group);
                    // read once, so this whole frame lays out the same rows.
                    bool open = _openGroups.Contains(group);
                    if (Row(group.Length > 0 ? group : T("Other"), CountText(items, N("{0} physbone"), N("{0} physbones")), items, canEdit, open ? 1 : 0, first))
                    {
                        if (open) _openGroups.Remove(group);
                        else _openGroups.Add(group);
                        Repaint();
                    }
                    first = false;
                    if (!open) continue;
                    foreach (OutfitFeature f in items) SubRow(f, canEdit);
                    GUILayout.Space(4);
                }
                EndCard();
            }

            GridCard(T("Colliders"), FeatureKind.Collider, N("{0} collider"), N("{0} colliders"), T("Without them, moving parts can go through the body."), canEdit);
            GridCard(T("Contacts"), FeatureKind.Contact, N("{0} contact"), N("{0} contacts"), T("Touch reactions stop working."), canEdit);
        }

        // -------------------------------------------------------------------
        // cards and rows
        // -------------------------------------------------------------------

        // A card: its title, how many there are, a switch for all of them, and a hint.
        private void BeginCard(string title, List<OutfitFeature> items, string one, string many, string hint, bool canEdit)
        {
            GUILayout.Space(10);
            Rect box = EditorGUILayout.BeginVertical();
            if (Event.current.type == EventType.Repaint)
            {
                Rounded(box, ColSurface, 0f, 4f);
                Rounded(box, ColLine, 1f, 4f);
            }
            GUILayout.Space(12);
            GUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.BeginVertical();

            Rect head = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            Rect sw = SwitchRect(head);
            var titleText = new GUIContent(title.ToUpperInvariant());
            float titleWidth = _styleCardTitle.CalcSize(titleText).x;
            GUI.Label(new Rect(head.x, head.y, titleWidth, head.height), titleText, _styleCardTitle);
            GUI.Label(new Rect(head.x + titleWidth + 8f, head.y, Mathf.Max(0f, sw.x - head.x - titleWidth - 48f), head.height), CountText(items, one, many), _styleCardMeta);
            var all = new GUIContent(T("All"));
            float allWidth = Mathf.Max(28f, _styleCardAll.CalcSize(all).x);
            GUI.Label(new Rect(sw.x - allWidth - 6f, head.y, allWidth, head.height), all, _styleCardAll);
            SwitchFor(sw, items, canEdit);

            GUILayout.Space(2);
            GUILayout.Label(hint, _styleMini);
            GUILayout.Space(8);
            SeparatorLine();
        }

        private void EndCard()
        {
            GUILayout.Space(4);
            GUILayout.EndVertical();
            GUILayout.Space(16);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.EndVertical();
        }

        private void ColorRow(string label, FeatureKind kind, bool canEdit, ref bool first)
        {
            List<OutfitFeature> items = Of(kind);
            if (items.Count == 0) return;
            var names = new List<string>();
            foreach (OutfitFeature f in items) names.Add(f.Name);
            Row(label, string.Join(", ", names.ToArray()), items, canEdit, -1, first);
            first = false;
        }

        // One line on a card: what it is, what it covers, and its switch. A row
        // with an arrow (0 closed, 1 open) returns true when its clicked.
        private bool Row(string label, string detail, List<OutfitFeature> items, bool canEdit, int arrow, bool first)
        {
            if (!first) SeparatorLine();
            Rect r = GUILayoutUtility.GetRect(10f, 38f, GUILayout.ExpandWidth(true));
            Rect sw = SwitchRect(r);
            var body = new Rect(r.x, r.y, sw.x - r.x - 6f, r.height);
            bool opens = arrow >= 0;
            if (opens && body.Contains(Event.current.mousePosition))
            {
                DrawRect(new Rect(r.x - 8f, r.y, r.width + 16f, r.height), ColSurface2);
                Repaint();
            }

            float x = r.x;
            if (opens)
            {
                DrawArrow(new Rect(x, r.y, 12f, r.height), arrow == 1, ColMuted);
                x += 18f;
            }
            bool allOff = items.TrueForAll(f => !f.On);
            float w = Mathf.Max(0f, sw.x - x - 12f);
            DimLabel(new Rect(x, r.y + 3f, w, 18f), Fit(label, w, _styleItem), _styleItem, allOff);
            DimLabel(new Rect(x, r.y + 20f, w, 14f), Fit(detail, w, _styleItemDetail), _styleItemDetail, allOff);
            SwitchFor(sw, items, canEdit);
            return opens && GUI.Button(body, GUIContent.none, GUIStyle.none);
        }

        // one physbone inside an open group.
        private void SubRow(OutfitFeature f, bool canEdit)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 26f, GUILayout.ExpandWidth(true));
            Rect sw = SwitchRect(r);
            DrawRect(new Rect(r.x + 5f, r.y, 1f, r.height), ColLine);
            float x = r.x + 18f;
            float w = Mathf.Max(0f, sw.x - x - 12f);
            DimLabel(new Rect(x, r.y, w, r.height), Fit(f.Name, w, _styleSubItem), _styleSubItem, !f.On);
            SwitchFor(sw, new List<OutfitFeature> { f }, canEdit);
        }

        // Colliders and contacts: their names in two columns, each with a switch
        private void GridCard(string title, FeatureKind kind, string one, string many, string hint, bool canEdit)
        {
            List<OutfitFeature> items = Of(kind);
            if (items.Count == 0) return;
            BeginCard(title, items, one, many, hint, canEdit);
            for (int i = 0; i < items.Count; i += 2)
            {
                if (i > 0) SeparatorLine();
                Rect r = GUILayoutUtility.GetRect(10f, 30f, GUILayout.ExpandWidth(true));
                float half = (r.width - 28f) * 0.5f;
                Cell(new Rect(r.x, r.y, half, r.height), items[i], canEdit);
                if (i + 1 < items.Count) Cell(new Rect(r.xMax - half, r.y, half, r.height), items[i + 1], canEdit);
            }
            EndCard();
        }

        private void Cell(Rect r, OutfitFeature f, bool canEdit)
        {
            Rect sw = SwitchRect(r);
            float w = Mathf.Max(0f, sw.x - r.x - 10f);
            DimLabel(new Rect(r.x, r.y, w, r.height), Fit(f.Name, w, _styleSubItem), _styleSubItem, !f.On);
            SwitchFor(sw, new List<OutfitFeature> { f }, canEdit);
        }

        // -------------------------------------------------------------------
        // the switch
        // -------------------------------------------------------------------

        private static Rect SwitchRect(Rect row)
        {
            return new Rect(row.xMax - SwitchWidth, row.y + (row.height - SwitchHeight) * 0.5f, SwitchWidth, SwitchHeight);
        }

        // all on turns everything off; anything off turns everything back on
        private void SwitchFor(Rect r, List<OutfitFeature> items, bool canEdit)
        {
            int on = 0;
            foreach (OutfitFeature f in items) if (f.On) on++;
            bool all = on == items.Count;
            if (PillSwitch(r, all ? 2 : on > 0 ? 1 : 0, canEdit))
            {
                OutfitFeatures.Set(Selected, items, !all);
                AfterFeatureChange();
            }
        }

        // state: 0 off, 1 some on, 2 on. On is yellow with the knob right, off
        // is dark with the knob left, and a mix sits in the middle. True when clicked
        private bool PillSwitch(Rect r, int state, bool canEdit)
        {
            bool hover = canEdit && r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                Color track = state == 2 ? (hover ? ColYellowHover : ColYellow) : state == 1 ? ColYellowOff : hover ? ColSwitchOffHover : ColSwitchOff;
                Color knob = state == 2 ? ColBg : state == 1 ? ColYellow : ColMuted;
                float d = r.height - 6f;
                float x = state == 2 ? r.xMax - 3f - d : state == 1 ? r.center.x - d * 0.5f : r.x + 3f;
                float alpha = canEdit ? 1f : 0.35f;
                Pill(r, track, alpha);
                Pill(new Rect(x, r.y + 3f, d, d), knob, alpha);
            }
            if (hover) Repaint();
            return canEdit && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        private static void Pill(Rect r, Color color, float alpha)
        {
            color.a *= alpha;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, 0f, r.height * 0.5f);
        }

        private static void DrawArrow(Rect r, bool open, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            Vector2 c = r.center;
            Handles.color = color;
            if (open) Handles.DrawAAConvexPolygon(new Vector3(c.x - 4f, c.y - 2f), new Vector3(c.x + 4f, c.y - 2f), new Vector3(c.x, c.y + 3f));
            else Handles.DrawAAConvexPolygon(new Vector3(c.x - 2f, c.y - 4f), new Vector3(c.x + 3f, c.y), new Vector3(c.x - 2f, c.y + 4f));
        }

        // -------------------------------------------------------------------
        // helpers
        // -------------------------------------------------------------------

        private static void DimLabel(Rect r, string text, GUIStyle style, bool dim)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color before = GUI.color;
            if (dim) GUI.color = new Color(before.r, before.g, before.b, before.a * 0.5f);
            GUI.Label(r, text, style);
            GUI.color = before;
        }

        // shortens text that doesn't fit, ending it with "..."
        private static string Fit(string text, float width, GUIStyle style)
        {
            if (width <= 0f || style.CalcSize(new GUIContent(text)).x <= width) return text;
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (style.CalcSize(new GUIContent(text.Substring(0, mid).TrimEnd() + "...")).x <= width) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + "...";
        }

        // "13 physbones", or "13 physbones,  2 off"
        private static string CountText(List<OutfitFeature> items, string one, string many)
        {
            int off = items.FindAll(f => !f.On).Count;
            string count = Plural(items.Count, one, many);
            return off > 0 ? T("{0},  {1} off", count, off) : count;
        }

        // physbone groups in name order; the physbones no piece uses come last.
        private static List<string> GroupNames(List<OutfitFeature> physBones)
        {
            var names = new List<string>();
            bool other = false;
            foreach (OutfitFeature f in physBones)
            {
                if (f.Group.Length == 0) other = true;
                else if (!names.Contains(f.Group)) names.Add(f.Group);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            if (other) names.Add("");
            return names;
        }

        private static bool IsColor(FeatureKind kind)
        {
            return kind == FeatureKind.Hue || kind == FeatureKind.Saturation || kind == FeatureKind.Brightness || kind == FeatureKind.OtherColor;
        }

        private List<OutfitFeature> Of(FeatureKind kind)
        {
            return _features.FindAll(f => f.Kind == kind);
        }

        private void AfterFeatureChange()
        {
            _features = null;
            _pieces = null;
            _scan = null;
            MarkDirty();
            GUIUtility.ExitGUI();
        }
    }
}
#endif
