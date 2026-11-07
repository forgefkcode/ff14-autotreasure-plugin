using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoTreasure;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool UniqueOnly { get; set; } = true;
    public HashSet<uint> MarkedUnique { get; set; } = []; // items the player marked as treasure-only
    public HashSet<uint> MarkedCommon { get; set; } = []; // items from TreasureOnlyItems the player wants hidden

    public bool IsUnique(uint id) => MarkedUnique.Contains(id) || TreasureOnlyItems.Ids.Contains(id) && !MarkedCommon.Contains(id);

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/autotreasure";
    private const string ShortCommand = "/atr";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem _windows = new("AutoTreasure");
    private readonly Ledger _ledger;
    private readonly Tracker _tracker;
    private readonly MainWindow _main;

    public Plugin()
    {
        var config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _ledger = new Ledger(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "ledger.json"));
        _tracker = new Tracker(_ledger);
        _main = new MainWindow(config, _ledger, _tracker);
        _windows.AddWindow(_main);

        var help = new CommandInfo(OnCommand) { HelpMessage = "開關挖寶記帳視窗" };
        CommandManager.AddHandler(Command, help);
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "同 " + Command });
        PluginInterface.UiBuilder.Draw += _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += _main.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= _main.Toggle;
        CommandManager.RemoveHandler(Command);
        CommandManager.RemoveHandler(ShortCommand);
        _windows.RemoveAllWindows();
        _tracker.Dispose();
        _ledger.SaveIfDirty(true);
    }

    private void OnCommand(string command, string args) => _main.Toggle();
}
