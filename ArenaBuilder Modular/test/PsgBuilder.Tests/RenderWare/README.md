## RenderWare 6.14.00 test ports

This folder contains xUnit ports of **RenderWare Collision 6.14.00** unit tests (from the Skate documentation drop).

### Covered (ported + passing)

- **`test-unitcluster.cpp`** → `RenderWareUnitClusterTests.cs`
  - `SortAndCompressVertexSet`
  - `GetVertexCode`
- **`test-clusteredmeshbuilderutils.cpp`** → `RenderWareVertexCompressionTests.cs`, `RenderWareEdgeCodeTests.cs`
  - `VertexCompression::DetermineCompressionModeAndOffsetForRange`
  - `ClusteredMeshBuilderUtils::EdgeCosineToAngleByte` (and key edge-flag invariants)
- **`test-triangleneighborfinder.cpp`** → `RenderWareTriangleNeighborFinderTests.cs`
  - vertex→triangle map invariants
  - basic two-triangle edge mating
- **`test-trianglegeometry.cpp`** → `RenderWareTriangleGeometryTests.cs`
  - `TriangleValidator::IsTriangleValid`

### Not yet ported (requires missing features or large subsystems)

The RW suite includes many additional tests (unit/cluster builders, cluster data builder, full clustered-mesh builder, kd-tree runtime queries, etc.).
We can add ports incrementally as we implement/align those subsystems (quads, group IDs, full RW cluster layout, query pipeline).

