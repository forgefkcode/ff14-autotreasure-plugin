namespace AutoTreasure.Automation;

// Settings of the auto-treasure run, the same as the SND scripts' configs (descriptions are shown in the settings tab).
public sealed class AutoConfig
{
    public int MaxRounds { get; set; }
    public int MaxMaps { get; set; }
    public bool AutoBuy { get; set; } = true;
    public string RotationPlugin { get; set; } = "Auto";
    public string BossModPlugin { get; set; } = "Auto";
    public bool CrossWorld { get; set; } = true;
    public string DataCenter { get; set; } = "陸行鳥";
    public int MaxMapPrice { get; set; }
    public int GilFloor { get; set; }
    public int MarketTerritoryId { get; set; } = 129;
    public string MarketAetheryte { get; set; } = "";
    public float MarketX { get; set; } = -121;
    public float MarketY { get; set; } = 18;
    public float MarketZ { get; set; } = 12;
    public string FavoritesCallback { get; set; } = "12 6";
    public string MarketBoardName { get; set; } = "";
    public int FlagWait { get; set; } = 120;
    public bool EnterDungeon { get; set; } = true;
    public float WalkRadius { get; set; } = 15;
    public float DigRadius { get; set; } = 3;
    public float LeashRadius { get; set; } = 15;
    public int FieldLootDelay { get; set; } = 5;
    public int PortalWait { get; set; } = 10;
    public int EnemyWait { get; set; } = 20;
    public float EnemySearchRange { get; set; } = 100;
    public int MaxErrors { get; set; } = 3;
    public string DoorName { get; set; } = "寶物庫的門";
    public string DoorChoice { get; set; } = "Random";
    public int DoorHintWait { get; set; } = 5;
    public int DoorAnimWait { get; set; } = 10;
    public float PassageDistance { get; set; } = 10;
    public bool GamblePlay { get; set; } = true;
    public string GambleBigName { get; set; } = "大";
    public string GambleSmallName { get; set; } = "小";
    public string ChestName { get; set; } = "寶箱";
    public string DungeonChestName { get; set; } = "寶箱";
    public string PortalName { get; set; } = "傳送魔紋";
    public string ExitPortalName { get; set; } = "退出點";
    public string EchoLogs { get; set; } = "All";

    // empty names would never match anything (e.g. digging again and again next to the coffer): use the defaults
    public void FillEmptyNames()
    {
        var d = new AutoConfig();
        if (string.IsNullOrEmpty(DoorName)) DoorName = d.DoorName;
        if (string.IsNullOrEmpty(GambleBigName)) GambleBigName = d.GambleBigName;
        if (string.IsNullOrEmpty(GambleSmallName)) GambleSmallName = d.GambleSmallName;
        if (string.IsNullOrEmpty(ChestName)) ChestName = d.ChestName;
        if (string.IsNullOrEmpty(DungeonChestName)) DungeonChestName = ChestName;
        if (string.IsNullOrEmpty(PortalName)) PortalName = d.PortalName;
        if (string.IsNullOrEmpty(ExitPortalName)) ExitPortalName = d.ExitPortalName;
    }
}

// What differs between the maps. Only G17 runs in the plugin so far.
public sealed record MapProfile(string Grade, uint MapItemId, uint DecipheredItemId, ushort VaultTerritory, HashSet<uint> Zones, (uint Territory, Vector3 Pos)[] Spots)
{
    // G17 (Timeworn Br'aaxskin Map) and its vault Cenote Ja Ja Gural. The 40 treasure spots (5 Dawntrail zones x 8, no Living
    // Memory) with heights, from TreasureSpot (TreasureHuntRank 28) -> Level.
    public static readonly MapProfile G17 = new("G17", 43557, 2003563, 1209, [1187, 1188, 1189, 1190, 1191],
    [
        (1190, new(-163.11f, 16.42f, -19.52f)), (1190, new(-444.13f, 28.52f, -352.61f)), (1190, new(-88.17f, -17.88f, 652.72f)),
        (1190, new(523.50f, 16.09f, 572.31f)), (1190, new(393.24f, -15.02f, -652.16f)), (1190, new(-145.82f, 15.42f, -490.89f)),
        (1190, new(704.46f, -14.48f, -224.88f)), (1190, new(208.80f, 4.96f, -85.16f)),
        (1191, new(239.06f, 115.76f, 467.92f)), (1191, new(-231.01f, -5.06f, 717.21f)), (1191, new(-211.30f, 28.82f, 38.01f)),
        (1191, new(-241.63f, 21.28f, -419.33f)), (1191, new(719.28f, 147.96f, 205.58f)), (1191, new(-623.45f, 20.95f, -11.40f)),
        (1191, new(34.50f, 92.38f, -116.99f)), (1191, new(484.90f, 131.38f, -653.29f)),
        (1187, new(639.24f, 123.36f, 379.99f)), (1187, new(-391.72f, -45.41f, -365.01f)), (1187, new(6.18f, 10.24f, 119.35f)),
        (1187, new(14.66f, 23.86f, 228.04f)), (1187, new(-286.58f, 69.40f, 457.84f)), (1187, new(-228.89f, 62.63f, 96.72f)),
        (1187, new(373.81f, 63.80f, 130.12f)), (1187, new(638.49f, 109.35f, 560.16f)),
        (1188, new(771.13f, 41.22f, -759.98f)), (1188, new(497.28f, 10.70f, -326.05f)), (1188, new(-75.05f, 20.30f, -724.78f)),
        (1188, new(-727.42f, 61.38f, -103.17f)), (1188, new(-621.86f, 129.51f, 620.41f)), (1188, new(-129.01f, 118.70f, 51.61f)),
        (1188, new(823.96f, 23.36f, 11.69f)), (1188, new(540.50f, 122.53f, 732.10f)),
        (1189, new(64.04f, -164.63f, 717.38f)), (1189, new(-301.69f, -169.47f, 268.90f)), (1189, new(-790.39f, 31.85f, -92.20f)),
        (1189, new(760.93f, 35.58f, -448.47f)), (1189, new(485.30f, 11.24f, 22.83f)), (1189, new(-511.32f, -153.80f, 588.87f)),
        (1189, new(163.28f, -159.93f, -24.64f)), (1189, new(56.46f, 8.21f, -781.69f)),
    ]);
}
