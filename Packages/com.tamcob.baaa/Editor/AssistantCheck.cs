#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace BAAA
{
    internal enum Severity { Problem, Warning, Note }

    internal enum RefState { Set, Empty, Missing }

    internal sealed class IssueFix
    {
        public string Label;
        public Action Apply;
        // when set, the window shows these as a menu and runs the one picked.
        public Func<List<KeyValuePair<string, Action>>> Choices;
    }

    internal sealed class Issue
    {
        public Severity Severity;
        public string Title;
        public string Detail;
        public UnityEngine.Object Target;
        public readonly List<IssueFix> Fixes = new List<IssueFix>();
    }

    internal sealed class VfFeature
    {
        public Component Component;
        public SerializedProperty Prop;
        public string Type;
    }

    // reads vrcfury components thru unitys saved data instead of vrcfurys own classes so nothing here
    // breaks when vrcfury is missing or changes. field names match vrcfury 1.1429
    internal static class VrcFuryData
    {
        // Components inside EditorOnly objects are left out: VRCFury skips them too,
        // and thats where dropped pieces keep their toggles on unpacked outfits.
        public static List<VfFeature> Features(GameObject root)
        {
            var list = new List<VfFeature>();
            foreach (var c in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!AssistantScope.IsVrcFury(c) || AssistantScope.InsideEditorOnly(c.transform)) continue;
                var so = new SerializedObject(c);
                var content = so.FindProperty("content");
                if (IsManaged(content))
                    list.Add(new VfFeature { Component = c, Prop = content, Type = ShortType(content.managedReferenceFullTypename) });

                // Older VRCFury kept a list of features on one component
                var legacy = so.FindProperty("config.features");
                if (legacy == null || !legacy.isArray) continue;
                for (int i = 0; i < legacy.arraySize; i++)
                {
                    var f = legacy.GetArrayElementAtIndex(i);
                    if (IsManaged(f))
                        list.Add(new VfFeature { Component = c, Prop = f, Type = ShortType(f.managedReferenceFullTypename) });
                }
            }
            return list;
        }

        public static string FeatureType(Component c)
        {
            var content = new SerializedObject(c).FindProperty("content");
            return IsManaged(content) ? ShortType(content.managedReferenceFullTypename) : null;
        }

        public static bool IsManaged(SerializedProperty p)
        {
            return p != null && p.propertyType == SerializedPropertyType.ManagedReference
                && !string.IsNullOrEmpty(p.managedReferenceFullTypename);
        }

        // "VRCFury VF.Model.Feature.Toggle" -> "Toggle"
        public static string ShortType(string fullTypename)
        {
            if (string.IsNullOrEmpty(fullTypename)) return "";
            int cut = Math.Max(fullTypename.LastIndexOf('.'), Math.Max(fullTypename.LastIndexOf(' '), fullTypename.LastIndexOf('/')));
            return fullTypename.Substring(cut + 1);
        }

        public static IEnumerable<SerializedProperty> Actions(SerializedProperty state)
        {
            var actions = state != null ? state.FindPropertyRelative("actions") : null;
            if (actions == null || !actions.isArray) yield break;
            for (int i = 0; i < actions.arraySize; i++)
            {
                var a = actions.GetArrayElementAtIndex(i);
                if (IsManaged(a)) yield return a;
            }
        }

        public static RefState StateOf(SerializedProperty p)
        {
            if (p == null || p.propertyType != SerializedPropertyType.ObjectReference) return RefState.Empty;
            if (p.objectReferenceValue != null) return RefState.Set;
            return p.objectReferenceInstanceIDValue != 0 ? RefState.Missing : RefState.Empty;
        }

        // VRCFury asset slots (like menu icons) hold a direct link plus an
        // "id" of guid|path it falls back to, so a stale link alone is fine
        public static RefState AssetSlotState(SerializedProperty slot)
        {
            if (slot == null) return RefState.Empty;
            var objRef = slot.FindPropertyRelative("objRef");
            RefState direct = StateOf(objRef);
            if (direct == RefState.Set) return RefState.Set;

            var idProp = slot.FindPropertyRelative("id");
            string id = idProp != null ? idProp.stringValue : "";
            if (string.IsNullOrEmpty(id)) return direct;

            string guid = id.Split('|', ':')[0];
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.LoadMainAssetAtPath(path) != null) return RefState.Set;
            int bar = id.IndexOf('|');
            if (bar >= 0 && AssetDatabase.LoadMainAssetAtPath(id.Substring(bar + 1)) != null) return RefState.Set;
            return RefState.Missing;
        }

        public static bool Bool(SerializedProperty parent, string name, bool fallback = false)
        {
            var p = parent.FindPropertyRelative(name);
            return p != null && p.propertyType == SerializedPropertyType.Boolean ? p.boolValue : fallback;
        }

        public static string Text(SerializedProperty parent, string name)
        {
            var p = parent.FindPropertyRelative(name);
            return p != null && p.propertyType == SerializedPropertyType.String ? p.stringValue : "";
        }

        // Toggle names are menu paths: "Clothes/Outfit/Toggle Hood" -> "Toggle Hood"
        public static string MenuName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "(unnamed)";
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }
    }

    // poiyomi through reflection, same way vrcfury does it
    internal static class Poiyomi
    {
        private static bool _searched;
        private static bool _installed;
        private static MethodInfo _renameSuffix;
        private static MethodInfo _usesOptimizer;

        private static void Find()
        {
            if (_searched) return;
            _searched = true;
            Type optimizer = AssistantScope.FindType("Thry.ThryEditor.ShaderOptimizer") ?? AssistantScope.FindType("Thry.ShaderOptimizer");
            if (optimizer == null) return;
            _installed = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            _renameSuffix = optimizer.GetMethod("GetRenamedPropertySuffix", flags, null, new[] { typeof(Material) }, null);
            _usesOptimizer = optimizer.GetMethod("IsShaderUsingThryOptimizer", flags, null, new[] { typeof(Shader) }, null);
        }

        public static bool Installed
        {
            get { Find(); return _installed; }
        }

        public static bool IsLocked(Material m)
        {
            return m != null && m.shader != null && m.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal);
        }

        public static bool IsUnlocked(Material m)
        {
            if (m == null || m.shader == null || IsLocked(m)) return false;
            Find();
            if (_usesOptimizer != null)
            {
                try { return (bool)_usesOptimizer.Invoke(null, new object[] { m.shader }); }
                catch (Exception) { }
            }
            return m.shader.name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string RenameSuffix(Material m)
        {
            Find();
            if (_renameSuffix != null)
            {
                try { return (string)_renameSuffix.Invoke(null, new object[] { m }); }
                catch (Exception) { }
            }
            return Clean(m.GetTag("thry_rename_suffix", false, m.name));
        }

        // Poiyomi's own cleaning (ShaderOptimizer.CleanStringForPropertyNames),
        // used when Poiyomi is not there to ask
        private static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(s.Trim().Replace(" ", "")))
            {
                bool letter = b >= 65 && b <= 122 && b != 91 && b != 92 && b != 93 && b != 94 && b != 96;
                bool digit = b >= 48 && b <= 57;
                if (letter || digit) sb.Append((char)b);
                else sb.Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        public static string BaseName(string property)
        {
            int dot = property.IndexOf('.');
            return dot > 0 ? property.Substring(0, dot) : property;
        }

        // True when the material has this property now, or will once Poiyomi
        // locks it (renamed animated properties only exist on locked materials)
        public static bool WillHave(Material m, string property)
        {
            if (m == null || m.shader == null || string.IsNullOrEmpty(property)) return false;
            string prop = BaseName(property);
            if (m.HasProperty(prop)) return true;
            if (!IsUnlocked(m)) return false;

            string suffix = RenameSuffix(m);
            if (string.IsNullOrEmpty(suffix) || !prop.EndsWith("_" + suffix, StringComparison.Ordinal)) return false;
            string original = prop.Substring(0, prop.Length - suffix.Length - 1);
            return m.HasProperty(original) && m.GetTag(original + "Animated", false, "") == "2";
        }
    }

    internal sealed class VersionInfo
    {
        public bool VrcFuryPresent;
        public string VrcFuryInstalled;
        public string VrcFuryOutfit;
        public bool PoiyomiPresent;
        public string PoiyomiInstalled;
        public string PoiyomiOutfit;
        public bool PoiyomiMajorChanged;
    }

    // which vrcfury and poiyomi versions are installed vs what the outfit was made with.
    // vrcfury stamps its version on every component, poiyomi writes it into each locked shader
    internal static class Versions
    {
        private static readonly Regex VersionNumber = new Regex(@"\d+(\.\d+)+");

        public static VersionInfo For(Outfit outfit)
        {
            var info = new VersionInfo
            {
                VrcFuryPresent = AssistantScope.VrcFuryInstalled,
                VrcFuryInstalled = PackageVersion("com.vrcfury.vrcfury"),
                PoiyomiPresent = Poiyomi.Installed,
                PoiyomiInstalled = PackageVersion("com.poiyomi.toon") ?? ShaderVersion(Shader.Find(".poiyomi/Poiyomi Toon")),
            };
            if (outfit != null && outfit.Root != null)
            {
                var vrcfury = new List<string>();
                var poiyomi = new List<string>();
                foreach (var c in outfit.Root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (!AssistantScope.IsVrcFury(c)) continue;
                    var p = new SerializedObject(c).FindProperty("vrcfuryVersion");
                    if (p != null && !string.IsNullOrEmpty(p.stringValue)) vrcfury.Add(p.stringValue);
                }
                foreach (var r in outfit.Root.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (Poiyomi.IsLocked(m)) poiyomi.Add(ShaderVersion(m.shader));
                info.VrcFuryOutfit = Highest(vrcfury);
                info.PoiyomiOutfit = Highest(poiyomi);
            }
            int installed = Major(info.PoiyomiInstalled);
            int made = Major(info.PoiyomiOutfit);
            info.PoiyomiMajorChanged = installed > 0 && made > 0 && installed != made;
            return info;
        }

        internal static string PackageVersion(string name)
        {
            var p = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + name);
            return p != null && !string.IsNullOrEmpty(p.version) ? p.version : null;
        }

        // Poiyomi shaders carry a label like "<color=#E75898ff>Poiyomi 9.2.74</color>"
        private static string ShaderVersion(Shader shader)
        {
            if (shader == null) return null;
            int i = shader.FindPropertyIndex("shader_master_label");
            if (i < 0) return null;
            Match m = VersionNumber.Match(shader.GetPropertyDescription(i));
            return m.Success ? m.Value : null;
        }

        private static string Highest(List<string> versions)
        {
            string best = null;
            Version bestParsed = null;
            foreach (string v in versions)
            {
                Version parsed;
                if (v == null || !Version.TryParse(v, out parsed)) continue;
                if (bestParsed == null || parsed > bestParsed)
                {
                    best = v;
                    bestParsed = parsed;
                }
            }
            return best;
        }

        public static int Major(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            int dot = version.IndexOf('.');
            int major;
            return int.TryParse(dot > 0 ? version.Substring(0, dot) : version, out major) ? major : 0;
        }
    }

    // problems with the whole project, not one outfit. any of them stops the upload, so every
    // outfit's check shows them
    internal static class ProjectCheck
    {
        private static List<string> _vrcFury;
        private static List<string> _poiyomi;

        static ProjectCheck()
        {
            EditorApplication.projectChanged += () =>
            {
                _vrcFury = null;
                _poiyomi = null;
            };
        }

        public static void AddTo(List<Issue> issues)
        {
            if (EditorUtility.scriptCompilationFailed)
            {
                var errors = new Issue
                {
                    Severity = Severity.Problem,
                    Title = Lang.T("Your project has script errors"),
                    Detail = Lang.T("Unity can't upload until they're fixed. They're the red errors in the Console, often from another tool."),
                };
                errors.Fixes.Add(new IssueFix { Label = Lang.T("Open the Console"), Apply = () => EditorApplication.ExecuteMenuItem("Window/General/Console") });
                issues.Add(errors);
            }

            List<string> vrcFury = VrcFuryCopies();
            if (vrcFury.Count > 1)
                issues.Add(Twice(Lang.T("VRCFury is in this project twice"),
                    Lang.T("Two copies break it. Delete the one in {0} and keep the one from the Creator Companion.", Extra(vrcFury, "/Runtime/")), vrcFury));
            List<string> poiyomi = PoiyomiCopies();
            if (poiyomi.Count > 1)
                issues.Add(Twice(Lang.T("Poiyomi is in this project twice"),
                    Lang.T("Two copies can break its shaders. Delete the one in {0} and keep the one from the Creator Companion.", Extra(poiyomi, "/Scripts/")), poiyomi));
        }

        // one path per copy, found by a script only that tool has
        public static List<string> VrcFuryCopies()
        {
            if (_vrcFury == null) _vrcFury = Copies("VRCFury t:MonoScript", "/Model/VRCFury.cs");
            return _vrcFury;
        }

        public static List<string> PoiyomiCopies()
        {
            if (_poiyomi == null) _poiyomi = Copies("ShaderOptimizer t:MonoScript", "/ThryEditor/Editor/ShaderOptimizer.cs");
            return _poiyomi;
        }

        private static List<string> Copies(string search, string ending)
        {
            var list = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets(search))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith(ending, StringComparison.Ordinal) && !list.Contains(p)) list.Add(p);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static Issue Twice(string title, string detail, List<string> copies)
        {
            string extra = ExtraPath(copies);
            return new Issue
            {
                Severity = Severity.Problem,
                Title = title,
                Detail = detail,
                Target = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(extra),
            };
        }

        // the creator companion keeps its copy in Packages, so the extra one is the one in Assets
        private static string ExtraPath(List<string> copies)
        {
            foreach (string p in copies)
                if (p.StartsWith("Assets/", StringComparison.Ordinal)) return p;
            return copies[copies.Count - 1];
        }

        // the folder the extra copy lives in, like "Assets/VRCFury" or "Assets/_PoiyomiShaders"
        internal static string Extra(List<string> copies, string inside)
        {
            string p = ExtraPath(copies);
            int cut = p.IndexOf(inside, StringComparison.Ordinal);
            return cut > 0 ? p.Substring(0, cut) : System.IO.Path.GetDirectoryName(p).Replace('\\', '/');
        }
    }

    internal static class OutfitCheck
    {
        public const string VrcFuryInstallUrl = "https://vrcfury.com/download";
        public const string PoiyomiInstallUrl = "https://www.poiyomi.com/download";

        public static List<Issue> Run(Outfit outfit)
        {
            var issues = new List<Issue>();
            if (outfit == null || outfit.Root == null) return issues;
            ProjectCheck.AddTo(issues);
            new Pass(outfit, issues).Run();
            // stable sort: problems first, the rest in the order they were found
            var ordered = new List<Issue>();
            foreach (Severity s in new[] { Severity.Problem, Severity.Warning, Severity.Note })
                foreach (var i in issues) if (i.Severity == s) ordered.Add(i);
            return ordered;
        }

        private sealed class Pass
        {
            private readonly Outfit _o;
            private readonly List<Issue> _issues;
            private readonly bool _canFix;
            private readonly HashSet<string> _reported = new HashSet<string>();
            private readonly List<SkinnedMeshRenderer> _linked = new List<SkinnedMeshRenderer>();
            private readonly Dictionary<string, List<KeyValuePair<Component, string>>> _missingBodies =
                new Dictionary<string, List<KeyValuePair<Component, string>>>();
            private List<VfFeature> _features;
            private Renderer[] _outfitRenderers;
            private Renderer[] _avatarRenderers;

            public Pass(Outfit o, List<Issue> issues)
            {
                _o = o;
                _issues = issues;
                _canFix = o.Place != OutfitPlace.PackFile;
            }

            public void Run()
            {
                GameObject root = _o.Root;
                // how the assistant hides an outfit while its Extreme or Quest
                // version stands in for it. Listed here, that version is gone
                if (_o.Place == OutfitPlace.Scene && root.CompareTag("EditorOnly"))
                {
                    var hidden = Add(Severity.Warning, Lang.T("This outfit won't upload"),
                        Lang.T("It's tagged EditorOnly, and the copy that stood in for it is gone."),
                        root);
                    if (_canFix) hidden.Fixes.Add(new IssueFix { Label = Lang.T("Show it again"), Apply = () => ShowAgain(root) });
                    // everything inside is left out too, so theres nothing else to check
                    return;
                }
                if (!AssistantScope.VrcFuryInstalled)
                {
                    if (HasMissingScripts(root)) _issues.Add(VrcFuryMissing());
                    return;
                }

                _outfitRenderers = root.GetComponentsInChildren<Renderer>(true);
                _avatarRenderers = _o.Avatar != null ? _o.Avatar.GetComponentsInChildren<Renderer>(true) : _outfitRenderers;

                // Locked Poiyomi materials carry their own shader, so only a pink
                // material means Poiyomi is really needed and missing
                bool pink = !Poiyomi.Installed && HasPinkMaterial(_outfitRenderers);
                if (pink) _issues.Add(PoiyomiMissing());

                _features = VrcFuryData.Features(root);
                CheckBase();
                bool colorChanger = false;
                foreach (var f in _features)
                {
                    if (f.Type == "Toggle") colorChanger |= CheckToggle(f, !pink);
                    else if (f.Type == "BlendShapeLink") CheckLink(f);
                }
                ReportMissingBodies();
                CheckUnlinkedMeshes();
                CheckPrefixedShapes();
                CheckBrokenReferences();

                // Extreme and Quest versions have no color sliders on purpose
                bool slidersRemoved = _o.Copy != null && _o.Copy.Kind != CopyKind.Decimated;
                if (!colorChanger && !slidersRemoved && root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0)
                {
                    Add(Severity.Note, Lang.T("No color slider"),
                        Lang.T("Most outfits have one. Reimporting the pack brings it back."),
                        root);
                }
            }

            // when most of the outfits body bones arent on the avatar its on another base, or
            // another version of this one where the bones got renamed
            private void CheckBase()
            {
                BoneMatch m = OutfitScanner.MatchBones(_o);
                if (m == null || m.Body < 4 || m.Matched * 2 >= m.Body) return;
                Component link = null;
                foreach (var f in _features)
                {
                    if (f.Type != "ArmatureLink") continue;
                    link = f.Component;
                    break;
                }
                Add(Severity.Warning, Lang.T("This outfit doesn't match your avatar's bones"),
                    Lang.T("Only {0} of its {1} body bones match {2}. It's probably made for a different base, or a different version of yours.",
                        m.Matched, m.Body, Quote(_o.Avatar.name)),
                    link);
            }

            // ---------------------------------------------------------------
            // toggles and sliders
            // ---------------------------------------------------------------

            private bool CheckToggle(VfFeature f, bool checkMaterials)
            {
                SerializedProperty t = f.Prop;
                string name = VrcFuryData.MenuName(VrcFuryData.Text(t, "name"));
                bool slider = VrcFuryData.Bool(t, "slider");
                bool colorChanger = false;

                var states = new List<SerializedProperty> { t.FindPropertyRelative("state") };
                bool separateLocal = VrcFuryData.Bool(t, "separateLocal");
                if (separateLocal) states.Add(t.FindPropertyRelative("localState"));
                if (VrcFuryData.Bool(t, "hasTransition"))
                {
                    states.Add(t.FindPropertyRelative("transitionStateIn"));
                    states.Add(t.FindPropertyRelative("transitionStateOut"));
                    if (separateLocal)
                    {
                        states.Add(t.FindPropertyRelative("localTransitionStateIn"));
                        states.Add(t.FindPropertyRelative("localTransitionStateOut"));
                    }
                }

                foreach (var state in states)
                {
                    foreach (var action in VrcFuryData.Actions(state))
                    {
                        switch (VrcFuryData.ShortType(action.managedReferenceFullTypename))
                        {
                            case "ObjectToggleAction":
                                CheckObjectAction(f, name, slider, action);
                                break;
                            case "BlendShapeAction":
                                CheckBlendShapeAction(f, name, slider, action);
                                break;
                            case "MaterialPropertyAction":
                                if (slider) colorChanger = true;
                                if (checkMaterials) CheckMaterialAction(f, name, slider, action);
                                break;
                        }
                    }
                }

                if (VrcFuryData.Bool(t, "enableIcon"))
                {
                    var icon = t.FindPropertyRelative("icon");
                    if (icon != null && VrcFuryData.AssetSlotState(icon) != RefState.Set)
                    {
                        MarkReported(f.Component, icon.FindPropertyRelative("objRef"));
                        var issue = Add(Severity.Note, Lang.T("{0} has no menu icon", Quote(name)),
                            slider ? Lang.T("The icon file is missing. The slider still works.") : Lang.T("The icon file is missing. The toggle still works."),
                            f.Component);
                        AddRestore(issue, f.Component, icon.propertyPath);
                    }
                }
                return colorChanger;
            }

            private void CheckObjectAction(VfFeature f, string name, bool slider, SerializedProperty action)
            {
                var obj = action.FindPropertyRelative("obj");
                RefState state = VrcFuryData.StateOf(obj);
                if (state == RefState.Set) return;

                MarkReported(f.Component, obj);
                string detail = state == RefState.Missing
                    ? Lang.T("The object it switches was deleted.")
                    : Lang.T("Its object slot is empty.");
                string title = slider ? Lang.T("Slider {0} has nothing to switch", Quote(name)) : Lang.T("Toggle {0} has nothing to switch", Quote(name));
                var issue = Add(Severity.Problem, title, detail, f.Component);
                AddRestore(issue, f.Component, obj.propertyPath);
            }

            private void CheckBlendShapeAction(VfFeature f, string name, bool slider, SerializedProperty action)
            {
                string shape = VrcFuryData.Text(action, "blendShape");
                if (string.IsNullOrEmpty(shape)) return;

                if (!VrcFuryData.Bool(action, "allRenderers", true))
                {
                    var rp = action.FindPropertyRelative("renderer");
                    RefState rs = VrcFuryData.StateOf(rp);
                    if (rs != RefState.Set)
                    {
                        MarkReported(f.Component, rp);
                        var gone = Add(Severity.Problem, MeshGone(slider, name),
                            Lang.T("The mesh with {0} was deleted or never set.", Quote(shape)), f.Component);
                        AddRestore(gone, f.Component, rp.propertyPath);
                        return;
                    }
                    var smr = rp.objectReferenceValue as SkinnedMeshRenderer;
                    if (smr != null && !HasShape(smr, shape))
                    {
                        Add(Severity.Warning, Lang.T("{0} isn't on {1}", Quote(shape), Quote(smr.name)),
                            Lang.T("{0} doesn't have that blendshape. The rest still works.", Quote(smr.name)),
                            f.Component);
                    }
                    return;
                }

                // These usually live on the avatar's body, so they can only be checked on an avatar
                if (_o.Avatar == null) return;
                foreach (var r in _avatarRenderers)
                {
                    var s = r as SkinnedMeshRenderer;
                    if (s != null && HasShape(s, shape)) return;
                }
                Add(Severity.Warning, Lang.T("Your avatar has no {0} blendshape", Quote(shape)),
                    slider
                        ? Lang.T("Slider {0} uses it on the body. Your avatar base might be a different version.", Quote(name))
                        : Lang.T("Toggle {0} uses it on the body. Your avatar base might be a different version.", Quote(name)),
                    f.Component);
            }

            private static string MeshGone(bool slider, string name)
            {
                return slider
                    ? Lang.T("Slider {0} points at a mesh that's gone", Quote(name))
                    : Lang.T("Toggle {0} points at a mesh that's gone", Quote(name));
            }

            private void CheckMaterialAction(VfFeature f, string name, bool slider, SerializedProperty action)
            {
                string property = VrcFuryData.Text(action, "propertyName");
                if (string.IsNullOrEmpty(property)) return;

                var scope = new List<Renderer>();
                if (VrcFuryData.Bool(action, "affectAllMeshes"))
                {
                    scope.AddRange(_avatarRenderers);
                }
                else
                {
                    // VRCFury 1.x points at a GameObject; older versions at a Renderer
                    var r2 = action.FindPropertyRelative("renderer2");
                    var r1 = action.FindPropertyRelative("renderer");
                    var go = r2 != null ? r2.objectReferenceValue as GameObject : null;
                    Renderer single = go != null ? go.GetComponent<Renderer>() : (r1 != null ? r1.objectReferenceValue as Renderer : null);
                    if (single == null)
                    {
                        var bad = VrcFuryData.StateOf(r2) == RefState.Missing ? r2 : r1;
                        MarkReported(f.Component, bad);
                        var gone = Add(Severity.Problem, MeshGone(slider, name),
                            Lang.T("The mesh it recolors was deleted or never set."), f.Component);
                        if (bad != null) AddRestore(gone, f.Component, bad.propertyPath);
                        return;
                    }
                    scope.Add(single);
                }

                foreach (var r in scope)
                    foreach (var m in r.sharedMaterials)
                        if (Poiyomi.WillHave(m, property)) return;

                string title = slider ? Lang.T("Slider {0} won't work", Quote(name)) : Lang.T("Toggle {0} won't work", Quote(name));
                var issue = Add(Severity.Problem, title, Diagnose(property, scope), f.Component);
                if (_canFix) AddMaterialFixes(issue, property, scope);
            }

            // why no material has the property this slider changes
            private static string Diagnose(string property, List<Renderer> scope)
            {
                string prop = Poiyomi.BaseName(property);
                foreach (var r in scope)
                {
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null) continue;
                        string suffix = Poiyomi.RenameSuffix(m);
                        for (int i = prop.LastIndexOf('_'); i > 0; i = prop.LastIndexOf('_', i - 1))
                        {
                            string original = prop.Substring(0, i);
                            string asked = prop.Substring(i + 1);
                            string tag = m.GetTag(original + "Animated", false, "");
                            if (tag == "" && !m.HasProperty(original)) continue;

                            if (tag == "2" && asked != suffix)
                                return Lang.T("The material {0} was renamed or copied, so the setting is now called {1}. Rename it back or use the pack's material.", Quote(m.name), Quote(original + "_" + suffix));
                            if (tag == "2")
                                return Lang.T("The material {0} was locked too early. Unlock it in Poiyomi and lock it again.", Quote(m.name));
                            return Lang.T("{0} on {1} isn't marked animated and renamed. Unlock it, right-click the setting, mark it, then lock again.", Quote(original), Quote(m.name));
                        }
                    }
                }
                return Lang.T("No material on this outfit has {0}. A material was probably swapped.", Quote(prop));
            }

            private void AddMaterialFixes(Issue issue, string property, List<Renderer> scope)
            {
                var swapped = new List<Renderer>();
                foreach (var r in scope)
                {
                    if (!r.transform.IsChildOf(_o.Root.transform) || !PrefabUtility.IsPartOfPrefabInstance(r)) continue;
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(r);
                    if (source == null) continue;

                    bool sourceWorks = false;
                    foreach (var m in source.sharedMaterials)
                        if (Poiyomi.WillHave(m, property)) sourceWorks = true;
                    if (!sourceWorks) continue;

                    var mats = new SerializedObject(r).FindProperty("m_Materials");
                    if (mats != null && mats.prefabOverride) swapped.Add(r);
                }
                if (swapped.Count == 0) return;

                issue.Detail = swapped.Count == 1
                    ? Lang.T("{0} uses a different material now.", Quote(swapped[0].name))
                    : Lang.T("{0} pieces use a different material now.", swapped.Count);
                issue.Fixes.Add(new IssueFix
                {
                    Label = swapped.Count == 1 ? Lang.T("Put the pack's material back") : Lang.T("Put the pack's materials back"),
                    Apply = () =>
                    {
                        Undo.IncrementCurrentGroup();
                        Undo.SetCurrentGroupName(Lang.T("Put the pack's materials back"));
                        int group = Undo.GetCurrentGroup();
                        foreach (var r in swapped)
                            if (r != null) RevertProperty(r, "m_Materials");
                        Undo.CollapseUndoOperations(group);
                    },
                });
            }

            // ---------------------------------------------------------------
            // Blendshape links
            // ---------------------------------------------------------------

            private void CheckLink(VfFeature f)
            {
                SerializedProperty link = f.Prop;
                int meshes = 0;
                bool missing = false;

                var skins = link.FindPropertyRelative("linkSkins");
                if (skins != null && skins.isArray)
                {
                    for (int i = 0; i < skins.arraySize; i++)
                    {
                        var rp = skins.GetArrayElementAtIndex(i).FindPropertyRelative("renderer");
                        RefState s = VrcFuryData.StateOf(rp);
                        if (s == RefState.Set)
                        {
                            var smr = rp.objectReferenceValue as SkinnedMeshRenderer;
                            if (smr != null) { _linked.Add(smr); meshes++; }
                        }
                        else if (s == RefState.Missing)
                        {
                            missing = true;
                            ReportGoneLinkMesh(f, rp);
                        }
                    }
                }

                // Older VRCFury layout: a list of objects instead of meshes.
                var objs = link.FindPropertyRelative("objs");
                if (objs != null && objs.isArray)
                {
                    for (int i = 0; i < objs.arraySize; i++)
                    {
                        var op = objs.GetArrayElementAtIndex(i);
                        RefState s = VrcFuryData.StateOf(op);
                        if (s == RefState.Set)
                        {
                            var go = op.objectReferenceValue as GameObject;
                            var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
                            if (smr != null) { _linked.Add(smr); meshes++; }
                        }
                        else if (s == RefState.Missing)
                        {
                            missing = true;
                            ReportGoneLinkMesh(f, op);
                        }
                    }
                }

                if (meshes == 0 && !missing)
                    Add(Severity.Warning, Lang.T("A blendshape link is empty"), Lang.T("It has no meshes in it."), f.Component);

                string body = VrcFuryData.Text(link, "baseObj");
                if (body.Length == 0)
                {
                    Add(Severity.Warning, Lang.T("A blendshape link has no body to follow"),
                        Lang.T("It doesn't say which avatar mesh to follow."),
                        f.Component);
                    return;
                }
                if (_o.Avatar == null || FindBody(body) != null) return;

                List<KeyValuePair<Component, string>> targets;
                if (!_missingBodies.TryGetValue(body, out targets))
                {
                    targets = new List<KeyValuePair<Component, string>>();
                    _missingBodies[body] = targets;
                }
                targets.Add(new KeyValuePair<Component, string>(f.Component, link.propertyPath + ".baseObj"));
            }

            private void ReportGoneLinkMesh(VfFeature f, SerializedProperty prop)
            {
                MarkReported(f.Component, prop);
                var issue = Add(Severity.Warning, Lang.T("A blendshape link points at a mesh that's gone"),
                    Lang.T("One of its meshes was deleted."), f.Component);
                AddRestore(issue, f.Component, prop.propertyPath);
            }

            private SkinnedMeshRenderer FindBody(string name)
            {
                foreach (var smr in _o.Avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr.name == name) return smr;
                return null;
            }

            private void ReportMissingBodies()
            {
                foreach (var pair in _missingBodies)
                {
                    int n = pair.Value.Count;
                    var issue = Add(Severity.Warning, Lang.T("Can't find {0} on your avatar", Quote(pair.Key)),
                        n == 1
                            ? Lang.T("A blendshape link follows a mesh called {0}, and {1} doesn't have one. Pick your body mesh.", Quote(pair.Key), Quote(_o.Avatar.name))
                            : Lang.T("{2} blendshape links follow a mesh called {0}, and {1} doesn't have one. Pick your body mesh.", Quote(pair.Key), Quote(_o.Avatar.name), n),
                        pair.Value[0].Key);
                    if (_canFix) issue.Fixes.Add(PickBodyFix(pair.Value));
                }
            }

            private IssueFix PickBodyFix(List<KeyValuePair<Component, string>> targets)
            {
                GameObject avatar = _o.Avatar;
                Transform outfitRoot = _o.Root.transform;
                return new IssueFix
                {
                    Label = Lang.T("Pick your body mesh"),
                    Choices = () =>
                    {
                        var choices = new List<KeyValuePair<string, Action>>();
                        foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            if (smr.transform.IsChildOf(outfitRoot) || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
                            string body = smr.name;
                            choices.Add(new KeyValuePair<string, Action>(
                                Lang.T("{0}  ({1} blendshapes)", body, smr.sharedMesh.blendShapeCount),
                                () => SetLinkBody(targets, body)));
                        }
                        return choices;
                    },
                };
            }

            private void CheckUnlinkedMeshes()
            {
                if (_linked.Count == 0) return;
                var linked = new HashSet<SkinnedMeshRenderer>(_linked);
                var shapes = new HashSet<string>();
                foreach (var smr in _linked) AddShapes(smr, shapes);

                Component template = null;
                foreach (var f in _features)
                {
                    if (f.Type == "BlendShapeLink" && f.Prop.propertyPath == "content")
                    {
                        template = f.Component;
                        break;
                    }
                }

                foreach (var smr in _o.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (linked.Contains(smr) || smr.sharedMesh == null || AssistantScope.InsideEditorOnly(smr.transform)) continue;
                    int shared = 0;
                    for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                        if (shapes.Contains(smr.sharedMesh.GetBlendShapeName(i))) shared++;
                    if (shared == 0) continue;

                    var issue = Add(Severity.Warning, Lang.T("{0} doesn't follow your body sliders", Quote(smr.name)),
                        Lang.T("It has {0} body blendshapes but no blendshape link.", shared),
                        smr);
                    if (_canFix && template != null)
                    {
                        Component from = template;
                        SkinnedMeshRenderer target = smr;
                        issue.Fixes.Add(new IssueFix { Label = Lang.T("Add a blendshape link"), Apply = () => AddLink(from, target) });
                    }
                }
            }

            // ---------------------------------------------------------------
            // Blendshape names with a stray prefix
            // ---------------------------------------------------------------

            // some exports glue a name to the front of a mesh's blendshapes
            // ("corset1.Female_Breasts" where the body has "Female_Breasts").
            // VRCFury links names that match, ignoring only case and spaces, so
            // those never follow the body. Only a name whose part after the dot
            // matches is flagged; names that are really different are left alone.
            private void CheckPrefixedShapes()
            {
                var seen = new HashSet<SkinnedMeshRenderer>();
                foreach (var f in _features)
                {
                    if (f.Type != "BlendShapeLink") continue;
                    SerializedProperty link = f.Prop;
                    bool exact = VrcFuryData.Bool(link, "exactMatch");
                    HashSet<string> byHand = MappedByHand(link);
                    SkinnedMeshRenderer body = _o.Avatar != null ? FindBody(VrcFuryData.Text(link, "baseObj")) : null;
                    foreach (SkinnedMeshRenderer smr in LinkedBy(link))
                    {
                        if (smr.sharedMesh == null || AssistantScope.InsideEditorOnly(smr.transform) || !seen.Add(smr)) continue;
                        // The names it should match: the body's, or with no body to
                        // look at, the ones the outfits pieces have.
                        var reference = new HashSet<string>();
                        if (body != null) AddShapes(body, reference);
                        else foreach (var other in _linked) AddShapes(other, reference);
                        var squashed = new HashSet<string>();
                        foreach (string s in reference) squashed.Add(Squash(s));

                        var stray = new List<string>();
                        Mesh mesh = smr.sharedMesh;
                        for (int i = 0; i < mesh.blendShapeCount; i++)
                        {
                            string name = mesh.GetBlendShapeName(i);
                            int dot = name.LastIndexOf('.');
                            if (dot <= 0 || dot >= name.Length - 1 || byHand.Contains(name)) continue;
                            if (body != null && Links(name, reference, squashed, exact)) continue;
                            if (Links(name.Substring(dot + 1), reference, squashed, exact)) stray.Add(name);
                        }
                        if (stray.Count == 0) continue;
                        string first = stray[0];
                        string bare = first.Substring(first.LastIndexOf('.') + 1);
                        string detail;
                        if (stray.Count == 1)
                            detail = body != null
                                ? Lang.T("One of its blendshapes is called {0} instead of {1}, so it doesn't follow the matching blendshape on {2}.", Quote(first), Quote(bare), Quote(body.name))
                                : Lang.T("One of its blendshapes is called {0} instead of {1}, so it doesn't follow the matching blendshape on your body.", Quote(first), Quote(bare));
                        else
                            detail = body != null
                                ? Lang.T("{3} of its blendshapes have a prefix, like {0} instead of {1}, so they don't follow the matching blendshapes on {2}.", Quote(first), Quote(bare), Quote(body.name), stray.Count)
                                : Lang.T("{2} of its blendshapes have a prefix, like {0} instead of {1}, so they don't follow the matching blendshapes on your body.", Quote(first), Quote(bare), stray.Count);
                        Add(Severity.Warning, Lang.T("{0} has blendshapes with a stray prefix", Quote(smr.name)), detail, smr);
                    }
                }
            }

            private static bool Links(string name, HashSet<string> reference, HashSet<string> squashed, bool exact)
            {
                return reference.Contains(name) || (!exact && squashed.Contains(Squash(name)));
            }

            // VRCFury's loose match: lower case, no spaces
            private static string Squash(string s)
            {
                return Regex.Replace(s.ToLowerInvariant(), @"\s", "");
            }

            // names on the linked meshes that the link maps by hand
            private static HashSet<string> MappedByHand(SerializedProperty link)
            {
                var names = new HashSet<string>();
                var includes = link.FindPropertyRelative("includes");
                if (includes == null || !includes.isArray) return names;
                for (int i = 0; i < includes.arraySize; i++)
                {
                    var e = includes.GetArrayElementAtIndex(i);
                    string linked = VrcFuryData.Text(e, "nameOnLinked");
                    names.Add(linked.Trim().Length > 0 ? linked : VrcFuryData.Text(e, "nameOnBase"));
                }
                return names;
            }

            private static List<SkinnedMeshRenderer> LinkedBy(SerializedProperty link)
            {
                var list = new List<SkinnedMeshRenderer>();
                var skins = link.FindPropertyRelative("linkSkins");
                if (skins != null && skins.isArray)
                {
                    for (int i = 0; i < skins.arraySize; i++)
                    {
                        var r = skins.GetArrayElementAtIndex(i).FindPropertyRelative("renderer");
                        var smr = r != null ? r.objectReferenceValue as SkinnedMeshRenderer : null;
                        if (smr != null) list.Add(smr);
                    }
                }
                var objs = link.FindPropertyRelative("objs");
                if (objs != null && objs.isArray)
                {
                    for (int i = 0; i < objs.arraySize; i++)
                    {
                        var go = objs.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                        var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
                        if (smr != null) list.Add(smr);
                    }
                }
                return list;
            }

            // ---------------------------------------------------------------
            // everything else
            // ---------------------------------------------------------------

            private void CheckBrokenReferences()
            {
                var seen = new HashSet<int>();
                foreach (var f in _features)
                {
                    if (!seen.Add(f.Component.GetInstanceID())) continue;
                    var so = new SerializedObject(f.Component);
                    var it = so.GetIterator();
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (VrcFuryData.StateOf(it) != RefState.Missing) continue;
                        if (_reported.Contains(Key(f.Component, it.propertyPath))) continue;
                        if (IsResolvedAssetSlot(so, it.propertyPath)) continue;

                        string feature = FeatureTitle(VrcFuryData.FeatureType(f.Component) ?? f.Type);
                        var issue = Add(Severity.Warning, Lang.T("Broken link in {0}", feature),
                            Lang.T("Something it points at on {0} was deleted ({1}).", Quote(f.Component.gameObject.name), it.displayName),
                            f.Component);
                        AddRestore(issue, f.Component, it.propertyPath);
                    }
                }
            }

            private static bool IsResolvedAssetSlot(SerializedObject so, string path)
            {
                if (!path.EndsWith(".objRef", StringComparison.Ordinal)) return false;
                var slot = so.FindProperty(path.Substring(0, path.Length - ".objRef".Length));
                return VrcFuryData.AssetSlotState(slot) == RefState.Set;
            }

            // ---------------------------------------------------------------
            // fixes that put the packs version back
            // ---------------------------------------------------------------

            private void AddRestore(Issue issue, Component comp, string path)
            {
                if (!_canFix || comp == null || !PrefabUtility.IsPartOfPrefabInstance(comp)) return;
                var source = PrefabUtility.GetCorrespondingObjectFromSource(comp);
                if (source == null) return;
                var sourceProp = new SerializedObject(source).FindProperty(path);
                if (sourceProp == null) return;
                bool sourceGood = sourceProp.propertyType == SerializedPropertyType.ObjectReference
                    ? VrcFuryData.StateOf(sourceProp) == RefState.Set
                    : VrcFuryData.AssetSlotState(sourceProp) == RefState.Set;
                if (!sourceGood) return; // the pack itself is broken here; nothing to put back

                var instanceProp = new SerializedObject(comp).FindProperty(path);
                if (instanceProp != null && instanceProp.prefabOverride)
                {
                    issue.Fixes.Add(new IssueFix { Label = Lang.T("Restore from the pack"), Apply = () => RevertProperty(comp, path) });
                    return;
                }

#if UNITY_2022_2_OR_NEWER
                // the object it pointed at was deleted from this copy of the outfit.
                var target = sourceProp.propertyType == SerializedPropertyType.ObjectReference ? AsGameObject(sourceProp.objectReferenceValue) : null;
                GameObject instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(comp);
                if (target == null || instanceRoot == null) return;
                foreach (var removed in PrefabUtility.GetRemovedGameObjects(instanceRoot))
                {
                    if (!Covers(removed.assetGameObject, target)) continue;
                    var gone = removed;
                    GameObject outfitRoot = _o.Root;
                    string label = Lang.T("Bring back {0}", Quote(target.name));
                    issue.Fixes.Add(new IssueFix
                    {
                        Label = label,
                        Apply = () =>
                        {
                            Undo.IncrementCurrentGroup();
                            Undo.SetCurrentGroupName(label);
                            int group = Undo.GetCurrentGroup();
                            gone.Revert(InteractionMode.UserAction);
                            // links that pointed at the deleted copy stay broken until reset
                            RepairBrokenLinks(outfitRoot);
                            Undo.CollapseUndoOperations(group);
                        },
                    });
                    return;
                }
#endif
            }

            private static GameObject AsGameObject(UnityEngine.Object o)
            {
                var go = o as GameObject;
                if (go != null) return go;
                var c = o as Component;
                return c != null ? c.gameObject : null;
            }

            private static bool Covers(GameObject removedAsset, GameObject target)
            {
                if (removedAsset == null) return false;
                for (Transform t = target.transform; t != null; t = t.parent)
                {
                    for (GameObject g = t.gameObject; g != null; g = PrefabUtility.GetCorrespondingObjectFromSource(g))
                    {
                        if (g == removedAsset) return true;
                    }
                }
                return false;
            }

            // ---------------------------------------------------------------
            // helpers
            // ---------------------------------------------------------------

            private Issue Add(Severity severity, string title, string detail, UnityEngine.Object target)
            {
                var issue = new Issue { Severity = severity, Title = title, Detail = detail, Target = target };
                _issues.Add(issue);
                return issue;
            }

            private void MarkReported(Component c, SerializedProperty p)
            {
                if (c != null && p != null) _reported.Add(Key(c, p.propertyPath));
            }

            private static string Key(Component c, string path)
            {
                return c.GetInstanceID() + ":" + path;
            }

            private static bool HasMissingScripts(GameObject root)
            {
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb == null) return true;
                return false;
            }

            private static bool HasPinkMaterial(Renderer[] renderers)
            {
                foreach (var r in renderers)
                    foreach (var m in r.sharedMaterials)
                        if (m != null && (m.shader == null || m.shader.name == "Hidden/InternalErrorShader")) return true;
                return false;
            }

            private static bool HasShape(SkinnedMeshRenderer smr, string shape)
            {
                return smr.sharedMesh != null && smr.sharedMesh.GetBlendShapeIndex(shape) >= 0;
            }

            private static void AddShapes(SkinnedMeshRenderer smr, HashSet<string> shapes)
            {
                if (smr.sharedMesh == null) return;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                    shapes.Add(smr.sharedMesh.GetBlendShapeName(i));
            }

            private static string Quote(string s)
            {
                return Lang.Quote(s);
            }

            private static string FeatureTitle(string type)
            {
                switch (type)
                {
                    case "BlendShapeLink": return "BlendShape Link";
                    case "ArmatureLink": return "Armature Link";
                    case "SetIcon": return "Override Menu Icon";
                    case "FullController": return "Full Controller";
                    case "WorldConstraint": return "Droppable";
                    case "": return Lang.T("VRCFury component");
                    default: return ObjectNames.NicifyVariableName(type);
                }
            }

            private static Issue VrcFuryMissing()
            {
                var issue = new Issue
                {
                    Severity = Severity.Problem,
                    Title = Lang.T("VRCFury isn't installed"),
                    Detail = Lang.T("This outfit runs on VRCFury. Add it with the VRChat Creator Companion."),
                };
                issue.Fixes.Add(new IssueFix { Label = Lang.T("Get VRCFury"), Apply = () => Application.OpenURL(VrcFuryInstallUrl) });
                return issue;
            }

            private static Issue PoiyomiMissing()
            {
                var issue = new Issue
                {
                    Severity = Severity.Problem,
                    Title = Lang.T("Poiyomi isn't installed"),
                    Detail = Lang.T("Some materials need Poiyomi Toon and show up pink without it."),
                };
                issue.Fixes.Add(new IssueFix { Label = Lang.T("Get Poiyomi"), Apply = () => Application.OpenURL(PoiyomiInstallUrl) });
                return issue;
            }
        }

        // -------------------------------------------------------------------
        // fix actions. Each one is a single Ctrl+Z.
        // -------------------------------------------------------------------

        internal static void RevertProperty(UnityEngine.Object target, string path)
        {
            var p = new SerializedObject(target).FindProperty(path);
            if (p == null) return;
            try
            {
                PrefabUtility.RevertPropertyOverride(p, InteractionMode.UserAction);
            }
            catch (Exception)
            {
                // older Unity versions cant revert one field inside VRCFury's
                // data; restoring the whole component is the next best thing
                PrefabUtility.RevertObjectOverride(target, InteractionMode.UserAction);
            }
        }

        // Brings back an outfit the assistant hid for a copy that's gone.
        internal static void ShowAgain(GameObject root)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Show the outfit again"));
            int group = Undo.GetCurrentGroup();
            Undo.RecordObject(root, Lang.T("Show the outfit again"));
            root.tag = "Untagged";
            root.SetActive(true);
            if (PrefabUtility.IsPartOfPrefabInstance(root)) PrefabUtility.RecordPrefabInstancePropertyModifications(root);
            Undo.CollapseUndoOperations(group);
        }

        // Resets every VRCFury link on the outfit that is broken in this copy
        // but fine in the pack.
        internal static void RepairBrokenLinks(GameObject outfitRoot)
        {
            if (outfitRoot == null) return;
            foreach (var c in outfitRoot.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!AssistantScope.IsVrcFury(c) || !PrefabUtility.IsPartOfPrefabInstance(c)) continue;
                var source = PrefabUtility.GetCorrespondingObjectFromSource(c);
                if (source == null) continue;

                var sourceData = new SerializedObject(source);
                var broken = new List<string>();
                var it = new SerializedObject(c).GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference || !it.prefabOverride) continue;
                    if (VrcFuryData.StateOf(it) == RefState.Set) continue;
                    var sp = sourceData.FindProperty(it.propertyPath);
                    if (sp != null && VrcFuryData.StateOf(sp) == RefState.Set) broken.Add(it.propertyPath);
                }
                foreach (string path in broken) RevertProperty(c, path);
            }
        }

        internal static void SetLinkBody(List<KeyValuePair<Component, string>> targets, string body)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Pick your body mesh"));
            int group = Undo.GetCurrentGroup();
            foreach (var pair in targets)
            {
                if (pair.Key == null) continue;
                var so = new SerializedObject(pair.Key);
                var p = so.FindProperty(pair.Value);
                if (p == null || p.propertyType != SerializedPropertyType.String) continue;
                p.stringValue = body;
                so.ApplyModifiedProperties();
            }
            Undo.CollapseUndoOperations(group);
        }

        // copies one of the outfit's own blendshape links and points it at the mesh
        internal static void AddLink(Component template, SkinnedMeshRenderer target)
        {
            if (template == null || target == null) return;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Lang.T("Add a blendshape link"));
            int group = Undo.GetCurrentGroup();
            GameObject host = template.gameObject;
            if (!ComponentUtility.CopyComponent(template) || !ComponentUtility.PasteComponentAsNew(host)) return;

            Component[] all = host.GetComponents(template.GetType());
            Component added = all[all.Length - 1];
            var so = new SerializedObject(added);
            var skins = so.FindProperty("content.linkSkins");
            if (skins != null && skins.isArray)
            {
                skins.arraySize = 1;
                skins.GetArrayElementAtIndex(0).FindPropertyRelative("renderer").objectReferenceValue = target;
            }
            var objs = so.FindProperty("content.objs");
            if (objs != null && objs.isArray) objs.arraySize = 0;
            so.ApplyModifiedProperties();
            Undo.CollapseUndoOperations(group);
        }
    }
}
#endif
