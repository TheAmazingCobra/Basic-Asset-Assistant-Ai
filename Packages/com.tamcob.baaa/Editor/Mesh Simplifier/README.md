# Mesh Simplifier

UnityMeshSimplifier 3.1.1 by Mattias Edlund, MIT license (see LICENSE.md and
Third Party Notices.md). https://github.com/Whinarn/UnityMeshSimplifier

The Asset Assistant uses it for the triangle reduction slider of the Quest
version. Changes from the original:

- The namespace is `BAAA.MeshSimplification`, so it
  never clashes with a copy of the original package in the same project.
- Only the files the simplifier itself needs are here; the LOD generator,
  mesh combiner, components and editor files were left out.
