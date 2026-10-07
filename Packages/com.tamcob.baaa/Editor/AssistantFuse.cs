#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BAAA
{
    // where one material's triangles go in the fused mesh
    internal sealed class SlotPlan
    {
        public int Slot;
        // The part of the atlas its UVs move into. The whole texture when it has no atlas
        public Rect Cell = new Rect(0f, 0f, 1f, 1f);
        // Tiling and offset baked into the UVs first, when every material shares one texture
        public Vector4 St = new Vector4(1f, 1f, 0f, 0f);
        // moves each triangle's UVs into 0..1 first, for textures that repeat
        public bool Wrap;
    }

    internal sealed class FusedMesh
    {
        public Mesh Mesh;
        public Transform[] Bones;
        public Transform RootBone;
        public Bounds LocalBounds;
        public float[] Weights;
        public Renderer Template;
    }

    // squashes the renderers into one skinned mesh. bones, bind poses and blendshapes all carry over
    // so it still moves and fits like before (body sliders too)
    internal static class MeshFuser
    {
        private sealed class Source
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public int[] BoneMap;
            public int OwnBone = -1;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector4[] Tangents;
            public readonly List<Vector4>[] Uvs = new List<Vector4>[4];
            public Color32[] Colors;
            public BoneWeight[] Weights;
            // fused vertex -> the source vertex it came from.
            public readonly List<int> NewIndex = new List<int>();
            public readonly List<int> OldIndex = new List<int>();
        }

        public static FusedMesh Fuse(List<Renderer> renderers, Func<Material, SlotPlan> planOf, int slots, string name)
        {
            var sources = new List<Source>();
            var bones = new List<Transform>();
            var poses = new List<Matrix4x4>();
            var byBone = new Dictionary<Transform, List<int>>();

            Func<Transform, Matrix4x4, int> boneIndex = (t, pose) =>
            {
                List<int> list;
                if (!byBone.TryGetValue(t, out list))
                {
                    list = new List<int>();
                    byBone[t] = list;
                }
                foreach (int i in list)
                    if (SamePose(poses[i], pose)) return i;
                bones.Add(t);
                poses.Add(pose);
                list.Add(bones.Count - 1);
                return bones.Count - 1;
            };

            bool anyNormals = false, anyTangents = false, anyColors = false;
            var uvDims = new int[4];
            foreach (Renderer r in renderers)
            {
                Mesh mesh = MeshOf(r);
                if (mesh == null) continue;
                var s = new Source { Renderer = r, Mesh = mesh };
                s.Vertices = mesh.vertices;
                s.Normals = mesh.normals;
                s.Tangents = mesh.tangents;
                s.Colors = mesh.colors32;
                s.Weights = mesh.boneWeights;
                for (int k = 0; k < 4; k++)
                {
                    var attr = VertexAttribute.TexCoord0 + k;
                    if (!mesh.HasVertexAttribute(attr)) continue;
                    s.Uvs[k] = new List<Vector4>();
                    mesh.GetUVs(k, s.Uvs[k]);
                    uvDims[k] = Math.Max(uvDims[k], mesh.GetVertexAttributeDimension(attr));
                }
                anyNormals |= s.Normals.Length == s.Vertices.Length;
                anyTangents |= s.Tangents.Length == s.Vertices.Length;
                anyColors |= s.Colors.Length == s.Vertices.Length;

                var smr = r as SkinnedMeshRenderer;
                Matrix4x4[] bindposes = mesh.bindposes;
                if (smr != null && bindposes.Length > 0 && s.Weights.Length == s.Vertices.Length)
                {
                    Transform[] smrBones = smr.bones;
                    s.BoneMap = new int[bindposes.Length];
                    for (int i = 0; i < bindposes.Length; i++)
                    {
                        Transform t = i < smrBones.Length ? smrBones[i] : null;
                        Matrix4x4 pose = bindposes[i];
                        if (t == null)
                        {
                            // A bone thats gone: hold those vertices where they are now.
                            t = smr.rootBone != null ? smr.rootBone : smr.transform;
                            pose = t.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                        }
                        s.BoneMap[i] = boneIndex(t, pose);
                    }
                }
                else
                {
                    // Not skinned: every vertex follows the renderer's own object
                    s.OwnBone = boneIndex(r.transform, Matrix4x4.identity);
                }
                sources.Add(s);
            }
            if (sources.Count == 0) return null;

            // Missing normals and tangents are worked out the way Unity would
            foreach (Source s in sources)
            {
                bool needNormals = anyNormals && s.Normals.Length != s.Vertices.Length;
                bool needTangents = anyTangents && s.Tangents.Length != s.Vertices.Length;
                if (!needNormals && !needTangents) continue;
                Mesh tmp = UnityEngine.Object.Instantiate(s.Mesh);
                if (needNormals) tmp.RecalculateNormals();
                if (needTangents) tmp.RecalculateTangents();
                if (needNormals) s.Normals = tmp.normals;
                if (needTangents) s.Tangents = tmp.tangents;
                UnityEngine.Object.DestroyImmediate(tmp);
            }

            Dictionary<string, float> rest = BakeWeightDifferences(sources);

            // Vertices and triangles
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uvs = new List<Vector4>[4];
            for (int k = 0; k < 4; k++) if (uvDims[k] > 0) uvs[k] = new List<Vector4>();
            var colors = new List<Color32>();
            var weights = new List<BoneWeight>();
            var tris = new List<int>[slots];
            for (int i = 0; i < slots; i++) tris[i] = new List<int>();
            var planIds = new Dictionary<SlotPlan, int>();

            foreach (Source s in sources)
            {
                var map = new Dictionary<long, int>();
                Material[] mats = s.Renderer.sharedMaterials;
                int subs = s.Mesh.subMeshCount;
                for (int mi = 0; mi < mats.Length; mi++)
                {
                    Material m = mats[mi];
                    // extra materials draw the last submesh again, like Unity does.
                    int si = Math.Min(mi, subs - 1);
                    if (m == null || si < 0 || s.Mesh.GetTopology(si) != MeshTopology.Triangles) continue;
                    SlotPlan plan = planOf(m);
                    if (plan == null || plan.Slot < 0 || plan.Slot >= slots) continue;
                    int planId;
                    if (!planIds.TryGetValue(plan, out planId))
                    {
                        planId = planIds.Count;
                        planIds[plan] = planId;
                    }

                    int[] sub = s.Mesh.GetTriangles(si);
                    List<int> target = tris[plan.Slot];
                    var corner = new Vector2[3];
                    for (int t = 0; t + 2 < sub.Length; t += 3)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            Vector2 uv = Uv(s, sub[t + j]);
                            corner[j] = new Vector2(uv.x * plan.St.x + plan.St.z, uv.y * plan.St.y + plan.St.w);
                        }
                        int sx = 0, sy = 0;
                        if (plan.Wrap)
                        {
                            sx = Mathf.FloorToInt(Mathf.Min(corner[0].x, Mathf.Min(corner[1].x, corner[2].x)) + 1e-4f);
                            sy = Mathf.FloorToInt(Mathf.Min(corner[0].y, Mathf.Min(corner[1].y, corner[2].y)) + 1e-4f);
                            sx = Mathf.Clamp(sx, -100, 100);
                            sy = Mathf.Clamp(sy, -100, 100);
                        }
                        for (int j = 0; j < 3; j++)
                        {
                            int v = sub[t + j];
                            long key = ((long)planId << 40) | ((long)(sx + 128) << 32) | ((long)(sy + 128) << 24) | (uint)v;
                            int index;
                            if (!map.TryGetValue(key, out index))
                            {
                                Vector2 uv = corner[j];
                                if (plan.Wrap)
                                {
                                    uv.x = Mathf.Clamp01(uv.x - sx);
                                    uv.y = Mathf.Clamp01(uv.y - sy);
                                }
                                uv = new Vector2(plan.Cell.x + uv.x * plan.Cell.width, plan.Cell.y + uv.y * plan.Cell.height);
                                index = verts.Count;
                                AddVertex(s, v, uv, verts, normals, tangents, uvs, colors, weights, anyNormals, anyTangents, anyColors);
                                map[key] = index;
                            }
                            target.Add(index);
                        }
                    }
                }
            }

            var mesh2 = new Mesh { name = name };
            mesh2.indexFormat = verts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh2.SetVertices(verts);
            if (anyNormals) mesh2.SetNormals(normals);
            if (anyTangents) mesh2.SetTangents(tangents);
            for (int k = 0; k < 4; k++)
            {
                if (uvs[k] == null) continue;
                if (uvDims[k] <= 2) mesh2.SetUVs(k, uvs[k].ConvertAll(u => new Vector2(u.x, u.y)));
                else if (uvDims[k] == 3) mesh2.SetUVs(k, uvs[k].ConvertAll(u => new Vector3(u.x, u.y, u.z)));
                else mesh2.SetUVs(k, uvs[k]);
            }
            if (anyColors) mesh2.SetColors(colors);
            mesh2.boneWeights = weights.ToArray();
            mesh2.bindposes = poses.ToArray();
            mesh2.subMeshCount = slots;
            for (int i = 0; i < slots; i++) mesh2.SetTriangles(tris[i], i, false);
            float[] shapeWeights = AddBlendShapes(sources, mesh2, verts.Count, rest);
            mesh2.RecalculateBounds();

            Source main = sources[0];
            foreach (Source s in sources) if (s.Vertices.Length > main.Vertices.Length) main = s;
            var mainSmr = main.Renderer as SkinnedMeshRenderer;
            Transform rootBone = mainSmr != null && mainSmr.rootBone != null ? mainSmr.rootBone : bones.Count > 0 ? bones[0] : main.Renderer.transform;

            return new FusedMesh
            {
                Mesh = mesh2,
                Bones = bones.ToArray(),
                RootBone = rootBone,
                LocalBounds = BoundsIn(rootBone, renderers),
                Weights = shapeWeights,
                Template = main.Renderer,
            };
        }

        private static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            var filter = r.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        private static bool SamePose(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return false;
            return true;
        }

        private static Vector2 Uv(Source s, int v)
        {
            List<Vector4> uv = s.Uvs[0];
            return uv != null && v < uv.Count ? new Vector2(uv[v].x, uv[v].y) : Vector2.zero;
        }

        private static void AddVertex(Source s, int v, Vector2 uv0, List<Vector3> verts, List<Vector3> normals, List<Vector4> tangents,
            List<Vector4>[] uvs, List<Color32> colors, List<BoneWeight> weights, bool anyNormals, bool anyTangents, bool anyColors)
        {
            s.NewIndex.Add(verts.Count);
            s.OldIndex.Add(v);
            verts.Add(s.Vertices[v]);
            if (anyNormals) normals.Add(s.Normals[v].normalized);
            if (anyTangents) tangents.Add(s.Tangents[v]);
            for (int k = 0; k < 4; k++)
            {
                if (uvs[k] == null) continue;
                List<Vector4> src = s.Uvs[k];
                Vector4 value = src != null && v < src.Count ? src[v] : Vector4.zero;
                if (k == 0) value = new Vector4(uv0.x, uv0.y, value.z, value.w);
                uvs[k].Add(value);
            }
            if (anyColors) colors.Add(s.Colors.Length == s.Vertices.Length ? s.Colors[v] : new Color32(255, 255, 255, 255));

            if (s.BoneMap == null)
            {
                weights.Add(new BoneWeight { boneIndex0 = s.OwnBone, weight0 = 1f });
                return;
            }
            BoneWeight w = s.Weights[v];
            w.boneIndex0 = Remap(s.BoneMap, w.boneIndex0, w.weight0);
            w.boneIndex1 = Remap(s.BoneMap, w.boneIndex1, w.weight1);
            w.boneIndex2 = Remap(s.BoneMap, w.boneIndex2, w.weight2);
            w.boneIndex3 = Remap(s.BoneMap, w.boneIndex3, w.weight3);
            weights.Add(w);
        }

        private static int Remap(int[] map, int index, float weight)
        {
            if (index >= 0 && index < map.Length) return map[index];
            return weight > 0f ? map[0] : 0;
        }

        // -------------------------------------------------------------------
        // blendshapes
        // -------------------------------------------------------------------

        // A blendshape shared by several pieces gets one weight on the fused
        // mesh: the first pieces. Pieces that had another weight get the
        // difference baked in, so everything looks the same as before.
        private static Dictionary<string, float> BakeWeightDifferences(List<Source> sources)
        {
            var rest = new Dictionary<string, float>();
            foreach (Source s in sources)
            {
                var smr = s.Renderer as SkinnedMeshRenderer;
                for (int k = 0; k < s.Mesh.blendShapeCount; k++)
                {
                    string shape = s.Mesh.GetBlendShapeName(k);
                    if (!rest.ContainsKey(shape)) rest[shape] = smr != null ? smr.GetBlendShapeWeight(k) : 0f;
                }
            }
            foreach (Source s in sources)
            {
                var smr = s.Renderer as SkinnedMeshRenderer;
                for (int k = 0; k < s.Mesh.blendShapeCount; k++)
                {
                    float own = smr != null ? smr.GetBlendShapeWeight(k) : 0f;
                    float shared = rest[s.Mesh.GetBlendShapeName(k)];
                    if (Mathf.Abs(own - shared) < 0.01f) continue;
                    Vector3[] dv1, dn1, dv2, dn2;
                    Vector3[] dt1, dt2;
                    Sample(s.Mesh, k, own, out dv1, out dn1, out dt1);
                    Sample(s.Mesh, k, shared, out dv2, out dn2, out dt2);
                    for (int v = 0; v < s.Vertices.Length; v++)
                    {
                        s.Vertices[v] += dv1[v] - dv2[v];
                        if (s.Normals.Length == s.Vertices.Length) s.Normals[v] += dn1[v] - dn2[v];
                        if (s.Tangents.Length == s.Vertices.Length)
                        {
                            Vector3 d = dt1[v] - dt2[v];
                            s.Tangents[v] += new Vector4(d.x, d.y, d.z, 0f);
                        }
                    }
                }
            }
            return rest;
        }

        // the offsets a blendshape gives at this weight, between its frames
        // the way Unity blends them.
        private static void Sample(Mesh mesh, int shape, float weight, out Vector3[] dv, out Vector3[] dn, out Vector3[] dt)
        {
            int n = mesh.vertexCount;
            dv = new Vector3[n];
            dn = new Vector3[n];
            dt = new Vector3[n];
            int frames = mesh.GetBlendShapeFrameCount(shape);
            if (frames == 0 || Mathf.Abs(weight) < 1e-4f) return;

            int hi = 0;
            while (hi < frames - 1 && mesh.GetBlendShapeFrameWeight(shape, hi) < weight) hi++;
            float wHi = mesh.GetBlendShapeFrameWeight(shape, hi);
            float wLo = hi > 0 ? mesh.GetBlendShapeFrameWeight(shape, hi - 1) : 0f;
            var hv = new Vector3[n];
            var hn = new Vector3[n];
            var ht = new Vector3[n];
            mesh.GetBlendShapeFrameVertices(shape, hi, hv, hn, ht);
            var lv = new Vector3[n];
            var ln = new Vector3[n];
            var lt = new Vector3[n];
            if (hi > 0) mesh.GetBlendShapeFrameVertices(shape, hi - 1, lv, ln, lt);
            float span = wHi - wLo;
            float f = Mathf.Abs(span) > 1e-5f ? (weight - wLo) / span : 1f;
            for (int v = 0; v < n; v++)
            {
                dv[v] = lv[v] + (hv[v] - lv[v]) * f;
                dn[v] = ln[v] + (hn[v] - ln[v]) * f;
                dt[v] = lt[v] + (ht[v] - lt[v]) * f;
            }
        }

        private static float[] AddBlendShapes(List<Source> sources, Mesh mesh, int count, Dictionary<string, float> rest)
        {
            // every blendshape name, in the order the pieces have them
            var order = new List<string>();
            var frameWeights = new Dictionary<string, List<float>>();
            foreach (Source s in sources)
            {
                for (int k = 0; k < s.Mesh.blendShapeCount; k++)
                {
                    string shape = s.Mesh.GetBlendShapeName(k);
                    List<float> list;
                    if (!frameWeights.TryGetValue(shape, out list))
                    {
                        list = new List<float>();
                        frameWeights[shape] = list;
                        order.Add(shape);
                    }
                    for (int f = 0; f < s.Mesh.GetBlendShapeFrameCount(k); f++)
                    {
                        float w = s.Mesh.GetBlendShapeFrameWeight(k, f);
                        if (!list.Exists(x => Mathf.Abs(x - w) < 1e-3f)) list.Add(w);
                    }
                }
            }

            var result = new float[order.Count];
            for (int i = 0; i < order.Count; i++)
            {
                string shape = order[i];
                List<float> weights = frameWeights[shape];
                weights.Sort();
                foreach (float fw in weights)
                {
                    var dv = new Vector3[count];
                    var dn = new Vector3[count];
                    var dt = new Vector3[count];
                    foreach (Source s in sources)
                    {
                        int k = s.Mesh.GetBlendShapeIndex(shape);
                        if (k < 0 || s.NewIndex.Count == 0) continue;
                        Vector3[] sv, sn, st;
                        Sample(s.Mesh, k, fw, out sv, out sn, out st);
                        for (int j = 0; j < s.NewIndex.Count; j++)
                        {
                            int to = s.NewIndex[j];
                            int from = s.OldIndex[j];
                            dv[to] = sv[from];
                            dn[to] = sn[from];
                            dt[to] = st[from];
                        }
                    }
                    mesh.AddBlendShapeFrame(shape, fw, dv, dn, dt);
                }
                float w0;
                result[i] = rest.TryGetValue(shape, out w0) ? w0 : 0f;
            }
            return result;
        }

        // the bounds of all the pieces, in the space of the fused meshs root bone
        private static Bounds BoundsIn(Transform rootBone, List<Renderer> renderers)
        {
            bool any = false;
            var b = new Bounds();
            foreach (Renderer r in renderers)
            {
                Bounds w = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? w.min.x : w.max.x,
                        (i & 2) == 0 ? w.min.y : w.max.y,
                        (i & 4) == 0 ? w.min.z : w.max.z);
                    Vector3 local = rootBone.InverseTransformPoint(corner);
                    if (!any)
                    {
                        b = new Bounds(local, Vector3.zero);
                        any = true;
                    }
                    else b.Encapsulate(local);
                }
            }
            return b;
        }
    }
}
#endif
