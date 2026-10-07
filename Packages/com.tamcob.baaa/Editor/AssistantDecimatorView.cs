#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // decimator tab. on a normal outfit it makes a decimated copy where toggles etc still work,
    // on extreme/quest copies it just reduces the fused mesh
    public partial class BaaaWindow
    {
        // VRChat's limit for a PC avatar ranked Poor, body included
        private const int PcTriangles = 70000;

        private DecimateInfo _decimate;
        // the outfit's triangles the slider sits on, -1 takes it from the copy
        private float _target = -1f;
        private bool _targetPending;
        private double _targetChanged;
        // one mesh's own share while its slider moves
        private int _meshPending = -1;
        private float _meshKeep = -1f;
        private double _meshChanged;

        private void DrawDecimator()
        {
            Outfit o = Selected;
            if (o.Copy == null)
            {
                PageHeader(T("Decimator"), T("Fewer *triangles*"), T("Fewer triangles. Toggles, color changers and pieces keep working."));
                BeginPanel();
                PanelTitle(T("Decimated version"), T("Makes a copy and swaps it in. You can go back anytime."));
                Notice(Tone.Warn, T("At your own risk"), T(OwnRisk));
                if (o.Place != OutfitPlace.Scene)
                {
                    GUILayout.Space(4);
                    GUILayout.Label(o.Place == OutfitPlace.PackFile
                        ? T("This is the pack's own prefab. Put the outfit in your scene first.")
                        : T("Copies are made from outfits in your scene, not from the pack's own prefab."), _styleWarn);
                    EndPanel();
                    return;
                }
                WearingLine(T("Pieces you took off stay off."));
                GUILayout.Space(6);
                GUILayout.Label(TriangleLimits(), _styleMini);
                GUILayout.Space(16);
                if (PrimaryButton(T("Make decimated version"), true))
                {
                    if (EditorUtility.DisplayDialog(T("Make decimated version"),
                        T("Makes a copy of {0} and swaps it in. Nothing changes until you move the slider.", Quote(o.Root.name)) + "\n\n" + T(OwnRisk),
                        T("Make it"), T("Cancel")))
                        RunMake(o, CopyKind.Decimated, 0);
                    GUIUtility.ExitGUI();
                }
                EndPanel();
                return;
            }

            bool decimated = o.Copy.Kind == CopyKind.Decimated;
            PageHeader(T("Decimator"), T("Fewer *triangles*"), decimated
                ? T("Drag to cut triangles. Everything else keeps working.")
                : T("Drag to cut triangles on the fused mesh."));
            Notice(Tone.Warn, T("At your own risk"), T(OwnRisk));

            DecimateInfo d = _decimate;
            if (d == null)
            {
                GUILayout.Label(T("Looking at the meshes..."), _styleBody);
                return;
            }
            if (d.Missing)
            {
                Notice(Tone.Warn, T("Make it again first"), T("It was made before the decimator kept full-detail meshes."));
                DrawCopyPanel(o);
                return;
            }
            if (_target < 0f) _target = d.Target;

            Tiles(
                Tile(T("Triangles"), Number(d.Triangles)),
                Tile(T("At full detail"), Number(d.FullTriangles), null, Tone.Neutral),
                Tile(T("Kept"), d.FullTriangles > 0 ? Mathf.RoundToInt(100f * d.Triangles / d.FullTriangles) + "%" : "100%"));
            GUILayout.Space(12);

            BeginPanel();
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Keep {0} triangles", Number((long)_target)), _styleTitleLine);
            GUILayout.FlexibleSpace();
            if (_targetPending || _meshPending >= 0) Badge(T("Updating"), Tone.Yellow);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            float next = Mathf.Round(Slider(_target, d.Floor, Mathf.Max(d.Floor + 1, d.FullTriangles)));
            if (Mathf.Abs(next - _target) >= 1f)
            {
                _target = next;
                _targetPending = true;
                _targetChanged = EditorApplication.timeSinceStartup;
            }
            GUILayout.Space(8);
            GUILayout.Label(T("Updates as you drag. Too low looks blocky. Drag back up anytime."), _styleMini);
            if (decimated && d.Names.Count > 1)
                GUILayout.Label(T("The cut goes where it shows least. Meshes under {0} triangles stay as they are.", Number(MeshReducer.Small)), _styleMini);
            GUILayout.Space(10);
            DrawFit(o, d);
            GUILayout.Space(6);
            GUILayout.Label(TriangleLimits(), _styleMini);
            EndPanel();

            if (d.Names.Count > 1)
            {
                ScanSection(T("Meshes"));
                for (int i = 0; i < d.Names.Count; i++) DrawMeshRow(o, d, i, decimated);
            }

            if (o.Copy.Kind == CopyKind.Extreme || o.Copy.Kind == CopyKind.Quest)
            {
                GUILayout.Space(16);
                DrawRebake(o, d);
            }

            if (decimated)
            {
                GUILayout.Space(16);
                DrawCopyPanel(o);
            }
        }

        // "fit under 70,000": the outfit's count that brings the whole avatar there
        private void DrawFit(Outfit o, DecimateInfo d)
        {
            if (d.Others < 0) return;
            long limit = o.Copy.Kind == CopyKind.Quest ? QuestTriangles : PcTriangles;
            long want = limit - d.Others;
            bool fits = d.FullTriangles + d.Others <= limit;
            bool can = !fits && want >= d.Floor;
            GUILayout.BeginHorizontal();
            if (SecondaryButton(T("Fit under {0}", Number(limit)), can))
            {
                _target = want;
                _targetPending = true;
                // no waiting, it's a click
                _targetChanged = EditorApplication.timeSinceStartup - 1.0;
            }
            GUILayout.Space(10);
            string line = fits ? T("Fits at full detail, body included.")
                : can ? T("Counts the whole avatar, body included.")
                : T("The rest of the avatar already has {0} triangles.", Number(d.Others));
            GUILayout.Label(line, _styleMini, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawMeshRow(Outfit o, DecimateInfo d, int i, bool modes)
        {
            bool small = d.Small[i];
            // the text sits level with the middle of the buttons next to it
            float drop = modes && !small ? 8f : 0f;
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(170));
            GUILayout.Space(drop);
            GUILayout.Label(d.Names[i], _styleKey);
            GUILayout.EndVertical();
            GUILayout.BeginVertical();
            GUILayout.Space(drop);
            GUILayout.Label(small ? T("{0} triangles, too small to cut", Number(d.Full[i])) : T("{0} of {1} triangles", Number(d.Now[i]), Number(d.Full[i])), _styleValue);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (modes && !small)
            {
                GUILayout.BeginVertical(GUILayout.Width(250));
                int mode = (int)d.Modes[i];
                int picked = Segmented(mode, new[] { T("Auto"), T("Keep all"), T("Custom") }, -1);
                GUILayout.EndVertical();
                if (picked != mode)
                {
                    string error = OutfitCopies.SetMeshMode(o, i, (MeshMode)picked, d.Keeps[i] < 0.999f ? d.Keeps[i] : 0.5f);
                    if (error != null) EditorUtility.DisplayDialog(T("Decimator"), error, T("OK"));
                    AfterDecimate();
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.EndHorizontal();

            if (modes && !small && d.Modes[i] == MeshMode.Own)
            {
                float keep = _meshPending == i ? _meshKeep : d.Keeps[i];
                GUILayout.BeginHorizontal();
                GUILayout.Space(170);
                GUILayout.Label(T("Keep {0}", Mathf.RoundToInt(keep * 100f) + "%"), _styleMini, GUILayout.Width(90), GUILayout.Height(26));
                float k = Slider(keep, 0.05f, 1f);
                GUILayout.EndHorizontal();
                if (Mathf.Abs(k - keep) > 0.001f)
                {
                    _meshPending = i;
                    _meshKeep = k;
                    _meshChanged = EditorApplication.timeSinceStartup;
                }
            }
            GUILayout.Space(4);
        }

        // Extreme and Quest have textures of their own, so those can be baked again for fewer triangles
        private void DrawRebake(Outfit o, DecimateInfo d)
        {
            BeginPanel();
            PanelTitle(T("Re-bake textures"), T("Bakes the full-detail look onto fewer triangles, so prints and seams stay put. Takes a moment."));
            GUILayout.Space(6);
            if (d.Rebaked)
            {
                GUILayout.Label(T("Baked for this triangle count. Moving the slider puts the plain textures back."), _styleBody);
                GUILayout.Space(8);
                if (SecondaryButton(T("Use the plain textures")))
                {
                    string error = OutfitCopies.UsePlainTextures(o);
                    if (error != null) EditorUtility.DisplayDialog(T("Re-bake textures"), error, T("OK"));
                    AfterDecimate();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                bool cut = d.Triangles < d.FullTriangles;
                if (!cut)
                {
                    GUILayout.Label(T("Cut some triangles first."), _styleMini);
                    GUILayout.Space(6);
                }
                if (PrimaryButton(T("Re-bake textures"), cut && !_targetPending))
                {
                    string error = OutfitCopies.Rebake(o);
                    if (error != null) EditorUtility.DisplayDialog(T("Re-bake textures"), error, T("OK"));
                    AfterDecimate();
                    GUIUtility.ExitGUI();
                }
            }
            EndPanel();
        }

        private static string TriangleLimits()
        {
            return T("Very Poor starts above {0} triangles on PC and {1} on Quest, body included.", Number(PcTriangles), Number(QuestTriangles));
        }

        // the sliders wait until they stop moving for a moment
        private void UpdateTarget()
        {
            double now = EditorApplication.timeSinceStartup;
            Outfit o = Selected;
            if (_targetPending && Settled(_targetChanged, now))
            {
                _targetPending = false;
                if (o != null && o.Copy != null)
                {
                    string error = OutfitCopies.SetTarget(o, (long)_target);
                    if (error != null) Debug.LogWarning("Asset Assistant: " + error);
                    AfterDecimate();
                }
            }
            if (_meshPending >= 0 && Settled(_meshChanged, now))
            {
                int index = _meshPending;
                _meshPending = -1;
                if (o != null && o.Copy != null)
                {
                    string error = OutfitCopies.SetMeshMode(o, index, MeshMode.Own, _meshKeep);
                    if (error != null) Debug.LogWarning("Asset Assistant: " + error);
                    AfterDecimate();
                }
            }
        }

        private static bool Settled(double changed, double now)
        {
            if (now - changed < 0.35) return false;
            return GUIUtility.hotControl == 0 || now - changed >= 1.0;
        }

        private void AfterDecimate()
        {
            _decimate = null;
            _quest = null;
            _scan = null;
            _rank = null;
            _compare = null;
            Repaint();
        }
    }
}
#endif
