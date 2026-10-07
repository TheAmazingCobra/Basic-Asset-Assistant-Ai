#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    // something the extreme version changes on one material, listed before its made
    internal sealed class ExtremeNote
    {
        public string Material;
        public string Text;
    }

    // the extreme version: one fresh poiyomi material for the whole outfit. a locked material has its
    // features compiled in, so it cant just get new textures, the new one is built on the installed
    // poiyomi with the settings of the material that covers most of the outfit. every materials look
    // (texture, tint, color adjust, decals) is drawn into one atlas, normal maps and glow get atlases
    // too, and features with a mask stay on just the parts that had them. see-through and non poiyomi
    // materials keep there own slot
    internal static partial class AtlasBuilder
    {
        private const string PoiyomiToon = ".poiyomi/Poiyomi Toon";
        // what a lock adds to a material, it means nothing on a fresh one
        private static readonly HashSet<string> LockOnly = new HashSet<string>(StringComparer.Ordinal) { "_ShaderOptimizerEnabled", "_ForgotToLockMaterial" };
        private static readonly string[] DecalKeywords = { "GEOM_TYPE_BRANCH", "GEOM_TYPE_BRANCH_DETAIL", "GEOM_TYPE_FROND", "DEPTH_OF_FIELD_COC_VIEW" };
        private static readonly string[] EmissionKeywords = { "_EMISSION", "POI_EMISSION_1", "POI_EMISSION_2", "POI_EMISSION_3" };

        // a poiyomi feature: its switch, its keyword, the settings that belong to it, and the mask that
        // can keep it to some parts. UvBound ones draw a pattern over UV0, the atlas would squash it
        private sealed class Feature
        {
            public string Name;
            public string Toggle;
            public string Keyword;
            public string Prefix;
            public string Mask;
            public bool Packed;
            public bool UvBound;
        }

        private static readonly Feature[] Features =
        {
            new Feature { Name = Lang.N("matcap"), Toggle = "_MatcapEnable", Keyword = "POI_MATCAP0", Prefix = "_Matcap", Mask = "_MatcapMask" },
            new Feature { Name = Lang.N("second matcap"), Toggle = "_Matcap2Enable", Keyword = "COLOR_GRADING_HDR_3D", Prefix = "_Matcap2", Mask = "_Matcap2Mask" },
            new Feature { Name = Lang.N("third matcap"), Toggle = "_Matcap3Enable", Keyword = "POI_MATCAP2", Prefix = "_Matcap3", Mask = "_Matcap3Mask" },
            new Feature { Name = Lang.N("fourth matcap"), Toggle = "_Matcap4Enable", Keyword = "POI_MATCAP3", Prefix = "_Matcap4", Mask = "_Matcap4Mask" },
            new Feature { Name = Lang.N("rim light"), Toggle = "_EnableRimLighting", Keyword = "_GLOSSYREFLECTIONS_OFF", Prefix = "_Rim", Mask = "_RimMask" },
            new Feature { Name = Lang.N("second rim light"), Toggle = "_EnableRim2Lighting", Keyword = "POI_RIM2", Prefix = "_Rim2", Mask = "_Rim2Mask" },
            new Feature { Name = Lang.N("cubemap"), Toggle = "_CubeMapEnabled", Keyword = "_CUBEMAP", Prefix = "_CubeMap", Mask = "_CubeMapMask" },
            new Feature { Name = Lang.N("reflections"), Toggle = "_MochieBRDF", Keyword = "MOCHIE_PBR", Prefix = "_Mochie", Mask = "_MochieMetallicMaps", Packed = true },
            new Feature { Name = Lang.N("clear coat"), Toggle = "_ClearCoatBRDF", Keyword = "POI_CLEARCOAT", Prefix = "_ClearCoat", Mask = "_ClearCoatMaps", Packed = true },
            new Feature { Name = Lang.N("glitter"), Toggle = "_GlitterEnable", Keyword = "_SUNDISK_SIMPLE", Prefix = "_Glitter", UvBound = true },
            new Feature { Name = Lang.N("detail texture"), Toggle = "_DetailEnabled", Keyword = "FINALPASS", Prefix = "_Detail", UvBound = true },
            new Feature { Name = Lang.N("dissolve"), Toggle = "_EnableDissolve", Keyword = "DISTORT", Prefix = "_Dissolve", UvBound = true },
            new Feature { Name = Lang.N("flipbook"), Toggle = "_EnableFlipbook", Keyword = "_SUNDISK_HIGH_QUALITY", Prefix = "_Flipbook", UvBound = true },
            new Feature { Name = Lang.N("text"), Toggle = "_TextEnabled", Keyword = "EFFECT_BUMP", Prefix = "_Text", UvBound = true },
            new Feature { Name = Lang.N("subsurface scattering"), Toggle = "_SubsurfaceScattering", Keyword = "POI_SUBSURFACESCATTERING", Prefix = "_SSS" },
            new Feature { Name = Lang.N("backlight"), Toggle = "_BacklightEnabled", Keyword = "POI_BACKLIGHT", Prefix = "_Backlight" },
            new Feature { Name = Lang.N("stylized specular"), Toggle = "_StylizedSpecular", Keyword = "POI_STYLIZED_StylizedSpecular", Prefix = "_StylizedSpecular" },
            new Feature { Name = Lang.N("anisotropic shine"), Toggle = "_EnableAniso", Keyword = "POI_ANISOTROPICS", Prefix = "_Aniso" },
            new Feature { Name = Lang.N("environment rim"), Toggle = "_EnableEnvironmentalRim", Keyword = "POI_ENVIRORIM", Prefix = "_RimEnviro" },
        };

        private sealed class Layout
        {
            public readonly List<Mat> Members = new List<Mat>();
            public readonly List<Material> Separate = new List<Material>();
            public readonly List<ExtremeNote> Notes = new List<ExtremeNote>();
            // the feature, and the material whose settings it keeps
            public readonly Dictionary<Feature, Mat> Kept = new Dictionary<Feature, Mat>();
            public Mat Base;
            public Shader Shader;
        }

        // what the extreme version would do with these renderers, without making anything
        public static List<ExtremeNote> ExtremeNotes(List<Renderer> renderers)
        {
            return LayOut(renderers, null).Notes;
        }

        public static MaterialPlan PlanExtreme(List<Renderer> renderers, int atlasMax, GeneratedFolder folder, string baseName)
        {
            var plan = new MaterialPlan();
            HashSet<Material> decalApart = SplitDecals(renderers, folder, baseName, plan);
            Layout layout = LayOut(renderers, decalApart);
            if (layout.Members.Count > 0 && layout.Shader == null)
                throw new InvalidOperationException(Lang.T("The Extreme version needs Poiyomi installed. The Check tab shows how to get it."));
            if (layout.Members.Count > 0 && !CanDraw)
            {
                plan.NoGraphics = true;
                foreach (Mat m in layout.Members) layout.Separate.Add(m.Material);
                layout.Members.Clear();
            }

            if (layout.Members.Count > 0)
            {
                var cells = new List<Cell>();
                foreach (Mat m in layout.Members)
                {
                    var c = new Cell { Size = m.Size };
                    c.Members.Add(m);
                    cells.Add(c);
                }
                int side = PackCells(cells, atlasMax);
                Material merged = BakeExtreme(layout, cells, side, renderers, folder, baseName);
                plan.Slots.Add(merged);
                plan.Atlases = 1;
                plan.Merged = cells.Count - 1;
                foreach (Cell c in cells)
                {
                    Mat m = c.Members[0];
                    TexEnv main;
                    var st = new Vector4(1f, 1f, 0f, 0f);
                    if (m.Saved.Textures.TryGetValue("_MainTex", out main)) st = new Vector4(main.Scale.x, main.Scale.y, main.Offset.x, main.Offset.y);
                    plan.Of[m.Material] = new SlotPlan { Slot = 0, Cell = Inner(c, side), St = st, Wrap = true };
                }
            }
            foreach (Material s in layout.Separate)
            {
                plan.Of[s] = new SlotPlan { Slot = plan.Slots.Count };
                plan.Slots.Add(s);
            }
            return plan;
        }

        // -------------------------------------------------------------------
        // deciding who goes on the atlas
        // -------------------------------------------------------------------

        // decalApart null means preview: nothing is made yet, so decals that clash get split off later.
        // after the split the halves aren't checked again, the split already made them fit
        private static Layout LayOut(List<Renderer> renderers, HashSet<Material> decalApart)
        {
            var layout = new Layout();
            Dictionary<Material, long> triangles = TrianglesPerMaterial(renderers);
            foreach (Material material in Distinct(renderers))
            {
                var m = new Mat { Material = material, Saved = Read(material) };
                string why = null;
                if (OriginalShaderName(material).IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) < 0)
                    why = Lang.T("{0} keeps its own material, it doesn't use Poiyomi.", material.name);
                // opaque and cutout go on the atlas, anything that blends doesn't
                else if (m.Saved.F("_Mode", 0f) > 1.5f || material.renderQueue > 2500)
                    why = Lang.T("{0} keeps its own material, it's see-through.", material.name);
                else
                {
                    int spots = 1;
                    if (decalApart != null) spots = decalApart.Contains(material) ? 0 : 1;
                    else
                    {
                        DecalLayers layers = LayerDecals(m, renderers);
                        if (layers != null) spots = layers.Overflow ? 0 : layers.Count;
                    }
                    if (spots == 0) why = Lang.T("{0} keeps its own material, its decal can't go on the atlas.", material.name);
                    else if (spots == 2) Note(layout, m, Lang.T("{0} gets a second spot on the atlas for its decal.", material.name));
                    else if (spots > 2) Note(layout, m, Lang.T("{0} gets {1} spots on the atlas for its decal.", material.name, spots));
                }
                if (why != null)
                {
                    layout.Separate.Add(material);
                    layout.Notes.Add(new ExtremeNote { Material = material.name, Text = why });
                    continue;
                }
                foreach (var pair in m.Saved.Textures)
                {
                    if (!IsUvMapped(pair.Key, pair.Value.Texture, m.Saved, true)) continue;
                    m.UvProps.Add(pair.Key);
                    if (pair.Value.Texture != null && (pair.Key == "_MainTex" || pair.Key == "_BumpMap" || pair.Key == "_EmissionMap"))
                        m.Size = Math.Max(m.Size, Math.Max(pair.Value.Texture.width, pair.Value.Texture.height));
                }
                m.Size = Mathf.Clamp(m.Size > 0 ? m.Size : 256, 64, 8192);
                layout.Members.Add(m);
            }
            if (layout.Members.Count == 0) return layout;

            long most = -1;
            foreach (Mat m in layout.Members)
            {
                long t;
                triangles.TryGetValue(m.Material, out t);
                if (t > most)
                {
                    most = t;
                    layout.Base = m;
                }
            }
            layout.Shader = Shader.Find(OriginalShaderName(layout.Base.Material)) ?? Shader.Find(PoiyomiToon);

            foreach (Feature f in Features)
            {
                var with = layout.Members.FindAll(m => m.Saved.F(f.Toggle, 0f) > 0.5f);
                if (with.Count == 0) continue;
                if (f.UvBound)
                {
                    foreach (Mat m in with) Note(layout, m, Lang.T("{0} loses its {1}.", m.Material.name, Lang.T(f.Name)));
                    continue;
                }
                Mat owner = with.Contains(layout.Base) ? layout.Base : Biggest(with, triangles);
                layout.Kept[f] = owner;
                foreach (Mat m in with)
                    if (m != owner && !SameFeature(f, m.Saved, owner.Saved))
                        Note(layout, m, Lang.T("{0} loses its {1}, another part's is used instead.", m.Material.name, Lang.T(f.Name)));
                // without a mask the feature reaches everything on the atlas
                if (f.Mask != null) continue;
                foreach (Mat m in layout.Members)
                    if (!with.Contains(m)) Note(layout, m, Lang.T("{0} gets the {1} too.", m.Material.name, Lang.T(f.Name)));
            }
            return layout;
        }

        private static void Note(Layout layout, Mat m, string text)
        {
            layout.Notes.Add(new ExtremeNote { Material = m.Material.name, Text = text });
        }

        private static Mat Biggest(List<Mat> list, Dictionary<Material, long> triangles)
        {
            Mat best = list[0];
            long most = -1;
            foreach (Mat m in list)
            {
                long t;
                triangles.TryGetValue(m.Material, out t);
                if (t > most)
                {
                    most = t;
                    best = m;
                }
            }
            return best;
        }

        private static Dictionary<Material, long> TrianglesPerMaterial(List<Renderer> renderers)
        {
            var result = new Dictionary<Material, long>();
            foreach (Renderer r in renderers)
            {
                Mesh mesh = MeshOf(r);
                if (mesh == null) continue;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    int si = Math.Min(i, mesh.subMeshCount - 1);
                    if (mats[i] == null || si < 0) continue;
                    long t;
                    result.TryGetValue(mats[i], out t);
                    result[mats[i]] = t + (long)mesh.GetIndexCount(si) / 3;
                }
            }
            return result;
        }

        // the same feature with the same settings, leaving out its mask, which may differ
        private static bool SameFeature(Feature f, Saved a, Saved b)
        {
            foreach (var p in a.Floats)
            {
                if (!Belongs(p.Key, f)) continue;
                float other;
                if (!b.Floats.TryGetValue(p.Key, out other) || Mathf.Abs(p.Value - other) > 1e-4f) return false;
            }
            foreach (var p in a.Colors)
            {
                if (!Belongs(p.Key, f)) continue;
                Color other;
                if (!b.Colors.TryGetValue(p.Key, out other) || ((Vector4)p.Value - (Vector4)other).sqrMagnitude > 1e-8f) return false;
            }
            foreach (var p in a.Textures)
                if (Belongs(p.Key, f) && p.Value.Texture != b.T(p.Key)) return false;
            return true;
        }

        // "_MatcapColor" is the first matcap's, "_Matcap2Color" isn't
        private static bool Belongs(string name, Feature f)
        {
            if (!name.StartsWith(f.Prefix, StringComparison.Ordinal)) return false;
            if (name.Length > f.Prefix.Length && char.IsDigit(name[f.Prefix.Length])) return false;
            return f.Mask == null || !name.StartsWith(f.Mask, StringComparison.Ordinal);
        }

        // -------------------------------------------------------------------
        // drawing the atlases and building the material
        // -------------------------------------------------------------------

        private static Material BakeExtreme(Layout layout, List<Cell> cells, int side, List<Renderer> renderers, GeneratedFolder folder, string baseName)
        {
            bool linearSpace = PlayerSettings.colorSpace == ColorSpace.Linear;
            bool anyNormal = layout.Members.Exists(m => m.Saved.T("_BumpMap") != null);
            bool anyGlow = layout.Members.Exists(m => GlowSlots(m.Saved).Count > 0 || HasDecalGlow(m.Saved));

            var albedo = Filled(side, new Color32(0, 0, 0, 255));
            Color32[] normals = anyNormal ? Filled(side, new Color32(128, 128, 255, 255)) : null;
            Color32[] glow = anyGlow ? Filled(side, new Color32(0, 0, 0, 255)) : null;
            var masks = new Dictionary<Feature, Color32[]>();
            foreach (var pair in layout.Kept)
                if (pair.Key.Mask != null) masks[pair.Key] = Filled(side, new Color32(0, 0, 0, 0));

            foreach (Cell c in cells)
            {
                Mat m = c.Members[0];
                Saved s = m.Saved;
                int inner = c.Place.width - 2 * c.Pad;
                Color32[] color = BakeColor(s, m.Material, inner, linearSpace, Cutout(m));
                Color32[] light = anyGlow ? BakeGlow(s, color, inner, linearSpace) : null;
                BakeDecals(m, renderers, inner, color, light, linearSpace);
                Put(albedo, side, c, color, default(Color32));
                if (light != null) Put(glow, side, c, light, new Color32(0, 0, 0, 255));
                if (normals != null)
                {
                    Texture bump = s.T("_BumpMap");
                    Color32[] n = bump != null ? ReadTexture(bump, inner, inner, true, true) : null;
                    if (n != null) ScaleNormals(n, s.F("_BumpScale", 1f));
                    Put(normals, side, c, n, new Color32(128, 128, 255, 255));
                }
                foreach (var pair in masks)
                {
                    Feature f = pair.Key;
                    Mat owner = layout.Kept[f];
                    bool on = s.F(f.Toggle, 0f) > 0.5f && (m == owner || SameFeature(f, s, owner.Saved));
                    Put(pair.Value, side, c, on ? FeatureMask(f, s, inner) : null, new Color32(0, 0, 0, 0));
                }
            }

            Mat b = layout.Base;
            var mat = new Material(layout.Shader) { name = baseName };
            foreach (var p in b.Saved.Floats)
                if (!LockOnly.Contains(p.Key) && mat.HasProperty(p.Key)) mat.SetFloat(p.Key, p.Value);
            foreach (var p in b.Saved.Colors)
                if (mat.HasProperty(p.Key)) mat.SetColor(p.Key, p.Value);
            foreach (var p in b.Saved.Textures)
            {
                if (!mat.HasProperty(p.Key)) continue;
                mat.SetTexture(p.Key, p.Value.Texture);
                mat.SetTextureScale(p.Key, p.Value.Scale);
                mat.SetTextureOffset(p.Key, p.Value.Offset);
            }
            var keywords = new HashSet<string>(Keywords(b.Material));

            // a cutout part on an opaque base: the whole thing turns cutout, solid parts have full alpha
            Mat cutout = Cutout(b) ? b : layout.Members.Find(Cutout);
            if (cutout != null && cutout != b)
            {
                foreach (string p in RenderState)
                {
                    float v;
                    if (cutout.Saved.Floats.TryGetValue(p, out v)) Set(mat, p, v);
                }
                mat.SetOverrideTag("RenderType", cutout.Material.GetTag("RenderType", false, "TransparentCutout"));
            }
            int queue = cutout != null ? cutout.Material.renderQueue : b.Material.renderQueue;

            // everything below was drawn into the atlas, so the material does it no more
            mat.SetColor("_Color", Color.white);
            Set(mat, "_MainColorAdjustToggle", 0f);
            keywords.Remove("COLOR_GRADING_HDR");
            for (int d = 0; d < 4; d++) Set(mat, "_DecalEnabled" + (d == 0 ? "" : d.ToString()), 0f);
            foreach (string k in DecalKeywords) keywords.Remove(k);
            for (int e = 0; e < 4; e++) Set(mat, "_EnableEmission" + (e == 0 ? "" : e.ToString()), 0f);
            foreach (string k in EmissionKeywords) keywords.Remove(k);

            foreach (Feature f in Features)
            {
                Mat owner;
                if (!layout.Kept.TryGetValue(f, out owner))
                {
                    Set(mat, f.Toggle, 0f);
                    keywords.Remove(f.Keyword);
                    continue;
                }
                if (owner != b) CopyFeature(f, owner.Saved, mat);
                Set(mat, f.Toggle, 1f);
                keywords.Add(f.Keyword);
            }

            // every texture laid out on the mesh moves to the atlas layout: the main ones get theirs,
            // masks of kept features get theirs, the rest get one with each part's own texture in it
            var handled = new HashSet<string>(StringComparer.Ordinal) { "_MainTex", "_BumpMap", "_MainColorAdjustTexture", "_DecalMask", "_AlphaMask" };
            for (int e = 0; e < 4; e++)
            {
                string k = e == 0 ? "" : e.ToString();
                handled.Add("_EmissionMap" + k);
                handled.Add("_EmissionMask" + k);
            }
            for (int d = 0; d < 4; d++) handled.Add("_DecalTexture" + (d == 0 ? "" : d.ToString()));
            foreach (Feature f in Features)
                if (f.Mask != null) handled.Add(f.Mask);

            Texture2D albedoTex = SaveAtlas(folder, baseName + " albedo.png", albedo, side, false, false, true);
            SetAtlas(mat, "_MainTex", albedoTex);
            if (normals != null)
            {
                SetAtlas(mat, "_BumpMap", SaveAtlas(folder, baseName + " normal.png", normals, side, true, true, false));
                Set(mat, "_BumpScale", 1f);
            }
            else if (mat.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", null);
            if (glow != null)
            {
                SetAtlas(mat, "_EmissionMap", SaveAtlas(folder, baseName + " emission.png", glow, side, false, false, false));
                if (mat.HasProperty("_EmissionMask")) mat.SetTexture("_EmissionMask", null);
                mat.SetColor("_EmissionColor", Color.white);
                Set(mat, "_EmissionStrength", 1f);
                Set(mat, "_EmissionBaseColorAsMap", 0f);
                Set(mat, "_EnableEmission", 1f);
                keywords.Add("_EMISSION");
            }
            foreach (var pair in masks)
            {
                Feature f = pair.Key;
                SetAtlas(mat, f.Mask, SaveAtlas(folder, baseName + " " + Readable(f.Mask) + ".png", pair.Value, side, true, false, false));
                if (!f.Packed)
                {
                    Set(mat, f.Mask + "Channel", 0f);
                    Set(mat, f.Mask + "Invert", 0f);
                }
            }
            foreach (string p in new[] { "_MainColorAdjustTexture", "_DecalMask", "_AlphaMask" })
                if (mat.HasProperty(p)) mat.SetTexture(p, null);

            // only slots some part has a texture in and the merged material reads. poiyomi gives every slot a
            // UV picker, empty or not, and a blank full size atlas for each one cost hundreds of MB
            var rest = new List<string>();
            foreach (Mat m in layout.Members)
                foreach (string p in m.UvProps)
                    if (!handled.Contains(p) && !rest.Contains(p) && mat.HasProperty(p) && !OwnedByDroppedFeature(p, layout)
                        && layout.Members.Exists(x => x.Saved.T(p) != null) && TextureUse.Uses(mat, p)) rest.Add(p);
            foreach (string p in rest) SetAtlas(mat, p, BakeProperty(p, cells, side, folder, baseName + " " + Readable(p)));

            mat.shaderKeywords = new List<string>(keywords).ToArray();
            mat.renderQueue = queue;
            return folder.Save(mat, baseName + ".mat");
        }

        // the settings that make a poiyomi material cutout
        private static readonly string[] RenderState =
        {
            "_Mode", "_Cutoff", "_SrcBlend", "_DstBlend", "_SrcBlendAlpha", "_DstBlendAlpha", "_BlendOp", "_BlendOpAlpha",
            "_ZWrite", "_ZTest", "_AlphaToCoverage", "_AlphaToMask", "_AlphaForceOpaque", "_AlphaPremultiply",
        };

        private static bool Cutout(Mat m)
        {
            return m.Saved.F("_Mode", 0f) > 0.5f;
        }

        // textures of a feature the atlas can't keep don't need an atlas of their own
        private static bool OwnedByDroppedFeature(string prop, Layout layout)
        {
            foreach (Feature f in Features)
                if (prop.StartsWith(f.Prefix, StringComparison.Ordinal) && !layout.Kept.ContainsKey(f)) return true;
            return false;
        }

        private static void Set(Material m, string prop, float value)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, value);
        }

        // an atlas sits on UV0 with nothing moved or panned
        private static void SetAtlas(Material m, string prop, Texture tex)
        {
            if (!m.HasProperty(prop)) return;
            m.SetTexture(prop, tex);
            m.SetTextureScale(prop, Vector2.one);
            m.SetTextureOffset(prop, Vector2.zero);
            Set(m, prop + "UV", 0f);
            if (m.HasProperty(prop + "Pan")) m.SetVector(prop + "Pan", Vector4.zero);
        }

        private static void CopyFeature(Feature f, Saved from, Material to)
        {
            foreach (var p in from.Floats)
                if (Belongs(p.Key, f) && to.HasProperty(p.Key)) to.SetFloat(p.Key, p.Value);
            foreach (var p in from.Colors)
                if (Belongs(p.Key, f) && to.HasProperty(p.Key)) to.SetColor(p.Key, p.Value);
            foreach (var p in from.Textures)
            {
                if (!Belongs(p.Key, f) || !to.HasProperty(p.Key)) continue;
                to.SetTexture(p.Key, p.Value.Texture);
                to.SetTextureScale(p.Key, p.Value.Scale);
                to.SetTextureOffset(p.Key, p.Value.Offset);
            }
        }

        private static Texture2D SaveAtlas(GeneratedFolder folder, string file, Color32[] pixels, int side, bool linear, bool normal, bool keepAlpha)
        {
            return folder.SavePng(pixels, side, linear, file, imp =>
            {
                imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.sRGBTexture = !linear;
                imp.alphaSource = keepAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                imp.mipmapEnabled = true;
                imp.streamingMipmaps = true;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = side;
                imp.textureCompression = TextureImporterCompression.Compressed;
                imp.crunchedCompression = false;
            });
        }

        // a feature mask as the merged material reads it: one value in red, from the part's own mask
        // (its channel and invert worked out), or on everywhere when it has no mask. Packed maps copy as is
        private static Color32[] FeatureMask(Feature f, Saved s, int size)
        {
            Texture tex = s.T(f.Mask);
            if (f.Packed) return tex != null ? ReadTexture(tex, size, size, true, false) : Filled(size, new Color32(255, 255, 255, 255));
            if (tex == null) return Filled(size, new Color32(255, 255, 255, 255));
            Color32[] px = ReadTexture(tex, size, size, true, false);
            int channel = Mathf.Clamp(Mathf.RoundToInt(s.F(f.Mask + "Channel", 0f)), 0, 3);
            bool invert = s.F(f.Mask + "Invert", 0f) > 0.5f;
            for (int i = 0; i < px.Length; i++)
            {
                byte v = channel == 0 ? px[i].r : channel == 1 ? px[i].g : channel == 2 ? px[i].b : px[i].a;
                if (invert) v = (byte)(255 - v);
                px[i] = new Color32(v, v, v, 255);
            }
            return px;
        }

        // -------------------------------------------------------------------
        // glow
        // -------------------------------------------------------------------

        private static List<int> GlowSlots(Saved s)
        {
            var slots = new List<int>();
            for (int e = 0; e < 4; e++)
            {
                string k = e == 0 ? "" : e.ToString();
                Color c = s.C("_EmissionColor" + k, Color.black);
                if (s.F("_EnableEmission" + k, 0f) > 0.5f && s.F("_EmissionStrength" + k, 0f) > 0f && c.r + c.g + c.b > 0.001f) slots.Add(e);
            }
            return slots;
        }

        private static bool HasDecalGlow(Saved s)
        {
            for (int d = 0; d < 4; d++)
            {
                string k = d == 0 ? "" : d.ToString();
                if (s.F("_DecalEnabled" + k, 0f) > 0.5f && s.F("_DecalEmissionStrength" + k, 0f) > 0f) return true;
            }
            return false;
        }

        // every emission slot that's on, added up. "use base colors" reads the baked albedo
        private static Color32[] BakeGlow(Saved s, Color32[] albedo, int size, bool linearSpace)
        {
            var sum = new Vector3[size * size];
            foreach (int e in GlowSlots(s))
            {
                string k = e == 0 ? "" : e.ToString();
                Texture map = s.T("_EmissionMap" + k);
                Color32[] px = s.F("_EmissionBaseColorAsMap" + k, 0f) > 0.5f ? albedo
                    : map != null ? ReadTexture(map, size, size, false, false) : null;
                Color color = s.C("_EmissionColor" + k, Color.white);
                Vector3 colorL = linearSpace ? V(color.linear) : V(color);
                float strength = s.F("_EmissionStrength" + k, 0f);
                Texture maskTex = s.T("_EmissionMask" + k);
                Color32[] mask = maskTex != null ? ReadTexture(maskTex, size, size, true, false) : null;
                int channel = Mathf.Clamp(Mathf.RoundToInt(s.F(e == 0 ? "_EmissionMaskChannel" : "_EmissionMask" + k + "Channel", 0f)), 0, 3);
                bool invert = s.F("_EmissionMaskInvert" + k, 0f) > 0.5f;
                for (int i = 0; i < sum.Length; i++)
                {
                    Vector3 c = px != null ? new Vector3(px[i].r / 255f, px[i].g / 255f, px[i].b / 255f) : Vector3.one;
                    if (linearSpace) c = ToLinear(c);
                    float m = 1f;
                    if (mask != null)
                    {
                        Color32 mk = mask[i];
                        m = (channel == 0 ? mk.r : channel == 1 ? mk.g : channel == 2 ? mk.b : mk.a) / 255f;
                        if (invert) m = 1f - m;
                    }
                    sum[i] += Vector3.Scale(c, colorL) * strength * m;
                }
            }
            return ToPixels(sum, linearSpace);
        }

        private static Color32[] ToPixels(Vector3[] colors, bool linearSpace)
        {
            var px = new Color32[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                Vector3 c = new Vector3(Mathf.Clamp01(colors[i].x), Mathf.Clamp01(colors[i].y), Mathf.Clamp01(colors[i].z));
                if (linearSpace) c = ToGamma(c);
                px[i] = new Color32(ToByte(c.x), ToByte(c.y), ToByte(c.z), 255);
            }
            return px;
        }

        // -------------------------------------------------------------------
        // decals, drawn the way poiyomi places them (decalUV in the shader)
        // -------------------------------------------------------------------

        private sealed class Decal
        {
            public int Channel;
            public Vector2 Position;
            public float Rotation;
            public Vector2 Scale;
            public Vector4 Side;
            public Color Color;
            public bool Tiled;
            public float Blend;
            public float Alpha;
            public float Intensity;
            public float Glow;
            public int Symmetry;
            public bool Hue;
            public float HueShift;
            public float HueSpace;
            public float HueSelect;
            public Vector4 St;
            public Color32[] Pixels;
            public int W;
            public int H;
            public int MaskChannel;
        }

        private static List<Decal> DecalsOf(Saved s, bool withPixels)
        {
            var list = new List<Decal>();
            for (int d = 0; d < 4; d++)
            {
                string k = d == 0 ? "" : d.ToString();
                if (s.F("_DecalEnabled" + k, 0f) < 0.5f) continue;
                int channel = Mathf.RoundToInt(s.F("_DecalTextureUV" + k, 0f));
                // panosphere, world space and the like aren't laid out on the mesh
                if (channel < 0 || channel > 3) continue;
                Color pos = s.C("_DecalPosition" + k, new Color(0.5f, 0.5f, 0f, 0f));
                Color scale = s.C("_DecalScale" + k, new Color(1f, 1f, 1f, 0f));
                Color side = s.C("_DecalSideOffset" + k, Color.clear);
                TexEnv tex;
                s.Textures.TryGetValue("_DecalTexture" + k, out tex);
                var decal = new Decal
                {
                    Channel = channel,
                    Position = new Vector2(pos.r, pos.g),
                    Rotation = s.F("_DecalRotation" + k, 0f),
                    Scale = new Vector2(scale.r, scale.g),
                    Side = new Vector4(side.r, side.g, side.b, side.a),
                    Color = s.C("_DecalColor" + k, Color.white),
                    Tiled = s.F("_DecalTiled" + k, 0f) > 0.5f,
                    Blend = s.F("_DecalBlendType" + k, 0f),
                    Alpha = s.F("_DecalBlendAlpha" + k, 1f),
                    Intensity = s.F("_DecalAlphaIntensity" + k, 1f),
                    Glow = s.F("_DecalEmissionStrength" + k, 0f),
                    Symmetry = Mathf.RoundToInt(s.F("_DecalSymmetryMode" + k, 0f)),
                    Hue = s.F("_DecalHueShiftEnabled" + k, 0f) > 0.5f,
                    HueShift = s.F("_DecalHueShift" + k, 0f),
                    HueSpace = s.F("_DecalHueShiftColorSpace" + k, 0f),
                    HueSelect = s.F("_DecalHueShiftSelectOrShift" + k, 1f),
                    St = tex.Texture != null ? new Vector4(tex.Scale.x, tex.Scale.y, tex.Offset.x, tex.Offset.y) : new Vector4(1f, 1f, 0f, 0f),
                    MaskChannel = Mathf.Clamp(Mathf.RoundToInt(s.F("_Decal" + d + "MaskChannel", 0f)), 0, 3),
                };
                if (withPixels && tex.Texture != null)
                {
                    decal.W = Mathf.Clamp(tex.Texture.width, 4, 2048);
                    decal.H = Mathf.Clamp(tex.Texture.height, 4, 2048);
                    decal.Pixels = ReadTexture(tex.Texture, decal.W, decal.H, false, false);
                }
                list.Add(decal);
            }
            return list;
        }

        // poiyomi's decalUV(): symmetry, then a turn around the decal's center, then its box mapped to 0..1
        private static Vector2 DecalUv(Decal d, Vector2 uv)
        {
            if (d.Symmetry == 1) uv.x = Mathf.Abs(uv.x - 0.5f) + 0.5f;
            if (d.Symmetry == 2 && uv.x < 0.5f) uv.x += 0.5f;
            var so = new Vector4(-d.Side.x, d.Side.y, -d.Side.z, d.Side.w);
            Vector2 center = d.Position + new Vector2((so.x + so.y) / 2f, (so.z + so.w) / 2f);
            float theta = d.Rotation * Mathf.Deg2Rad;
            float cs = Mathf.Cos(theta), sn = Mathf.Sin(theta);
            uv = new Vector2((uv.x - center.x) * cs - (uv.y - center.y) * sn + center.x, (uv.x - center.x) * sn + (uv.y - center.y) * cs + center.y);
            Vector2 min = -d.Scale / 2f + d.Position + new Vector2(so.x, so.z);
            Vector2 max = d.Scale / 2f + d.Position + new Vector2(so.y, so.w);
            return new Vector2(Remap(uv.x, min.x, max.x), Remap(uv.y, min.y, max.y));
        }

        private static float Remap(float x, float from, float to)
        {
            return Mathf.Abs(to - from) < 1e-8f ? 0f : (x - from) / (to - from);
        }

        // draws the decals into the cell: every triangle of the material is laid out in the cell's
        // UV0 space (with the tiling and wrap the fused mesh gets) and reads the decal's own UVs there
        private static void BakeDecals(Mat m, List<Renderer> renderers, int size, Color32[] albedo, Color32[] glow, bool linearSpace)
        {
            List<Decal> decals = DecalsOf(m.Saved, true);
            if (decals.Count == 0) return;
            Texture maskTex = m.Saved.T("_DecalMask");
            Color32[] mask = maskTex != null ? ReadTexture(maskTex, size, size, true, false) : null;
            int maskChannelUv = Mathf.RoundToInt(m.Saved.F("_DecalMaskUV", 0f));
            Vector4 st = MainSt(m.Saved);

            foreach (Decal d in decals)
            {
                if (d.Pixels == null && d.Color.a <= 0f) continue;
                var done = new bool[size * size];
                ForEachTriangle(m.Material, renderers, st, size, d.Channel, maskChannelUv, (pixel, decalUv, maskUv) =>
                {
                    if (done[pixel]) return;
                    done[pixel] = true;
                    Vector2 uv = DecalUv(d, decalUv);
                    if (!d.Tiled && (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f)) return;
                    Vector4 sample = d.Pixels != null
                        ? Bilinear(d.Pixels, d.W, d.H, new Vector2(uv.x * d.St.x + d.St.z, uv.y * d.St.y + d.St.w))
                        : Vector4.one;
                    var dc = new Vector3(sample.x * d.Color.r, sample.y * d.Color.g, sample.z * d.Color.b);
                    if (linearSpace) dc = ToLinear(dc);
                    float a = sample.w * d.Color.a;
                    if (d.Hue) dc = HueShift(dc, d.HueShift, d.HueSpace, d.HueSelect);
                    if (mask != null)
                    {
                        Color32 mk = SampleNearest(mask, size, maskUv);
                        a *= (d.MaskChannel == 0 ? mk.r : d.MaskChannel == 1 ? mk.g : d.MaskChannel == 2 ? mk.b : mk.a) / 255f;
                    }
                    float mixed = Mathf.Clamp01(d.Intensity * a * Mathf.Clamp01(d.Alpha));
                    if (mixed <= 0f) return;
                    Color32 bc = albedo[pixel];
                    var b = new Vector3(bc.r / 255f, bc.g / 255f, bc.b / 255f);
                    if (linearSpace) b = ToLinear(b);
                    Vector3 c = Vector3.Lerp(b, CustomBlend(b, dc, d.Blend), mixed);
                    c = new Vector3(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z));
                    if (linearSpace) c = ToGamma(c);
                    albedo[pixel] = new Color32(ToByte(c.x), ToByte(c.y), ToByte(c.z), 255);
                    if (glow != null && d.Glow > 0f)
                    {
                        Color32 gc = glow[pixel];
                        var g = new Vector3(gc.r / 255f, gc.g / 255f, gc.b / 255f);
                        if (linearSpace) g = ToLinear(g);
                        g += dc * a * d.Glow;
                        g = new Vector3(Mathf.Clamp01(g.x), Mathf.Clamp01(g.y), Mathf.Clamp01(g.z));
                        if (linearSpace) g = ToGamma(g);
                        glow[pixel] = new Color32(ToByte(g.x), ToByte(g.y), ToByte(g.z), 255);
                    }
                });
            }
        }

        // poiyomi's customBlend(), the modes a decal can use
        private static Vector3 CustomBlend(Vector3 a, Vector3 b, float mode)
        {
            switch (Mathf.RoundToInt(mode))
            {
                case 1: return Vector3.Min(a, b);
                case 2: return Vector3.Scale(a, b);
                case 5: return Vector3.Max(a, b);
                case 6: return Vector3.one - Vector3.Scale(Vector3.one - a, Vector3.one - b);
                case 7: return Vector3.Max(a - b, Vector3.zero);
                case 8: return a + b;
                case 9: return new Vector3(Overlay(a.x, b.x), Overlay(a.y, b.y), Overlay(a.z, b.z));
                case 20: return a + Vector3.Scale(a, b);
                default: return b;
            }
        }

        private static float Overlay(float a, float b)
        {
            return a < 0.5f ? 2f * a * b : 1f - 2f * (1f - a) * (1f - b);
        }

        // when two triangles share a spot of UV0 (a mirrored half, say) but a decal on another UV
        // channel puts different bits of it there, the atlas can only hold one of them
        private static bool DecalsClash(Mat m, List<Renderer> renderers)
        {
            List<Decal> decals = DecalsOf(m.Saved, false);
            const int size = 128;
            foreach (Decal d in decals)
            {
                if (d.Channel == 0) continue;
                var seen = new Vector2[size * size];
                var has = new bool[size * size];
                int covered = 0, clashes = 0;
                ForEachTriangle(m.Material, renderers, MainSt(m.Saved), size, d.Channel, 0, (pixel, decalUv, maskUv) =>
                {
                    Vector2 uv = DecalUv(d, decalUv);
                    bool inside = d.Tiled || (uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f);
                    if (!has[pixel])
                    {
                        has[pixel] = true;
                        seen[pixel] = uv;
                        if (inside) covered++;
                        return;
                    }
                    if (inside && (seen[pixel] - uv).sqrMagnitude > 0.0004f) clashes++;
                });
                if (covered > 0 && clashes > covered / 50) return true;
            }
            return false;
        }

        private sealed class DecalLayers
        {
            public int Count;
            // more layers were needed than we make, so the material keeps its own
            public bool Overflow;
            public readonly Dictionary<Renderer, int[]> Of = new Dictionary<Renderer, int[]>();
        }

        private static readonly Vector2 Nowhere = new Vector2(float.NaN, float.NaN);

        // a decal on another UV channel over mirrored UV0 can't share the texture with the other half.
        // the material's triangles get sorted into layers where nothing shares a spot of the texture
        // with different decal on it. null when the decals sit fine as they are
        private static DecalLayers LayerDecals(Mat m, List<Renderer> renderers)
        {
            const int grid = 256;
            const int maxLayers = 4;
            List<Decal> decals = DecalsOf(m.Saved, false).FindAll(d => d.Channel != 0 && !d.Tiled);
            if (decals.Count == 0 || !DecalsClash(m, renderers)) return null;
            int n = decals.Count;
            Vector4 st = MainSt(m.Saved);

            // per layer: the pixels taken, and the bit of each decal there (Nowhere for none)
            var used = new List<bool[]>();
            var shown = new List<Vector2[]>();
            var result = new DecalLayers();
            var uv0 = new List<Vector2>();
            var uvD = new List<Vector2>[n];
            for (int d = 0; d < n; d++) uvD[d] = new List<Vector2>();
            var pixels = new List<int>();
            var marks = new List<Vector2>();
            var corner = new Vector2[3];
            var dc = new Vector2[n * 3];
            foreach (Renderer r in renderers)
            {
                Mesh mesh = MeshOf(r);
                Material[] mats = r.sharedMaterials;
                int slot = Array.IndexOf(mats, m.Material);
                if (mesh == null || slot < 0 || mats.Length != mesh.subMeshCount || mesh.GetTopology(slot) != MeshTopology.Triangles) continue;
                mesh.GetUVs(0, uv0);
                for (int d = 0; d < n; d++) mesh.GetUVs(decals[d].Channel, uvD[d]);
                int[] tris = mesh.GetTriangles(slot);
                var layers = new int[tris.Length / 3];
                for (int t = 0; t < layers.Length; t++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        Vector2 uv = Pick(uv0, tris[t * 3 + j]);
                        corner[j] = new Vector2(uv.x * st.x + st.z, uv.y * st.y + st.w);
                    }
                    float sx = Mathf.Floor(Mathf.Min(corner[0].x, Mathf.Min(corner[1].x, corner[2].x)) + 1e-4f);
                    float sy = Mathf.Floor(Mathf.Min(corner[0].y, Mathf.Min(corner[1].y, corner[2].y)) + 1e-4f);
                    for (int j = 0; j < 3; j++)
                        corner[j] = new Vector2(Mathf.Clamp01(corner[j].x - sx) * grid, Mathf.Clamp01(corner[j].y - sy) * grid);
                    for (int d = 0; d < n; d++)
                        for (int j = 0; j < 3; j++) dc[d * 3 + j] = Pick(uvD[d], tris[t * 3 + j]);
                    pixels.Clear();
                    marks.Clear();
                    Raster(corner[0], corner[1], corner[2], grid, (index, w) =>
                    {
                        pixels.Add(index);
                        for (int d = 0; d < n; d++)
                        {
                            Vector2 uv = DecalUv(decals[d], dc[d * 3] * w.x + dc[d * 3 + 1] * w.y + dc[d * 3 + 2] * w.z);
                            marks.Add(uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f ? uv : Nowhere);
                        }
                    });

                    int layer = -1;
                    for (int l = 0; l < used.Count && layer < 0; l++)
                        if (Fits(used[l], shown[l], n, pixels, marks)) layer = l;
                    if (layer < 0)
                    {
                        if (used.Count < maxLayers)
                        {
                            used.Add(new bool[grid * grid]);
                            shown.Add(new Vector2[grid * grid * n]);
                        }
                        else result.Overflow = true;
                        layer = used.Count - 1;
                    }
                    for (int i = 0; i < pixels.Count; i++)
                    {
                        used[layer][pixels[i]] = true;
                        for (int d = 0; d < n; d++) shown[layer][pixels[i] * n + d] = marks[i * n + d];
                    }
                    layers[t] = layer;
                }
                result.Of[r] = layers;
            }
            result.Count = used.Count;
            return result;
        }

        // a triangle fits a layer when every spot it covers is free, or shows the same thing there:
        // no decal on either, or the same bit of the decal
        private static bool Fits(bool[] used, Vector2[] shown, int n, List<int> pixels, List<Vector2> marks)
        {
            for (int i = 0; i < pixels.Count; i++)
            {
                int p = pixels[i];
                if (!used[p]) continue;
                for (int d = 0; d < n; d++)
                {
                    Vector2 a = shown[p * n + d], b = marks[i * n + d];
                    bool inA = !float.IsNaN(a.x), inB = !float.IsNaN(b.x);
                    if (inA != inB || (inA && (a - b).sqrMagnitude > 0.0004f)) return false;
                }
            }
            return true;
        }

        // every layer past the first gets a copy of the material, so its own spot on the atlas.
        // the split meshes only live for this fuse. Gives back the materials that keep their own
        private static HashSet<Material> SplitDecals(List<Renderer> renderers, GeneratedFolder folder, string baseName, MaterialPlan plan)
        {
            var apart = new HashSet<Material>();
            foreach (Material material in new List<Material>(Distinct(renderers)))
            {
                var m = new Mat { Material = material, Saved = Read(material) };
                DecalLayers layers = LayerDecals(m, renderers);
                if (layers == null || layers.Count <= 1) continue;
                if (layers.Overflow)
                {
                    apart.Add(material);
                    continue;
                }

                // the first layer keeps the material, the others get copies of it
                var parts = new Material[layers.Count];
                parts[0] = material;
                string name = material.name.StartsWith(baseName, StringComparison.Ordinal) ? material.name : baseName + " " + material.name;
                string safe = string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars()));
                for (int l = 1; l < parts.Length; l++)
                {
                    var clone = new Material(material);
                    CopyTags(material, clone);
                    parts[l] = folder.Save(clone, safe + " part " + (l + 1) + ".mat");
                }
                foreach (var pair in layers.Of)
                {
                    Renderer r = pair.Key;
                    Mesh mesh = MeshOf(r);
                    Material[] mats = r.sharedMaterials;
                    int slot = Array.IndexOf(mats, material);
                    int[] tris = mesh.GetTriangles(slot);
                    var lists = new List<int>[parts.Length];
                    for (int l = 0; l < lists.Length; l++) lists[l] = new List<int>();
                    for (int t = 0; t < pair.Value.Length; t++)
                    {
                        List<int> to = lists[pair.Value[t]];
                        to.Add(tris[t * 3]);
                        to.Add(tris[t * 3 + 1]);
                        to.Add(tris[t * 3 + 2]);
                    }
                    int extra = 0;
                    for (int l = 1; l < lists.Length; l++) if (lists[l].Count > 0) extra++;
                    if (extra == 0) continue;

                    Mesh split = UnityEngine.Object.Instantiate(mesh);
                    split.name = mesh.name;
                    plan.Temporary.Add(split);
                    split.subMeshCount = mesh.subMeshCount + extra;
                    split.SetTriangles(lists[0], slot);
                    var withParts = new List<Material>(mats);
                    int sub = mesh.subMeshCount;
                    for (int l = 1; l < lists.Length; l++)
                    {
                        if (lists[l].Count == 0) continue;
                        split.SetTriangles(lists[l], sub++);
                        withParts.Add(parts[l]);
                    }
                    var smr = r as SkinnedMeshRenderer;
                    if (smr != null) smr.sharedMesh = split;
                    else r.GetComponent<MeshFilter>().sharedMesh = split;
                    r.sharedMaterials = withParts.ToArray();
                }
            }
            return apart;
        }

        private static Vector4 MainSt(Saved s)
        {
            TexEnv main;
            return s.Textures.TryGetValue("_MainTex", out main)
                ? new Vector4(main.Scale.x, main.Scale.y, main.Offset.x, main.Offset.y)
                : new Vector4(1f, 1f, 0f, 0f);
        }

        // every triangle of the material, placed the way the fused mesh will place it in its cell
        // (tiling, then each triangle wrapped into 0..1), drawn pixel by pixel. The callback gets the
        // pixel and the other two UV channels there
        private static void ForEachTriangle(Material material, List<Renderer> renderers, Vector4 st, int size, int channel, int maskChannel,
            Action<int, Vector2, Vector2> pixel)
        {
            var uv0 = new List<Vector2>();
            var uvA = new List<Vector2>();
            var uvB = new List<Vector2>();
            foreach (Renderer r in renderers)
            {
                Mesh mesh = MeshOf(r);
                if (mesh == null) continue;
                Material[] mats = r.sharedMaterials;
                mesh.GetUVs(0, uv0);
                if (uv0.Count == 0) continue;
                mesh.GetUVs(channel, uvA);
                mesh.GetUVs(maskChannel, uvB);
                for (int mi = 0; mi < mats.Length; mi++)
                {
                    int si = Math.Min(mi, mesh.subMeshCount - 1);
                    if (mats[mi] != material || si < 0 || mesh.GetTopology(si) != MeshTopology.Triangles) continue;
                    int[] tris = mesh.GetTriangles(si);
                    var corner = new Vector2[3];
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            Vector2 uv = uv0[tris[t + j]];
                            corner[j] = new Vector2(uv.x * st.x + st.z, uv.y * st.y + st.w);
                        }
                        float sx = Mathf.Floor(Mathf.Min(corner[0].x, Mathf.Min(corner[1].x, corner[2].x)) + 1e-4f);
                        float sy = Mathf.Floor(Mathf.Min(corner[0].y, Mathf.Min(corner[1].y, corner[2].y)) + 1e-4f);
                        for (int j = 0; j < 3; j++)
                            corner[j] = new Vector2(Mathf.Clamp01(corner[j].x - sx) * size, Mathf.Clamp01(corner[j].y - sy) * size);
                        int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                        Vector2 a0 = Pick(uvA, a), a1 = Pick(uvA, b), a2 = Pick(uvA, c);
                        Vector2 b0 = Pick(uvB, a), b1 = Pick(uvB, b), b2 = Pick(uvB, c);
                        Raster(corner[0], corner[1], corner[2], size, (index, w) =>
                            pixel(index, a0 * w.x + a1 * w.y + a2 * w.z, b0 * w.x + b1 * w.y + b2 * w.z));
                    }
                }
            }
        }

        private static Vector2 Pick(List<Vector2> list, int i)
        {
            return i < list.Count ? list[i] : Vector2.zero;
        }

        // fills a triangle given in pixels. A pixel counts when its center is within half a pixel
        // of the triangle, so seams between islands don't get gaps. The callback gets barycentrics
        internal static void Raster(Vector2 p0, Vector2 p1, Vector2 p2, int size, Action<int, Vector3> pixel)
        {
            float area = Edge(p0, p1, p2);
            if (Mathf.Abs(area) < 1e-9f) return;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)) - 1f));
            int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x)) + 1f));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)) - 1f));
            int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y)) + 1f));
            float abs = Mathf.Abs(area);
            // how far outside an edge, in barycentric units, half a pixel is
            float t0 = 0.5f * (p2 - p1).magnitude / abs;
            float t1 = 0.5f * (p0 - p2).magnitude / abs;
            float t2 = 0.5f * (p1 - p0).magnitude / abs;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(p1, p2, p) / area;
                    float w1 = Edge(p2, p0, p) / area;
                    float w2 = Edge(p0, p1, p) / area;
                    if (w0 < -t0 || w1 < -t1 || w2 < -t2) continue;
                    pixel(y * size + x, new Vector3(w0, w1, w2));
                }
            }
        }

        private static float Edge(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        // reads a picture at a UV, repeating it, between its four nearest pixels
        private static Vector4 Bilinear(Color32[] px, int w, int h, Vector2 uv)
        {
            float x = (uv.x - Mathf.Floor(uv.x)) * w - 0.5f;
            float y = (uv.y - Mathf.Floor(uv.y)) * h - 0.5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            Vector4 c00 = At(px, w, h, ix, iy), c10 = At(px, w, h, ix + 1, iy), c01 = At(px, w, h, ix, iy + 1), c11 = At(px, w, h, ix + 1, iy + 1);
            return Vector4.Lerp(Vector4.Lerp(c00, c10, fx), Vector4.Lerp(c01, c11, fx), fy);
        }

        private static Vector4 At(Color32[] px, int w, int h, int x, int y)
        {
            x = ((x % w) + w) % w;
            y = ((y % h) + h) % h;
            Color32 c = px[y * w + x];
            return new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f);
        }

        private static Color32 SampleNearest(Color32[] px, int size, Vector2 uv)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((uv.x - Mathf.Floor(uv.x)) * size), 0, size - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((uv.y - Mathf.Floor(uv.y)) * size), 0, size - 1);
            return px[y * size + x];
        }
    }
}
#endif
