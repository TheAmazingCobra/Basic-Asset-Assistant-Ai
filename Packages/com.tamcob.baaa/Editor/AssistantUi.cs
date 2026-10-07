#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // how the window looks. dark bg, and yellow is the only accent.
    // the mono font comes from the OS so we dont have to ship a font file in every pack
    public partial class BaaaWindow
    {
        private static readonly Color ColBg = new Color(0.051f, 0.047f, 0.039f);
        private static readonly Color ColSurface = new Color(0.078f, 0.075f, 0.059f);
        private static readonly Color ColSurface2 = new Color(0.106f, 0.102f, 0.082f);
        private static readonly Color ColLine = new Color(0.165f, 0.157f, 0.125f);
        private static readonly Color ColLine2 = new Color(0.25f, 0.24f, 0.2f);
        private static readonly Color ColText = new Color(0.941f, 0.937f, 0.914f);
        private static readonly Color ColMuted = new Color(0.659f, 0.651f, 0.612f);
        private static readonly Color ColDim = new Color(0.435f, 0.427f, 0.392f);
        private static readonly Color ColYellow = new Color(1f, 0.839f, 0f);
        private static readonly Color ColYellowHover = new Color(1f, 0.898f, 0.25f);
        private static readonly Color ColYellowOff = new Color(0.35f, 0.3f, 0.06f);
        private static readonly Color ColGreen = new Color(0.4f, 0.86f, 0.55f);
        private static readonly Color ColWarn = new Color(0.95f, 0.66f, 0.3f);
        private static readonly Color ColRedSoft = new Color(0.93f, 0.5f, 0.5f);
        private static readonly Color ColTip = new Color(0.14f, 0.122f, 0.035f);
        private static readonly Color ColWarnBg = new Color(0.15f, 0.105f, 0.05f);
        private static readonly Color ColRedBg = new Color(0.15f, 0.07f, 0.07f);
        private static readonly Color ColGreenBg = new Color(0.06f, 0.12f, 0.08f);

        private const float Pad = 18f;

        private enum Tone { Neutral, Yellow, Good, Warn, Bad }

        private struct TileData
        {
            public string Label;
            public string Value;
            public string Sub;
            public Tone Tone;
        }

        // Created from the system's mono font, so packs never ship a font file.
        private static Font _mono;

        private GUIStyle _styleWordmark;
        private GUIStyle _styleBrand;
        private GUIStyle _styleKicker;
        private GUIStyle _styleHeading;
        private GUIStyle _styleSubtitle;
        private GUIStyle _styleHeader;
        private GUIStyle _styleBody;
        private GUIStyle _styleGroup;
        private GUIStyle _styleSection;
        private GUIStyle _styleFieldLabel;
        private GUIStyle _stylePopup;
        private GUIStyle _styleRow;
        private GUIStyle _styleMini;
        private GUIStyle _styleTitle;
        private GUIStyle _styleTitleLine;
        private GUIStyle _styleKey;
        private GUIStyle _styleValue;
        private GUIStyle _styleTab;
        private GUIStyle _styleTabHover;
        private GUIStyle _styleTabActive;
        private GUIStyle _styleTabCount;
        private GUIStyle _styleTabLocked;
        private GUIStyle _styleTabLockedHover;
        private GUIStyle _styleSegment;
        private GUIStyle _styleSegmentActive;
        private GUIStyle _styleSegmentOff;
        private GUIStyle _styleBtnPrimary;
        private GUIStyle _styleBtnPrimaryOff;
        private GUIStyle _styleBtnSmall;
        private GUIStyle _styleBtnSecondary;
        private GUIStyle _styleBtnSecondaryOff;
        private GUIStyle _styleBadge;
        private GUIStyle _styleWarn;
        private GUIStyle _styleGood;
        private GUIStyle _styleNoticeText;
        private GUIStyle _styleTileLabel;
        private GUIStyle _styleTileValue;
        private GUIStyle _styleTileSub;
        private readonly Dictionary<Tone, GUIStyle> _toneKickers = new Dictionary<Tone, GUIStyle>();
        private readonly Dictionary<Tone, GUIStyle> _toneValues = new Dictionary<Tone, GUIStyle>();
        // Unity keeps a window's fields when it reloads scripts. Without this,
        // styles added in an update would never be built in an open window
        [NonSerialized] private bool _stylesBuilt;

        private static Font Mono()
        {
            if (_mono == null)
            {
                try
                {
                    // the japanese ones at the end are only there for the letters the mono fonts dont have
                    _mono = Font.CreateDynamicFontFromOSFont(new[]
                    {
                        "Space Mono", "JetBrains Mono", "Cascadia Mono", "Consolas", "Menlo", "Monaco", "Courier New",
                        "Yu Gothic UI", "Meiryo UI", "MS Gothic", "Hiragino Sans", "Noto Sans CJK JP", "Noto Sans Mono CJK JP",
                    }, 11);
                }
                catch (Exception)
                {
                    _mono = null;
                }
            }
            return _mono;
        }

        private void BuildStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;
            Font mono = Mono();

            _styleWordmark = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15, richText = true, alignment = TextAnchor.MiddleLeft, normal = { textColor = ColText } };
            _styleBrand = new GUIStyle(EditorStyles.label) { font = mono, fontSize = 10, alignment = TextAnchor.MiddleLeft, normal = { textColor = ColDim } };
            _styleKicker = new GUIStyle(EditorStyles.boldLabel) { font = mono, fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = ColYellow } };
            _styleHeading = new GUIStyle(EditorStyles.boldLabel) { fontSize = 22, richText = true, wordWrap = true, normal = { textColor = ColText } };
            _styleSubtitle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 12, wordWrap = true, normal = { textColor = ColMuted } };
            _styleHeader = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, normal = { textColor = ColText } };
            _styleBody = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 12, wordWrap = true, normal = { textColor = ColMuted } };
            _styleGroup = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, wordWrap = true, normal = { textColor = ColText } };
            _styleSection = new GUIStyle(EditorStyles.boldLabel) { font = mono, fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = ColDim } };
            _styleFieldLabel = new GUIStyle(_styleSection) { alignment = TextAnchor.MiddleLeft };
            _stylePopup = new GUIStyle(EditorStyles.popup) { fixedHeight = 28f, fontSize = 12 };
            _styleRow = new GUIStyle(EditorStyles.label) { fontSize = 12, wordWrap = true, normal = { textColor = ColMuted } };
            _styleMini = new GUIStyle(EditorStyles.miniLabel) { fontSize = 11, wordWrap = true, normal = { textColor = ColDim } };
            _styleTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, wordWrap = true, normal = { textColor = ColText } };
            _styleTitleLine = new GUIStyle(_styleTitle) { wordWrap = false, alignment = TextAnchor.MiddleLeft };
            _styleKey = new GUIStyle(EditorStyles.label) { fontSize = 12, wordWrap = true, normal = { textColor = ColMuted } };
            _styleValue = new GUIStyle(EditorStyles.label) { fontSize = 12, wordWrap = true, normal = { textColor = ColText } };
            _styleTab = new GUIStyle(EditorStyles.label) { font = mono, fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = ColDim } };
            _styleTabHover = new GUIStyle(_styleTab) { normal = { textColor = ColText } };
            _styleTabActive = new GUIStyle(_styleTab) { normal = { textColor = ColYellow } };
            _styleTabCount = new GUIStyle(_styleTab) { fontSize = 9, normal = { textColor = ColBg } };
            // the advanced tabs before the agreement, grayed out well past the normal dim
            _styleTabLocked = new GUIStyle(_styleTab) { normal = { textColor = new Color(ColDim.r, ColDim.g, ColDim.b, 0.35f) } };
            _styleTabLockedHover = new GUIStyle(_styleTab) { normal = { textColor = new Color(ColDim.r, ColDim.g, ColDim.b, 0.6f) } };
            _styleSegment = new GUIStyle(EditorStyles.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, normal = { textColor = ColText } };
            _styleSegmentActive = new GUIStyle(_styleSegment) { fontStyle = FontStyle.Bold, normal = { textColor = ColBg } };
            _styleSegmentOff = new GUIStyle(_styleSegment) { normal = { textColor = ColDim } };
            _styleBtnPrimary = new GUIStyle(EditorStyles.boldLabel)
            {
                font = mono,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = ColBg },
                hover = { textColor = ColBg },
                active = { textColor = ColBg },
            };
            _styleBtnPrimaryOff = new GUIStyle(_styleBtnPrimary) { normal = { textColor = new Color(0.2f, 0.18f, 0.08f) } };
            _styleBtnSmall = new GUIStyle(_styleBtnPrimary) { fontSize = 10, padding = new RectOffset(14, 14, 0, 0) };
            _styleBtnSecondary = new GUIStyle(_styleBtnPrimary) { fontSize = 10, normal = { textColor = ColText }, hover = { textColor = ColText }, active = { textColor = ColText } };
            _styleBtnSecondaryOff = new GUIStyle(_styleBtnSecondary) { normal = { textColor = ColDim } };
            _styleBadge = new GUIStyle(EditorStyles.boldLabel) { font = mono, fontSize = 9, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = ColText } };
            _styleWarn = new GUIStyle(EditorStyles.label) { fontSize = 12, wordWrap = true, normal = { textColor = ColWarn } };
            _styleGood = new GUIStyle(EditorStyles.label) { fontSize = 12, normal = { textColor = ColGreen } };
            _styleNoticeText = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 12, wordWrap = true, normal = { textColor = ColText } };
            _styleTileLabel = new GUIStyle(EditorStyles.boldLabel) { font = mono, fontSize = 9, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, normal = { textColor = ColDim } };
            _styleTileValue = new GUIStyle(EditorStyles.boldLabel) { fontSize = 20, clipping = TextClipping.Clip, normal = { textColor = ColYellow } };
            _styleTileSub = new GUIStyle(EditorStyles.miniLabel) { fontSize = 10, clipping = TextClipping.Clip, normal = { textColor = ColDim } };
            _toneKickers.Clear();
            _toneValues.Clear();
            foreach (Tone t in Enum.GetValues(typeof(Tone)))
            {
                _toneKickers[t] = new GUIStyle(_styleKicker) { normal = { textColor = ToneColor(t) } };
                _toneValues[t] = new GUIStyle(_styleTileValue) { normal = { textColor = t == Tone.Neutral ? ColText : ToneColor(t) } };
            }

            // Pieces tab
            _styleCaption = new GUIStyle(EditorStyles.miniLabel) { fontSize = 10, alignment = TextAnchor.UpperCenter, clipping = TextClipping.Clip, normal = { textColor = ColMuted } };
            _styleCaptionDim = new GUIStyle(_styleCaption) { normal = { textColor = ColDim } };
            _styleDropped = new GUIStyle(_styleBadge) { normal = { textColor = ColRedSoft } };
            _styleInitials = new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter, normal = { textColor = ColMuted } };

            // Features tab
            _styleCardTitle = new GUIStyle(_styleKicker) { alignment = TextAnchor.MiddleLeft };
            _styleCardMeta = new GUIStyle(EditorStyles.miniLabel) { fontSize = 10, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, normal = { textColor = ColDim } };
            _styleCardAll = new GUIStyle(_styleSection) { alignment = TextAnchor.MiddleRight };
            _styleItem = new GUIStyle(EditorStyles.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, normal = { textColor = ColText } };
            _styleItemDetail = new GUIStyle(EditorStyles.miniLabel) { fontSize = 10, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, normal = { textColor = ColDim } };
            _styleSubItem = new GUIStyle(EditorStyles.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, normal = { textColor = ColMuted } };

            // japanese fonts have no bold at these small sizes and unity smears a fake one, so the
            // little mono labels go regular there
            if (Lang.Now.Code == "ja")
            {
                var small = new List<GUIStyle>
                {
                    _styleKicker, _styleSection, _styleFieldLabel, _styleTab, _styleTabHover, _styleTabActive, _styleTabCount,
                    _styleBtnPrimary, _styleBtnPrimaryOff, _styleBtnSmall, _styleBtnSecondary, _styleBtnSecondaryOff,
                    _styleBadge, _styleTileLabel, _styleCardTitle, _styleCardAll, _styleDropped,
                };
                small.AddRange(_toneKickers.Values);
                foreach (GUIStyle s in small) s.fontStyle = FontStyle.Normal;
            }
        }

        private static Color ToneColor(Tone t)
        {
            switch (t)
            {
                case Tone.Yellow: return ColYellow;
                case Tone.Good: return ColGreen;
                case Tone.Warn: return ColWarn;
                case Tone.Bad: return ColRedSoft;
                default: return ColMuted;
            }
        }

        private static Color ToneBg(Tone t)
        {
            switch (t)
            {
                case Tone.Yellow: return ColTip;
                case Tone.Good: return ColGreenBg;
                case Tone.Warn: return ColWarnBg;
                case Tone.Bad: return ColRedBg;
                default: return ColSurface;
            }
        }

        // -------------------------------------------------------------------
        // page structure
        // -------------------------------------------------------------------

        // A mono kicker, a big uppercase heading with its *starred* word in
        // yellow, and a short line under it
        private void PageHeader(string kicker, string heading, string subtitle)
        {
            GUILayout.Space(20);
            GUILayout.Label(kicker.ToUpperInvariant(), _styleKicker);
            GUILayout.Space(2);
            GUILayout.Label(Markup(heading), _styleHeading);
            if (!string.IsNullOrEmpty(subtitle))
            {
                GUILayout.Space(4);
                GUILayout.Label(subtitle, _styleSubtitle);
            }
            GUILayout.Space(16);
        }

        private static string Markup(string heading)
        {
            var sb = new StringBuilder();
            bool yellow = false;
            foreach (char c in heading.ToUpperInvariant())
            {
                if (c != '*')
                {
                    sb.Append(c);
                    continue;
                }
                sb.Append(yellow ? "</color>" : "<color=#FFD600>");
                yellow = !yellow;
            }
            if (yellow) sb.Append("</color>");
            return sb.ToString();
        }

        // A bordered panel. accent, when set, draws a bar down its left edge
        private void BeginPanel(Color accent)
        {
            Rect box = EditorGUILayout.BeginVertical();
            if (Event.current.type == EventType.Repaint)
            {
                Rounded(box, ColSurface, 0f, 4f);
                Rounded(box, ColLine, 1f, 4f);
                if (accent.a > 0f) DrawRect(new Rect(box.x, box.y + 6f, 3f, Mathf.Max(0f, box.height - 12f)), accent);
            }
            GUILayout.Space(14);
            GUILayout.BeginHorizontal();
            GUILayout.Space(16);
            GUILayout.BeginVertical();
        }

        private void BeginPanel()
        {
            BeginPanel(Color.clear);
        }

        private void EndPanel()
        {
            GUILayout.EndVertical();
            GUILayout.Space(16);
            GUILayout.EndHorizontal();
            GUILayout.Space(14);
            EditorGUILayout.EndVertical();
            GUILayout.Space(12);
        }

        private void PanelTitle(string title, string description)
        {
            GUILayout.Label(title, _styleGroup);
            if (!string.IsNullOrEmpty(description))
            {
                GUILayout.Space(3);
                GUILayout.Label(description, _styleBody);
            }
            GUILayout.Space(12);
        }

        // A tinted box for things to know before going on.
        private void Notice(Tone tone, string label, string text)
        {
            Rect box = EditorGUILayout.BeginVertical();
            if (Event.current.type == EventType.Repaint)
            {
                Rounded(box, ToneBg(tone), 0f, 4f);
                DrawRect(new Rect(box.x, box.y, 3f, box.height), ToneColor(tone));
            }
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Space(15);
            GUILayout.BeginVertical();
            if (!string.IsNullOrEmpty(label))
            {
                GUILayout.Label(label.ToUpperInvariant(), _toneKickers[tone]);
                GUILayout.Space(3);
            }
            GUILayout.Label(text, _styleNoticeText);
            GUILayout.EndVertical();
            GUILayout.Space(12);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
            EditorGUILayout.EndVertical();
            GUILayout.Space(8);
        }

        private void ScanSection(string title)
        {
            GUILayout.Space(18);
            GUILayout.Label(title.ToUpperInvariant(), _styleSection);
            GUILayout.Space(5);
            SeparatorLine();
            GUILayout.Space(7);
        }

        private void ScanRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _styleKey, GUILayout.Width(170));
            GUILayout.Label(value, _styleValue);
            GUILayout.EndHorizontal();
            GUILayout.Space(3);
        }

        // -------------------------------------------------------------------
        // controls
        // -------------------------------------------------------------------

        // The big yellow button, label in mono capitals
        private bool PrimaryButton(string label, bool enabled)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 38f, GUILayout.ExpandWidth(true));
            bool hover = enabled && r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                Rounded(r, !enabled ? ColYellowOff : hover ? ColYellowHover : ColYellow, 0f, 3f);
                GUI.Label(r, label.ToUpperInvariant(), enabled ? _styleBtnPrimary : _styleBtnPrimaryOff);
            }
            if (hover) Repaint();
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        private bool SmallPrimaryButton(string label)
        {
            var content = new GUIContent(label.ToUpperInvariant());
            Rect r = GUILayoutUtility.GetRect(content, _styleBtnSmall, GUILayout.Height(28));
            bool hover = r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                Rounded(r, hover ? ColYellowHover : ColYellow, 0f, 3f);
                GUI.Label(r, content, _styleBtnSmall);
            }
            if (hover) Repaint();
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // an outlined button that lights up yellow under the mouse
        private bool SecondaryButton(string label, bool enabled = true)
        {
            var content = new GUIContent(label.ToUpperInvariant());
            float w = _styleBtnSecondary.CalcSize(content).x + 28f;
            Rect r = GUILayoutUtility.GetRect(w, 28f, GUILayout.Width(w), GUILayout.Height(28f));
            bool hover = enabled && r.Contains(Event.current.mousePosition);
            if (Event.current.type == EventType.Repaint)
            {
                Rounded(r, hover ? ColSurface2 : ColSurface, 0f, 3f);
                Rounded(r, !enabled ? ColLine : hover ? ColYellow : ColLine2, 1f, 3f);
                GUI.Label(r, content, enabled ? _styleBtnSecondary : _styleBtnSecondaryOff);
            }
            if (hover) Repaint();
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // A small chip: solid yellow for the good news, tinted for the rest
        private void Badge(string text, Tone tone)
        {
            var content = new GUIContent(text.ToUpperInvariant());
            float w = _styleBadge.CalcSize(content).x + 14f;
            Rect r = GUILayoutUtility.GetRect(w, 18f, GUILayout.Width(w), GUILayout.Height(18f));
            DrawBadge(r, content, tone);
        }

        private void DrawBadge(Rect r, GUIContent content, Tone tone)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color c = ToneColor(tone);
            bool solid = tone == Tone.Yellow;
            Rounded(r, solid ? c : new Color(c.r, c.g, c.b, 0.12f), 0f, 3f);
            if (!solid) Rounded(r, new Color(c.r, c.g, c.b, 0.5f), 1f, 3f);
            Color before = _styleBadge.normal.textColor;
            _styleBadge.normal.textColor = solid ? ColBg : c;
            GUI.Label(r, content, _styleBadge);
            _styleBadge.normal.textColor = before;
        }

        private int Segmented(int value, string[] labels, int disabled)
        {
            Rect all = GUILayoutUtility.GetRect(10f, 34f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                Rounded(all, ColSurface, 0f, 4f);
                Rounded(all, ColLine, 1f, 4f);
            }
            float w = all.width / labels.Length;
            for (int i = 0; i < labels.Length; i++)
            {
                var r = new Rect(all.x + i * w, all.y, w, all.height);
                var inner = new Rect(r.x + 3f, r.y + 3f, r.width - 6f, r.height - 6f);
                bool off = i == disabled;
                bool active = value == i && !off;
                bool hover = !off && !active && r.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                {
                    if (active) Rounded(inner, ColYellow, 0f, 3f);
                    else if (hover) Rounded(inner, ColSurface2, 0f, 3f);
                    GUI.Label(r, labels[i], active ? _styleSegmentActive : off ? _styleSegmentOff : _styleSegment);
                }
                if (hover) Repaint();
                if (!off && GUI.Button(r, GUIContent.none, GUIStyle.none)) value = i;
            }
            return value;
        }

        // A labelled on/off switch row.
        private bool ToggleButton(string label, bool on)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 32f, GUILayout.ExpandWidth(true));
            Rect sw = SwitchRect(r);
            GUI.Label(new Rect(r.x, r.y, Mathf.Max(0f, sw.x - r.x - 8f), r.height), label, _styleItem);
            bool onSwitch = PillSwitch(sw, on ? 2 : 0, true);
            bool onLabel = GUI.Button(new Rect(r.x, r.y, Mathf.Max(0f, sw.x - r.x), r.height), GUIContent.none, GUIStyle.none);
            return onSwitch || onLabel ? !on : on;
        }

        // A yellow slider. Click or drag anywhere on it
        private float Slider(float value, float min, float max)
        {
            Rect r = GUILayoutUtility.GetRect(10f, 26f, GUILayout.ExpandWidth(true));
            int id = GUIUtility.GetControlID(FocusType.Passive, r);
            Event e = Event.current;
            float x0 = r.x + 9f;
            float x1 = r.xMax - 9f;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        value = Mathf.Lerp(min, max, Mathf.InverseLerp(x0, x1, e.mousePosition.x));
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        value = Mathf.Lerp(min, max, Mathf.InverseLerp(x0, x1, e.mousePosition.x));
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    float knob = Mathf.Lerp(x0, x1, Mathf.InverseLerp(min, max, value));
                    float cy = r.center.y;
                    Rounded(new Rect(x0, cy - 2f, x1 - x0, 4f), ColLine2, 0f, 2f);
                    Rounded(new Rect(x0, cy - 2f, knob - x0, 4f), ColYellow, 0f, 2f);
                    Rounded(new Rect(knob - 9f, cy - 9f, 18f, 18f), ColYellow, 0f, 9f);
                    Rounded(new Rect(knob - 4f, cy - 4f, 8f, 8f), ColBg, 0f, 4f);
                    break;
            }
            EditorGUIUtility.AddCursorRect(r, MouseCursor.SlideArrow);
            return Mathf.Clamp(value, min, max);
        }

        // A row of stat tiles: mono label, big yellow number, a small line under it.
        private void Tiles(params TileData[] tiles)
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < tiles.Length; i++)
            {
                TileData t = tiles[i];
                Rect r = GUILayoutUtility.GetRect(10f, t.Sub != null ? 70f : 58f, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint)
                {
                    Rounded(r, ColSurface, 0f, 4f);
                    Rounded(r, ColLine, 1f, 4f);
                    GUI.Label(new Rect(r.x + 12f, r.y + 10f, r.width - 18f, 14f), t.Label.ToUpperInvariant(), _styleTileLabel);
                    GUI.Label(new Rect(r.x + 12f, r.y + 24f, r.width - 18f, 28f), t.Value, _toneValues[t.Tone]);
                    if (t.Sub != null) GUI.Label(new Rect(r.x + 12f, r.y + 50f, r.width - 18f, 14f), t.Sub, _styleTileSub);
                }
                if (i < tiles.Length - 1) GUILayout.Space(8);
            }
            GUILayout.EndHorizontal();
        }

        private static TileData Tile(string label, string value, string sub = null, Tone tone = Tone.Yellow)
        {
            return new TileData { Label = label, Value = value, Sub = sub, Tone = tone };
        }

        // -------------------------------------------------------------------
        // drawing and text helpers
        // -------------------------------------------------------------------

        private static void DrawRect(Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, color);
        }

        // A rounded rectangle, filled, or just its outline when border is above zero.
        private static void Rounded(Rect r, Color color, float border, float radius)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, border, radius);
        }

        private void SeparatorLine()
        {
            Rect r = GUILayoutUtility.GetRect(10f, 1f, GUILayout.ExpandWidth(true));
            DrawRect(r, ColLine);
        }

        private static int CountOf(List<Issue> issues, Severity severity)
        {
            int n = 0;
            foreach (Issue i in issues) if (i.Severity == severity) n++;
            return n;
        }

        // text in the language the customer picked. the english is the key, see AssistantText
        private static string T(string en)
        {
            return Lang.T(en);
        }

        private static string T(string en, params object[] args)
        {
            return Lang.T(en, args);
        }

        private static string N(string en)
        {
            return en;
        }

        // one and many are whole lines with {0} for the number, like "{0} texture" and "{0} textures"
        private static string Plural(long n, string one, string many)
        {
            return T(n == 1 ? one : many, Number(n));
        }

        private static string Number(long n)
        {
            return n.ToString("N0", Lang.Culture);
        }

        private static string Megabytes(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", Lang.Culture) + " MB";
        }

        private static string Quote(string s)
        {
            return Lang.Quote(s);
        }

        // a numbered step: a small yellow chip with the number and the text next to it
        private void Step(int number, string text)
        {
            GUILayout.BeginHorizontal();
            Rect chip = GUILayoutUtility.GetRect(20f, 20f, GUILayout.Width(20f), GUILayout.Height(20f));
            Rounded(chip, ColYellow, 0f, 10f);
            if (Event.current.type == EventType.Repaint) GUI.Label(chip, number.ToString(CultureInfo.InvariantCulture), _styleTabCount);
            GUILayout.Space(10);
            GUILayout.Label(text, _styleRow);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
        }
    }
}
#endif
