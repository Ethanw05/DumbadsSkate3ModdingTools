namespace ArenaBuilder.Core.Platforms.Common;

/// <summary>
/// PS3 Pegasus / Skate object type IDs.
/// Values from Skate RW Object Type List (Skate-File-Format-Documentation).
/// </summary>
public static class RwTypeIds
{
    // ─── Arena / Core (0x0001xxxx) ────────────────────────────────────────────
    public const uint Null = 0x00000000;
    public const uint Na = 0x00010000;
    public const uint Arena = 0x00010001;
    public const uint Raw = 0x00010002;
    public const uint Subreference = 0x00010003;
    public const uint SectionManifest = 0x00010004;
    public const uint SectionTypes = 0x00010005;
    public const uint SectionExternalArenas = 0x00010006;
    public const uint SectionSubreferences = 0x00010007;
    public const uint SectionAtoms = 0x00010008;
    public const uint DefArenaImports = 0x00010009;
    public const uint DefArenaExports = 0x0001000A;
    public const uint DefArenaTypes = 0x0001000B;
    public const uint DefArenaDefinedArenaId = 0x0001000C;
    public const uint AttributePacket = 0x0001000D;
    public const uint AttributePacketDelegate = 0x0001000E;
    public const uint BitTable = 0x0001000F;
    public const uint ArenaLocalAtomTable = 0x00010010;
    public const uint BaseResourceStart = 0x00010030;
    public const uint BaseResource = 0x00010034; // PS3 default; X360 uses 0x00010031

    // ─── Graphics (0x0002xxxx) ────────────────────────────────────────────────
    public const uint MeshHelper = 0x00020081;
    public const uint Texture = 0x000200E8;
    public const uint VertexDescriptor = 0x000200E9;
    public const uint VertexBuffer = 0x000200EA;
    public const uint IndexBuffer = 0x000200EB;

    // ─── Collision (0x0008xxxx) ────────────────────────────────────────────────
    public const uint Volume = 0x00080001;
    public const uint SimpleMappedArray = 0x00080002;
    public const uint TriangleKdTreeProcedural = 0x00080003;
    public const uint KdTreeMappedArray = 0x00080004;
    public const uint BBox = 0x00080005;
    public const uint ClusteredMesh = 0x00080006;
    public const uint Octree = 0x00080008;

    // ─── Pegasus data (0x00EB00xx) ─────────────────────────────────────────────
    public const uint RenderMeshData = 0x00EB0000;
    public const uint RenderModelData = 0x00EB0001;
    public const uint SimpleTriMeshData = 0x00EB0003;
    public const uint SplineData = 0x00EB0004;
    public const uint RenderMaterialData = 0x00EB0005;
    public const uint CollisionMaterialData = 0x00EB0006;
    public const uint RollerDescData = 0x00EB0007;
    public const uint VersionData = 0x00EB0008;
    public const uint LocationDescData = 0x00EB0009;
    public const uint CollisionModelData = 0x00EB000A;
    public const uint TableOfContents = 0x00EB000B;
    public const uint CollisionBezierData = 0x00EB000C;
    public const uint InstanceData = 0x00EB000D;
    public const uint RenderBlendShapeData = 0x00EB000E;
    public const uint WorldPainterLayerData = 0x00EB000F;
    public const uint WorldPainterQuadTreeData = 0x00EB0010;
    public const uint WorldPainterDictionaryData = 0x00EB0011;
    public const uint NavMeshData = 0x00EB0012;
    public const uint RainData = 0x00EB0013;
    public const uint AiPathData = 0x00EB0014;
    public const uint StatsData = 0x00EB0015;
    public const uint MassiveData = 0x00EB0016;
    public const uint DepthMapData = 0x00EB0017;
    public const uint LionData = 0x00EB0018;
    public const uint TriggerInstanceData = 0x00EB0019;
    public const uint WaypointData = 0x00EB001A;
    public const uint EmbeddedData = 0x00EB001B;
    public const uint EmitterWaypointData = 0x00EB001C;
    public const uint DmoData = 0x00EB001D;
    public const uint HotPointData = 0x00EB001E;
    public const uint GrabData = 0x00EB001F;
    public const uint SpatialMap = 0x00EB0020;
    public const uint VisualIndicatorData = 0x00EB0021;
    public const uint NavMesh2Data = 0x00EB0022;
    public const uint RenderOptiMeshData = 0x00EB0023;
    public const uint IrradianceData = 0x00EB0024;
    public const uint AntifrustumData = 0x00EB0025;
    public const uint BlobData = 0x00EB0026;
    public const uint NavPowerData = 0x00EB0027;
    public const uint TeamBrandData = 0x00EB0028;
    public const uint WorldScriptInstanceData = 0x00EB0029;

    // ─── Pegasus subrefs (0x00EB006x) ──────────────────────────────────────────
    public const uint SplineSubRef = 0x00EB0064;
    public const uint RollerDescSubRef = 0x00EB0065;
    public const uint RenderMaterialSubRef = 0x00EB0066;
    public const uint CollisionMaterialSubRef = 0x00EB0067;
    public const uint LocationDescSubRef = 0x00EB0068;
    public const uint InstanceSubRef = 0x00EB0069;
    public const uint WaypointSubRef = 0x00EB006A;
    public const uint TriggerInstanceSubRef = 0x00EB006B;
    public const uint EmitterWaypointSubRef = 0x00EB006C;
    public const uint DmoSubRef = 0x00EB006D;
    public const uint HotPointSubRef = 0x00EB006E;
    public const uint GrabSubRef = 0x00EB006F;
    public const uint VisualIndicatorSubRef = 0x00EB0070;

    // ─── Arena dictionary ─────────────────────────────────────────────────────
    public const uint ArenaDictionary = 0x00EC0010;
}

