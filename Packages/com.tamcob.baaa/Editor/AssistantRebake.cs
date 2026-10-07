#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace BAAA
{
    // the decimator's texture bake for Extreme and Quest versions. every full-detail vertex finds its
    // spot on the reduced mesh, then every full-detail triangle gets drawn at those spots into a new
    // texture, so the reduced mesh shows what the full one showed there
    internal static partial class AtlasBuilder
    {
        internal sealed class RebakeMap
        {
            // the full mesh's triangles on the parts that use the texture, and its uvs
            public int[] Triangles;
            public Vector2[] Uv;
            // per full vertex: the uv where it landed on the reduced mesh, and that uv island
            public Vector2[] Landed;
            public int[] Island;
        }

        internal static RebakeMap MapForRebake(Mesh full, Mesh reduced, List<int> submeshes)
        {
            Vector3[] fv = full.vertices;
            Vector3[] rv = reduced.vertices;
            var fuv = new List<Vector2>();
            var ruvList = new List<Vector2>();
            full.GetUVs(0, fuv);
            reduced.GetUVs(0, ruvList);
            var map = new RebakeMap { Triangles = new int[0], Uv = fuv.ToArray(), Landed = new Vector2[fv.Length], Island = new int[fv.Length] };
            for (int i = 0; i < map.Island.Length; i++) map.Island[i] = -1;
            Vector2[] ruv = ruvList.ToArray();
            if (map.Uv.Length != fv.Length || ruv.Length != rv.Length) return map;

            var ft = new List<int>();
            var rt = new List<int>();
            foreach (int s in submeshes)
            {
                if (s < full.subMeshCount && full.GetTopology(s) == MeshTopology.Triangles) ft.AddRange(full.GetTriangles(s));
                if (s < reduced.subMeshCount && reduced.GetTopology(s) == MeshTopology.Triangles) rt.AddRange(reduced.GetTriangles(s));
            }
            map.Triangles = ft.ToArray();
            int[] rtris = rt.ToArray();
            if (rtris.Length == 0) return map;

            // uv islands of the reduced mesh: uv seams split vertices, so triangles that share one are on the same island
            var parent = new int[rv.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            for (int t = 0; t + 2 < rtris.Length; t += 3)
            {
                IslandJoin(parent, rtris[t], rtris[t + 1]);
                IslandJoin(parent, rtris[t], rtris[t + 2]);
            }
            var grid = new SurfaceGrid(rv, rtris);
            float slack = Mathf.Max(reduced.bounds.size.magnitude * 1e-4f, 1e-6f);
            var seen = new bool[fv.Length];
            foreach (int v in map.Triangles)
            {
                if (seen[v]) continue;
                seen[v] = true;
                Vector3 bary;
                float distance;
                int t = grid.Closest(fv[v], ruv, map.Uv[v], slack, out bary, out distance);
                if (t < 0) continue;
                int a = rtris[t * 3], b = rtris[t * 3 + 1], c = rtris[t * 3 + 2];
                map.Landed[v] = ruv[a] * bary.x + ruv[b] * bary.y + ruv[c] * bary.z;
                map.Island[v] = IslandRoot(parent, a);
            }
            return map;
        }

        // the texture again for the reduced mesh. where nothing lands cleanly the old pixels stay,
        // thats what the reduced mesh shows there anyway
        internal static Color32[] Rebake(RebakeMap map, Color32[] source, int side)
        {
            var result = (Color32[])source.Clone();
            int[] tris = map.Triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int island = map.Island[a];
                if (island < 0 || map.Island[b] != island || map.Island[c] != island) continue;
                Vector2 ua = map.Uv[a], ub = map.Uv[b], uc = map.Uv[c];
                Vector2 la = map.Landed[a], lb = map.Landed[b], lc = map.Landed[c];
                float before = Edge(ua, ub, uc);
                float after = Edge(la, lb, lc);
                // flipped, flat, or stretched way out
                if (before * after <= 0f || Mathf.Abs(after) > 4f * Mathf.Abs(before)) continue;
                Raster(la * side, lb * side, lc * side, side, (index, w) =>
                {
                    Vector4 col = Bilinear(source, side, side, ua * w.x + ub * w.y + uc * w.z);
                    result[index] = new Color32((byte)(col.x * 255f + 0.5f), (byte)(col.y * 255f + 0.5f), (byte)(col.z * 255f + 0.5f), (byte)(col.w * 255f + 0.5f));
                });
            }
            return result;
        }

        private static int IslandRoot(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void IslandJoin(int[] parent, int a, int b)
        {
            a = IslandRoot(parent, a);
            b = IslandRoot(parent, b);
            if (a != b) parent[a] = b;
        }
    }
}
#endif
