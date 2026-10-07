#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using BAAA.MeshSimplification;
using UnityEngine;

namespace BAAA
{
    // the decimator's engine. the bundled one (Meshia, in its own assembly that only builds when the
    // project has burst, which every VRChat project does) looks at UVs, bones and blendshapes.
    // without it the old simplifier runs like it always did
    internal static class MeshReducer
    {
        private const string Bridge = "BAAA.Simplifier.BaaaSimplifier, BAAA.Simplifier";
        // two vertices that share less than half their bone weights never merge
        internal const float MinBoneOverlap = 0.5f;
        // a blendshape can pull two vertices apart up to twice how far apart they are, more and they stay separate
        internal const float BlendShapeStretch = 2f;
        // meshes under this many triangles are left alone, theres nothing to win there
        internal const int Small = 500;
        private const int Passes = 4;

        private static bool _looked;
        private static MethodInfo _simplify;
        private static MethodInfo _curve;
        private static readonly Dictionary<int, float[]> Curves = new Dictionary<int, float[]>();

        // tests switch the bundled one off to check the old one still works
        internal static bool OldOnly;

        internal static bool Bundled
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    try
                    {
                        Type bridge = Type.GetType(Bridge, false);
                        if (bridge != null)
                        {
                            _simplify = bridge.GetMethod("Simplify", BindingFlags.Public | BindingFlags.Static);
                            _curve = bridge.GetMethod("ErrorCurve", BindingFlags.Public | BindingFlags.Static);
                        }
                    }
                    catch (Exception)
                    {
                        _simplify = null;
                        _curve = null;
                    }
                }
                return !OldOnly && _simplify != null && _curve != null;
            }
        }

        // a copy of full with about target triangles
        internal static Mesh Reduce(Mesh full, int target)
        {
            int triangles = OutfitScanner.Triangles(full);
            if (target >= triangles) return Copy(full);
            float keep = (float)target / Mathf.Max(1, triangles);
            if (!Bundled) return Old(full, keep);

            bool[] locked = null;
            Mesh result = null;
            for (int pass = 0; pass < Passes; pass++)
            {
                if (result != null) UnityEngine.Object.DestroyImmediate(result);
                result = new Mesh();
                string error = Simplify(full, result, target, locked);
                if (error != null)
                {
                    Debug.LogWarning("Asset Assistant: the decimator couldn't do " + full.name + " (" + error + "), the old one did it instead.");
                    UnityEngine.Object.DestroyImmediate(result);
                    return Old(full, keep);
                }
                // the last go keeps what it got
                if (pass == Passes - 1 || Spikes.Lock(full, result, ref locked, pass) == 0) break;
            }
            result.name = full.name;
            return result;
        }

        private static string Simplify(Mesh full, Mesh into, int target, bool[] locked)
        {
            try
            {
                return (string)_simplify.Invoke(null, new object[] { full, into, target, locked, MinBoneOverlap, BlendShapeStretch });
            }
            catch (Exception e)
            {
                return (e.InnerException ?? e).Message;
            }
        }

        // how bad the worst merge is to get full down to each triangle count, index is the count, for the
        // mesh scaled to one unit across. null without the bundled engine. kept until unity reloads
        internal static float[] Curve(Mesh full)
        {
            if (full == null || !Bundled) return null;
            int key = full.GetInstanceID();
            float[] curve;
            if (Curves.TryGetValue(key, out curve)) return curve;
            try
            {
                curve = (float[])_curve.Invoke(null, new object[] { full, null, MinBoneOverlap, BlendShapeStretch });
            }
            catch (Exception e)
            {
                Debug.LogWarning("Asset Assistant: couldn't measure " + full.name + " (" + (e.InnerException ?? e).Message + ").");
                curve = null;
            }
            if (curve != null && curve.Length != OutfitScanner.Triangles(full) + 1) curve = null;
            Curves[key] = curve;
            return curve;
        }

        internal static Mesh Copy(Mesh full)
        {
            Mesh copy = UnityEngine.Object.Instantiate(full);
            copy.name = full.name;
            return copy;
        }

        // the old simplifier: shape only, the same share off every mesh. Open edges and UV seams
        // stay put so hems keep their shape
        internal static Mesh Old(Mesh full, float keep)
        {
            if (keep >= 0.999f) return Copy(full);
            var options = SimplificationOptions.Default;
            options.PreserveBorderEdges = true;
            options.PreserveUVSeamEdges = true;
            options.PreserveUVFoldoverEdges = true;
            options.EnableSmartLink = true;
            options.VertexLinkDistance = 0.0001;
            var simplifier = new MeshSimplifier { SimplificationOptions = options };
            simplifier.Initialize(full);
            simplifier.SimplifyMesh(keep);
            Mesh result = simplifier.ToMesh();
            result.name = full.name;
            return result;
        }

        internal static int[] AllTriangles(Mesh mesh)
        {
            var all = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) all.AddRange(mesh.GetTriangles(s));
            return all.ToArray();
        }
    }

    // a made-up pose: every bone gets pushed its own way and each vertex follows its weights. a kept
    // vertex whose weights don't match what the full mesh has at that spot lands somewhere else,
    // thats a spike. so is one that ended up far off the surface to begin with
    internal static class Spikes
    {
        private const int Poses = 2;
        // how far the bones get pushed, how far off a vertex may land then, and how far off the
        // surface it may sit at rest, all of the mesh's size
        private const float Push = 0.1f;
        private const float Off = 0.035f;
        private const float Away = 0.03f;
        // spots this much further than the closest one still count as "there". layered cloth has its
        // front and back on different bones a millimeter apart, the match is the one with the same bones
        private const float Slack = 0.01f;

        internal struct Spike
        {
            public Vector3 At;
            public float Radius;
        }

        internal static List<Spike> Find(Mesh full, Mesh reduced)
        {
            var spikes = new List<Spike>();
            Vector3[] fv = full.vertices;
            int[] ft = MeshReducer.AllTriangles(full);
            Vector3[] rv = reduced.vertices;
            if (fv.Length == 0 || ft.Length == 0 || rv.Length == 0) return spikes;
            BoneWeight[] fw = full.boneWeights;
            BoneWeight[] rw = reduced.boneWeights;
            int bones = full.bindposes.Length;
            bool skinned = bones > 0 && fw.Length == fv.Length && rw.Length == rv.Length;
            float size = Mathf.Max(full.bounds.size.magnitude, 1e-4f);

            Vector3[][] pushes = null;
            if (skinned)
            {
                var random = new System.Random(1234);
                pushes = new Vector3[Poses][];
                for (int p = 0; p < Poses; p++)
                {
                    pushes[p] = new Vector3[bones];
                    for (int b = 0; b < bones; b++) pushes[p][b] = Direction(random) * (size * Push);
                }
            }

            var grid = new SurfaceGrid(fv, ft);
            for (int i = 0; i < rv.Length; i++)
            {
                Vector3 bary;
                float distance;
                int t;
                if (skinned)
                {
                    BoneWeight kept = rw[i];
                    t = grid.Closest(rv[i], size * Slack, (tri, at) => Mismatch(kept, tri, at, ft, fw, pushes), out bary, out distance);
                }
                else t = grid.Closest(rv[i], out bary, out distance);
                if (t < 0) continue;
                int a = ft[t * 3], b = ft[t * 3 + 1], c = ft[t * 3 + 2];
                bool spike = distance > size * Away || (skinned && Mismatch(rw[i], t, bary, ft, fw, pushes) > size * Off);
                if (!spike) continue;
                float edge = Mathf.Max((fv[a] - fv[b]).magnitude, Mathf.Max((fv[b] - fv[c]).magnitude, (fv[c] - fv[a]).magnitude));
                spikes.Add(new Spike
                {
                    At = fv[a] * bary.x + fv[b] * bary.y + fv[c] * bary.z,
                    Radius = Mathf.Max(edge * 2f, size * 0.005f),
                });
            }
            return spikes;
        }

        // locks the full mesh's vertices around each spike so the next go leaves them be, a wider spot
        // each go. gives back how many got locked that weren't already
        internal static int Lock(Mesh full, Mesh reduced, ref bool[] locked, int pass)
        {
            List<Spike> spikes = Find(full, reduced);
            if (spikes.Count == 0) return 0;
            Vector3[] fv = full.vertices;
            if (locked == null || locked.Length != fv.Length) locked = new bool[fv.Length];
            int added = 0;
            foreach (Spike s in spikes)
            {
                float r = s.Radius * (1 + pass);
                float r2 = r * r;
                for (int v = 0; v < fv.Length; v++)
                {
                    if (locked[v] || (fv[v] - s.At).sqrMagnitude > r2) continue;
                    locked[v] = true;
                    added++;
                }
            }
            return added;
        }

        // how far apart a kept vertex and a spot on the full mesh end up, in the worse of the poses
        private static float Mismatch(BoneWeight kept, int t, Vector3 bary, int[] ft, BoneWeight[] fw, Vector3[][] pushes)
        {
            float worst = 0f;
            foreach (Vector3[] push in pushes)
            {
                Vector3 there = Pushed(fw[ft[t * 3]], push) * bary.x + Pushed(fw[ft[t * 3 + 1]], push) * bary.y + Pushed(fw[ft[t * 3 + 2]], push) * bary.z;
                worst = Mathf.Max(worst, (Pushed(kept, push) - there).magnitude);
            }
            return worst;
        }

        private static Vector3 Pushed(BoneWeight w, Vector3[] push)
        {
            return At(push, w.boneIndex0) * w.weight0 + At(push, w.boneIndex1) * w.weight1
                + At(push, w.boneIndex2) * w.weight2 + At(push, w.boneIndex3) * w.weight3;
        }

        private static Vector3 At(Vector3[] push, int bone)
        {
            return bone >= 0 && bone < push.Length ? push[bone] : Vector3.zero;
        }

        private static Vector3 Direction(System.Random random)
        {
            while (true)
            {
                var v = new Vector3((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f);
                float m = v.sqrMagnitude;
                if (m > 0.01f && m <= 1f) return v / Mathf.Sqrt(m);
            }
        }
    }

    // a surface's triangles sorted into boxes, to find the closest spot on it quick
    internal sealed class SurfaceGrid
    {
        private readonly Vector3[] _v;
        private readonly int[] _t;
        private readonly Vector3 _min;
        private readonly float _cell;
        private readonly int _nx, _ny, _nz;
        private readonly int[] _start;
        private readonly int[] _items;
        private readonly int[] _seen;
        private int _stamp;
        private readonly List<Near> _near = new List<Near>();

        private struct Near
        {
            public int Triangle;
            public float Distance;
            public Vector3 Bary;
        }

        public SurfaceGrid(Vector3[] vertices, int[] triangles)
        {
            _v = vertices;
            _t = triangles;
            int count = triangles.Length / 3;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < count * 3; i++)
            {
                Vector3 p = vertices[triangles[i]];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            if (count == 0) min = max = Vector3.zero;
            Vector3 size = max - min;
            float diag = Mathf.Max(size.magnitude, 1e-5f);
            float thin = diag * 1e-3f;
            float volume = Mathf.Max(size.x, thin) * Mathf.Max(size.y, thin) * Mathf.Max(size.z, thin);
            float cell = Mathf.Max(Mathf.Pow(volume / Mathf.Max(1, count), 1f / 3f), diag / 200f);
            int nx, ny, nz;
            while (true)
            {
                nx = Mathf.Max(1, Mathf.CeilToInt(size.x / cell));
                ny = Mathf.Max(1, Mathf.CeilToInt(size.y / cell));
                nz = Mathf.Max(1, Mathf.CeilToInt(size.z / cell));
                if ((long)nx * ny * nz <= 1000000) break;
                cell *= 1.25f;
            }
            _min = min;
            _cell = cell;
            _nx = nx;
            _ny = ny;
            _nz = nz;

            int cells = nx * ny * nz;
            var start = new int[cells + 1];
            for (int t = 0; t < count; t++) ForCells(t, c => start[c + 1]++);
            for (int c = 0; c < cells; c++) start[c + 1] += start[c];
            var items = new int[start[cells]];
            var fill = new int[cells];
            for (int t = 0; t < count; t++)
            {
                int tri = t;
                ForCells(t, c => items[start[c] + fill[c]++] = tri);
            }
            _start = start;
            _items = items;
            _seen = new int[count];
        }

        private void ForCells(int t, Action<int> cell)
        {
            Vector3 a = _v[_t[t * 3]], b = _v[_t[t * 3 + 1]], c = _v[_t[t * 3 + 2]];
            Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
            int x0 = Index(lo.x - _min.x, _nx), x1 = Index(hi.x - _min.x, _nx);
            int y0 = Index(lo.y - _min.y, _ny), y1 = Index(hi.y - _min.y, _ny);
            int z0 = Index(lo.z - _min.z, _nz), z1 = Index(hi.z - _min.z, _nz);
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) cell(x + _nx * (y + _ny * z));
        }

        private int Index(float d, int n)
        {
            return Mathf.Clamp(Mathf.FloorToInt(d / _cell), 0, n - 1);
        }

        // the closest spot: which triangle, where on it, how far. -1 when theres no surface
        public int Closest(Vector3 p, out Vector3 bary, out float distance)
        {
            return Search(p, 0f, null, out bary, out distance);
        }

        // the same, but out of the spots within slack of the closest one it takes the one that scores
        // lowest. distance stays the closest one's
        public int Closest(Vector3 p, float slack, Func<int, Vector3, float> score, out Vector3 bary, out float distance)
        {
            return Search(p, slack, score, out bary, out distance);
        }

        // on a uv seam both sides are just as close, this takes the side whose uv there is nearest to uv
        public int Closest(Vector3 p, Vector2[] uvs, Vector2 uv, float slack, out Vector3 bary, out float distance)
        {
            return Search(p, slack, (t, b) => (uvs[_t[t * 3]] * b.x + uvs[_t[t * 3 + 1]] * b.y + uvs[_t[t * 3 + 2]] * b.z - uv).sqrMagnitude, out bary, out distance);
        }

        private int Search(Vector3 p, float slack, Func<int, Vector3, float> score, out Vector3 bary, out float distance)
        {
            bary = Vector3.zero;
            distance = float.MaxValue;
            if (_seen.Length == 0) return -1;
            _stamp++;
            _near.Clear();
            int cx = Index(p.x - _min.x, _nx), cy = Index(p.y - _min.y, _ny), cz = Index(p.z - _min.z, _nz);
            int most = Mathf.Max(_nx, Mathf.Max(_ny, _nz));
            int best = -1;
            for (int k = 0; k <= most; k++)
            {
                // nothing in this ring can beat what we have
                if (k > 0 && (k - 1) * _cell > distance + slack) break;
                for (int z = cz - k; z <= cz + k; z++)
                {
                    if (z < 0 || z >= _nz) continue;
                    for (int y = cy - k; y <= cy + k; y++)
                    {
                        if (y < 0 || y >= _ny) continue;
                        // only the ring's shell, the inside was done already
                        int step = k == 0 || Math.Abs(z - cz) == k || Math.Abs(y - cy) == k ? 1 : 2 * k;
                        for (int x = cx - k; x <= cx + k; x += step)
                        {
                            if (x < 0 || x >= _nx) continue;
                            int cell = x + _nx * (y + _ny * z);
                            for (int i = _start[cell]; i < _start[cell + 1]; i++)
                            {
                                int t = _items[i];
                                if (_seen[t] == _stamp) continue;
                                _seen[t] = _stamp;
                                Vector3 b;
                                Vector3 q = OnTriangle(p, _v[_t[t * 3]], _v[_t[t * 3 + 1]], _v[_t[t * 3 + 2]], out b);
                                float d = (q - p).magnitude;
                                if (score != null && d <= distance + slack) _near.Add(new Near { Triangle = t, Distance = d, Bary = b });
                                if (d < distance)
                                {
                                    distance = d;
                                    bary = b;
                                    best = t;
                                }
                            }
                        }
                    }
                }
            }
            if (score == null || _near.Count < 2) return best;
            float lowest = float.MaxValue;
            foreach (Near n in _near)
            {
                if (n.Distance > distance + slack) continue;
                float s = score(n.Triangle, n.Bary);
                if (s >= lowest) continue;
                lowest = s;
                best = n.Triangle;
                bary = n.Bary;
            }
            return best;
        }

        // closest point on a triangle, from Ericson's Real-Time Collision Detection
        private static Vector3 OnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                bary = new Vector3(1f, 0f, 0f);
                return a;
            }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                bary = new Vector3(0f, 1f, 0f);
                return b;
            }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 - d3 > 0f ? d1 / (d1 - d3) : 0f;
                bary = new Vector3(1f - v, v, 0f);
                return a + ab * v;
            }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                bary = new Vector3(0f, 0f, 1f);
                return c;
            }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 - d6 > 0f ? d2 / (d2 - d6) : 0f;
                bary = new Vector3(1f - w, 0f, w);
                return a + ac * w;
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float sum = (d4 - d3) + (d5 - d6);
                float w = sum > 0f ? (d4 - d3) / sum : 0f;
                bary = new Vector3(0f, 1f - w, w);
                return b + (c - b) * w;
            }
            float all = va + vb + vc;
            if (Mathf.Abs(all) < 1e-30f)
            {
                bary = new Vector3(1f, 0f, 0f);
                return a;
            }
            float vv = vb / all, ww = vc / all;
            bary = new Vector3(1f - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }
    }

    internal sealed class BudgetMesh
    {
        public int Full;
        // renderers showing it, VRChat counts it once for each
        public int Uses = 1;
        public float[] Curve;
        // a count the slider doesn't change (too small, full detail, or its own share). -1 follows the slider
        public int Fixed = -1;
    }

    // splits one triangle count for the whole outfit between its meshes. with the bundled engine
    // every mesh stops at the same worst merge, so the cuts land where they cost the least detail.
    // without it every mesh loses the same share
    internal static class TriangleBudget
    {
        // the slider's low end: set meshes as they are, the rest at a tenth
        internal static long Floor(List<BudgetMesh> meshes)
        {
            long floor = 0;
            foreach (BudgetMesh m in meshes) floor += (long)m.Uses * (m.Fixed >= 0 ? m.Fixed : Mathf.CeilToInt(m.Full * 0.1f));
            return floor;
        }

        internal static long Full(List<BudgetMesh> meshes)
        {
            long full = 0;
            foreach (BudgetMesh m in meshes) full += (long)m.Uses * m.Full;
            return full;
        }

        // how many triangles each mesh keeps so all together land on target
        internal static int[] Split(List<BudgetMesh> meshes, long target)
        {
            var counts = new int[meshes.Count];
            long rest = target;
            long autoFull = 0;
            int autos = 0;
            bool curves = true;
            for (int i = 0; i < meshes.Count; i++)
            {
                BudgetMesh m = meshes[i];
                if (m.Fixed >= 0)
                {
                    counts[i] = Mathf.Min(m.Fixed, m.Full);
                    rest -= (long)m.Uses * counts[i];
                    continue;
                }
                counts[i] = m.Full;
                autoFull += (long)m.Uses * m.Full;
                autos++;
                if (m.Curve == null) curves = false;
            }
            if (autos == 0 || rest >= autoFull) return counts;
            rest = Math.Max(rest, 0);

            if (!curves || autos == 1)
            {
                double share = (double)rest / Math.Max(1, autoFull);
                for (int i = 0; i < meshes.Count; i++)
                    if (meshes[i].Fixed < 0) counts[i] = Mathf.Clamp((int)Math.Round(meshes[i].Full * share), 1, meshes[i].Full);
                return counts;
            }

            // every worst-merge value there is, then the smallest one that fits
            var values = new List<float>();
            foreach (BudgetMesh m in meshes)
            {
                if (m.Fixed >= 0) continue;
                foreach (float v in m.Curve)
                    if (!float.IsInfinity(v) && !float.IsNaN(v)) values.Add(v);
            }
            if (values.Count == 0) return counts;
            values.Sort();
            int lo = 0, hi = values.Count - 1, found = values.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Total(meshes, values[mid]) <= rest)
                {
                    found = mid;
                    hi = mid - 1;
                }
                else lo = mid + 1;
            }
            for (int i = 0; i < meshes.Count; i++)
                if (meshes[i].Fixed < 0) counts[i] = Count(meshes[i], values[found]);

            // one step of the limit can drop a mesh a long stretch at once (a costly merge, then lots of
            // cheap ones), landing well under the target. the meshes that dropped get the difference back,
            // any count along a mesh's way down is one the engine stops at
            long left = rest - Total(meshes, values[found]);
            int before = found;
            while (before > 0 && values[before - 1] >= values[found]) before--;
            float higher = before > 0 ? values[before - 1] : float.NegativeInfinity;
            for (int i = 0; i < meshes.Count && left > 0; i++)
            {
                BudgetMesh m = meshes[i];
                if (m.Fixed >= 0 || m.Uses <= 0) continue;
                int add = (int)Math.Min(Count(m, higher) - counts[i], left / m.Uses);
                if (add <= 0) continue;
                counts[i] += add;
                left -= (long)add * m.Uses;
            }
            return counts;
        }

        private static long Total(List<BudgetMesh> meshes, float limit)
        {
            long total = 0;
            foreach (BudgetMesh m in meshes)
                if (m.Fixed < 0) total += (long)m.Uses * Count(m, limit);
            return total;
        }

        // the fewest triangles the mesh gets to without a merge worse than limit
        private static int Count(BudgetMesh m, float limit)
        {
            float[] c = m.Curve;
            int lo = 0, hi = c.Length - 1;
            if (c[hi] > limit) return hi;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (c[mid] <= limit) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }
    }
}
#endif
