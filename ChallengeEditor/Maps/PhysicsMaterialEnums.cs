using System.Text;

namespace ChallengeEditor;

// Numeric values match BlenRose's enum IDs exactly so future emit code (sidecar
// JSON or direct ArenaBuilder hand-off) can cast straight to int.
//
// Source tables: PsgTools\CursorWorkSpace\BlenRose\BlenRose.py
//   PHYSICS_SURFACE_ITEMS (line 251), AUDIO_SURFACE_ITEMS (line 154),
//   SURFACE_PATTERN_ITEMS (line 267), MATERIAL_CLASS_ITEMS (line 289).

public enum PhysicsSurface
{
    Undefined = 0,
    Smooth = 1,
    Rough = 2,
    Slow = 3,
    Slippery = 4,
    VerySlow = 5,
    Unrideable = 6,
    DoNotAlign = 7,
    Stair = 8,
    InstantBail = 9,
    SlipperyRagdoll = 10,
    BouncyRagdoll = 11,
    Water = 12,
}

public enum AudioSurface
{
    Undefined = 0,
    Asphalt_Smooth = 1,
    Asphalt_Rough = 2,
    Concrete_Polished = 3,
    Concrete_Rough = 4,
    Concrete_Aggregate = 5,
    Wood_Ramp = 6,
    Plywood = 7,
    Dirt = 8,
    Metal = 9,
    Grass = 10,
    Metal_Solid_Round_1 = 11,
    Metal_Solid_Round_1_Up = 12,
    Metal_Solid_Round_2 = 13,
    Metal_Solid_Square_1 = 14,
    Metal_Solid_Square_2 = 15,
    Metal_Hollow_Round_1 = 16,
    Metal_Hollow_Round_1_Dead = 17,
    Metal_Hollow_Round_1_Dn = 18,
    Metal_Hollow_Round_2 = 19,
    Metal_Hollow_Round_2_Dead = 20,
    Metal_Hollow_Round_2_Dn = 21,
    Metal_Hollow_Round_3 = 22,
    Metal_Hollow_Round_4 = 23,
    Metal_Hollow_Square_1 = 24,
    Metal_Hollow_Square_2 = 25,
    Metal_Hollow_Square_3 = 26,
    Metal_Hollow_Square_3_Dead = 27,
    Metal_Hollow_Square_4 = 28,
    Metal_Hollow_1 = 29,
    Metal_Hollow_2 = 30,
    Metal_Sheet = 31,
    Metal_Complex_1 = 32,
    Metal_Complex_2 = 33,
    Metal_Complex_3 = 34,
    Metal_Complex_4 = 35,
    Metal_Complex_5 = 36,
    Metal_Complex_6 = 37,
    Metal_Complex_7 = 38,
    Metal_Complex_8 = 39,
    Metal_Complex_Debris = 40,
    Wood_1 = 41,
    Wood_1_Up = 42,
    Wood_2 = 43,
    Wood_3 = 44,
    Wood_3_Up = 45,
    Wood_4 = 46,
    Plastic_1 = 47,
    Plastic_2 = 48,
    Plastic_3 = 49,
    Plastic_4 = 50,
    Glass_Thick_Large = 51,
    Glass_Thin_Small = 52,
    Concrete_Curb = 53,
    Concrete_Bench = 54,
    Leaves = 55,
    Bush = 56,
    Pottery = 57,
    Paper = 58,
    Cardboard = 59,
    Garbage_Bag = 60,
    Garbage_Spill = 61,
    Bottle = 62,
    Tile_Ceramic = 63,
    Marble_or_Slate = 64,
    Brick_Smooth = 65,
    Brick_Coarse = 66,
    Manhole_Metal = 67,
    Metal_Grate_Sewer = 68,
    Metal_Grate_Planter = 69,
    DeepSnow = 70,
    PackedSnow = 71,
    Ice = 72,
    Antennas = 73,
    Chandelier = 74,
    Plexiglass_Small = 75,
    Plexiglass_Large = 76,
    Potted_Plant = 77,
    Crumpled_Paper = 78,
    Cloth = 79,
    Pop_Can = 80,
    Paper_Cup = 81,
    Wire_Cable = 82,
    VolleyBall = 83,
    OilDrum = 84,
    DMORail = 85,
    Fruit = 86,
    Plastic_Bottle = 87,
    Drum_Pylon = 88,
    Metal_Rail_4 = 89,
    Wood_5 = 90,
    Metal_Ramp = 91,
    Complex_Plastic_1 = 92,
    Max_Mappable_Surface = 93,
}

public enum SurfacePattern
{
    None = 0,
    SpiderCrack = 1,
    Square2x2 = 2,
    Square4x4 = 3,
    Square8x8 = 4,
    Square12x12 = 5,
    Square24x24 = 6,
    IrregularSmall = 7,
    IrregularMedium = 8,
    IrregularLarge = 9,
    Slats = 10,
    Sidewalk = 11,
    BrickTileRandomSize = 12,
    MiniTile = 13,
    Special1 = 14,
    Special2 = 15,
}

/// <summary>
/// BlenRose's "AttributorMaterialName" class side — the first half of a
/// <c>class.subclass</c> material identifier (e.g. <c>environmentsimple</c> of
/// <c>environmentsimple.default</c>). Sourced from
/// <c>MATERIAL_CLASS_ITEMS</c> in BlenRose.py:289. The numeric IDs here are
/// just our serialization ordinals — BlenRose itself keys by the string name,
/// so a future emit step would call <c>ToString().ToLower()</c>.
/// </summary>
public enum MaterialClass
{
    Advertisement = 0,
    Animated = 1,
    Basic = 2,
    Building = 3,
    Character = 4,
    CharAttributes = 5,
    DefaultEnvironment = 6,
    DmoAttributes = 7,
    DynamicObject = 8,
    EnvAttributes = 9,
    Environment = 10,
    EnvironmentPark = 11,
    EnvironmentSimple = 12,
    Fog = 13,
    Glare = 14,
    GodRay = 15,
    Incandescent = 16,
    NameMap = 17,
    Ocean = 18,
    ProxyWorld = 19,
    Sky = 20,
    Terrain = 21,
    TrafficLight = 22,
    Tree = 23,
    Vehicle = 24,
    VisualIndicator = 25,
    Water = 26,
}

/// <summary>
/// Sorted (alphabetical) ImGui combo labels for each BlenRose enum, plus
/// value↔combo-index mapping. ImGui.Combo wants a null-separated string and a
/// flat int index, but our enum values aren't in alphabetical order — so we
/// sort once at startup and translate at edit time.
/// </summary>
public static class PhysicsMaterialLabels
{
    public static readonly EnumLabels<PhysicsSurface> Physics = EnumLabels<PhysicsSurface>.Build();
    public static readonly EnumLabels<AudioSurface>   Audio   = EnumLabels<AudioSurface>.Build();
    public static readonly EnumLabels<SurfacePattern> Pattern = EnumLabels<SurfacePattern>.Build();
    public static readonly EnumLabels<MaterialClass>  Class   = EnumLabels<MaterialClass>.Build();
}

/// <summary>
/// Alphabetically-sorted label bundle for one enum. Built once via
/// <see cref="Build"/>; exposes the null-separated string ImGui consumes plus
/// forward / reverse mappings so the inspector can round-trip the user's
/// selection back to the enum value.
/// </summary>
public sealed class EnumLabels<TEnum> where TEnum : struct, Enum
{
    /// <summary>Null-separated, double-null-terminated label string for <c>ImGui.Combo</c>.</summary>
    public string Labels { get; }
    /// <summary>Number of entries (use as the <c>items_count</c> param to ImGui.Combo).</summary>
    public int Count { get; }
    /// <summary>Combo-index → enum value.</summary>
    public TEnum[] Values { get; }
    private readonly Dictionary<TEnum, int> _valueToIndex;

    private EnumLabels(string labels, TEnum[] values, Dictionary<TEnum, int> valueToIndex)
    {
        Labels = labels;
        Count = values.Length;
        Values = values;
        _valueToIndex = valueToIndex;
    }

    /// <summary>Find the combo index for a stored enum value (0 fallback for unknown).</summary>
    public int IndexOf(TEnum v) => _valueToIndex.TryGetValue(v, out int i) ? i : 0;

    /// <summary>Find the enum value the user just picked (clamped to valid range).</summary>
    public TEnum ValueAt(int i) => Values[Math.Clamp(i, 0, Values.Length - 1)];

    public static EnumLabels<TEnum> Build()
    {
        // Sort by stringified enum name so Combo entries land alphabetically.
        // Comparison is ordinal to match the runtime cost of the lookup — case
        // is consistent per-enum (we never mix Mixed and lower).
        var sorted = Enum.GetValues<TEnum>()
            .OrderBy(v => v.ToString(), StringComparer.Ordinal)
            .ToArray();
        var sb = new StringBuilder();
        var map = new Dictionary<TEnum, int>(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            sb.Append(sorted[i].ToString());
            sb.Append('\0');
            map[sorted[i]] = i;
        }
        sb.Append('\0');
        return new EnumLabels<TEnum>(sb.ToString(), sorted, map);
    }
}
