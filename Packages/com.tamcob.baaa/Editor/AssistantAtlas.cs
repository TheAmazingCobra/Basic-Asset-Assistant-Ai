#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BAAA
{
    // what material each slot of the fused mesh gets, and where every old material ends up
    internal sealed class MaterialPlan
    {
        public readonly List<Material> Slots = new List<Material>();
        public readonly Dictionary<Material, SlotPlan> Of = new Dictionary<Material, SlotPlan>();
        public int Atlases;
        // source materials that now share a slot with another one
        public int Merged;
        // atlases were skipped: there was no graphics card to draw them with
        public bool NoGraphics;
        // Quest: some parts were see-through, and Quest's shader is always solid.
        public bool SeeThrough;
        // meshes and materials made just for the fuse, thrown away after it
        public readonly List<UnityEngine.Object> Temporary = new List<UnityEngine.Object>();
    }

    // atlas stuff shared by the fused, extreme and quest versions. the extreme one lives in AssistantExtreme,
    // quest bakes every color into one texture for vrchats toon standard
    internal static partial class AtlasBuilder
    {
        public const string ToonStandard = "VRChat/Mobile/Toon Standard";
        public const string ToonLit = "VRChat/Mobile/Toon Lit";
        // VRChat's default shadow ramp for Toon Standard ("Realistic Soft")
        private const string RampGuid = "636cf1b5dfca6f54b94ca3d2ff8216c9";

        private struct TexEnv
        {
            public Texture Texture;
            public Vector2 Scale;
            public Vector2 Offset;
        }

        // A material's saved settings, read from its file, so locked Poiyomi
        // materials read the same as unlocked ones
        private sealed class Saved
        {
            public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
            public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();
            public readonly Dictionary<string, TexEnv> Textures = new Dictionary<string, TexEnv>();

            public float F(string name, float fallback)
            {
                float v;
                return Floats.TryGetValue(name, out v) ? v : fallback;
            }

            public Color C(string name, Color fallback)
            {
                Color v;
                return Colors.TryGetValue(name, out v) ? v : fallback;
            }

            public Texture T(string name)
            {
                TexEnv t;
                return Textures.TryGetValue(name, out t) ? t.Texture : null;
            }
        }

        private sealed class Mat
        {
            public Material Material;
            public Saved Saved;
            public readonly List<string> UvProps = new List<string>();
            public int Size;
        }

        private sealed class Cell
        {
            public readonly List<Mat> Members = new List<Mat>();
            public int Size;
            public float Overshoot;
            public RectInt Place;
            public int Pad;
        }

        public static bool CanDraw
        {
            get { return SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null; }
        }

        // the fused version: one mesh, and every material keeps its own slot, as it is
        public static MaterialPlan PlanFused(List<Renderer> renderers)
        {
            var plan = new MaterialPlan();
            foreach (Material m in Distinct(renderers))
            {
                plan.Of[m] = new SlotPlan { Slot = plan.Slots.Count };
                plan.Slots.Add(m);
            }
            return plan;
        }

        private static IEnumerable<Material> Distinct(List<Renderer> renderers)
        {
            var seen = new HashSet<Material>();
            foreach (Renderer r in renderers)
                foreach (Material m in r.sharedMaterials)
                    if (m != null && seen.Add(m)) yield return m;
        }

        private static bool IsUvMapped(string prop, Texture tex, Saved s, bool poiyomi)
        {
            string lower = prop.ToLowerInvariant();
            if (lower.Contains("matcap") || lower.Contains("ramp") || lower.Contains("cube") || lower.Contains("lut")) return false;
            if (tex != null && !(tex is Texture2D)) return false;
            // Poiyomi gives every texture laid out on the mesh its own UV picker.
            if (s.Floats.ContainsKey(prop + "UV")) return true;
            return !poiyomi;
        }

        private static string OriginalShaderName(Material m)
        {
            if (m.shader == null) return "";
            return Poiyomi.IsLocked(m) ? m.GetTag("OriginalShader", false, m.shader.name) : m.shader.name;
        }

        private static List<string> Keywords(Material m)
        {
            var list = new List<string>(Poiyomi.IsLocked(m)
                ? m.GetTag("OriginalKeywords", false, "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                : m.shaderKeywords);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static Saved Read(Material m)
        {
            var s = new Saved();
            var props = new SerializedObject(m).FindProperty("m_SavedProperties");
            if (props == null) return s;

            // A locked Poiyomi material renames the settings its sliders move
            // ("_MainHueShift_MainVelvet"); they're read under their own name
            string suffix = Poiyomi.IsLocked(m) ? "_" + Poiyomi.RenameSuffix(m) : null;
            Func<string, string> plain = n =>
            {
                if (suffix == null || !n.EndsWith(suffix, StringComparison.Ordinal)) return n;
                string original = n.Substring(0, n.Length - suffix.Length);
                return m.GetTag(original + "Animated", false, "") == "2" ? original : n;
            };

            Pairs(props.FindPropertyRelative("m_Floats"), (n, p) => Put(s.Floats, plain(n), n, p.floatValue));
            Pairs(props.FindPropertyRelative("m_Ints"), (n, p) => Put(s.Floats, plain(n), n, p.intValue));
            Pairs(props.FindPropertyRelative("m_Colors"), (n, p) => Put(s.Colors, plain(n), n, p.colorValue));
            Pairs(props.FindPropertyRelative("m_TexEnvs"), (n, p) =>
            {
                var texture = p.FindPropertyRelative("m_Texture");
                var scale = p.FindPropertyRelative("m_Scale");
                var offset = p.FindPropertyRelative("m_Offset");
                s.Textures[n] = new TexEnv
                {
                    Texture = texture != null ? texture.objectReferenceValue as Texture : null,
                    Scale = scale != null ? scale.vector2Value : Vector2.one,
                    Offset = offset != null ? offset.vector2Value : Vector2.zero,
                };
            });
            return s;
        }

        // the renamed setting is the one in use, so it wins over the old one.
        private static void Put<T>(Dictionary<string, T> d, string name, string savedAs, T value)
        {
            if (name != savedAs || !d.ContainsKey(name)) d[name] = value;
        }

        private static void Pairs(SerializedProperty array, Action<string, SerializedProperty> read)
        {
            if (array == null || !array.isArray) return;
            for (int i = 0; i < array.arraySize; i++)
            {
                var e = array.GetArrayElementAtIndex(i);
                var first = e.FindPropertyRelative("first");
                var second = e.FindPropertyRelative("second");
                if (first != null && second != null) read(first.stringValue, second);
            }
        }

        private static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var filter = r.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        // "_EmissionMap" -> "EmissionMap"
        private static string Readable(string prop)
        {
            return prop.TrimStart('_');
        }

        // -------------------------------------------------------------------
        // packing
        // -------------------------------------------------------------------

        // places every cell in the smallest square atlas that holds them, up to
        // atlasMax; cells shrink by half until they fit. Returns the side.
        private static int PackCells(List<Cell> cells, int atlasMax)
        {
            var sizes = new int[cells.Count];
            for (int i = 0; i < cells.Count; i++) sizes[i] = Math.Min(NextPow2(cells[i].Size), atlasMax);
            for (int round = 0; ; round++)
            {
                if (round > 12) throw new InvalidOperationException(Lang.T("Too many materials to fit in one atlas."));
                long area = 0;
                int biggest = 0;
                foreach (int s in sizes)
                {
                    area += (long)s * s;
                    biggest = Math.Max(biggest, s);
                }
                int side = Math.Max(biggest, NextPow2((int)Math.Ceiling(Math.Sqrt(area))));
                for (; side <= atlasMax; side *= 2)
                {
                    RectInt[] places;
                    if (!Pack(sizes, side, out places)) continue;
                    for (int i = 0; i < cells.Count; i++)
                    {
                        cells[i].Place = places[i];
                        // the border also covers UVs that reach a little past the edge
                        int pad = Math.Max(Mathf.Clamp(sizes[i] / 64, 2, 8), Mathf.CeilToInt(cells[i].Overshoot * sizes[i]) + 1);
                        cells[i].Pad = Math.Min(pad, sizes[i] / 4);
                    }
                    return side;
                }
                for (int i = 0; i < sizes.Length; i++) sizes[i] = Math.Max(32, sizes[i] / 2);
            }
        }

        // squares with power-of-two sides pack into a power-of-two square by
        // splitting it into quarters, biggest first
        private static bool Pack(int[] sizes, int side, out RectInt[] places)
        {
            places = new RectInt[sizes.Length];
            var order = new List<int>();
            for (int i = 0; i < sizes.Length; i++) order.Add(i);
            order.Sort((a, b) => sizes[b].CompareTo(sizes[a]));
            var free = new List<RectInt> { new RectInt(0, 0, side, side) };
            foreach (int i in order)
            {
                int want = sizes[i];
                int best = -1;
                for (int f = 0; f < free.Count; f++)
                    if (free[f].width >= want && (best < 0 || free[f].width < free[best].width)) best = f;
                if (best < 0) return false;
                RectInt r = free[best];
                free.RemoveAt(best);
                while (r.width > want)
                {
                    int h = r.width / 2;
                    free.Add(new RectInt(r.x + h, r.y, h, h));
                    free.Add(new RectInt(r.x, r.y + h, h, h));
                    free.Add(new RectInt(r.x + h, r.y + h, h, h));
                    r = new RectInt(r.x, r.y, h, h);
                }
                places[i] = r;
            }
            return true;
        }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p *= 2;
            return p;
        }

        // A cell's texture sits inside a small border of its own edge pixels,
        // so mip maps don't bleed the neighbours in
        private static Rect Inner(Cell c, int side)
        {
            return new Rect((c.Place.x + c.Pad) / (float)side, (c.Place.y + c.Pad) / (float)side,
                (c.Place.width - 2 * c.Pad) / (float)side, (c.Place.height - 2 * c.Pad) / (float)side);
        }

        // -------------------------------------------------------------------
        // drawing textures into the atlas
        // -------------------------------------------------------------------

        private static Texture2D BakeProperty(string prop, List<Cell> cells, int side, GeneratedFolder folder, string name)
        {
            Texture sample = null;
            Material shaderOwner = cells[0].Members[0].Material;
            foreach (Cell c in cells)
                foreach (Mat m in c.Members)
                    if (sample == null && m.Saved.T(prop) != null) sample = m.Saved.T(prop);

            TextureImporter sampleImporter = sample != null ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sample)) as TextureImporter : null;
            bool normal = sampleImporter != null ? sampleImporter.textureType == TextureImporterType.NormalMap : IsNormalProperty(shaderOwner, prop);
            bool linear = normal || (sampleImporter != null && !sampleImporter.sRGBTexture);
            Color32 fill = DefaultFill(shaderOwner, prop, normal);

            var pixels = new Color32[side * side];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            foreach (Cell c in cells)
            {
                Texture tex = c.Members[0].Saved.T(prop);
                int inner = c.Place.width - 2 * c.Pad;
                Color32[] content = tex != null ? ReadTexture(tex, inner, inner, linear, normal) : null;
                Put(pixels, side, c, content, fill);
            }

            return folder.SavePng(pixels, side, linear, name + ".png", imp =>
            {
                imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = !linear;
                imp.alphaSource = TextureImporterAlphaSource.FromInput;
                imp.alphaIsTransparency = sampleImporter != null && sampleImporter.alphaIsTransparency;
                imp.mipmapEnabled = true;
                imp.streamingMipmaps = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = side;
                imp.textureCompression = TextureImporterCompression.Compressed;
                imp.crunchedCompression = false;
                if (sampleImporter != null)
                {
                    imp.filterMode = sampleImporter.filterMode;
                    imp.anisoLevel = sampleImporter.anisoLevel;
                }
            });
        }

        private static bool IsNormalProperty(Material m, string prop)
        {
            if (m.shader == null) return false;
            int i = m.shader.FindPropertyIndex(prop);
            return i >= 0 && (m.shader.GetPropertyFlags(i) & ShaderPropertyFlags.Normal) != 0;
        }

        // what the shader reads where a material has no texture
        private static Color32 DefaultFill(Material m, string prop, bool normal)
        {
            if (normal) return new Color32(128, 128, 255, 255);
            string name = "white";
            if (m.shader != null)
            {
                int i = m.shader.FindPropertyIndex(prop);
                if (i >= 0) name = m.shader.GetPropertyTextureDefaultName(i);
            }
            switch (name)
            {
                case "black": return new Color32(0, 0, 0, 0);
                case "gray":
                case "grey":
                case "linearGray":
                case "linearGrey": return new Color32(128, 128, 128, 128);
                case "bump": return new Color32(128, 128, 255, 255);
                case "red": return new Color32(255, 0, 0, 0);
                default: return new Color32(255, 255, 255, 255);
            }
        }

        // Draws a texture at the given size on the graphics card and reads it
        // back. Normal maps come back as plain RGB normals
        private static Color32[] ReadTexture(Texture tex, int w, int h, bool linear, bool normal)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            RenderTexture before = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            read.Apply(false, false);
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] px = read.GetPixels32();
            UnityEngine.Object.DestroyImmediate(read);
            if (normal)
            {
                for (int i = 0; i < px.Length; i++)
                {
                    // Unity keeps normal maps as "x in alpha" or "x in red"; this reads both.
                    float x = px[i].r / 255f * (px[i].a / 255f) * 2f - 1f;
                    float y = px[i].g / 255f * 2f - 1f;
                    float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                    px[i] = new Color32(ToByte(x * 0.5f + 0.5f), ToByte(y * 0.5f + 0.5f), ToByte(z * 0.5f + 0.5f), 255);
                }
            }
            return px;
        }

        private static byte ToByte(float v)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        }

        // writes a cell's pixels and repeats its edges into its border
        private static void Put(Color32[] atlas, int side, Cell c, Color32[] content, Color32 fill)
        {
            int inner = c.Place.width - 2 * c.Pad;
            for (int y = 0; y < c.Place.height; y++)
            {
                int sy = Mathf.Clamp(y - c.Pad, 0, inner - 1);
                for (int x = 0; x < c.Place.width; x++)
                {
                    int sx = Mathf.Clamp(x - c.Pad, 0, inner - 1);
                    atlas[(c.Place.y + y) * side + c.Place.x + x] = content != null ? content[sy * inner + sx] : fill;
                }
            }
        }

        // -------------------------------------------------------------------
        // Quest
        // -------------------------------------------------------------------

        public static Shader QuestShader()
        {
            return Shader.Find(ToonStandard) ?? Shader.Find(ToonLit);
        }

        public static MaterialPlan PlanQuest(List<Renderer> renderers, int atlasMax, GeneratedFolder folder, string baseName)
        {
            var plan = new MaterialPlan();
            Shader shader = QuestShader();
            var cells = new List<Cell>();
            foreach (Material m in Distinct(renderers))
            {
                var info = new Mat { Material = m, Saved = Read(m) };
                Texture main = info.Saved.T("_MainTex") ?? (m.HasProperty("_MainTex") ? m.mainTexture : null);
                info.Size = main != null ? Mathf.Clamp(Math.Max(main.width, main.height), 64, 8192) : 128;
                var cell = new Cell { Size = info.Size };
                cell.Members.Add(info);
                cells.Add(cell);
                float mode = info.Saved.F("_Mode", 0f);
                if (mode > 0.5f || m.renderQueue > 2450) plan.SeeThrough = true;
            }

            int side = PackCells(cells, atlasMax);
            bool linearSpace = PlayerSettings.colorSpace == ColorSpace.Linear;
            var albedo = new Color32[side * side];
            Color32[] normals = null;
            Color32[] emission = null;
            bool anyNormal = false, anyEmission = false;
            foreach (Cell c in cells)
            {
                Saved s = c.Members[0].Saved;
                anyNormal |= s.T("_BumpMap") != null;
                anyEmission |= Glows(s);
            }
            if (anyNormal && shader != null && shader.FindPropertyIndex("_BumpMap") >= 0) normals = Filled(side, new Color32(128, 128, 255, 255));
            if (anyEmission && shader != null && shader.FindPropertyIndex("_EmissionMap") >= 0) emission = Filled(side, new Color32(0, 0, 0, 255));

            foreach (Cell c in cells)
            {
                Mat m = c.Members[0];
                Saved s = m.Saved;
                int inner = c.Place.width - 2 * c.Pad;
                Put(albedo, side, c, BakeColor(s, m.Material, inner, linearSpace), default(Color32));
                if (normals != null)
                {
                    Texture bump = s.T("_BumpMap");
                    Color32[] n = bump != null ? ReadTexture(bump, inner, inner, true, true) : null;
                    if (n != null) ScaleNormals(n, s.F("_BumpScale", 1f));
                    Put(normals, side, c, n, new Color32(128, 128, 255, 255));
                }
                if (emission != null) Put(emission, side, c, Glows(s) ? BakeEmission(s, inner, linearSpace) : null, new Color32(0, 0, 0, 255));
            }

            Action<TextureImporter, bool> quest = (imp, isNormal) =>
            {
                imp.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = !isNormal;
                imp.mipmapEnabled = true;
                imp.streamingMipmaps = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = side;
                imp.textureCompression = TextureImporterCompression.Compressed;
                imp.SetPlatformTextureSettings(new TextureImporterPlatformSettings
                {
                    name = "Android",
                    overridden = true,
                    maxTextureSize = side,
                    format = TextureImporterFormat.ASTC_6x6,
                    textureCompression = TextureImporterCompression.Compressed,
                });
            };
            Texture2D albedoTex = folder.SavePng(albedo, side, false, baseName + " albedo.png", imp => quest(imp, false));
            Texture2D normalTex = normals != null ? folder.SavePng(normals, side, true, baseName + " normal.png", imp => quest(imp, true)) : null;
            Texture2D emissionTex = emission != null ? folder.SavePng(emission, side, false, baseName + " emission.png", imp => quest(imp, false)) : null;

            var mat = new Material(shader != null ? shader : Shader.Find("Standard")) { name = baseName };
            mat.SetTexture("_MainTex", albedoTex);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (normalTex != null)
            {
                mat.SetTexture("_BumpMap", normalTex);
                mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("USE_NORMAL_MAPS");
            }
            if (emissionTex != null)
            {
                mat.SetTexture("_EmissionMap", emissionTex);
                mat.SetColor("_EmissionColor", Color.white);
                if (mat.HasProperty("_EmissionStrength")) mat.SetFloat("_EmissionStrength", 1f);
            }
            if (mat.HasProperty("_Ramp") && mat.GetTexture("_Ramp") == null)
            {
                string rampPath = AssetDatabase.GUIDToAssetPath(RampGuid);
                Texture2D ramp = rampPath.Length > 0 ? AssetDatabase.LoadAssetAtPath<Texture2D>(rampPath) : null;
                if (ramp == null) ramp = Resources.Load<Texture2D>("VRChat/ShadowRampRealisticSoft");
                if (ramp != null) mat.SetTexture("_Ramp", ramp);
            }
            mat = folder.Save(mat, baseName + ".mat");

            plan.Atlases = 1;
            plan.Merged = Math.Max(0, cells.Count - 1);
            plan.Slots.Add(mat);
            foreach (Cell c in cells)
            {
                Mat m = c.Members[0];
                TexEnv main;
                Vector4 st = new Vector4(1f, 1f, 0f, 0f);
                if (m.Saved.Textures.TryGetValue("_MainTex", out main)) st = new Vector4(main.Scale.x, main.Scale.y, main.Offset.x, main.Offset.y);
                plan.Of[m.Material] = new SlotPlan { Slot = 0, Cell = Inner(c, side), St = st, Wrap = true };
            }
            return plan;
        }

        private static Color32[] Filled(int side, Color32 c)
        {
            var px = new Color32[side * side];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            return px;
        }

        private static bool Glows(Saved s)
        {
            Color c = s.C("_EmissionColor", Color.black);
            return s.F("_EnableEmission", 0f) > 0.5f && s.F("_EmissionStrength", 0f) > 0f && (c.r + c.g + c.b) > 0.001f;
        }

        private static void ScaleNormals(Color32[] px, float scale)
        {
            if (Mathf.Abs(scale - 1f) < 1e-3f) return;
            for (int i = 0; i < px.Length; i++)
            {
                float x = (px[i].r / 255f * 2f - 1f) * scale;
                float y = (px[i].g / 255f * 2f - 1f) * scale;
                float z = Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Min(1f, x * x + y * y)));
                var n = new Vector3(x, y, z).normalized;
                px[i] = new Color32(ToByte(n.x * 0.5f + 0.5f), ToByte(n.y * 0.5f + 0.5f), ToByte(n.z * 0.5f + 0.5f), 255);
            }
        }

        // The color the outfit shows now: its texture times its tint, with
        // Poiyomi's Color Adjust (hue, saturation, brightness, gamma) applied
        // the way Poiyomi does it.
        // keepAlpha: cutout parts keep their texture's alpha times the tint's, everything else is solid
        private static Color32[] BakeColor(Saved s, Material m, int size, bool linearSpace, bool keepAlpha = false)
        {
            Texture main = s.T("_MainTex");
            Color32[] px = main != null ? ReadTexture(main, size, size, false, false) : Filled(size, new Color32(255, 255, 255, 255));
            Color tint = s.C("_Color", Color.white);
            Vector3 tintL = linearSpace ? V(tint.linear) : V(tint);

            bool adjust = s.F("_MainColorAdjustToggle", 0f) > 0.5f;
            // read into a linear target, the mask comes back as the shader sees it
            Texture maskTex = adjust ? s.T("_MainColorAdjustTexture") : null;
            Color32[] mask = maskTex != null ? ReadTexture(maskTex, size, size, true, false) : null;
            bool hueOn = s.F("_MainHueShiftToggle", 0f) > 0.5f;
            float hue = s.F("_MainHueShift", 0f);
            float space = s.F("_MainHueShiftColorSpace", 0f);
            float select = s.F("_MainHueShiftSelectOrShift", 1f);
            bool replace = s.F("_MainHueShiftReplace", 1f) > 0.5f;
            float gamma = s.F("_MainGamma", 1f);
            float saturation = s.F("_Saturation", 0f);
            float brightness = s.F("_MainBrightness", 0f);

            for (int i = 0; i < px.Length; i++)
            {
                Vector3 c = new Vector3(px[i].r / 255f, px[i].g / 255f, px[i].b / 255f);
                if (linearSpace) c = ToLinear(c);
                c = Vector3.Scale(c, tintL);
                if (adjust)
                {
                    Vector4 k = mask != null
                        ? new Vector4(mask[i].r / 255f, mask[i].g / 255f, mask[i].b / 255f, mask[i].a / 255f)
                        : Vector4.one;
                    if (hueOn)
                    {
                        if (replace) c = Vector3.Lerp(c, HueShift(c, hue, space, select), k.x);
                        else c = HueShift(c, Frac(hue - (1f - k.x)), space, select);
                    }
                    var powed = new Vector3(Mathf.Pow(Mathf.Abs(c.x), gamma), Mathf.Pow(Mathf.Abs(c.y), gamma), Mathf.Pow(Mathf.Abs(c.z), gamma));
                    c = Vector3.LerpUnclamped(c, powed, k.w);
                    float grey = Vector3.Dot(c, new Vector3(0.3f, 0.59f, 0.11f));
                    c = Vector3.LerpUnclamped(c, new Vector3(grey, grey, grey), -saturation * k.z);
                    c = Vector3.LerpUnclamped(c, c * (brightness + 1f), k.y);
                    c = new Vector3(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z));
                }
                if (linearSpace) c = ToGamma(c);
                byte alpha = keepAlpha ? ToByte(px[i].a / 255f * tint.a) : (byte)255;
                px[i] = new Color32(ToByte(c.x), ToByte(c.y), ToByte(c.z), alpha);
            }
            return px;
        }

        // a material copy needs poiyomi's tags too: without them a locked one can't say which of its
        // settings are the renamed slider ones. The rename suffix comes from the name when there's no
        // tag for it, and a copy has a new name, so that one gets written down
        internal static void CopyTags(Material from, Material to)
        {
            var map = new SerializedObject(from).FindProperty("stringTagMap");
            if (map != null && map.isArray)
            {
                for (int i = 0; i < map.arraySize; i++)
                {
                    var e = map.GetArrayElementAtIndex(i);
                    var first = e.FindPropertyRelative("first");
                    var second = e.FindPropertyRelative("second");
                    if (first != null && second != null) to.SetOverrideTag(first.stringValue, second.stringValue);
                }
            }
            if (Poiyomi.IsLocked(from)) to.SetOverrideTag("thry_rename_suffix", Poiyomi.RenameSuffix(from));
        }

        private static Color32[] BakeEmission(Saved s, int size, bool linearSpace)
        {
            Texture map = s.T("_EmissionMap");
            Color32[] px = map != null ? ReadTexture(map, size, size, false, false) : Filled(size, new Color32(255, 255, 255, 255));
            Color color = s.C("_EmissionColor", Color.white);
            Vector3 colorL = linearSpace ? V(color.linear) : V(color);
            float strength = s.F("_EmissionStrength", 0f);
            Texture maskTex = s.T("_EmissionMask");
            Color32[] mask = maskTex != null ? ReadTexture(maskTex, size, size, true, false) : null;
            int channel = Mathf.Clamp(Mathf.RoundToInt(s.F("_EmissionMaskChannel", 0f)), 0, 3);
            bool invert = s.F("_EmissionMaskInvert", 0f) > 0.5f;
            for (int i = 0; i < px.Length; i++)
            {
                Vector3 c = new Vector3(px[i].r / 255f, px[i].g / 255f, px[i].b / 255f);
                if (linearSpace) c = ToLinear(c);
                float k = 1f;
                if (mask != null)
                {
                    Color32 mk = mask[i];
                    k = (channel == 0 ? mk.r : channel == 1 ? mk.g : channel == 2 ? mk.b : mk.a) / 255f;
                    if (invert) k = 1f - k;
                }
                c = Vector3.Scale(c, colorL) * strength * k;
                c = new Vector3(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z));
                if (linearSpace) c = ToGamma(c);
                px[i] = new Color32(ToByte(c.x), ToByte(c.y), ToByte(c.z), 255);
            }
            return px;
        }

        private static Vector3 V(Color c)
        {
            return new Vector3(c.r, c.g, c.b);
        }

        private static Vector3 ToLinear(Vector3 c)
        {
            return new Vector3(Mathf.GammaToLinearSpace(c.x), Mathf.GammaToLinearSpace(c.y), Mathf.GammaToLinearSpace(c.z));
        }

        private static Vector3 ToGamma(Vector3 c)
        {
            return new Vector3(Mathf.LinearToGammaSpace(c.x), Mathf.LinearToGammaSpace(c.y), Mathf.LinearToGammaSpace(c.z));
        }

        private static float Frac(float x)
        {
            return x - Mathf.Floor(x);
        }

        // Poiyomi's hueShift(): OKLab by default, HSV when the color space is set to 1
        private static Vector3 HueShift(Vector3 c, float shift, float colorSpace, float selectOrShift)
        {
            Vector3 oklab = HueShiftOkLab(c, shift, selectOrShift);
            Vector3 hsv = HueShiftHsv(c, shift, selectOrShift);
            return Vector3.Lerp(oklab, hsv, Mathf.Clamp01(colorSpace));
        }

        private static Vector3 HueShiftOkLab(Vector3 color, float shift, float selectOrShift)
        {
            float l = 0.4122214708f * color.x + 0.5363325363f * color.y + 0.0514459929f * color.z;
            float m = 0.2119034982f * color.x + 0.6806995451f * color.y + 0.1073969566f * color.z;
            float s = 0.0883024619f * color.x + 0.2817188376f * color.y + 0.6299787005f * color.z;
            float l_ = Cbrt(l), m_ = Cbrt(m), s_ = Cbrt(s);
            float L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
            float a = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
            float b = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;
            float chroma = Mathf.Sqrt(a * a + b * b);
            if (selectOrShift <= 0.5f)
            {
                a = chroma;
                b = 0f;
            }
            float sin = Mathf.Sin(shift * Mathf.PI * 2f), cos = Mathf.Cos(shift * Mathf.PI * 2f);
            float a2 = cos * a - sin * b;
            float b2 = sin * a + cos * b;
            float l2 = L + 0.3963377774f * a2 + 0.2158037573f * b2;
            float m2 = L - 0.1055613458f * a2 - 0.0638541728f * b2;
            float s2 = L - 0.0894841775f * a2 - 1.2914855480f * b2;
            l2 = l2 * l2 * l2;
            m2 = m2 * m2 * m2;
            s2 = s2 * s2 * s2;
            return new Vector3(
                4.0767416621f * l2 - 3.3077115913f * m2 + 0.2309699292f * s2,
                -1.2684380046f * l2 + 2.6097574011f * m2 - 0.3413193965f * s2,
                -0.0041960863f * l2 - 0.7034186147f * m2 + 1.7076147010f * s2);
        }

        private static float Cbrt(float x)
        {
            return x < 0f ? -Mathf.Pow(-x, 1f / 3f) : Mathf.Pow(x, 1f / 3f);
        }

        private static Vector3 HueShiftHsv(Vector3 color, float offset, float selectOrShift)
        {
            Vector3 hsv = RgbToHsv(color);
            hsv.x = hsv.x * selectOrShift + offset;
            return HsvToRgb(hsv);
        }

        // Poiyomi's RGBtoHSV, step for step.
        private static Vector3 RgbToHsv(Vector3 c)
        {
            Vector4 p = c.y >= c.z ? new Vector4(c.y, c.z, 0f, -1f / 3f) : new Vector4(c.z, c.y, -1f, 2f / 3f);
            Vector4 q = c.x >= p.x ? new Vector4(c.x, p.y, p.z, p.x) : new Vector4(p.x, p.y, p.w, c.x);
            float d = q.x - Mathf.Min(q.w, q.y);
            const float e = 1.0e-10f;
            return new Vector3(Mathf.Abs(q.z + (q.w - q.y) / (6f * d + e)), d / (q.x + e), q.x);
        }

        private static Vector3 HsvToRgb(Vector3 c)
        {
            float px = Mathf.Abs(Frac(c.x + 1f) * 6f - 3f);
            float py = Mathf.Abs(Frac(c.x + 2f / 3f) * 6f - 3f);
            float pz = Mathf.Abs(Frac(c.x + 1f / 3f) * 6f - 3f);
            return c.z * Vector3.Lerp(Vector3.one, new Vector3(Mathf.Clamp01(px - 1f), Mathf.Clamp01(py - 1f), Mathf.Clamp01(pz - 1f)), c.y);
        }
    }
}
#endif
