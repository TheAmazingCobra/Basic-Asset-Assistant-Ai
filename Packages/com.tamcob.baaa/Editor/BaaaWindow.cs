#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// BAAA is its own assembly now that its a package, our test harness still needs the internals
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Assembly-CSharp-Editor")]

namespace BAAA
{
    // main window: header, outfit picker, tabs and the check / perf scanner / texture pages.
    // the other tabs live in the other partial files (AssistantUi, PiecesView, ColorsView, FeaturesView, CopiesView, DecimatorView)
    public partial class BaaaWindow : EditorWindow
    {
        private const int TabCheck = 0;
        private const int TabPieces = 1;
        private const int TabColors = 2;
        private const int TabScanner = 3;
        private const int TabPerformance = 4;
        internal const int TabFuser = 5;
        internal const int TabQuest = 6;
        internal const int TabDecimator = 7;
        private const int TabFeatures = 8;
        internal const int TabVrcFury = 9;
        private static readonly string[] TabNames =
        {
            N("Check"), N("Pieces"), N("Colors"), N("Perf Scanner"), N("Performance"), N("Fuser"), N("Quest"), N("Decimator"), N("Features"), N("VRCFury"),
        };
        // the order the bar shows them in (Features next to Colors, VRCFury after Performance). the numbers
        // above stay put, so a window that was open keeps its tab
        internal static readonly int[] TabOrder =
        {
            TabCheck, TabPieces, TabColors, TabFeatures, TabScanner, TabPerformance, TabVrcFury, TabFuser, TabQuest, TabDecimator,
        };
        private static readonly string[] TextureSizeLabels = { N("Default"), N("Low · 1K"), N("Medium · 2K"), N("High · 4K") };
        private static readonly string[] IconSizeLabels = { N("Default"), N("Low · 64"), N("Medium · 256"), N("High · 512") };
        private static readonly string[] CompressionLabels = { N("Default"), N("Low"), N("Normal"), N("High") };

        private List<Outfit> _outfits = new List<Outfit>();
        private readonly Dictionary<Outfit, List<Issue>> _issues = new Dictionary<Outfit, List<Issue>>();
        private int _selected;
        private int _tab;
        private Vector2 _scroll;
        private VersionInfo _versions;
        private OutfitScan _scan;
        private AvatarRank _rank;
        // the same avatar with the outfit left out
        private AvatarRank _rankWithout;
        private bool _dirty = true;
        private double _dirtySince;
        [NonSerialized] private double _copiedAt = -10.0;

        private PackTextures _pack;
        private TextureChoice _choice;
        private string _packNow = "";
        private int _pending;
        private int _restorable;

        // our goat, next to the name and on the window tab. unity cant draw emoji so its a picture
        private const string GoatGuid = "55022ea2bdbd02eeb981da14ad6f7379";
        private static Texture2D _goat;

        internal static Texture2D Goat()
        {
            if (_goat == null)
            {
                string path = AssetDatabase.GUIDToAssetPath(GoatGuid);
                if (!string.IsNullOrEmpty(path)) _goat = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            return _goat;
        }

        [MenuItem("BAAA/Open tool")]
        public static void ShowWindow()
        {
            Open();
        }

        private static BaaaWindow Open()
        {
            bool wasOpen = HasOpenInstances<BaaaWindow>();
            var win = GetWindow<BaaaWindow>(false, "BAAA", true);
            win.titleContent = new GUIContent("BAAA", Goat());
            win.minSize = new Vector2(600f, 520f);
            if (!wasOpen)
            {
                float w = Mathf.Clamp(Screen.currentResolution.width * 0.36f, 680f, 940f);
                float h = Mathf.Clamp(Screen.currentResolution.height * 0.74f, 600f, 980f);
                win.position = new Rect((Screen.currentResolution.width - w) * 0.5f, (Screen.currentResolution.height - h) * 0.5f, w, h);
            }
            win.Show();
            return win;
        }

        // from the star in the hierarchy: open on that outfit's check
        internal static void ShowOutfit(GameObject root)
        {
            BaaaWindow win = Open();
            win._selectNext = root;
            win._tab = TabCheck;
            win._scroll = Vector2.zero;
            win.RefreshNow();
            win.Focus();
        }

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += MarkDirty;
            EditorApplication.projectChanged += OnProjectChanged;
            EditorApplication.playModeStateChanged += OnPlayMode;
            Undo.undoRedoPerformed += MarkDirty;
            PrefabStage.prefabStageOpened += OnPrefabStage;
            PrefabStage.prefabStageClosing += OnPrefabStage;
            // buttons and pieces light up under the mouse
            wantsMouseMove = true;
            // a window left open from before still has the old title
            titleContent = new GUIContent("BAAA", Goat());
            RefreshNow();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkDirty;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            Undo.undoRedoPerformed -= MarkDirty;
            PrefabStage.prefabStageOpened -= OnPrefabStage;
            PrefabStage.prefabStageClosing -= OnPrefabStage;
            OutfitColors.ClearPreview();
        }

        private void OnPrefabStage(PrefabStage stage)
        {
            MarkDirty();
        }

        // a language file might be one of the things that changed
        private void OnProjectChanged()
        {
            Lang.Reload();
            MarkDirty();
        }

        private void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode) return;
            OutfitColors.ClearPreview();
            _colorsShown = false;
        }

        private void MarkDirty()
        {
            if (!_dirty) _dirtySince = EditorApplication.timeSinceStartup;
            _dirty = true;
        }

        private void RefreshNow()
        {
            _dirty = true;
            _dirtySince = 0;
        }

        // Scene edits arrive in bursts, so the checks rerun a moment after the last one
        private void Update()
        {
            UpdateTarget();
            UpdateColors();
            if (_copiedAt > 0 && EditorApplication.timeSinceStartup - _copiedAt > 4.0)
            {
                _copiedAt = -10.0;
                Repaint();
            }
            if (!_dirty || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup - _dirtySince < 0.4) return;
            Refresh();
            Repaint();
        }

        private Outfit Selected
        {
            get { return _selected >= 0 && _selected < _outfits.Count ? _outfits[_selected] : null; }
        }

        internal void Refresh()
        {
            _dirty = false;
            GameObject keep = _selectNext != null ? _selectNext : Selected != null ? Selected.Root : null;
            _selectNext = null;
            _outfits = AssistantScope.FindOutfits();
            _issues.Clear();
            foreach (var o in _outfits)
                _issues[o] = OutfitCheck.Run(o);

            _selected = 0;
            for (int i = 0; i < _outfits.Count; i++)
            {
                if (_outfits[i].Root == keep)
                {
                    _selected = i;
                    break;
                }
            }
            RefreshSelected();
        }

        private void RefreshSelected()
        {
            Outfit o = Selected;
            _scan = null;
            _rank = null;
            _compare = null;
            _extremeNotes = null;
            _pieces = null;
            _features = null;
            _colors = null;
            _colorsShown = false;
            _optimizers = null;
            _quest = null;
            _decimate = null;
            if (!_targetPending) _target = -1f;
            if (_meshPending < 0) _meshKeep = -1f;
            _versions = o != null ? Versions.For(o) : null;
            // a creators pack folder when we know it, otherwise just what the outfit uses
            _pack = o == null ? null : o.PackFolder != null ? AssistantTextures.Collect(o.PackFolder) : AssistantTextures.CollectFor(o);
            RefreshPack(true);
        }

        private void RefreshPack(bool takeCurrentChoice)
        {
            if (_pack == null)
            {
                _packNow = "";
                _pending = 0;
                _restorable = 0;
                return;
            }
            bool consistent;
            TextureChoice current;
            _packNow = AssistantTextures.Describe(_pack, out consistent, out current);
            if (takeCurrentChoice && consistent) _choice = current;
            _pending = AssistantTextures.Pending(_pack, _choice);
            _restorable = AssistantTextures.Restorable(_pack);
        }

        private void OnGUI()
        {
            BuildStyles();
            // the heavy lookups only run for the tab thats open, and before
            // layout starts so the frame stays consistent.
            if (Event.current.type == EventType.Layout)
            {
                Outfit o = Selected;
                if (o != null)
                {
                    if (_tab == TabScanner && _scan == null) _scan = OutfitScanner.Scan(o);
                    if (_tab == TabScanner && _rank == null && o.Avatar != null && o.Place == OutfitPlace.Scene)
                    {
                        _rank = Ranks.For(o.Avatar);
                        _rankWithout = Ranks.For(o.Avatar, o.Root.transform);
                    }
                    if ((_tab == TabPieces || _tab == TabFuser || _tab == TabQuest || _tab == TabDecimator) && _pieces == null) _pieces = OutfitPieces.Find(o);
                    if ((_tab == TabFuser || _tab == TabQuest || _tab == TabDecimator) && _compare == null && o.Copy != null && o.Original != null) _compare = CopyCompare.Of(o);
                    if (_tab == TabFuser && _extremeNotes == null && o.Copy == null && o.Place == OutfitPlace.Scene && Poiyomi.Installed) _extremeNotes = AtlasBuilder.ExtremeNotes(WornRenderers(o));
                    if (_tab == TabDecimator && _decimate == null && o.Copy != null) _decimate = OutfitCopies.Decimation(o);
                    if (_tab == TabFeatures && _features == null) _features = OutfitFeatures.Find(o);
                    if (_tab == TabVrcFury && _optimizers == null && o.Avatar != null && AssistantScope.VrcFuryInstalled) _optimizers = VrcFuryOptimizers.For(o.Avatar);
                    if (_tab == TabQuest && _quest == null && o.Copy != null && o.Copy.Kind == CopyKind.Quest) _quest = OutfitCopies.Quest(o);
                }
                KeepColorPreview();
            }
            DrawRect(new Rect(0, 0, position.width, position.height), ColBg);

            GUILayout.BeginHorizontal();
            GUILayout.Space(Pad);
            GUILayout.BeginVertical();
            DrawHeader();

            if (_outfits.Count == 0)
            {
                PageHeader(T("Asset Assistant"), T("No outfits *yet*"), _dirty
                    ? T("Looking for outfits...")
                    : T("Put an outfit on your avatar and it'll show up here."));
                EndFrame();
                return;
            }

            DrawOutfitPicker();
            GUILayout.Space(12);
            // a locked tab can still be the open one (reset pop-ups, or the window coming back), it shuts
            if (LockedTab(_tab)) _tab = TabCheck;
            DrawTabs();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_tab == TabCheck) DrawCheck();
            else if (_tab == TabPieces) DrawPieces();
            else if (_tab == TabColors) DrawColors();
            else if (_tab == TabScanner) DrawScanner();
            else if (_tab == TabPerformance)
            {
                PageHeader(T("Performance"), T("Make it *lighter*"), T("Texture sizes and compression."));
                DrawPerformance();
            }
            else if (_tab == TabVrcFury)
            {
                PageHeader(T("VRCFury"), T("Built-in *optimizers*"), T("They come with VRCFury and work on your whole avatar when you upload."));
                DrawOptimizations();
            }
            else if (_tab == TabFuser) DrawExtreme();
            else if (_tab == TabQuest) DrawQuest();
            else if (_tab == TabDecimator) DrawDecimator();
            else DrawFeatures();
            GUILayout.Space(16);
            EditorGUILayout.EndScrollView();

            EndFrame();
        }

        private void EndFrame()
        {
            GUILayout.EndVertical();
            GUILayout.Space(Pad);
            GUILayout.EndHorizontal();
        }

        // -------------------------------------------------------------------
        // header, outfit picker and tabs
        // -------------------------------------------------------------------

        private void DrawHeader()
        {
            GUILayout.Space(14);
            GUILayout.BeginHorizontal();
            Texture2D goat = Goat();
            if (goat != null)
            {
                Rect r = GUILayoutUtility.GetRect(26f, 28f, GUILayout.Width(26f), GUILayout.Height(28f));
                GUI.DrawTexture(new Rect(r.x, r.y + 1f, 26f, 26f), goat, ScaleMode.ScaleToFit, true);
                GUILayout.Space(6);
                // the goat can load after OnEnable when the project was still importing
                if (titleContent.image == null) titleContent = new GUIContent("BAAA", goat);
            }
            GUILayout.Label("<color=#FFD600>BAAA</color>", _styleWordmark, GUILayout.Height(28));
            GUILayout.Space(6);
            GUILayout.Label("BASIC ASSET ASSISTANT AI", _styleBrand, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            if (SecondaryButton(Lang.Now.Short)) ShowLanguages();
            GUILayout.EndHorizontal();
            GUILayout.Space(12);
        }

        private void ShowLanguages()
        {
            var menu = new GenericMenu();
            foreach (Lang.Language l in Lang.All())
            {
                string code = l.Code;
                menu.AddItem(new GUIContent(l.Name), l.Code == Lang.Now.Code, () =>
                {
                    Lang.Use(code);
                    // some styles depend on the language
                    _stylesBuilt = false;
                    AssistantHierarchy.MarkDirty();
                    RefreshNow();
                    Repaint();
                });
            }
            menu.ShowAsContext();
        }

        private void DrawOutfitPicker()
        {
            var labels = new string[_outfits.Count];
            for (int i = 0; i < _outfits.Count; i++)
            {
                int problems = CountOf(_issues[_outfits[i]], Severity.Problem);
                // A slash would turn into a submenu in the dropdown.
                labels[i] = _outfits[i].Label.Replace('/', '∕')
                    + (problems > 0 ? "   (" + Plural(problems, "{0} problem", "{0} problems") + ")" : "");
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Outfit").ToUpperInvariant(), _styleFieldLabel, GUILayout.Width(58), GUILayout.Height(28));
            int next = EditorGUILayout.Popup(_selected, labels, _stylePopup, GUILayout.Height(28));
            GUILayout.Space(8);
            if (SecondaryButton(T("Rescan"))) RefreshNow();
            GUILayout.EndHorizontal();

            if (next != _selected)
            {
                _selected = next;
                _scroll = Vector2.zero;
                RefreshSelected();
                GUIUtility.ExitGUI();
            }
        }

        // Mono labels in capitals, the open one in yellow. Wraps to a second
        // row when the window is narrow.
        private void DrawTabs()
        {
            int open = CountOf(_issues[Selected], Severity.Problem) + CountOf(_issues[Selected], Severity.Warning);
            float avail = Mathf.Max(100f, position.width - 2f * Pad);
            var widths = new float[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                widths[i] = _styleTab.CalcSize(new GUIContent(T(TabNames[i]).ToUpperInvariant())).x + 24f;
                if (i == TabCheck && open > 0) widths[i] += 22f;
            }
            var rows = new List<List<int>> { new List<int>() };
            float x = 0f;
            foreach (int i in TabOrder)
            {
                if (x + widths[i] > avail && rows[rows.Count - 1].Count > 0)
                {
                    rows.Add(new List<int>());
                    x = 0f;
                }
                rows[rows.Count - 1].Add(i);
                x += widths[i];
            }

            foreach (List<int> row in rows)
            {
                Rect line = GUILayoutUtility.GetRect(avail, 34f, GUILayout.ExpandWidth(true));
                float cx = line.x;
                foreach (int i in row)
                {
                    DrawTab(new Rect(cx, line.y, widths[i], line.height), i, open);
                    cx += widths[i];
                }
            }
            SeparatorLine();
        }

        private void DrawTab(Rect r, int i, int open)
        {
            bool active = _tab == i;
            bool hover = !active && r.Contains(Event.current.mousePosition);
            Rect label = r;
            if (i == TabCheck && open > 0)
            {
                label.width -= 22f;
                var count = new GUIContent(open > 99 ? "99" : open.ToString());
                var chip = new Rect(label.xMax - 6f, r.center.y - 8f, 18f, 16f);
                Rounded(chip, ColYellow, 0f, 8f);
                if (Event.current.type == EventType.Repaint) GUI.Label(chip, count, _styleTabCount);
            }
            if (Event.current.type == EventType.Repaint)
            {
                GUIStyle style = LockedTab(i) ? (hover ? _styleTabLockedHover : _styleTabLocked) : active ? _styleTabActive : hover ? _styleTabHover : _styleTab;
                GUI.Label(label, T(TabNames[i]).ToUpperInvariant(), style);
                if (active) DrawRect(new Rect(r.x + 10f, r.yMax - 2f, r.width - 20f, 2f), ColYellow);
            }
            if (hover) Repaint();
            if (GUI.Button(r, GUIContent.none, GUIStyle.none)) OpenTab(i);
        }

        // Fuser, Quest and Decimator stay grayed out until the advanced options agreement is accepted in this project
        internal static bool LockedTab(int tab)
        {
            return (tab == TabFuser || tab == TabQuest || tab == TabDecimator) && !AdvancedAccepted;
        }

        // every way into a tab comes through here, so a locked one asks for the agreement first
        private void OpenTab(int tab)
        {
            if (LockedTab(tab))
            {
                if (AcceptAdvanced())
                {
                    _tab = tab;
                    _scroll = Vector2.zero;
                }
                // a modal dialog went up in the middle of this frame
                GUIUtility.ExitGUI();
            }
            _tab = tab;
            _scroll = Vector2.zero;
        }

        private static string[] Ts(string[] labels)
        {
            var list = new string[labels.Length];
            for (int i = 0; i < labels.Length; i++) list[i] = T(labels[i]);
            return list;
        }

        // -------------------------------------------------------------------
        // Check
        // -------------------------------------------------------------------

        private void DrawCheck()
        {
            Outfit o = Selected;
            List<Issue> issues = _issues[o];
            PageHeader(T("Check"), T("Outfit *health*"), T("Finds what's broken and fixes it."));
            if (o.Place == OutfitPlace.PackFile)
                Notice(Tone.Neutral, null, T("This is the pack's own prefab. Put the outfit in your scene to fix things."));
            else if (o.Place == OutfitPlace.Scene && o.Avatar == null)
                Notice(Tone.Neutral, null, T("Put the outfit on your avatar to check the body blendshapes too."));

            DrawVersions();

            if (issues.Count == 0)
            {
                DrawAllGood();
            }
            else
            {
                ScanSection(issues.Count == 1 ? T("1 thing to look at") : T("{0} things to look at", issues.Count));
                foreach (Issue issue in issues) DrawIssue(issue);
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (SecondaryButton(T("Check again"))) RefreshNow();
            GUILayout.Space(6);
            if (SecondaryButton(T("Copy project info")))
            {
                EditorGUIUtility.systemCopyBuffer = ProjectReport.For(o, _pack);
                _copiedAt = EditorApplication.timeSinceStartup;
            }
            if (_copiedAt > 0)
            {
                GUILayout.Space(10);
                GUILayout.Label(T("Copied."), _styleMini, GUILayout.Height(28));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawVersions()
        {
            VersionInfo v = _versions;
            if (v == null) return;
            BeginPanel();
            PanelTitle(T("Versions"), null);
            ScanRow("VRCFury", VersionText(v.VrcFuryInstalled, v.VrcFuryPresent, v.VrcFuryOutfit, true));
            ScanRow("Poiyomi Toon", VersionText(v.PoiyomiInstalled, v.PoiyomiPresent, v.PoiyomiOutfit, false));
            GUILayout.Space(6);
            if (v.PoiyomiMajorChanged)
                GUILayout.Label(T("This outfit was made with Poiyomi {0} and you have {1}, so the color sliders might look a little different.", v.PoiyomiOutfit, v.PoiyomiInstalled), _styleWarn);
            else
                GUILayout.Label(T("Big Poiyomi updates, like 9 to 10, can change how the color sliders look."), _styleMini);
            EndPanel();
        }

        private static string VersionText(string installed, bool present, string outfit, bool saved)
        {
            string text = installed != null ? T("{0} installed", installed) : present ? T("installed, version unknown") : T("not installed");
            if (outfit != null) text += "  ·  " + (saved ? T("outfit saved with {0}", outfit) : T("outfit made with {0}", outfit));
            return text;
        }

        private void DrawAllGood()
        {
            BeginPanel(ColGreen);
            Badge(T("All good"), Tone.Good);
            GUILayout.Space(8);
            GUILayout.Label(T("Nothing's broken."), _styleNoticeText);
            GUILayout.Space(8);
            GUILayout.Label(T("This is an automated check, so it can miss things."), _styleBody);
            EndPanel();
        }

        private void DrawIssue(Issue issue)
        {
            Tone tone = issue.Severity == Severity.Problem ? Tone.Bad : issue.Severity == Severity.Warning ? Tone.Warn : Tone.Neutral;
            string word = issue.Severity == Severity.Problem ? T("Problem") : issue.Severity == Severity.Warning ? T("Warning") : T("Note");

            BeginPanel(ToneColor(tone));
            Badge(word, tone);
            GUILayout.Space(6);
            GUILayout.Label(issue.Title, _styleTitle);
            GUILayout.Space(3);
            GUILayout.Label(issue.Detail, _styleBody);

            if (issue.Fixes.Count > 0 || issue.Target != null)
            {
                GUILayout.Space(12);
                GUILayout.BeginHorizontal();
                foreach (IssueFix fix in issue.Fixes)
                {
                    if (SmallPrimaryButton(fix.Label))
                    {
                        if (fix.Choices != null) ShowChoices(fix);
                        else fix.Apply();
                        MarkDirty();
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.Space(6);
                }
                if (issue.Target != null && SecondaryButton(T("Select"))) SelectTarget(issue.Target);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            EndPanel();
        }

        private void ShowChoices(IssueFix fix)
        {
            var menu = new GenericMenu();
            foreach (var choice in fix.Choices())
            {
                System.Action run = choice.Value;
                // A slash would turn into a submenu
                menu.AddItem(new GUIContent(choice.Key.Replace('/', '∕')), false, () =>
                {
                    run();
                    MarkDirty();
                });
            }
            if (menu.GetItemCount() == 0) menu.AddDisabledItem(new GUIContent(T("Nothing to pick from on this avatar")));
            menu.ShowAsContext();
        }

        private static void SelectTarget(UnityEngine.Object target)
        {
            UnityEngine.Object obj = target;
            var c = target as Component;
            if (c != null) obj = c.gameObject;
            // Pieces inside a pack prefab can't be selected on their own, so show the prefab.
            if (EditorUtility.IsPersistent(obj) && !(obj is MonoScript))
                obj = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(obj));
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        // -------------------------------------------------------------------
        // perf Scanner
        // -------------------------------------------------------------------

        private void DrawScanner()
        {
            PageHeader(T("Perf Scanner"), T("What it *costs*"), T("What this outfit adds to your avatar. Hidden pieces count too."));
            DrawRank();
            OutfitScan s = _scan;
            if (s == null)
            {
                GUILayout.Label(T("Scanning..."), _styleBody);
                return;
            }

            ScanSection(T("This outfit"));
            Tiles(
                Tile(T("Triangles"), Number(s.Triangles)),
                Tile(T("Skinned meshes"), Number(s.SkinnedMeshes)),
                Tile(T("Material slots"), Number(s.MaterialSlots)),
                Tile(T("PhysBones"), Number(s.PhysBones)),
                Tile(T("VRAM"), Megabytes(s.TextureBytes + s.MeshBytes), T("estimated")));
            if (Selected.Avatar == null)
            {
                GUILayout.Space(8);
                GUILayout.Label(T("Put it on your avatar to see the bones it adds."), _styleMini);
            }

            ScanSection(T("Geometry"));
            ScanRow(T("Triangles"), Number(s.Triangles));
            ScanRow(T("Mesh renderers"), T("{0} skinned, {1} static", Number(s.SkinnedMeshes), Number(s.StaticMeshes)));
            ScanRow(T("Material slots"), Number(s.MaterialSlots));
            ScanRow(T("Blendshapes"), s.Blendshapes == 0
                ? T("none")
                : T("{0} on {1}, most on {2} ({3})", Number(s.Blendshapes), Plural(s.MeshesWithBlendshapes, "{0} mesh", "{0} meshes"),
                    Quote(s.MostBlendshapesMesh), Number(s.MostBlendshapes)));

            ScanSection(T("Bones"));
            ScanRow(T("Used by its meshes"), Number(s.Bones));
            Outfit o = Selected;
            string afterLink;
            if (s.LinkedToAvatar)
                afterLink = T("adds {0} to {1} and merges {2} into its own", Plural(s.BonesAdded, "{0} new bone", "{0} new bones"), Quote(o.Avatar.name), Number(s.BonesMerged));
            else if (o.Avatar != null)
                afterLink = T("can't tell, there's no Armature Link to follow");
            else
                afterLink = T("put it on your avatar to see");
            ScanRow(T("After linking"), afterLink);
            ScanRow(T("PhysBones"), s.PhysBones == 0 ? T("none") : T("{0}, moving {1}", Number(s.PhysBones), Plural(s.PhysBoneTransforms, "{0} bone", "{0} bones")));

            ScanSection(T("Textures and VRAM"));
            ScanRow(T("Estimated VRAM"), T("{0}  (textures {1}, meshes {2})", Megabytes(s.TextureBytes + s.MeshBytes), Megabytes(s.TextureBytes), Megabytes(s.MeshBytes)));
            if (s.Textures.Count == 0)
            {
                ScanRow(T("Textures"), T("none"));
            }
            else
            {
                ScanRow(T("Textures"), T("{0}, the biggest at {1} px", Plural(s.Textures.Count, "{0} texture", "{0} textures"), Number(s.Textures.Max(t => Math.Max(t.Width, t.Height)))));
                GUILayout.Space(6);
                foreach (ScannedTexture t in s.Textures)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16);
                    GUILayout.Label(t.Name, _styleValue, GUILayout.MinWidth(120));
                    GUILayout.Label(t.Width + " x " + t.Height, _styleKey, GUILayout.Width(96));
                    GUILayout.Label(t.Format, _styleKey, GUILayout.Width(120));
                    GUILayout.Label(Megabytes(t.Bytes), _styleKey, GUILayout.Width(64));
                    GUILayout.EndHorizontal();
                }
            }

            ScanSection(T("Menu"));
            if (!s.MenuRead)
            {
                ScanRow(T("Toggles"), T("can't read them without VRCFury"));
            }
            else
            {
                ScanRow(T("Toggles"), Number(s.Toggles));
                ScanRow(T("Color changers"), s.ColorChangers.Count == 0 ? T("none") : s.ColorChangers.Count + "  (" + Names(s.ColorChangers) + ")");
                if (s.OtherSliders > 0) ScanRow(T("Other sliders"), Number(s.OtherSliders));
                ScanRow(T("Separate parts"), s.Parts.Count == 0 ? T("none") : s.Parts.Count + "  (" + Names(s.Parts) + ")");
            }

            ScanSection(T("Shaders"));
            if (s.Shaders.Count == 0) ScanRow(T("Shaders"), T("none"));
            foreach (var shader in s.Shaders)
                ScanRow(shader.Key, Plural(shader.Value, "{0} material", "{0} materials"));

            if (s.Extras.Count > 0)
            {
                ScanSection(T("Other components"));
                ScanRow(T("Components"), string.Join(Lang.List, s.Extras.ToArray()));
            }
        }

        // "Hue, Hue, Saturation" -> "Hue x2, Saturation"
        private static string Names(List<string> names)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (string n in names)
            {
                int c;
                if (!counts.TryGetValue(n, out c)) order.Add(n);
                counts[n] = c + 1;
            }
            var parts = new List<string>();
            foreach (string n in order) parts.Add(counts[n] > 1 ? n + " x" + counts[n] : n);
            return string.Join(Lang.List, parts.ToArray());
        }

        // -------------------------------------------------------------------
        // VRChat rank of the whole avatar
        // -------------------------------------------------------------------

        private void DrawRank()
        {
            Outfit o = Selected;
            BeginPanel();
            PanelTitle(T("VRChat rank"), T("Your whole avatar with this outfit on. It's an estimate, VRCFury can change it a little when you upload."));
            if (o.Avatar == null || o.Place != OutfitPlace.Scene)
            {
                GUILayout.Label(T("Put the outfit on your avatar to see its rank."), _styleBody);
                EndPanel();
                return;
            }
            AvatarRank rank = _rank;
            if (rank == null)
            {
                GUILayout.Label(T("Looking at your avatar..."), _styleBody);
                EndPanel();
                return;
            }

            // big: with the outfit on. under it: the avatar on its own, so you see what the outfit costs
            AvatarRank bare = _rankWithout;
            Tiles(
                Tile(T("PC"), Ranks.Name(rank.Pc), bare != null ? T("{0} without this outfit", Ranks.Name(bare.Pc)) : null, RankTone(rank.Pc)),
                Tile(T("Quest"), Ranks.Name(rank.Quest), bare != null ? T("{0} without this outfit", Ranks.Name(bare.Quest)) : null, RankTone(rank.Quest)));
            if (rank.Quest >= Rank.Poor)
            {
                GUILayout.Space(8);
                GUILayout.Label(T("Quest hides Poor and Very Poor avatars by default."), _styleWarn);
            }
            DrawHolding(rank, false);
            DrawHolding(rank, true);
            EndPanel();
        }

        // the numbers at the avatar's worst rank, with the tab that helps
        private void DrawHolding(AvatarRank rank, bool quest)
        {
            Rank worst = quest ? rank.Quest : rank.Pc;
            if (worst == Rank.Excellent) return;
            ScanSection(quest ? T("Holding Quest back") : T("Holding PC back"));
            var better = (Rank)((int)worst - 1);
            // one button per tab, on the first line it helps with
            var offered = new HashSet<int>();
            foreach (RankLine line in rank.Lines)
            {
                if (line.Quest != quest || line.Rank != worst) continue;
                long[] limits = quest ? line.Limit.QuestLimits : line.Limit.PcLimits;
                GUILayout.BeginHorizontal(GUILayout.Height(30));
                GUILayout.Label(T(line.Limit.Name), _styleKey, GUILayout.Width(170));
                GUILayout.Label(T("{0}, {1} allows {2}", Amount(line.Value, line.Limit.Bytes), Ranks.Name(better), Amount(limits[(int)better], line.Limit.Bytes)), _styleValue);
                GUILayout.FlexibleSpace();
                int tab = FixTab(line.Limit.Fix, quest);
                if (tab >= 0 && offered.Add(tab) && SecondaryButton(T("Open {0}", T(TabNames[tab]))))
                {
                    OpenTab(tab);
                    _scroll = Vector2.zero;
                    GUIUtility.ExitGUI();
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
        }

        private int FixTab(RankFix fix, bool quest)
        {
            if (fix == RankFix.None) return -1;
            // most of quest starts with a quest version
            bool questCopy = Selected.Copy != null && Selected.Copy.Kind == CopyKind.Quest;
            if (quest && !questCopy) return TabQuest;
            switch (fix)
            {
                case RankFix.Decimator: return TabDecimator;
                case RankFix.Fuser: return quest ? TabQuest : TabFuser;
                case RankFix.Textures: return TabPerformance;
                case RankFix.Features: return quest ? TabQuest : TabFeatures;
                case RankFix.Pieces: return TabPieces;
                default: return TabQuest;
            }
        }

        private static Tone RankTone(Rank r)
        {
            switch (r)
            {
                case Rank.Excellent:
                case Rank.Good: return Tone.Good;
                case Rank.Medium: return Tone.Yellow;
                case Rank.Poor: return Tone.Warn;
                default: return Tone.Bad;
            }
        }

        private static string Amount(long value, bool bytes)
        {
            return bytes ? Megabytes(value) : Number(value);
        }

        // -------------------------------------------------------------------
        // Performance: texture quality
        // -------------------------------------------------------------------

        private void DrawPerformance()
        {
            BeginPanel();
            PanelTitle(T("Texture quality"), T("Smaller textures use less memory. Default keeps them the way they came."));
            if (_pack == null || _pack.Empty)
            {
                GUILayout.Label(T("There are no textures to change on this outfit."), _styleBody);
                EndPanel();
                return;
            }

            var covers = new List<string>
            {
                Plural(_pack.Textures.Count, "{0} texture", "{0} textures"),
                Plural(_pack.NormalMaps.Count, "{0} normal map", "{0} normal maps"),
            };
            if (_pack.Roughness.Count > 0) covers.Add(Plural(_pack.Roughness.Count, "{0} roughness map", "{0} roughness maps"));
            if (_pack.Metallic.Count > 0) covers.Add(Plural(_pack.Metallic.Count, "{0} metallic map", "{0} metallic maps"));
            covers.Add(Plural(_pack.Icons.Count, "{0} menu icon", "{0} menu icons"));
            GUILayout.Label(T("Covers {0} in {1}.", string.Join(Lang.List, covers.ToArray()), Quote(_pack.Name)), _styleRow);
            GUILayout.Label(T("Right now: {0}.", _packNow), _styleMini);
            if (_pack.OtherTool > 0)
                GUILayout.Label(T("{0} skipped because another tool manages them.", Plural(_pack.OtherTool, "{0} texture", "{0} textures")), _styleMini);

            var next = _choice;
            ScanSection(T("Textures"));
            next.Size = (SizeLevel)Segmented((int)_choice.Size, Ts(TextureSizeLabels), -1);
            GUILayout.Space(4);
            GUILayout.Label(T("Lower saves memory but can make things blurry."), _styleMini);

            ScanSection(T("Normal maps"));
            next.NormalSize = (SizeLevel)Segmented((int)_choice.NormalSize, Ts(TextureSizeLabels), -1);
            GUILayout.Space(4);
            GUILayout.Label(T("Fine detail like stitching, folds and strings. Lower flattens it."), _styleMini);

            // only when the pack has them
            if (_pack.Roughness.Count > 0)
            {
                ScanSection(T("Roughness maps"));
                next.RoughnessSize = (SizeLevel)Segmented((int)_choice.RoughnessSize, Ts(TextureSizeLabels), -1);
                GUILayout.Space(4);
                GUILayout.Label(T("How shiny or matte each spot looks. Lower softens it."), _styleMini);
            }
            if (_pack.Metallic.Count > 0)
            {
                ScanSection(T("Metallic maps"));
                next.MetallicSize = (SizeLevel)Segmented((int)_choice.MetallicSize, Ts(TextureSizeLabels), -1);
                GUILayout.Space(4);
                GUILayout.Label(T("Where the metal shine goes. Lower softens its edges."), _styleMini);
            }

            ScanSection(T("Menu icons"));
            next.IconSize = (SizeLevel)Segmented((int)_choice.IconSize, Ts(IconSizeLabels), -1);
            GUILayout.Space(4);
            GUILayout.Label(T("Your menu pictures. Medium is plenty."), _styleMini);

            ScanSection(T("Compression"));
            // Unity cant crunch High, so High waits while Crunch is on.
            next.Compression = (CompressionChoice)Segmented((int)_choice.Compression, Ts(CompressionLabels), _choice.Crunch ? (int)CompressionChoice.High : -1);
            GUILayout.Space(4);
            GUILayout.Label(CompressionHint(next.Compression), _styleMini);
            GUILayout.Space(8);
            next.Crunch = ToggleButton(T("Crunch compression"), _choice.Crunch);
            GUILayout.Label(T("Smaller download, slightly softer look. Doesn't work with High."), _styleMini);
            if (next.Crunch && next.Compression == CompressionChoice.High) next.Compression = CompressionChoice.Normal;

            if (next.Size != _choice.Size || next.NormalSize != _choice.NormalSize || next.IconSize != _choice.IconSize
                || next.RoughnessSize != _choice.RoughnessSize || next.MetallicSize != _choice.MetallicSize
                || next.Compression != _choice.Compression || next.Crunch != _choice.Crunch)
            {
                _choice = next;
                RefreshPack(false);
            }

            GUILayout.Space(16);
            bool canApply = _pending > 0;
            if (PrimaryButton(canApply ? T("Apply to {0}", Plural(_pending, "{0} texture", "{0} textures")) : T("Nothing to change"), canApply))
                ApplyQuality();
            GUILayout.Space(8);
            if (SecondaryButton(_restorable > 0 ? T("Restore as shipped ({0})", _restorable) : T("Restore as shipped"), _restorable > 0))
                RestoreQuality();
            EndPanel();
        }

        private static string CompressionHint(CompressionChoice c)
        {
            switch (c)
            {
                case CompressionChoice.Low:
                    return T("Lowest quality. Smaller on Quest, same as Normal on PC.");
                case CompressionChoice.Normal:
                    return T("Unity's standard. Most outfits ship with it.");
                case CompressionChoice.High:
                    return T("Sharpest. Can use twice the memory.");
                default:
                    return T("Keeps each texture's own setting.");
            }
        }

        private void ApplyQuality()
        {
            string pack = _pack.Name;
            string wait = _choice.Crunch ? T("Crunch is slow, so this can take a few minutes.") : T("This can take a minute.");
            bool go = EditorUtility.DisplayDialog(T("Change texture quality"),
                T("{0} in {1} will change and Unity will reimport them.", Plural(_pending, "{0} texture", "{0} textures"), Quote(pack)) + Lang.Space + wait
                + "\n\n" + T("Restore as shipped puts them back anytime."),
                T("Change them"), T("Cancel"));
            if (!go) return;

            bool cancelled;
            int planned = _pending;
            int done = AssistantTextures.Apply(_pack, _choice, out cancelled);
            if (cancelled)
                EditorUtility.DisplayDialog(T("Stopped"), T("Changed {0} of {1}. The rest weren't touched.", done, Plural(planned, "{0} texture", "{0} textures")), T("OK"));
            RefreshSelected();
            GUIUtility.ExitGUI();
        }

        private void RestoreQuality()
        {
            string pack = _pack.Name;
            bool go = EditorUtility.DisplayDialog(T("Restore as shipped"),
                T("Put {0} in {1} back the way they came?", Plural(_restorable, "{0} texture", "{0} textures"), Quote(pack)),
                T("Restore"), T("Cancel"));
            if (!go) return;

            bool cancelled;
            AssistantTextures.Restore(_pack, out cancelled);
            RefreshSelected();
            GUIUtility.ExitGUI();
        }
    }
}
#endif
