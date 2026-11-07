namespace AutoTreasure;

// The maps the auto-treasure scripts run. DecipheredItemId is the key item a deciphered map turns into (TreasureHuntRank);
// it disappears when the coffer is dug up.
public sealed record TreasureMap(string Grade, uint MapItemId, uint DecipheredItemId, ushort[] VaultTerritories);

public static class TreasureMaps
{
    public const int VaultFloors = 5; // door vaults: 5 floors; roulette vaults: up to 5 rounds
    private static readonly ushort[] RouletteVaults = [924, 1123];

    public static readonly TreasureMap[] All =
    [
        new("G12", 26745, 2002664, [879, 924]), // The Dungeons / The Shifting Oubliettes of Lyhe Ghiah
        new("G14", 36612, 2003246, [1000]),     // The Excitatron 6000
        new("G15", 39591, 2003457, [1123]),     // The Shifting Gymnasion Agonon
        new("G17", 43557, 2003563, [1209]),     // Cenote Ja Ja Gural
    ];

    public static TreasureMap? ByMapItem(uint itemId) => All.FirstOrDefault(m => m.MapItemId == itemId);
    public static TreasureMap? ByGrade(string grade) => All.FirstOrDefault(m => m.Grade == grade);
    public static TreasureMap? ByVault(ushort territory) => All.FirstOrDefault(m => m.VaultTerritories.Contains(territory));
    public static bool IsRoulette(ushort territory) => RouletteVaults.Contains(territory);
    public static string FloorWord(ushort territory) => IsRoulette(territory) ? "關" : "層";

    // "G12 選門型" where a grade has both kinds, else the grade
    public static string VaultLabel(TreasureMap map, ushort territory) =>
        map.VaultTerritories.Length > 1 ? $"{map.Grade} {(IsRoulette(territory) ? "轉盤型" : "選門型")}" : map.Grade;
}
