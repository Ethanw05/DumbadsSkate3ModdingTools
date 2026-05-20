using System.Buffers.Binary;
using ArenaBuilder.Collision;
using ArenaBuilder.Collision.ClusteredMesh;
using ArenaBuilder.Collision.Serialization;
using ArenaBuilder.Core;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.Collision;
using ArenaBuilder.Core.Platforms.PS3.Pegasus.Mesh;
using ArenaBuilder.Core.Psg;
using DlcBuilder.Modules.LocatorPsg;

namespace DlcBuilder.Modules.OtsPsg;

/// Composes the per-challenge `cSim_Global.psg` for an OTS challenge.
/// Stream File Tool packs the result into `cSim_Global.psf` and auto-generates
/// the matching `_Sim.psm` / `_Sim.pst` manifests when run against the
/// challenge's mission folder.
///
/// PSG dictionary layout (verified against retail OTS challenge dumps):
///   [0]  VersionData                (16 B,  type 0x00EB0008)
///   [1]  TriggerInstanceData        (var,   type 0x00EB0019)
///   [2]  Volume                     (96 B,  type 0x00080001)
///   [3]  ClusteredMesh              (var,   type 0x00080006)
///   [4]  CollisionModelData         (32 B,  type 0x00EB000A) — m_pCModel target for trigger 0
///   [5]  Volume                     (96 B,  type 0x00080001)
///   ...  per trigger (Volume + ClusteredMesh + CollisionModelData + Volume) ...
///   [Locationdescdata]              (var,   type 0x00EB0009)
///   [TableOfContents]               (var,   type 0x00EB000B)
///
/// Each `pegasus::tTriggerInstance` in TriggerInstanceData points at the
/// matching CollisionModelData via encoded dict index — `m_pCModel = 4 + i*4`
/// for trigger i. The LocationDescData holds the chevron / start / vis / wait
/// locator transforms that the runtime resolves via
/// `LocationManager::FindLocation(name)`.
public static class OtsPsgBytesBuilder
{
    private const uint TypeVersion       = 0x00EB0008;
    private const uint TypeTriggerData   = 0x00EB0019;
    private const uint TypeVolume        = 0x00080001;
    private const uint TypeClusterMesh   = 0x00080006;
    private const uint TypeCollModelData = 0x00EB000A;
    private const uint TypeLocDescData   = 0x00EB0009;
    private const uint TypeTOC           = 0x00EB000B;

    /// 64-entry type registry shared with stock content (matches
    /// CollisionPsgComposer's TypeRegistry64). Order is significant — the
    /// engine indexes types by position when binding RW objects.
    private static readonly uint[] TypeRegistry64 =
    {
        0x00000000, 0x00010030, 0x00010031, 0x00010032, 0x00010033, 0x00010034,
        0x00010010, 0x00EB0000, 0x00EB0001, 0x00EB0003, 0x00EB0004, 0x00EB0005,
        0x00EB0006, 0x00EB000A, 0x00EB000D, 0x00EB0019, 0x00EB0007, 0x00EB0008,
        0x00EB000C, 0x00EB0009, 0x00EB000B, 0x00EB000E, 0x00EB0011, 0x00EB000F,
        0x00EB0010, 0x00EB0012, 0x00EB0022, 0x00EB0013, 0x00EB0014, 0x00EB0015,
        0x00EB0016, 0x00EB001A, 0x00EB001C, 0x00EB001D, 0x00EB001B, 0x00EB001E,
        0x00EB001F, 0x00EB0021, 0x00EB0017, 0x00EB0020, 0x00EB0024, 0x00EB0023,
        0x00EB0025, 0x00EB0026, 0x00EB0027, 0x00EB0028, 0x00EB0029, 0x00EB0018,
        0x00EC0010, 0x00010000, 0x00010002, 0x000200EB, 0x000200EA, 0x000200E9,
        0x00020081, 0x000200E8, 0x00080002, 0x00080001, 0x00080006, 0x00080003,
        0x00080004, 0x00040006, 0x00040007, 0x0001000F
    };

    /// Build the full per-challenge `cSim_Global.psg` bytes.
    ///
    /// `locators` MUST be the per-OTS named transforms as INDEPENDENT
    /// top-level entries (optional chev_*, vis_1, startlocator,
    /// waitlocator). Verified against retail DW
    /// content/missions/ots_dwmc_01/cSim_Global/5822CECF4EF38F6C.psg
    /// (numLocs=6, numSub=12). The earlier shape — one anchor LocSpec with
    /// 6 sub-locations — left every per-OTS name unregistered, since
    /// `cLocationManager::RegArena` only iterates top-level tLocationDesc.
    public static void Build(
        string challengeKey,
        IReadOnlyList<OtsTriggerVolume> triggers,
        IReadOnlyList<LocationDescDataBuilder.LocSpec> locators,
        Stream output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeKey);
        ArgumentNullException.ThrowIfNull(triggers);
        if (triggers.Count == 0) throw new ArgumentException("Need at least one trigger volume.", nameof(triggers));
        ArgumentNullException.ThrowIfNull(locators);
        if (locators.Count == 0) throw new ArgumentException("Need at least one locator.", nameof(locators));
        ArgumentNullException.ThrowIfNull(output);

        var objects = new List<PsgObjectSpec>(2 + triggers.Count * 4 + 2);

        // [0] VersionData
        objects.Add(new PsgObjectSpec(VersionDataBuilder.Build(), TypeVersion));

        // [1] TriggerInstanceData — references each CollisionModelData by its
        // dict index. Dict layout from offset [2] onward is repeating
        // (Volume, ClusteredMesh, CollisionModelData, Volume) so the
        // CollisionModelData for trigger i sits at index 4 + i*4.
        var triggerSpecs = new List<TriggerInstanceDataBuilder.InstanceSpec>(triggers.Count);
        for (int i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            float xMin = float.PositiveInfinity, xMax = float.NegativeInfinity;
            float zMin = float.PositiveInfinity, zMax = float.NegativeInfinity;
            foreach (var p in t.Polygon)
            {
                if (p.X < xMin) xMin = p.X; if (p.X > xMax) xMax = p.X;
                if (p.Z < zMin) zMin = p.Z; if (p.Z > zMax) zMax = p.Z;
            }

            float cosY = MathF.Cos(t.YawRadians);
            float sinY = MathF.Sin(t.YawRadians);
            float[] matrix =
            {
                cosY, 0f, -sinY, 0f,
                0f,   1f,  0f,   0f,
                sinY, 0f,  cosY, 0f,
                0f,   0f,  0f,   1f,
            };

            // Polygon vertex order convention (both OTS and race builders):
            //   [0] = local (−hx, −hz)   [1] = local (+hx, −hz)
            //   [2] = local (+hx, +hz)   [3] = local (−hx, +hz)
            // Leading edge = local +X side, trailing = local −X side.
            var leading = new (float, float, float)[]
            {
                (t.Polygon[2].X, t.MinY, t.Polygon[2].Z),
                (t.Polygon[1].X, t.MinY, t.Polygon[1].Z),
            };
            var trailing = new (float, float, float)[]
            {
                (t.Polygon[3].X, t.MinY, t.Polygon[3].Z),
                (t.Polygon[0].X, t.MinY, t.Polygon[0].Z),
            };

            triggerSpecs.Add(new TriggerInstanceDataBuilder.InstanceSpec
            {
                BBoxMin = (xMin, t.MinY, zMin),
                BBoxMax = (xMax, t.MaxY, zMax),
                Guid = t.Guid,
                GuidLocal = t.GuidLocal,
                CollisionModelDictIndex = (uint)(4 + i * 4),
                Name = t.Name,
                Type = (TriggerInstanceDataBuilder.TriggerType)t.TriggerType,
                TransformMatrix = matrix,
                LeadingEdge = leading,
                TrailingEdge = trailing,
            });
        }
        objects.Add(new PsgObjectSpec(TriggerInstanceDataBuilder.Build(triggerSpecs), TypeTriggerData));

        // [2..] Per-trigger 4-tuple: Volume + ClusteredMesh + CollisionModelData + Volume.
        for (int i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            string ns = $"{challengeKey}::{t.Name}";
            uint clusteredMeshDictIndex = (uint)(objects.Count + 1);

            // Outer Volume container
            objects.Add(new PsgObjectSpec(VolumeBuilder.Build(clusteredMeshDictIndex), TypeVolume));

            // ClusteredMesh — fan-triangulated polygon prism
            var input = new OtsTriggerVolumeInput(t, ns);
            var pipeline = ClusteredMeshPipeline.BuildComplete(
                input.Vertices, input.Faces, enableVertexSmoothing: true);
            byte[] clusterBlob = ClusteredMeshBinarySerializer.Serialize(
                pipeline, granularity: 0.001f, forceUncompressed: true, surfaceIds: null);
            objects.Add(new PsgObjectSpec(clusterBlob, TypeClusterMesh));

            // CollisionModelData — referenced by tTriggerInstance.m_pCModel via dict index.
            // m_BoundingVolume must be the dict index of the inner Volume that
            // follows immediately after this CollisionModelData (= 5 + i*4).
            // Without it cTriggerVolumeManager::TestVolume dereferences a null
            // bounding volume during world load and the trigger is skipped.
            uint innerVolumeDictIndex = (uint)(objects.Count + 1);
            objects.Add(new PsgObjectSpec(CollisionModelDataBuilder.Build(innerVolumeDictIndex), TypeCollModelData));

            // Inner Volume — retail OTS PSGs use VOLUMETYPEBOX with half-extents
            // derived from the trigger's axis-aligned bounding box.
            float bxMin = float.PositiveInfinity, bxMax = float.NegativeInfinity;
            float bzMin = float.PositiveInfinity, bzMax = float.NegativeInfinity;
            foreach (var p in t.Polygon)
            {
                if (p.X < bxMin) bxMin = p.X; if (p.X > bxMax) bxMax = p.X;
                if (p.Z < bzMin) bzMin = p.Z; if (p.Z > bzMax) bzMax = p.Z;
            }
            float hx = (bxMax - bxMin) / 2f;
            float hy = (t.MaxY - t.MinY) / 2f;
            float hz = (bzMax - bzMin) / 2f;
            float cx = (bxMin + bxMax) / 2f;
            float cy = (t.MinY + t.MaxY) / 2f;
            float cz = (bzMin + bzMax) / 2f;
            objects.Add(new PsgObjectSpec(VolumeBuilder.BuildBox(hx, hy, hz, cx, cy, cz), TypeVolume));
        }

        // [..] LocationDescData — optional chev_*, vis_1, startlocator, waitlocator
        // emitted as INDEPENDENT top-level tLocationDesc entries (matches DW
        // ots_dwmc_01 cSim_Global PSG: numLocs=6 + 12 sub-spawn slots on
        // startlocator/waitlocator). Anything resolved by name through a
        // tLocationID field has to be a top-level entry — RegArena only
        // walks `m_LocationDescs`.
        byte[] locDescBlob = LocationDescDataBuilder.BuildMultiple(locators);
        objects.Add(new PsgObjectSpec(locDescBlob, TypeLocDescData));

        // TOC — built last; entries reference the above objects.
        // Pattern matches retail DW ots_dwmc_01 cSim_Global PSG:
        //   - N TriggerInstanceSubref entries (one per trigger; subref idx 0..N-1)
        //   - 1 TriggerInstanceData entry          (m_pObject = dict idx 1)
        //   - M LocationDescSubref entries         (one per top-level locator; subref idx N..N+M-1)
        //   - 1 LocationDescData entry             (m_pObject = dict idx of LocationDescData)
        //
        // Without the TriggerInstanceSubref TOC entries + matching subref
        // records, cTriggerVolumeManager::RegArena never discovers any of
        // the per-volume tTriggerInstance records, and the engine silently
        // skips trigger registration on world load. The challenge HUD still
        // shows (challenge_local_data is loaded via VLT), but ObjectiveTrigger
        // .EnteredVolume() never matches a registered volume so no points
        // are awarded. Verified against retail DW dump: 3 TriggerInstanceSubref
        // entries + 3 corresponding subref records at offsets 0x20/0x110/0x200
        // (= 0x20 + i * 0xF0 = TriggerInstanceData header pad + i*InstanceStride).
        // TOC LocationDescData GUID is derived from the *first* top-level locator.
        // Retail OTS always has `{key}_chev_1` first; keep that stable (see
        // `OtsLayout` structural chevrons + `SubLocSpec.OmitFromChallengeLocalVisualIndicators`).
        ulong locatorGuid = locators[0].Guid;
        ulong tableGuid = locatorGuid ^ 0x9E3779B97F4A7C15UL;
        ulong triggerDataGuid = Lookup8Hash.HashString(challengeKey + "::triggers");

        var tocEntries = new List<PsgTocEntry>(triggers.Count + 1 + locators.Count + 1);
        // TriggerInstanceSubref entries — one per trigger; ObjectPtr = 0x00800000 | subrefIndex
        for (int i = 0; i < triggers.Count; i++)
        {
            tocEntries.Add(new PsgTocEntry(
                NameOrHash: 0u,
                Guid: triggers[i].Guid,
                TypeId: 0x00EB006Bu,
                ObjectPtr: 0x00800000u + (uint)i));
        }
        // TriggerInstanceData entry — m_pObject = dict index 1 (TriggerInstanceData is always [1])
        tocEntries.Add(new PsgTocEntry(
            NameOrHash: 0u,
            Guid: triggerDataGuid,
            TypeId: TypeTriggerData,
            ObjectPtr: 1u));
        // LocationDescSubref entries — one per top-level locator; subref index continues from triggers
        for (int j = 0; j < locators.Count; j++)
        {
            tocEntries.Add(new PsgTocEntry(
                NameOrHash: 0u,
                Guid: locators[j].Guid,
                TypeId: 0x00EB0068u,
                ObjectPtr: 0x00800000u + (uint)(triggers.Count + j)));
        }
        // LocationDescData entry — m_pObject = dict index (TOC not yet appended, so == objects.Count - 1)
        tocEntries.Add(new PsgTocEntry(
            NameOrHash: 0u,
            Guid: tableGuid,
            TypeId: TypeLocDescData,
            ObjectPtr: (uint)(objects.Count - 1)));

        var tocSpec = new PsgTocSpec { Entries = tocEntries };
        byte[] tocBytes = OtsTocPayloadBuilder.Build(tocEntries);
        objects.Add(new PsgObjectSpec(tocBytes, TypeTOC));

        // Subrefs: one record per TOC subref entry, in the same order the
        // ObjectPtr indices reference (triggers 0..N-1 then locators N..N+M-1).
        // After TOC append, LocationDescData sits at objects.Count - 2.
        int locDescDictIndex = objects.Count - 2;
        var subrefRecords = new List<PsgSubrefRecord>(triggers.Count + locators.Count);
        // Per-trigger subrefs: each tTriggerInstance lives inside the TriggerInstanceData
        // blob (dict idx 1) at header pad + i * InstanceStride.
        for (int i = 0; i < triggers.Count; i++)
        {
            subrefRecords.Add(new PsgSubrefRecord(
                ObjectDictIndex: 1u,
                OffsetInObject: (uint)(TriggerInstanceDataBuilder.InstancesOffset + i * TriggerInstanceDataBuilder.InstanceStride)));
        }
        // Per-locator subrefs: each tLocationDesc lives inside the LocationDescData
        // blob at FirstDescOffset + j * LocationDescSize.
        for (int j = 0; j < locators.Count; j++)
        {
            subrefRecords.Add(new PsgSubrefRecord(
                ObjectDictIndex: (uint)locDescDictIndex,
                OffsetInObject: (uint)(LocationDescDataBuilder.FirstDescOffset + j * LocationDescDataBuilder.LocationDescSize)));
        }

        var spec = new PsgArenaSpec
        {
            ArenaId = (uint)(Lookup8Hash.HashString(challengeKey) & 0xFFFFFFFFu),
            Objects = objects,
            TypeRegistry = TypeRegistry64,
            Toc = tocSpec,
            Subrefs = new PsgSubrefSpec(subrefRecords),
            UseFileSizeAt0x44 = true,
            DictRelocIsZero = true,
            HeaderTypeIdAt0x70 = 1u,   // Sim PSG
        };

        GenericArenaWriter.Write(spec, output);
    }
}

/// On-disk Tableofcontents payload bytes for an OTS PSG. Same shape as
/// LocatorPsgBuilder's TOC: header + per-entry rows + the standard
/// 25-entry pegasus type-map manifest every retail content PSG declares.
internal static class OtsTocPayloadBuilder
{
    public static byte[] Build(IReadOnlyList<PsgTocEntry> entries)
    {
        const int headerSize = 0x14;
        const int entrySize = 24;

        var typeMap = new (uint Type, uint Count)[]
        {
            (0x00EB0066u, (uint)entries.Count),  // RenderMaterialSubref
            (0x00EB0005u, (uint)entries.Count),  // RenderMaterialData
            (0x00EB0067u, (uint)entries.Count),  // CollisionMaterialSubref
            (0x00EB0006u, (uint)entries.Count),  // CollisionMaterialData
            (0x00EB0001u, (uint)entries.Count),  // RenderModelData
            (0x00EB000Au, (uint)entries.Count),  // CollisionModelData
            (0x00EB0065u, (uint)entries.Count),  // RollerDescSubref
            (0x00EB0007u, (uint)entries.Count),  // RollerDescData
            (0x00EB0069u, (uint)entries.Count),  // InstanceSubref
            (0x00EB000Du, (uint)entries.Count),  // InstanceData
            (0x00EB006Bu, (uint)entries.Count),  // TriggerInstanceSubref
            (0x00EB0019u, (uint)entries.Count),  // TriggerInstanceData
            (0x00EB0064u, (uint)entries.Count),  // SplineSubref
            (0x00EB0004u, (uint)entries.Count),  // SplineData
            (0x00EB0068u, (uint)entries.Count),  // LocationDescSubref
            (0x00EB0009u, (uint)entries.Count),  // LocationDescData
            (0x00EB0016u, (uint)entries.Count),  // MassiveData
            (0x00EB0013u, (uint)entries.Count),  // RainData
            (0x00EB0014u, (uint)entries.Count),  // AIPathData
            (0x00EB0018u, (uint)entries.Count),  // LionData
            (0x00EB0017u, (uint)entries.Count),  // DepthMapData
            (0x00EB0020u, (uint)entries.Count),  // SpatialMap
            (0x00EB0024u, (uint)entries.Count),  // IrradianceData
            (0x00EB0026u, (uint)entries.Count),  // BlobData
            (0x00EB0027u, (uint)entries.Count),  // NavpowerData
        };

        int entriesStart = headerSize;
        int entriesEnd = entriesStart + entries.Count * entrySize;
        int typeMapStart = entriesEnd;
        int total = typeMapStart + typeMap.Length * 8;

        var buf = new byte[total];
        Span<byte> span = buf;

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x00, 4), (uint)entries.Count);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x04, 4), (uint)entriesStart);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x08, 4), (uint)typeMapStart);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x0C, 4), (uint)typeMap.Length);
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0x10, 4), (uint)typeMapStart);

        for (int i = 0; i < entries.Count; i++)
        {
            int off = entriesStart + i * entrySize;
            var e = entries[i];
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x00, 4), 0u);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x04, 4), 0xFEFFFFFFu);
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(off + 0x08, 8), e.Guid);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x10, 4), e.TypeId);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x14, 4), e.ObjectPtr);
        }

        for (int i = 0; i < typeMap.Length; i++)
        {
            int off = typeMapStart + i * 8;
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x00, 4), typeMap[i].Type);
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(off + 0x04, 4), typeMap[i].Count);
        }

        return buf;
    }
}
