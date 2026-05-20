namespace ArenaBuilder.WinForms;

/// <summary>
/// Layer GUIDs and WPDICT Lookup8 pairs for the WorldPainter UI.
/// Sourced from <c>documentation/WPDICT_WPLAYER_University_BlackBox_Lookup8.txt</c> (u64 → lo = low 32 bits, hi = high 32 bits).
/// </summary>
internal static class WorldPainterCatalog
{
    internal sealed class LayerEntry(string displayName, ulong guid)
    {
        public string DisplayName { get; } = displayName;
        public ulong Guid { get; } = guid;
        public override string ToString() => DisplayName;
    }

    internal sealed class KeyEntry(string displayName, uint lo, uint hi)
    {
        public string DisplayName { get; } = displayName;
        public uint Lo { get; } = lo;
        public uint Hi { get; } = hi;
        public override string ToString() => DisplayName;
    }

    /// <summary>64-bit Lookup8 id as stored in docs → WPDICT slot (lo, hi).</summary>
    private static KeyEntry H(string displayName, ulong lookup8) =>
        new(displayName, (uint)(lookup8 & 0xFFFFFFFFu), (uint)(lookup8 >> 32));

    public static readonly LayerEntry[] Layers =
    [
        new("Audio ambience", 0xEA754449D4731193UL),
        new("Audio emitters", 0xD2DB3C8744839633UL),
        new("Audio reverb", 0x6879AFCFA737F03AUL),
        new("District locations", 0xD5B9AC56592787A1UL),
        new("LivingWorld NPC census", 0x4CAF11B5298E3919UL),
        new("LivingWorld vehicle census", 0x7FC53B5B51129A55UL),
        new("Rendering colorcube", 0xB0FD5C7475333234UL),
        new("Rendering fog", 0xBB873BC53365F8A4UL),
        new("Rendering sky", 0x2620210EA80B33A3UL),
    ];

    private static readonly Dictionary<ulong, KeyEntry[]> KeysByGuid = new()
    {
        [0xEA754449D4731193UL] =
        [
            H("int_arena (DIST_BlackBoxPark)", 0x8BA0DBABF7A96E86UL),
            H("dt_apt (DIST_DownTown)", 0x1F741D87EB58E84FUL),
            H("dt_less_busy (DIST_DownTown)", 0xB6CE4BDEB63B6639UL),
            H("dt_main (DIST_DownTown)", 0xCEED3BB181EB9F10UL),
            H("dt_open (DIST_DownTown)", 0x34BEDA0D714EA7BCUL),
            H("dt_parks (DIST_DownTown)", 0x8169DB21903652B8UL),
            H("dt_rez (DIST_DownTown)", 0x18DE97EF090916C7UL),
            H("univ_campus (DIST_University)", 0xF50E796AC691F118UL),
            H("univ_housing (DIST_University)", 0xC9C5888A1BC5E219UL),
            H("univ_mt_high (DIST_University)", 0x503E77E54911C800UL),
            H("univ_mt_low (DIST_University)", 0x2DA17A16FE3ABA65UL),
        ],
        [0xD2DB3C8744839633UL] =
        [
            H("e_dwtn_canal_plaza (DIST_DownTown)", 0xA7B88391C588B507UL),
            H("e_dwtn_city_center (DIST_DownTown)", 0xF76635058F517312UL),
            H("e_dwtn_core_plaza (DIST_DownTown)", 0x38E0ADB5AA77E3D0UL),
            H("e_dwtn_lot_5 (DIST_DownTown)", 0x9C7A8FCBD091EDD6UL),
            H("e_dwtn_mall (DIST_DownTown)", 0xA73046282FCFAFCBUL),
            H("e_dwtn_mem_plaza (DIST_DownTown)", 0xC90126257C54D14FUL),
            H("e_dwtn_office_buildings (DIST_DownTown)", 0x87B9E6208CB64587UL),
            H("e_dwtn_parkade (DIST_DownTown)", 0xBDD1AB82082B9917UL),
            H("e_dwtn_rez_plaza (DIST_DownTown)", 0xA2E1D115B6A078D7UL),
            H("e_dwtn_skatepark (DIST_DownTown)", 0x21E0CE4F4588D1A4UL),
            H("e_dwtn_spillway_brewery (DIST_DownTown)", 0x7EE1991E4909C50EUL),
            H("e_dwtn_spillway_core_plaza (DIST_DownTown)", 0x3057EBA1A5A73ABEUL),
            H("e_dwtn_spillway_start (DIST_DownTown)", 0x1D04442CB66AF22AUL),
            H("e_dwtn_wall (DIST_DownTown)", 0x6E05D9DC366A445CUL),
            H("e_univ_chan_center (DIST_University)", 0xCD348998218A2CA4UL),
            H("e_univ_clocktower_sq (DIST_University)", 0x2A0956BA8DDFD11EUL),
            H("e_univ_comm_sk8park (DIST_University)", 0x239AC66EFCBE9EA1UL),
            H("e_univ_low_plaza (DIST_University)", 0x91D1E5A68F504927UL),
            H("e_univ_megapark (DIST_University)", 0x150713BC3C9701F4UL),
            H("e_univ_new_campus (DIST_University)", 0xF7800FF9B8813F84UL),
            H("e_univ_observatory (DIST_University)", 0xFEE7620F91B97732UL),
            H("e_univ_spillway (DIST_University)", 0x4A953BE98BB6EE94UL),
            H("e_univ_stadium (DIST_University)", 0xF3C30A627D87BEEEUL),
            H("e_univ_suburbs (DIST_University)", 0x7CE98AC50D3CB5EFUL),
            H("e_univ_training_facility (DIST_University)", 0xDA67734C137BB1B1UL),
        ],
        [0x6879AFCFA737F03AUL] =
        [
            H("reverb17 (DIST_BlackBoxPark)", 0x9339F7B7E0FE2167UL),
            H("reverb01 (DIST_DownTown)", 0xA2782D75A971CC8CUL),
            H("reverb03 (DIST_DownTown)", 0x676210D762D14388UL),
            H("reverb04 (DIST_DownTown)", 0x9572128EFDA5C188UL),
            H("reverb05 (DIST_DownTown)", 0x68A9E6020DF2076DUL),
            H("reverb10 (DIST_DownTown)", 0x51DECB0978657BD2UL),
            H("reverb16 (DIST_DownTown)", 0xC1B2248F0522A937UL),
            H("reverb18 (DIST_DownTown)", 0x0EEFF7316BF288E8UL),
            H("reverb21 (DIST_DownTown)", 0xDD0B4DF2D9681029UL),
            H("reverb22 (DIST_DownTown)", 0x8E7B8D05E04C7BDFUL),
            H("reverb01 (DIST_University)", 0xA2782D75A971CC8CUL),
            H("reverb02 (DIST_University)", 0x407AFA1D6C7CEAD8UL),
            H("reverb03 (DIST_University)", 0x676210D762D14388UL),
            H("reverb05 (DIST_University)", 0x68A9E6020DF2076DUL),
            H("reverb15 (DIST_University)", 0xD91803A45ED11E5EUL),
            H("reverb16 (DIST_University)", 0xC1B2248F0522A937UL),
            H("reverb21 (DIST_University)", 0xDD0B4DF2D9681029UL),
            H("reverb22 (DIST_University)", 0x8E7B8D05E04C7BDFUL),
        ],
        [0xD5B9AC56592787A1UL] =
        [
            H("blackbox_skatepark (DIST_BlackBoxPark)", 0x2CC4C4543D423EA0UL),
            H("dt_lot_01a (DIST_DownTown)", 0x9E4C129E2F4060E2UL),
            H("dt_lot_01b (DIST_DownTown)", 0x243DACEAF5382252UL),
            H("dt_lot_01c (DIST_DownTown)", 0xB469111E2AD19F37UL),
            H("dt_lot_02 (DIST_DownTown)", 0xF420886CDF1D7154UL),
            H("dt_lot_03 (DIST_DownTown)", 0x1C1F9A4EAF746763UL),
            H("dt_lot_04 (DIST_DownTown)", 0xA2AC3376F57D3B84UL),
            H("dt_lot_05 (DIST_DownTown)", 0x6E180FDB3506299AUL),
            H("dt_lot_06 (DIST_DownTown)", 0xD77F846FD4886835UL),
            H("dt_lot_07 (DIST_DownTown)", 0x143751D19C0BDD2CUL),
            H("dt_lot_08 (DIST_DownTown)", 0xBFAC7437742A5A3CUL),
            H("dt_lot_09 (DIST_DownTown)", 0x587F4390855BD273UL),
            H("dt_lot_10 (DIST_DownTown)", 0x560C20E52B7D92AFUL),
            H("dt_lot_11 (DIST_DownTown)", 0xCDB1FA3ABF920D62UL),
            H("dt_lot_12 (DIST_DownTown)", 0xE3559CC4BD48DDAAUL),
            H("dt_lot_13 (DIST_DownTown)", 0x5378870D82762C16UL),
            H("univ_dist_res_roads (DIST_University)", 0xAFCF9CEC067ABE0AUL),
            H("univ_lot_lisa_lane (DIST_University)", 0x34B39C8F98CD53DEUL),
            H("univ_lot_obs_roads (DIST_University)", 0xC40F94154D6BC8F9UL),
            H("univ_lot_overpass (DIST_University)", 0xFC55F7BA979ECFD8UL),
            H("univ_lot_un_01 (DIST_University)", 0x90F542284B22A7E9UL),
            H("univ_lot_un_02 (DIST_University)", 0xB6ABEA946BDC50A1UL),
            H("univ_lot_un_03 (DIST_University)", 0xADE3E1540DA0D9F1UL),
            H("univ_lot_un_04 (DIST_University)", 0x39722D587BF73754UL),
            H("univ_lot_un_05 (DIST_University)", 0x87172ED9D327EF50UL),
            H("univ_lot_un_06 (DIST_University)", 0x87F5CAB8111EEC11UL),
            H("univ_lot_un_07 (DIST_University)", 0xF39C31052D87E615UL),
            H("univ_lot_un_08 (DIST_University)", 0x4C2EE22EB576562AUL),
            H("univ_lot_un_09 (DIST_University)", 0x067F343A31E463BFUL),
            H("univ_lot_un_10 (DIST_University)", 0x4919424C436B0283UL),
            H("univ_lot_un_11 (DIST_University)", 0x712E38F7C46E0215UL),
        ],
        [0x4CAF11B5298E3919UL] =
        [
            H("aletown (DIST_DownTown)", 0x48700C9F676B9006UL),
            H("business_center (DIST_DownTown)", 0x2B9DF8BBA9E6EAD1UL),
            H("mall (DIST_DownTown)", 0xF07958B1A257DA50UL),
            H("memorial (DIST_DownTown)", 0x89161112121BE2AAUL),
            H("pedestrians (DIST_DownTown)", 0x5B6A0051DD6A295CUL),
            H("residential (DIST_DownTown)", 0xB086095DC91790A4UL),
            H("campus (DIST_University)", 0xBDBD9B6F39FCBA81UL),
            H("observatory (DIST_University)", 0x445CE02DD9797B7BUL),
        ],
        [0x7FC53B5B51129A55UL] =
        [
            H("dwntwn (DIST_DownTown)", 0x005BD18BED4C9BA7UL),
            H("univ (DIST_University)", 0xCF4DF7676CDC60DEUL),
        ],
        [0xB0FD5C7475333234UL] =
        [
            H("default (DIST_DownTown)", 0xD7EDBD362D7D2152UL),
        ],
        [0xBB873BC53365F8A4UL] =
        [
            H("fog_backup (DIST_DownTown)", 0x591A95E4AF90E2A0UL),
            H("fog_bigevent (DIST_DownTown)", 0x8965DB3C8C89C736UL),
            H("fog_cool (DIST_DownTown)", 0xEE4A0079EE02E665UL),
            H("fog_dark (DIST_DownTown)", 0xFDDDD8AA432784A8UL),
            H("fog_femain (DIST_DownTown)", 0x454D3492039D8E83UL),
            new("? unpaired u32 0x3785AD31 (DIST_DownTown)", 0x3785AD31u, 0u),
            new("? unpaired u32 0x5A4CA16B (DIST_DownTown)", 0x5A4CA16Bu, 0u),
            new("? unpaired u32 0x8808F2C8 (DIST_DownTown)", 0x8808F2C8u, 0u),
            new("? unpaired u32 0x89AAE0E9 (DIST_DownTown)", 0x89AAE0E9u, 0u),
            new("? unpaired u32 0xBE871DA5 (DIST_DownTown)", 0xBE871DA5u, 0u),
            new("? unpaired u32 0xFC1ABCEE (DIST_DownTown)", 0xFC1ABCEEu, 0u),
        ],
        [0x2620210EA80B33A3UL] =
        [
            H("sky_70s (DIST_DownTown)", 0x7C0B080CC88197B2UL),
            H("sky_bkup3 (DIST_DownTown)", 0x0B10071C213288DAUL),
            H("sky_creature (DIST_DownTown)", 0xF50397E7A8B5F138UL),
        ],
    };

    public static IReadOnlyList<KeyEntry> KeysFor(ulong layerGuid) =>
        KeysByGuid.TryGetValue(layerGuid, out var k) ? k : Array.Empty<KeyEntry>();
}
