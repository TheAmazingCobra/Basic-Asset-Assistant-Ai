#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // the vrcfury tab, the fuser and quest tabs and the panel every copy shows
    public partial class BaaaWindow
    {
        private static readonly string[] ExtremeAtlasLabels = { "2K", "4K", "8K" };
        private static readonly int[] ExtremeAtlasSizes = { 2048, 4096, 8192 };
        private static readonly string[] QuestAtlasLabels = { "1K", "2K", "4K" };
        private static readonly int[] QuestAtlasSizes = { 1024, 2048, 4096 };

        private static readonly string ExtremeTip = N("Toggles and color changers stop working. The colors you picked stay. Pick your pieces first. You can go back anytime.");
        private static readonly string QuestTip = N("No toggles or color changers on Quest. The colors you picked stay. Pick your pieces first. Your PC version keeps everything.");
        // shown wherever the tool atlases or decimates
        private static readonly string OwnRisk = N("This is an advanced setting and it can break your outfit. BAAA isn't responsible for outfit errors, so please don't ask the creator's support to fix them.");

        // VRChat's Quest limits for a Poor avatar, for the whole avatar, body included
        private const int QuestTriangles = 20000;
        private const int QuestPhysBones = 8;
        private const int QuestSkinnedMeshes = 2;
        private const int QuestMaterialSlots = 4;
        private const long QuestTextureBytes = 40L * 1048576L;

        private int _extremeAtlas = 2048;
        private int _questAtlas = 1024;
        private List<Optimizer> _optimizers;
        private QuestInfo _quest;
        private CopyCompare _compare;
        private List<ExtremeNote> _extremeNotes;
        // the outfit to show after the next refresh, like a copy that was just made
        [NonSerialized] private GameObject _selectNext;
        // unity only finds out about a newly installed module after a restart, so once is enough
        private static bool? _androidModule;

        // -------------------------------------------------------------------
        // VRCFury optimizations
        // -------------------------------------------------------------------

        private void DrawOptimizations()
        {
            BeginPanel();
            // the page header above already says what they are
            PanelTitle(T("VRCFury optimizations"), null);

            Outfit o = Selected;
            if (!AssistantScope.VrcFuryInstalled)
            {
                GUILayout.Label(T("VRCFury isn't installed. The Check tab shows how to fix that."), _styleWarn);
                EndPanel();
                return;
            }
            if (o.Place != OutfitPlace.Scene || o.Avatar == null)
            {
                GUILayout.Label(T("Put the outfit on your avatar to add these."), _styleBody);
                EndPanel();
                return;
            }
            if (_optimizers == null)
            {
                GUILayout.Label(T("Looking at your avatar..."), _styleBody);
                EndPanel();
                return;
            }

            foreach (Optimizer opt in _optimizers)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(opt.Title, _styleTitleLine);
                GUILayout.FlexibleSpace();
                if (!opt.Installed) Badge(T("Not available"), Tone.Neutral);
                else if (opt.OnAvatar) Badge(T("On avatar"), Tone.Good);
                else Badge(T("To add"), Tone.Yellow);
                GUILayout.EndHorizontal();
                GUILayout.Space(2);
                GUILayout.Label(T(opt.Why), _styleMini);
                GUILayout.Space(10);
            }
            // not one we add, vrcfury turns it on by itself when the avatar goes over the parameter limit
            GUILayout.BeginHorizontal();
            GUILayout.Label("Parameter Compressor", _styleTitleLine);
            GUILayout.FlexibleSpace();
            Badge(T("On by default"), Tone.Good);
            GUILayout.EndHorizontal();
            GUILayout.Space(2);
            GUILayout.Label(T("VRCFury handles parameter compression by itself now."), _styleMini);

            GUILayout.Space(14);
            int missing = _optimizers.FindAll(x => x.Installed && !x.OnAvatar).Count;
            if (PrimaryButton(missing > 0 ? T("Apply VRCFury optimizations") : T("All optimizations are on"), missing > 0))
            {
                bool go = EditorUtility.DisplayDialog(T("Apply VRCFury optimizations"),
                    T("{0} go on {1}, in an object called {2}. They affect your whole avatar.", Plural(missing, "{0} optimizer", "{0} optimizers"), Quote(o.Avatar.name), Quote(VrcFuryOptimizers.HolderName))
                    + "\n\n" + T("Ctrl+Z removes them."),
                    T("Apply"), T("Cancel"));
                if (go)
                {
                    VrcFuryOptimizers.Apply(o.Avatar);
                    _optimizers = null;
                    MarkDirty();
                }
                GUIUtility.ExitGUI();
            }
            EndPanel();
        }

        // -------------------------------------------------------------------
        // Extreme
        // -------------------------------------------------------------------

        // the fuser tab. the fused version is one mesh with every material as it was,
        // the extreme version is one mesh and one material on an atlas
        private void DrawExtreme()
        {
            Outfit o = Selected;
            PageHeader(T("Fuser"), T("One *mesh*"), T("Fuses the outfit into one mesh. Extreme also puts every texture on one atlas."));
            if (o.Copy != null)
            {
                if (o.Copy.Kind == CopyKind.Extreme || o.Copy.Kind == CopyKind.Fused)
                    DrawCopyPanel(o);
                else if (o.Copy.Kind == CopyKind.Quest)
                    Notice(Tone.Neutral, null, T("This is the {0} version. Go back to the original in the Quest tab to fuse it.", KindName(o.Copy.Kind)));
                else
                    Notice(Tone.Neutral, null, T("This is the {0} version. Go back to the original in the Decimator tab to fuse it.", KindName(o.Copy.Kind)));
                return;
            }

            Notice(Tone.Yellow, T("Heads up"), T(ExtremeTip));
            Notice(Tone.Warn, T("At your own risk"), T(OwnRisk));
            string blocked = CopyBlocked(o, CopyKind.Fused);
            if (blocked != null)
            {
                GUILayout.Space(4);
                GUILayout.Label(blocked, _styleWarn);
                return;
            }
            WearingLine(T("Pick them in the Pieces tab."));
            GUILayout.Space(10);

            BeginPanel();
            PanelTitle(T("Fused version"), T("One mesh. Every material stays as it is."));
            if (PrimaryButton(T("Make fused version"), true))
            {
                if (EditorUtility.DisplayDialog(T("Make fused version"), T(ExtremeTip) + "\n\n" + T(OwnRisk) + "\n\n" + T("This can take a minute."), T("Make it"), T("Cancel")))
                    RunMake(o, CopyKind.Fused, 0);
                GUIUtility.ExitGUI();
            }
            EndPanel();

            BeginPanel();
            PanelTitle(T("Extreme version"), T("One mesh and one material. Every texture goes on one atlas."));
            if (!Poiyomi.Installed)
            {
                GUILayout.Label(T("The Extreme version needs Poiyomi installed. The Check tab shows how to get it."), _styleWarn);
                EndPanel();
                return;
            }
            if (_extremeNotes != null)
            {
                ScanSection(T("What changes"));
                if (_extremeNotes.Count == 0) GUILayout.Label(T("Every material fits on the atlas as it is."), _styleRow);
                foreach (ExtremeNote n in _extremeNotes) GUILayout.Label("·  " + n.Text, _styleRow);
            }
            ScanSection(T("Atlas size"));
            _extremeAtlas = PickSize(_extremeAtlas, ExtremeAtlasSizes, ExtremeAtlasLabels);
            GUILayout.Space(4);
            GUILayout.Label(T("2K is the lightest, 8K the sharpest. The atlas only gets as big as it needs."), _styleMini);

            GUILayout.Space(16);
            if (PrimaryButton(T("Make Extreme version"), true))
            {
                if (EditorUtility.DisplayDialog(T("Make Extreme version"), T(ExtremeTip) + "\n\n" + T(OwnRisk) + "\n\n" + T("This can take a minute."), T("Make it"), T("Cancel")))
                    RunMake(o, CopyKind.Extreme, _extremeAtlas);
                GUIUtility.ExitGUI();
            }
            EndPanel();
        }

        // segmented size picker, returns the size in pixels
        private int PickSize(int current, int[] sizes, string[] labels)
        {
            int at = Math.Max(0, Array.IndexOf(sizes, current));
            return sizes[Segmented(at, labels, -1)];
        }

        private void WearingLine(string hint)
        {
            if (_pieces == null || _pieces.Count == 0) return;
            int kept = _pieces.FindAll(p => !p.Dropped).Count;
            GUILayout.Space(4);
            GUILayout.Label(T("Wearing {0} of {1}.", kept, Plural(_pieces.Count, "{0} piece", "{0} pieces")) + Lang.Space + hint, _styleRow);
        }

        private static string CopyBlocked(Outfit o, CopyKind kind)
        {
            if (kind != CopyKind.Decimated && !AssistantScope.VrcFuryInstalled) return T("VRCFury isn't installed, so this outfit's pieces can't be read. The Check tab shows how to fix that.");
            if (o.Place == OutfitPlace.PackFile) return T("This is the pack's own prefab. Put the outfit in your scene first.");
            if (o.Place != OutfitPlace.Scene) return T("Copies are made from outfits in your scene, not from the pack's own prefab.");
            if (kind == CopyKind.Quest && AtlasBuilder.QuestShader() == null)
                return T("VRChat's Quest shaders aren't in this project. Add the VRChat SDK with the Creator Companion first.");
            return null;
        }

        private static string KindName(CopyKind kind)
        {
            if (kind == CopyKind.Decimated) return T("decimated");
            return kind == CopyKind.Fused ? T("fused") : OutfitCopies.Label(kind);
        }

        // the Fused, Extreme, Quest and Decimated versions are advanced, so the Fuser, Quest and Decimator tabs stay
        // grayed out until someone says they understand we don't support them. asked once per project: the
        // answer sits in the project's UserSettings folder, which never ships with a pack
        internal const string AdvancedAcceptedKey = "BAAA.AdvancedAccepted";

        internal static bool AdvancedAccepted
        {
            get { return EditorUserSettings.GetConfigValue(AdvancedAcceptedKey) == "1"; }
            set { EditorUserSettings.SetConfigValue(AdvancedAcceptedKey, value ? "1" : ""); }
        }

        internal static bool AcceptAdvanced()
        {
            if (AdvancedAccepted) return true;
            bool accepted = EditorUtility.DisplayDialog(T("Advanced options"),
                T("I understand these are advanced options and support will NOT help me if I use them.") + "\n\n"
                + T("The Asset Assistant is a standalone helper and isn't included in my purchase. It was made to help new users."),
                T("I understand"), T("Cancel"));
            if (accepted) AdvancedAccepted = true;
            return accepted;
        }

        // every pop-up that only asks once asks again in this project, for testing. the support popup has
        // its own "Show Popup Again"
        [MenuItem("BAAA/Reset Pop-ups")]
        public static void ResetPopups()
        {
            ClearRememberedPopups();
            EditorUtility.DisplayDialog(T("Pop-ups reset"), T("The Asset Assistant will ask again in this project."), T("OK"));
        }

        internal static void ClearRememberedPopups()
        {
            AdvancedAccepted = false;
        }

        private void RunMake(Outfit o, CopyKind kind, int atlas)
        {
            GameObject made;
            string error = OutfitCopies.Make(o, kind, atlas, null, out made);
            AfterCopyChange(made);
            if (error != null)
                EditorUtility.DisplayDialog(T("Couldn't make the {0} version", KindName(kind)), error, T("OK"));
        }

        // the meshes a copy would fuse: what shows now, dropped pieces left out
        private static List<Renderer> WornRenderers(Outfit o)
        {
            var list = new List<Renderer>();
            foreach (Renderer r in o.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (AssistantScope.InsideEditorOnly(r.transform) || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                list.Add(r);
            }
            return list;
        }

        private void AfterCopyChange(GameObject select)
        {
            _selectNext = select;
            _extremeNotes = null;
            _pieces = null;
            _features = null;
            _scan = null;
            _quest = null;
            _decimate = null;
            _optimizers = null;
            _compare = null;
            RefreshNow();
        }

        // What every copy shows: where it came from, what it saved, and the way back
        private void DrawCopyPanel(Outfit o)
        {
            CopyKind kind = o.Copy.Kind;
            BeginPanel(ColYellow);
            Badge(T("{0} version", KindName(kind)), Tone.Yellow);
            GUILayout.Space(8);
            GUILayout.Label(T("Made from {0}", Quote(o.Copy.OriginalName)), _styleTitle);
            GUILayout.Space(3);
            string what = kind == CopyKind.Extreme
                ? T("One mesh and one material, no toggles or color changers.")
                : kind == CopyKind.Fused
                    ? T("One mesh with its materials as they were, no toggles or color changers.")
                    : kind == CopyKind.Quest
                        ? T("Quest shader, one mesh, no toggles. Go back before you upload for PC.")
                        : T("Fewer triangles. Everything else still works.");
            GUILayout.Label(what, _styleBody);

            if (OutfitCopies.OutOfDate(o.Copy))
            {
                GUILayout.Space(10);
                Notice(Tone.Warn, T("Out of date"), T("The pack was updated. Make it again to catch up."));
            }
            if (o.Original == null)
            {
                GUILayout.Space(10);
                Notice(Tone.Bad, T("Original missing"), T("The original isn't next to it anymore."));
            }
            DrawCompare();

            GUILayout.Space(12);
            GUILayout.BeginHorizontal();
            if (SecondaryButton(T("Make it again"), o.Original != null))
            {
                GameObject made;
                string error = OutfitCopies.Rebuild(o, out made);
                AfterCopyChange(made != null ? made : o.Original);
                if (error != null) EditorUtility.DisplayDialog(T("Couldn't make it again"), error, T("OK"));
                GUIUtility.ExitGUI();
            }
            GUILayout.Space(6);
            if (SecondaryButton(kind == CopyKind.Quest ? T("Go back to PC version") : T("Go back to the original"), o.Original != null))
            {
                GameObject original = o.Original;
                OutfitCopies.GoBack(o);
                AfterCopyChange(original);
                GUIUtility.ExitGUI();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label(T("Saved in {0}.", Path.GetDirectoryName(o.PrefabPath).Replace('\\', '/')), _styleMini);
            EndPanel();
        }

        // the original's numbers next to the copy's
        private void DrawCompare()
        {
            CopyCompare c = _compare;
            if (c == null) return;
            ScanSection(T("Before and after"));
            CompareRow(T("Triangles"), c.Before.Triangles, c.After.Triangles, false);
            CompareRow(T("Skinned meshes"), c.Before.SkinnedMeshes, c.After.SkinnedMeshes, false);
            CompareRow(T("Material slots"), c.Before.MaterialSlots, c.After.MaterialSlots, false);
            CompareRow(T("PhysBones"), c.Before.PhysBones, c.After.PhysBones, false);
            CompareRow(T("Texture memory"), c.Before.TextureBytes, c.After.TextureBytes, true);
        }

        private void CompareRow(string label, long before, long after, bool bytes)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _styleKey, GUILayout.Width(150));
            GUILayout.Label(Amount(before, bytes), _styleKey, GUILayout.Width(84));
            GUILayout.Label("→", _styleKey, GUILayout.Width(20));
            GUILayout.Label(Amount(after, bytes), _styleValue, GUILayout.Width(84));
            if (after == before)
            {
                GUILayout.Label(T("same"), _styleMini);
            }
            else
            {
                long change = before > 0 ? (long)Math.Round((after - before) * 100.0 / before) : 100;
                GUILayout.Label((change > 0 ? "+" : "") + change + "%", after < before ? _styleGood : _styleWarn);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(3);
        }

        // -------------------------------------------------------------------
        // Quest
        // -------------------------------------------------------------------

        private void DrawQuest()
        {
            Outfit o = Selected;
            PageHeader(T("Quest"), T("Ready for *Quest*"), T("Makes a Quest copy and swaps it in. Your PC version stays the same."));
            if (o.Copy != null && o.Copy.Kind == CopyKind.Quest)
            {
                DrawQuestCopy(o);
                return;
            }
            if (o.Copy != null)
            {
                if (o.Copy.Kind == CopyKind.Extreme || o.Copy.Kind == CopyKind.Fused)
                    Notice(Tone.Neutral, null, T("This is the {0} version. Go back to the original in the Fuser tab to make a Quest version.", KindName(o.Copy.Kind)));
                else
                    Notice(Tone.Neutral, null, T("This is the {0} version. Go back to the original in the Decimator tab to make a Quest version.", KindName(o.Copy.Kind)));
                return;
            }

            BeginPanel();
            PanelTitle(T("Quest version"), T("One mesh, one atlas, VRChat's Quest shader."));
            Notice(Tone.Yellow, T("Heads up"), T(QuestTip));
            Notice(Tone.Warn, T("At your own risk"), T(OwnRisk));

            string blocked = CopyBlocked(o, CopyKind.Quest);
            if (blocked != null)
            {
                GUILayout.Space(4);
                GUILayout.Label(blocked, _styleWarn);
                EndPanel();
                DrawQuestUpload();
                return;
            }
            WearingLine(T("Pick them in the Pieces tab."));

            ScanSection(T("Atlas size"));
            _questAtlas = PickSize(_questAtlas, QuestAtlasSizes, QuestAtlasLabels);
            GUILayout.Space(4);
            // 4k on quest eats alot of headset memory, so the hint turns orange when its picked
            if (_questAtlas == 4096)
                GUILayout.Label(T("4K usually isn't recommended for Quest. It uses a lot of memory on a headset."), _styleWarn);
            else
                GUILayout.Label(T("1K is lighter, 2K is sharper. 4K usually isn't recommended for Quest."), _styleMini);
            GUILayout.Space(8);
            GUILayout.Label(T("Physbones start off. Your base avatar needs its own Quest version too."), _styleMini);

            GUILayout.Space(16);
            if (PrimaryButton(T("Make Quest version"), true))
            {
                if (EditorUtility.DisplayDialog(T("Make Quest version"), T(QuestTip) + "\n\n" + T(OwnRisk) + "\n\n" + T("This can take a minute."), T("Make it"), T("Cancel")))
                    RunMake(o, CopyKind.Quest, _questAtlas);
                GUIUtility.ExitGUI();
            }
            EndPanel();
            DrawQuestUpload();
        }

        private void DrawQuestCopy(Outfit o)
        {
            QuestInfo q = _quest;
            if (q == null)
            {
                GUILayout.Label(T("Looking at the Quest version..."), _styleBody);
                return;
            }

            Tiles(
                Tile(T("Triangles"), Number(q.Triangles), T("limit {0}", Number(QuestTriangles)), q.Triangles > QuestTriangles ? Tone.Warn : Tone.Yellow),
                Tile(T("PhysBones"), Number(q.On.Count), T("limit {0}", Number(QuestPhysBones)), q.On.Count > QuestPhysBones ? Tone.Warn : Tone.Yellow),
                Tile(T("Skinned meshes"), Number(q.SkinnedMeshes), T("limit {0}", Number(QuestSkinnedMeshes)), q.SkinnedMeshes > QuestSkinnedMeshes ? Tone.Warn : Tone.Yellow),
                Tile(T("Material slots"), Number(q.MaterialSlots), T("limit {0}", Number(QuestMaterialSlots)), q.MaterialSlots > QuestMaterialSlots ? Tone.Warn : Tone.Yellow),
                Tile(T("VRAM"), Megabytes(q.TextureBytes), T("limit {0}", Megabytes(QuestTextureBytes)), q.TextureBytes > QuestTextureBytes ? Tone.Warn : Tone.Yellow));
            GUILayout.Space(8);
            GUILayout.Label(T("These are Quest's Poor limits, body included. Quest hides Poor and Very Poor avatars by default, so lower is better."), _styleMini);
            if (q.Triangles > QuestTriangles || q.On.Count > QuestPhysBones || q.TextureBytes > QuestTextureBytes)
            {
                GUILayout.Space(8);
                Notice(Tone.Warn, null, T("This outfit alone goes over a limit."));
            }

            ScanSection(T("Triangles"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("{0} of {1} triangles. Cut them down in the Decimator.", Number(q.Triangles), Number(q.FullTriangles)), _styleRow);
            GUILayout.Space(8);
            if (SecondaryButton(T("Open {0}", T(TabNames[TabDecimator])))) OpenTab(TabDecimator);
            GUILayout.EndHorizontal();

            ScanSection(T("PhysBones"));
            GUILayout.Label(T("All off to start. Quest allows {0} for your whole avatar.", QuestPhysBones), _styleMini);
            GUILayout.Space(8);
            DrawQuestPhysBones(o, q);

            GUILayout.Space(16);
            DrawCopyPanel(o);
            DrawQuestUpload();
        }

        private void DrawQuestPhysBones(Outfit o, QuestInfo q)
        {
            List<CopyPhysBone> all = o.Copy.PhysBones;
            if (all.Count == 0)
            {
                GUILayout.Label(T("This outfit has no physbones."), _styleRow);
                return;
            }
            var groups = new List<string>();
            foreach (CopyPhysBone p in all)
                if (p.Group.Length > 0 && !groups.Contains(p.Group)) groups.Add(p.Group);
            groups.Sort(StringComparer.OrdinalIgnoreCase);
            if (all.Exists(p => p.Group.Length == 0)) groups.Add("");

            BeginPanel();
            bool firstGroup = true;
            foreach (string group in groups)
            {
                if (!firstGroup) GUILayout.Space(10);
                firstGroup = false;
                GUILayout.Label((group.Length > 0 ? group : T("Other")).ToUpperInvariant(), _styleSection);
                GUILayout.Space(4);
                foreach (CopyPhysBone p in all)
                {
                    if (p.Group != group) continue;
                    Rect r = GUILayoutUtility.GetRect(10f, 28f, GUILayout.ExpandWidth(true));
                    Rect sw = SwitchRect(r);
                    bool on = q.On.Contains(p.Id);
                    DimLabel(new Rect(r.x, r.y, Mathf.Max(0f, sw.x - r.x - 12f), r.height), p.Name, _styleItem, !on);
                    if (PillSwitch(sw, on ? 2 : 0, true))
                    {
                        OutfitCopies.SetPhysBone(o, p.Id, !on);
                        _quest = null;
                        _compare = null;
                        MarkDirty();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            EndPanel();
        }

        // how a quest upload goes, and the android module it needs
        private void DrawQuestUpload()
        {
            if (_androidModule == null) _androidModule = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
            GUILayout.Space(4);
            BeginPanel();
            PanelTitle(T("Uploading for Quest"), null);
            if (_androidModule == false)
            {
                Notice(Tone.Warn, T("Android module missing"),
                    T("Unity's Android module isn't installed, and Quest uploads need it. In Unity Hub, open Installs, click the gear next to {0} and add Android Build Support.", Application.unityVersion));
                GUILayout.Space(6);
            }
            Step(1, T("Upload your PC version first."));
            Step(2, T("Make the Quest version here. It swaps in for this outfit."));
            Step(3, T("Switch Unity to Android (File, Build Settings) and upload the same avatar again."));
            Step(4, T("Go back to the PC version before your next PC upload."));
            EndPanel();
        }
    }
}
#endif
