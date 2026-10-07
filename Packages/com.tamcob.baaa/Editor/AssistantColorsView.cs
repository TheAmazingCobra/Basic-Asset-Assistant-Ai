#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // colors tab. one slider per color changer, it picks what the avatar loads in with and shows it
    // in the scene while the tab is open
    public partial class BaaaWindow
    {
        private List<ColorChanger> _colors;
        [NonSerialized] private ColorChanger _colorDrag;
        [NonSerialized] private float _colorDragValue;
        [NonSerialized] private bool _colorsShown;

        private void DrawColors()
        {
            PageHeader(T("Colors"), T("Pick your *colors*"), T("Choose the color it loads in. Drag a slider to see it on your avatar."));
            Outfit o = Selected;
            if (o.Copy != null && o.Copy.Kind != CopyKind.Decimated)
            {
                DrawBakedColors(o);
                return;
            }
            if (!AssistantScope.VrcFuryInstalled)
            {
                Notice(Tone.Warn, null, T("VRCFury isn't installed, so this outfit's color changers can't be read. The Check tab shows how to fix that."));
                return;
            }
            if (_colors == null)
            {
                GUILayout.Label(T("Looking at the color changers..."), _styleBody);
                return;
            }
            if (_colors.Count == 0)
            {
                GUILayout.Label(T("This outfit has no color changers."), _styleBody);
                return;
            }

            bool canEdit = OutfitPieces.CanEdit(o);
            if (!canEdit)
            {
                Notice(Tone.Neutral, null, o.Place == OutfitPlace.PackFile
                    ? T("This is the pack's own prefab. Put the outfit in your scene to change its colors.")
                    : T("Colors are changed on outfits in your scene, not on the pack's own prefab."));
            }

            foreach (ColorChanger c in _colors) DrawColorChanger(c, canEdit);
            GUILayout.Space(2);
            GUILayout.Label(T("You can still change them from your menu in VRChat. The scene only shows them while this tab is open."), _styleMini);
        }

        // fused, extreme and quest copies have no sliders, their colors got baked in when they were made
        private void DrawBakedColors(Outfit o)
        {
            Notice(Tone.Neutral, null, T("The colors are baked into this {0} version. Go back to the original to change them, then make it again.", KindName(o.Copy.Kind)));
            if (o.Copy.Colors.Count > 0)
            {
                BeginPanel();
                foreach (CopyColor c in o.Copy.Colors) ScanRow(c.Name, T("Baked at {0}", Percent(c.Value)));
                EndPanel();
            }
            else
            {
                GUILayout.Label(T("This outfit has no color changers."), _styleBody);
                GUILayout.Space(8);
            }
            if (SecondaryButton(T("Go back to the original"), o.Original != null))
            {
                GameObject original = o.Original;
                OutfitCopies.GoBack(o);
                AfterCopyChange(original);
                GUIUtility.ExitGUI();
            }
        }

        private void DrawColorChanger(ColorChanger c, bool canEdit)
        {
            BeginPanel();
            GUILayout.BeginHorizontal();
            GUILayout.Label(c.Name, _styleTitleLine);
            GUILayout.FlexibleSpace();
            Badge(ColorKindName(c.Kind), Tone.Neutral);
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            float shown = _colorDrag == c ? _colorDragValue : c.Value;
            float next = Slider(shown, 0f, 1f);
            if (canEdit && Mathf.Abs(next - shown) > 0.0001f)
            {
                _colorDrag = c;
                _colorDragValue = next;
                OutfitColors.Preview(_colors, x => x == c ? next : x.Value);
                Repaint();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Loads in at {0}", Percent(shown)), _styleRow);
            GUILayout.FlexibleSpace();
            if (c.Shipped >= 0f)
            {
                bool changed = Mathf.Abs(c.Value - c.Shipped) > 0.001f;
                if (SecondaryButton(T("Back to {0}", Percent(c.Shipped)), canEdit && changed && _colorDrag == null))
                {
                    OutfitColors.Reset(c);
                    AfterColorChange();
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.EndHorizontal();
            EndPanel();
        }

        // the value is saved when the mouse lets go, so one drag is one Ctrl+Z
        private void UpdateColors()
        {
            if (_colorDrag == null || GUIUtility.hotControl != 0) return;
            OutfitColors.SetDefault(_colorDrag, _colorDragValue);
            _colorDrag = null;
            AfterColorChange();
        }

        private void AfterColorChange()
        {
            _colors = null;
            _colorsShown = false;
            _scan = null;
            MarkDirty();
            Repaint();
        }

        // runs before each layout: shows the colors while the tab is open, puts the scene back after
        private void KeepColorPreview()
        {
            if (_tab == TabColors && Selected != null)
            {
                if (_colors == null) _colors = OutfitColors.Find(Selected);
                if (!_colorsShown && _colorDrag == null)
                {
                    OutfitColors.Preview(_colors, x => x.Value);
                    _colorsShown = true;
                }
            }
            else if (OutfitColors.Previewing)
            {
                OutfitColors.ClearPreview();
                _colorsShown = false;
            }
        }

        private static string ColorKindName(FeatureKind kind)
        {
            switch (kind)
            {
                case FeatureKind.Hue: return T("Hue");
                case FeatureKind.Saturation: return T("Saturation");
                case FeatureKind.Brightness: return T("Brightness");
                default: return T("Color");
            }
        }

        private static string Percent(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }
    }
}
#endif
