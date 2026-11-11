using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoTreasure;

// A map bought on the market board.
public sealed class Purchase
{
    public DateTime Time { get; set; }
    public string Character { get; set; } = "";
    public string Grade { get; set; } = "";
    public int Quantity { get; set; }
    public long Gil { get; set; } // price * quantity + tax
}

// A Gambler's Lure in a door vault. Items are what came in from the lure starting until LureLoot after it ended (the coffer
// pays out the multiplied treasure then).
public sealed class Lure
{
    public DateTime Time { get; set; }
    public int Floor { get; set; } // 0 = floor unknown
    public string? Result { get; set; } // success (all cleared) / cashout / failed / declined; null = no ending seen
    public int Multiplier { get; set; } = 1;
    public Dictionary<uint, int> Items { get; set; } = [];
}

// One deciphered map: the field coffer, and the vault if its portal was taken. Gil and items are what came in while it ran.
public sealed class Run
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public string Character { get; set; } = "";
    public string Grade { get; set; } = "";
    public bool Vault { get; set; }
    public ushort? VaultTerritory { get; set; }
    public int? Floor { get; set; } // how far the vault got: door vaults the floor, roulette vaults the round (1-5)
    public List<Lure>? Lures { get; set; }
    public long Gil { get; set; }
    public Dictionary<uint, int> Items { get; set; } = [];
}

// One go of the automation, from 「開始」 to 「停止」; starting again within Tracker.SessionJoin of the stop continues it. The
// ledger tab shows a session's purchases and runs by time.
public sealed class Session
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public string Character { get; set; } = "";

    public bool Contains(DateTime t) => t >= Start && (End == null || t <= End);
}

public sealed class LedgerData
{
    public int Version { get; set; } = 1;
    public List<Purchase> Purchases { get; set; } = [];
    public List<Run> Runs { get; set; } = [];
    public List<Session> Sessions { get; set; } = [];
}

// The records live in their own file in the plugin config folder, apart from the settings, and are saved a few seconds
// after the last change.
public sealed class Ledger
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _path;
    private DateTime? _dirtySince;

    public LedgerData Data { get; private set; } = new();

    public Ledger(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                Data = JsonSerializer.Deserialize<LedgerData>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception e)
        {
            // keep the unreadable file instead of overwriting it with an empty ledger
            var backup = path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(path, backup);
            Plugin.Log.Error(e, $"Could not read {path}, moved it to {backup}");
        }
    }

    public void MarkDirty() => _dirtySince ??= DateTime.Now;

    public void SaveIfDirty(bool now = false)
    {
        if (_dirtySince is not { } since || !now && DateTime.Now - since < TimeSpan.FromSeconds(5))
            return;
        _dirtySince = null;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Options));
            File.Move(tmp, _path, true);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"Could not save {_path}");
        }
    }

    public void Clear()
    {
        Data = new();
        MarkDirty();
        SaveIfDirty(true);
    }
}
