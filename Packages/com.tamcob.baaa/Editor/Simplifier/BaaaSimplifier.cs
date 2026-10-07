using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace BAAA.Simplifier
{
    // the asset assistant calls these by reflection, so only unity types and primitives in the signatures
    public static class BaaaSimplifier
    {
        // null means it worked, anything else is the reason it didn't. never throws
        public static string Simplify(Mesh source, Mesh destination, int targetTriangles, bool[] lockedVertices, float minBoneOverlap, float blendShapeStretch)
        {
            Mesh work = null;
            try
            {
                if (destination == null)
                {
                    return "destination mesh is null";
                }
                var problem = CheckSource(source, lockedVertices);
                if (problem != null)
                {
                    return problem;
                }

                var bounds = source.bounds;
                var target = new MeshSimplificationTarget
                {
                    Kind = MeshSimplificationTargetKind.AbsoluteTriangleCount,
                    Value = Math.Max(0, targetTriangles),
                };
                work = Normalized(source, out var center, out var scale);
                MeshSimplifier.Simplify(work, target, BuildOptions(minBoneOverlap, blendShapeStretch), null, lockedVertices, destination);
                Restore(destination, center, scale);
                destination.bounds = bounds; // matters when source == destination
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (work != null) UnityEngine.Object.DestroyImmediate(work);
            }
        }

        // curve[t] = worst merge cost it took to get down to t triangles or less, +inf if it never got there.
        // Simplify with the same args and target t walks the exact same merges. the costs are for the mesh
        // scaled to one unit across, so callers comparing meshes scale them by their size squared
        public static float[] ErrorCurve(Mesh source, bool[] lockedVertices, float minBoneOverlap, float blendShapeStretch)
        {
            Mesh work = null;
            try
            {
                if (CheckSource(source, lockedVertices) != null)
                {
                    return null;
                }
                work = Normalized(source, out _, out _);
                return MeshSimplifier.ComputeErrorCurve(work, BuildOptions(minBoneOverlap, blendShapeStretch), null, lockedVertices);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (work != null) UnityEngine.Object.DestroyImmediate(work);
            }
        }

        // a mesh imported at 1/100 scale is a few thousandths across, and its merge costs drown in float
        // rounding (most read as 0). so the work happens on a copy one unit across, scaled back after.
        // that also keeps the link distance and the blendshape allowance the same size on every mesh
        static Mesh Normalized(Mesh source, out Vector3 center, out float scale)
        {
            var bounds = source.bounds;
            center = bounds.center;
            var size = bounds.size.magnitude;
            scale = size > 1e-12f ? 1f / size : 1f;
            var work = UnityEngine.Object.Instantiate(source);
            var vertices = work.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = (vertices[i] - center) * scale;
            }
            work.vertices = vertices;
            ScaleBlendShapes(work, scale);
            work.RecalculateBounds();
            return work;
        }

        static void Restore(Mesh mesh, Vector3 center, float scale)
        {
            var back = 1f / scale;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = vertices[i] * back + center;
            }
            mesh.vertices = vertices;
            ScaleBlendShapes(mesh, back);
        }

        // position deltas grow and shrink with the mesh, normal and tangent deltas don't
        static void ScaleBlendShapes(Mesh mesh, float scale)
        {
            var shapes = mesh.blendShapeCount;
            if (shapes == 0)
            {
                return;
            }
            var count = mesh.vertexCount;
            var names = new string[shapes];
            var weights = new float[shapes][];
            var deltas = new Vector3[shapes][][];
            var normals = new Vector3[shapes][][];
            var tangents = new Vector3[shapes][][];
            for (int s = 0; s < shapes; s++)
            {
                names[s] = mesh.GetBlendShapeName(s);
                var frames = mesh.GetBlendShapeFrameCount(s);
                weights[s] = new float[frames];
                deltas[s] = new Vector3[frames][];
                normals[s] = new Vector3[frames][];
                tangents[s] = new Vector3[frames][];
                for (int f = 0; f < frames; f++)
                {
                    weights[s][f] = mesh.GetBlendShapeFrameWeight(s, f);
                    deltas[s][f] = new Vector3[count];
                    normals[s][f] = new Vector3[count];
                    tangents[s][f] = new Vector3[count];
                    mesh.GetBlendShapeFrameVertices(s, f, deltas[s][f], normals[s][f], tangents[s][f]);
                    for (int i = 0; i < count; i++)
                    {
                        deltas[s][f][i] *= scale;
                    }
                }
            }
            mesh.ClearBlendShapes();
            for (int s = 0; s < shapes; s++)
            {
                for (int f = 0; f < weights[s].Length; f++)
                {
                    mesh.AddBlendShapeFrame(names[s], weights[s][f], deltas[s][f], normals[s][f], tangents[s][f]);
                }
            }
        }

        static MeshSimplifierOptions BuildOptions(float minBoneOverlap, float blendShapeStretch)
        {
            var options = MeshSimplifierOptions.Default;
            // 0, negative or NaN all mean off
            options.MinBoneOverlap = minBoneOverlap > 0f ? minBoneOverlap : 0f;
            options.BlendShapeStretch = blendShapeStretch > 0f ? blendShapeStretch : 0f;
            return options;
        }

        // catch the stuff that would blow up inside a job, jobs only log their exceptions
        static string CheckSource(Mesh source, bool[] lockedVertices)
        {
            if (source == null)
            {
                return "source mesh is null";
            }
            var vertexCount = source.vertexCount;
            if (vertexCount == 0)
            {
                return "source mesh has no vertices";
            }
            if (lockedVertices != null && lockedVertices.Length != vertexCount)
            {
                return "lockedVertices has " + lockedVertices.Length + " entries but the mesh has " + vertexCount + " vertices";
            }

            using (var meshDataArray = MeshUtility.AcquireReadOnlyMeshData(source))
            {
                var meshData = meshDataArray[0];
                if (meshData.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32
                    || meshData.GetVertexAttributeDimension(VertexAttribute.Position) != 3)
                {
                    return "positions have to be Float32 x3";
                }
                // Normal up to BlendWeight are all read as floats
                for (var attribute = VertexAttribute.Normal; attribute <= VertexAttribute.BlendWeight; attribute++)
                {
                    if (meshData.HasVertexAttribute(attribute) && !IsFloatFormat(meshData.GetVertexAttributeFormat(attribute)))
                    {
                        return attribute + " uses " + meshData.GetVertexAttributeFormat(attribute) + ", not supported";
                    }
                }
                if (meshData.HasVertexAttribute(VertexAttribute.BlendIndices) && !IsIntFormat(meshData.GetVertexAttributeFormat(VertexAttribute.BlendIndices)))
                {
                    return "BlendIndices uses " + meshData.GetVertexAttributeFormat(VertexAttribute.BlendIndices) + ", not supported";
                }
                var triangles = 0;
                for (int i = 0; i < meshData.subMeshCount; i++)
                {
                    var subMesh = meshData.GetSubMesh(i);
                    if (subMesh.topology == MeshTopology.Triangles)
                    {
                        triangles += subMesh.indexCount / 3;
                    }
                }
                if (triangles == 0)
                {
                    return "source mesh has no triangles";
                }
            }
            return null;
        }

        static bool IsFloatFormat(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                    return true;
                default:
                    return false;
            }
        }

        static bool IsIntFormat(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.UInt8:
                case VertexAttributeFormat.SInt8:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return true;
                default:
                    return false;
            }
        }
    }
}
