namespace AutoTreasure.Automation;

// Settings of the auto-treasure run, the same as the SND scripts' configs (descriptions are shown in the settings tab).
public sealed class AutoConfig
{
    public string Grade { get; set; } = "G17"; // which map to run
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
    public bool FieldDodge { get; set; } = true;
    public float FieldDodgeRadius { get; set; } = 8;
    public int FieldLootDelay { get; set; } = 5;
    public int PortalWait { get; set; } = 10;
    public int EnemyWait { get; set; } = 20;
    public int RouletteWait { get; set; } = 45;
    public string SphereName { get; set; } = "召喚魔法陣";
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
        if (string.IsNullOrEmpty(SphereName)) SphereName = d.SphereName;
    }
}

// What differs between the maps; spots are the real positions (with height) from TreasureSpot -> Level, as in the SND scripts.
public enum VaultKind { Door, Roulette }

public sealed record MapProfile(string Grade, uint MapItemId, uint DecipheredItemId, (ushort Territory, VaultKind Kind)[] Vaults,
    HashSet<uint> Zones, bool EntryCircle, (uint Territory, Vector3 Pos)[] Spots)
{
    public VaultKind? VaultAt(ushort territory)
    {
        foreach (var (t, kind) in Vaults)
            if (t == territory)
                return kind;
        return null;
    }

    public static MapProfile ByGrade(string grade) => All.FirstOrDefault(m => m.Grade == grade) ?? G17;

    // G12 (Timeworn Zonureskin Map): the portal leads to The Dungeons of Lyhe Ghiah (door type) or The Shifting Oubliettes of
    // Lyhe Ghiah (roulette type), told apart by the territory. 48 spots in the 6 Shadowbringers zones (TreasureHuntRank 18)
    public static readonly MapProfile G12 = new("G12", 26745, 2002664, [(879, VaultKind.Door), (924, VaultKind.Roulette)], [813, 814, 815, 816, 817, 818], false,
    [
        (813, new(-550.00f, -1.60f, 200.00f)), (813, new(-660.00f, 68.03f, -20.00f)), (813, new(665.21f, 14.77f, 217.69f)),
        (813, new(-180.00f, 14.31f, -220.00f)), (813, new(-385.54f, 93.76f, -433.06f)), (813, new(-537.31f, 75.56f, -490.95f)),
        (813, new(837.93f, 79.81f, -381.92f)), (813, new(-160.00f, 100.43f, -690.00f)), (814, new(469.11f, 306.32f, -210.00f)),
        (814, new(588.12f, 2.24f, 513.63f)), (814, new(-680.00f, 358.62f, -169.55f)), (814, new(-400.00f, 357.91f, -238.76f)),
        (814, new(-69.74f, 347.56f, -220.54f)), (814, new(-46.44f, 420.47f, -612.00f)), (814, new(-480.00f, 372.17f, -374.80f)),
        (814, new(668.38f, 273.30f, -537.84f)), (815, new(549.95f, -54.90f, -642.29f)), (815, new(734.99f, -74.56f, -498.22f)),
        (815, new(310.91f, -22.43f, -426.42f)), (815, new(630.00f, -32.57f, -200.00f)), (815, new(-459.11f, -0.26f, -357.00f)),
        (815, new(-413.70f, -62.98f, 427.54f)), (815, new(270.00f, -38.95f, 120.00f)), (815, new(440.94f, -61.55f, 462.01f)),
        (816, new(-706.08f, 36.92f, -200.00f)), (816, new(-550.00f, 73.90f, -420.00f)), (816, new(-430.00f, 0.89f, -45.00f)),
        (816, new(10.00f, 59.76f, -689.07f)), (816, new(-362.56f, 24.90f, 302.29f)), (816, new(480.00f, 100.39f, -870.00f)),
        (816, new(590.00f, 98.92f, -550.00f)), (816, new(183.94f, 9.94f, -451.24f)), (817, new(235.45f, -22.28f, 665.67f)),
        (817, new(60.00f, -21.13f, 550.00f)), (817, new(-492.56f, 14.18f, -94.45f)), (817, new(170.00f, -5.12f, 310.00f)),
        (817, new(-399.00f, 7.32f, 128.75f)), (817, new(690.00f, 19.82f, 50.00f)), (817, new(647.47f, -40.67f, -219.87f)),
        (817, new(150.00f, -1.12f, -300.00f)), (818, new(-380.00f, 122.37f, -324.65f)), (818, new(-263.95f, 125.50f, -146.38f)),
        (818, new(-441.83f, 167.70f, -491.36f)), (818, new(-120.00f, 413.14f, -660.00f)), (818, new(200.00f, 363.34f, -500.00f)),
        (818, new(793.02f, 414.03f, -184.75f)), (818, new(556.64f, 446.00f, -814.63f)), (818, new(433.93f, 357.80f, -25.56f)),
    ]);

    // G14 (Timeworn Kumbhiraskin Map): The Excitatron 6000 (door type, floor 1 starts with DUNGEON_ENTRY). 40 spots in the 5
    // Endwalker zones without Elpis (TreasureHuntRank 22)
    public static readonly MapProfile G14 = new("G14", 36612, 2003246, [(1000, VaultKind.Door)], [956, 957, 958, 959, 960], true,
    [
        (956, new(-143.93f, -23.90f, -167.07f)), (956, new(162.41f, -24.70f, 410.09f)), (956, new(-733.87f, -1.95f, -26.82f)),
        (956, new(20.55f, -6.45f, 805.51f)), (956, new(138.44f, 80.74f, -528.54f)), (956, new(-651.64f, -25.43f, 449.09f)),
        (956, new(518.35f, 164.38f, -350.44f)), (956, new(174.52f, -19.52f, 16.72f)), (957, new(268.29f, 6.59f, 373.21f)),
        (957, new(24.47f, 25.13f, -21.51f)), (957, new(-20.01f, 49.32f, 290.72f)), (957, new(540.59f, 10.96f, -352.60f)),
        (957, new(-96.87f, 37.67f, -362.70f)), (957, new(-332.71f, 8.91f, 259.61f)), (957, new(-337.21f, 57.92f, -677.81f)),
        (957, new(283.27f, 8.42f, -626.73f)), (958, new(195.67f, 23.62f, -451.23f)), (958, new(639.38f, 15.84f, -574.87f)),
        (958, new(-253.41f, 10.80f, -464.50f)), (958, new(649.25f, 13.33f, -207.91f)), (958, new(-265.37f, 12.89f, -180.42f)),
        (958, new(-482.00f, 45.74f, 212.18f)), (958, new(335.67f, -8.85f, 250.52f)), (958, new(393.70f, -18.82f, 697.73f)),
        (959, new(668.35f, 146.05f, 423.07f)), (959, new(210.73f, 83.65f, 592.51f)), (959, new(-217.06f, 122.67f, -103.73f)),
        (959, new(-134.07f, 62.29f, 213.60f)), (959, new(-463.59f, 129.33f, 258.76f)), (959, new(-278.78f, 116.95f, 648.76f)),
        (959, new(451.88f, 103.64f, 174.77f)), (959, new(66.26f, 111.32f, 741.28f)), (960, new(264.07f, 391.64f, 701.90f)),
        (960, new(-0.81f, 61.67f, 744.21f)), (960, new(-765.30f, 68.75f, -119.85f)), (960, new(408.63f, 286.25f, -603.24f)),
        (960, new(214.70f, 234.77f, -211.25f)), (960, new(-665.94f, 81.02f, 462.78f)), (960, new(-178.06f, 73.38f, 277.13f)),
        (960, new(-436.64f, 264.46f, -403.35f)),
    ]);

    // G15 (Timeworn Ophiotauroskin Map): The Shifting Gymnasion Agonon (roulette type). 8 spots in Elpis (TreasureHuntRank 24)
    public static readonly MapProfile G15 = new("G15", 39591, 2003457, [(1123, VaultKind.Roulette)], [961], false,
    [
        (961, new(286.73f, 6.97f, 142.84f)), (961, new(53.96f, 7.30f, 158.08f)), (961, new(-230.29f, -44.65f, 478.54f)),
        (961, new(378.37f, 147.73f, -196.19f)), (961, new(422.32f, 150.71f, -598.17f)), (961, new(-423.94f, 312.19f, -635.18f)),
        (961, new(-480.29f, -33.78f, 584.59f)), (961, new(784.15f, 152.14f, -145.09f)),
    ]);

    // G17 (Timeworn Br'aaxskin Map): Cenote Ja Ja Gural (door type). 40 spots in the 5 Dawntrail zones without Living Memory
    // (TreasureHuntRank 28)
    public static readonly MapProfile G17 = new("G17", 43557, 2003563, [(1209, VaultKind.Door)], [1187, 1188, 1189, 1190, 1191], false,
    [
        (1190, new(-163.11f, 16.42f, -19.52f)), (1190, new(-444.13f, 28.52f, -352.61f)), (1190, new(-88.17f, -17.88f, 652.72f)),
        (1190, new(523.50f, 16.09f, 572.31f)), (1190, new(393.24f, -15.02f, -652.16f)), (1190, new(-145.82f, 15.42f, -490.89f)),
        (1190, new(704.46f, -14.48f, -224.88f)), (1190, new(208.80f, 4.96f, -85.16f)), (1191, new(239.06f, 115.76f, 467.92f)),
        (1191, new(-231.01f, -5.06f, 717.21f)), (1191, new(-211.30f, 28.82f, 38.01f)), (1191, new(-241.63f, 21.28f, -419.33f)),
        (1191, new(719.28f, 147.96f, 205.58f)), (1191, new(-623.45f, 20.95f, -11.40f)), (1191, new(34.50f, 92.38f, -116.99f)),
        (1191, new(484.90f, 131.38f, -653.29f)), (1187, new(639.24f, 123.36f, 379.99f)), (1187, new(-391.72f, -45.41f, -365.01f)),
        (1187, new(6.18f, 10.24f, 119.35f)), (1187, new(14.66f, 23.86f, 228.04f)), (1187, new(-286.58f, 69.40f, 457.84f)),
        (1187, new(-228.89f, 62.63f, 96.72f)), (1187, new(373.81f, 63.80f, 130.12f)), (1187, new(638.49f, 109.35f, 560.16f)),
        (1188, new(771.13f, 41.22f, -759.98f)), (1188, new(497.28f, 10.70f, -326.05f)), (1188, new(-75.05f, 20.30f, -724.78f)),
        (1188, new(-727.42f, 61.38f, -103.17f)), (1188, new(-621.86f, 129.51f, 620.41f)), (1188, new(-129.01f, 118.70f, 51.61f)),
        (1188, new(823.96f, 23.36f, 11.69f)), (1188, new(540.50f, 122.53f, 732.10f)), (1189, new(64.04f, -164.63f, 717.38f)),
        (1189, new(-301.69f, -169.47f, 268.90f)), (1189, new(-790.39f, 31.85f, -92.20f)), (1189, new(760.93f, 35.58f, -448.47f)),
        (1189, new(485.30f, 11.24f, 22.83f)), (1189, new(-511.32f, -153.80f, 588.87f)), (1189, new(163.28f, -159.93f, -24.64f)),
        (1189, new(56.46f, 8.21f, -781.69f)),
    ]);

    public static readonly MapProfile[] All = [G12, G14, G15, G17];
}
