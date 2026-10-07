#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BAAA
{
    internal enum Rank { Excellent, Good, Medium, Poor, VeryPoor }

    // which tab helps with a number
    internal enum RankFix { None, Decimator, Fuser, Textures, Features, Pieces, Quest }

    // what VRChat counts for its performance rank. hidden objects count too, EditorOnly ones dont
    // (they never upload)
    internal sealed class GearStats
    {
        public long Triangles;
        public long SkinnedMeshes;
        public long BasicMeshes;
        public long MaterialSlots;
        public long TextureBytes;
        public long QuestTextureBytes;
        public long PhysBones;
        public long PhysBoneTransforms;
        public long Colliders;
        public long Contacts;
        public long Constraints;
        public long Animators;
        public long Bones;
        public long Lights;
        public long Particles;
        public long Trails;
        public long Lines;
        public long Cloths;
        public long PhysicsColliders;
        public long Rigidbodies;
        public long Audio;
    }

    internal sealed class RankLimit
    {
        public string Name;
        public Func<GearStats, long> Pc;
        public Func<GearStats, long> Quest;
        // excellent, good, medium, poor. null when the platform doesnt rank it
        public long[] PcLimits;
        public long[] QuestLimits;
        public bool Bytes;
        public RankFix Fix;
    }

    internal sealed class RankLine
    {
        public RankLimit Limit;
        public bool Quest;
        public long Value;
        public Rank Rank;
    }

    internal sealed class AvatarRank
    {
        public Rank Pc;
        public Rank Quest;
        public readonly List<RankLine> Lines = new List<RankLine>();
    }

    // a copy next to the original it stands in for
    internal sealed class CopyCompare
    {
        public GearStats Before;
        public GearStats After;

        public static CopyCompare Of(Outfit copy)
        {
            if (copy == null || copy.Root == null || copy.Original == null) return null;
            return new CopyCompare
            {
                // the original waits EditorOnly on purpose, so its own tag doesnt count
                Before = Ranks.Measure(copy.Original, copy.Original.transform, false),
                After = Ranks.Measure(copy.Root, null, false),
            };
        }
    }

    // VRChat's limits, from creators.vrchat.com/avatars/avatar-performance-ranking-system (october 2026).
    // its an estimate: VRCFury merges bones and such at upload, so the real numbers can be a bit off
    internal static class Ranks
    {
        private const string ColliderType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider";
        private const string ContactReceiverType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";
        private const string ContactSenderType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender";
        private const string VrcConstraintNamespace = "VRC.SDK3.Dynamics.Constraint.Components";
        private const long Mb = 1048576L;

        public static readonly RankLimit[] Limits =
        {
            Limit(Lang.N("Triangles"), s => s.Triangles, new long[] { 32000, 70000, 70000, 70000 }, new long[] { 7500, 10000, 15000, 20000 }, RankFix.Decimator),
            new RankLimit
            {
                Name = Lang.N("Texture memory"), Pc = s => s.TextureBytes, Quest = s => s.QuestTextureBytes, Bytes = true, Fix = RankFix.Textures,
                PcLimits = new[] { 40 * Mb, 75 * Mb, 110 * Mb, 150 * Mb }, QuestLimits = new[] { 10 * Mb, 18 * Mb, 25 * Mb, 40 * Mb },
            },
            Limit(Lang.N("Skinned meshes"), s => s.SkinnedMeshes, new long[] { 1, 2, 8, 16 }, new long[] { 1, 1, 2, 2 }, RankFix.Fuser),
            Limit(Lang.N("Basic meshes"), s => s.BasicMeshes, new long[] { 4, 8, 16, 24 }, new long[] { 1, 1, 2, 2 }, RankFix.Fuser),
            Limit(Lang.N("Material slots"), s => s.MaterialSlots, new long[] { 4, 8, 16, 32 }, new long[] { 1, 1, 2, 4 }, RankFix.Fuser),
            Limit(Lang.N("PhysBones"), s => s.PhysBones, new long[] { 4, 8, 16, 32 }, new long[] { 0, 4, 6, 8 }, RankFix.Features),
            Limit(Lang.N("Bones moved by PhysBones"), s => s.PhysBoneTransforms, new long[] { 16, 64, 128, 256 }, new long[] { 0, 16, 32, 64 }, RankFix.Features),
            Limit(Lang.N("PhysBone colliders"), s => s.Colliders, new long[] { 4, 8, 16, 32 }, new long[] { 0, 4, 8, 16 }, RankFix.Features),
            Limit(Lang.N("Contacts"), s => s.Contacts, new long[] { 8, 16, 24, 32 }, new long[] { 2, 4, 8, 16 }, RankFix.Features),
            Limit(Lang.N("Constraints"), s => s.Constraints, new long[] { 100, 250, 300, 350 }, new long[] { 30, 60, 120, 150 }, RankFix.None),
            Limit(Lang.N("Animators"), s => s.Animators, new long[] { 1, 4, 16, 32 }, new long[] { 1, 1, 1, 2 }, RankFix.None),
            Limit(Lang.N("Bones"), s => s.Bones, new long[] { 75, 150, 256, 400 }, new long[] { 75, 90, 150, 150 }, RankFix.Pieces),
            Limit(Lang.N("Lights"), s => s.Lights, new long[] { 0, 0, 0, 1 }, null, RankFix.None),
            Limit(Lang.N("Particle systems"), s => s.Particles, new long[] { 0, 4, 8, 16 }, new long[] { 0, 0, 0, 2 }, RankFix.None),
            Limit(Lang.N("Trail renderers"), s => s.Trails, new long[] { 1, 2, 4, 8 }, new long[] { 0, 0, 0, 1 }, RankFix.None),
            Limit(Lang.N("Line renderers"), s => s.Lines, new long[] { 1, 2, 4, 8 }, new long[] { 0, 0, 0, 1 }, RankFix.None),
            Limit(Lang.N("Cloths"), s => s.Cloths, new long[] { 0, 1, 1, 1 }, null, RankFix.None),
            Limit(Lang.N("Physics colliders"), s => s.PhysicsColliders, new long[] { 0, 1, 8, 8 }, null, RankFix.None),
            Limit(Lang.N("Rigidbodies"), s => s.Rigidbodies, new long[] { 0, 1, 8, 8 }, null, RankFix.None),
            Limit(Lang.N("Audio sources"), s => s.Audio, new long[] { 1, 4, 8, 8 }, null, RankFix.None),
        };

        private static RankLimit Limit(string name, Func<GearStats, long> value, long[] pc, long[] quest, RankFix fix)
        {
            return new RankLimit { Name = name, Pc = value, Quest = value, PcLimits = pc, QuestLimits = quest, Fix = fix };
        }

        // VRChat's own names, kept in english in every language like the SDK shows them
        public static string Name(Rank r)
        {
            switch (r)
            {
                case Rank.Excellent: return "Excellent";
                case Rank.Good: return "Good";
                case Rank.Medium: return "Medium";
                case Rank.Poor: return "Poor";
                default: return "Very Poor";
            }
        }

        public static Rank Of(long value, long[] limits)
        {
            for (int i = 0; i < limits.Length; i++)
                if (value <= limits[i]) return (Rank)i;
            return Rank.VeryPoor;
        }

        // the whole avatar, the way it uploads. without leaves a part out, like the outfit, to see the avatar on its own
        public static AvatarRank For(GameObject avatar, Transform without = null)
        {
            GearStats s = Measure(avatar, null, true, without);
            var rank = new AvatarRank();
            foreach (RankLimit limit in Limits)
            {
                if (limit.PcLimits != null)
                {
                    long v = limit.Pc(s);
                    var line = new RankLine { Limit = limit, Value = v, Rank = Of(v, limit.PcLimits) };
                    rank.Lines.Add(line);
                    if (line.Rank > rank.Pc) rank.Pc = line.Rank;
                }
                if (limit.QuestLimits != null)
                {
                    long v = limit.Quest(s);
                    var line = new RankLine { Limit = limit, Quest = true, Value = v, Rank = Of(v, limit.QuestLimits) };
                    rank.Lines.Add(line);
                    if (line.Rank > rank.Quest) rank.Quest = line.Rank;
                }
            }
            return rank;
        }

        // counts everything under root. hiddenRoot is a root thats EditorOnly on purpose, like an
        // original waiting next to its copy: its own tag is ignored, the ones inside it still count.
        // bones needs VRCFury's Armature Link rule, so only the avatar's rank asks for it
        public static GearStats Measure(GameObject root, Transform hiddenRoot, bool bones, Transform without = null)
        {
            var s = new GearStats();
            if (root == null) return s;
            var textures = new HashSet<Texture>();
            var usedBones = new HashSet<Transform>();
            Dictionary<Transform, Transform> merged = bones && AssistantScope.VrcFuryInstalled ? OutfitScanner.MergeMap(root, root) : null;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (Hidden(r.transform, hiddenRoot) || Left(r.transform, without)) continue;
                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null)
                {
                    s.SkinnedMeshes++;
                    mesh = smr.sharedMesh;
                    if (bones)
                    {
                        foreach (Transform b in smr.bones)
                        {
                            if (b == null) continue;
                            Transform to;
                            usedBones.Add(merged != null && merged.TryGetValue(b, out to) ? to : b);
                        }
                    }
                }
                else if (r is MeshRenderer)
                {
                    s.BasicMeshes++;
                    var filter = r.GetComponent<MeshFilter>();
                    mesh = filter != null ? filter.sharedMesh : null;
                }
                else
                {
                    if (r is TrailRenderer) s.Trails++;
                    else if (r is LineRenderer) s.Lines++;
                    continue;
                }

                if (mesh != null) s.Triangles += OutfitScanner.Triangles(mesh);
                foreach (Material m in r.sharedMaterials)
                {
                    s.MaterialSlots++;
                    foreach (Texture t in TextureUse.Of(m)) textures.Add(t);
                }
            }
            s.Bones = usedBones.Count;

            foreach (Texture t in textures)
            {
                s.TextureBytes += AssistantScope.GpuBytes(t);
                s.QuestTextureBytes += QuestBytes(t);
            }

            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || Hidden(c.transform, hiddenRoot) || Left(c.transform, without)) continue;
                string type = c.GetType().FullName;
                if (type == AssistantScope.PhysBoneType)
                {
                    s.PhysBones++;
                    s.PhysBoneTransforms += OutfitScanner.Moves(c, null);
                }
                else if (type == ColliderType) s.Colliders++;
                else if (type == ContactReceiverType || type == ContactSenderType) s.Contacts++;
                else if (c is UnityEngine.Animations.IConstraint || (c.GetType().Namespace ?? "") == VrcConstraintNamespace) s.Constraints++;
                else if (c is Animator) s.Animators++;
                else if (c is Light) s.Lights++;
                else if (c is ParticleSystem) s.Particles++;
                else if (c is Cloth) s.Cloths++;
                else if (c is Collider) s.PhysicsColliders++;
                else if (c is Rigidbody) s.Rigidbodies++;
                else if (c is AudioSource) s.Audio++;
            }
            return s;
        }

        private static bool Left(Transform t, Transform without)
        {
            return without != null && t.IsChildOf(without);
        }

        private static bool Hidden(Transform t, Transform hiddenRoot)
        {
            for (Transform x = t; x != null; x = x.parent)
            {
                if (x == hiddenRoot) return false;
                if (x.CompareTag("EditorOnly")) return true;
            }
            return false;
        }

        // memory on a Quest: what the Android import makes of it, ASTC unless its set otherwise.
        // same sum the Quest tab uses: 16 bytes per block plus a third for mip maps
        internal static long QuestBytes(Texture tex)
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) return AssistantScope.GpuBytes(tex);
            var t2 = tex as Texture2D;
            if (t2 == null) return AssistantScope.GpuBytes(tex);
            long w = t2.width, h = t2.height;
            int block = 6;
            var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (imp != null)
            {
                TextureImporterPlatformSettings android = imp.GetPlatformTextureSettings("Android");
                if (android != null && android.overridden)
                {
                    long side = Math.Max(w, h);
                    if (android.maxTextureSize > 0 && side > android.maxTextureSize)
                    {
                        w = Math.Max(1, w * android.maxTextureSize / side);
                        h = Math.Max(1, h * android.maxTextureSize / side);
                    }
                    int astc = AstcBlock(android.format);
                    if (astc > 0) block = astc;
                    else if (android.format == TextureImporterFormat.RGBA32 || android.format == TextureImporterFormat.ARGB32) return Mips(w * h * 4, t2);
                }
                else if (imp.textureCompression == TextureImporterCompression.Uncompressed) return Mips(w * h * 4, t2);
                else if (imp.textureCompression == TextureImporterCompression.CompressedLQ) block = 8;
                else if (imp.textureCompression == TextureImporterCompression.CompressedHQ) block = 4;
            }
            return Mips(((w + block - 1) / block) * ((h + block - 1) / block) * 16, t2);
        }

        private static long Mips(long bytes, Texture2D t)
        {
            return t.mipmapCount > 1 ? bytes * 4 / 3 : bytes;
        }

        private static int AstcBlock(TextureImporterFormat f)
        {
            switch (f)
            {
                case TextureImporterFormat.ASTC_4x4: return 4;
                case TextureImporterFormat.ASTC_5x5: return 5;
                case TextureImporterFormat.ASTC_6x6: return 6;
                case TextureImporterFormat.ASTC_8x8: return 8;
                case TextureImporterFormat.ASTC_10x10: return 10;
                case TextureImporterFormat.ASTC_12x12: return 12;
                default: return 0;
            }
        }
    }
}
#endif
