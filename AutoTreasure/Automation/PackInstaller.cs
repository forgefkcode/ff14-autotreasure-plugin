namespace AutoTreasure.Automation;

// Installs the BossMod.Maps module pack (vault dodges for G12 / G14 / G15 / G17 and the Gambler's Lure card reading) into the
// TC Boss Mod's module folder, where it is loaded from memory (the file is not locked, so it can be replaced any time).
// The pack is built against Boss Mod [TC api13] 7.5.6.901 from lilasrepo and released here under the rolling tag bossmod-maps.
public static class PackInstaller
{
    public const string BuiltFor = "7.5.6.901";
    private const string Url = "https://github.com/forgefkcode/ff14-autotreasure-plugin/releases/download/bossmod-maps/BossMod.Maps.dll";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vbm-tc", "modules");
    public static string FilePath => Path.Combine(Dir, "BossMod.Maps.dll");
    public static bool Installed => File.Exists(FilePath);
    public static bool Busy { get; private set; }
    public static string Status { get; private set; } = "";

    // installed Boss Mod's version, null when it is not installed
    public static string? BossModVersion() =>
        Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "BossMod")?.Version.ToString();

    public static async Task Install()
    {
        if (Busy)
            return;
        Busy = true;
        Status = "下載中…";
        try
        {
            var bytes = await Http.GetByteArrayAsync(Url);
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, FilePath, true);
            Status = $"已安裝（{bytes.Length / 1024} KB）。到 /xlplugins 停用再啟用 Boss Mod，或重開遊戲後生效";
        }
        catch (Exception e)
        {
            Status = "安裝失敗：" + e.Message;
            Plugin.Log.Error(e, "Installing BossMod.Maps");
        }
        finally
        {
            Busy = false;
        }
    }

    public static void Remove()
    {
        try
        {
            File.Delete(FilePath);
            Status = "已移除。到 /xlplugins 停用再啟用 Boss Mod 後生效";
        }
        catch (Exception e)
        {
            Status = "移除失敗：" + e.Message;
        }
    }
}
