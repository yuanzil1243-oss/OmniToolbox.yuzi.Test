using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using OmenTools;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.HideTargetCircleOnline;

public sealed unsafe class HideTargetCircle : ModuleBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly HideTargetCircleConfig config = new();
    private readonly Stopwatch combatBufferTimer = new();
    private uint originalTargetCircleType;
    private bool hasOriginalTargetCircleType;
    private bool wasInCombat;
    private bool combatStateInitialized;

    public override ModuleInfo Info { get; } = new()
    {
        Title = "隐藏目标圈（测试）", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "隐藏选中目标的目标圈，并可按拔出武器、战斗状态和战斗结束延迟控制显示。"
    };
    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        LoadConfig();
        originalTargetCircleType = DService.Instance().GameConfig.UiControl.GetUInt("TargetCircleType");
        hasOriginalTargetCircleType = true;
        combatBufferTimer.Reset();
        wasInCombat = false;
        combatStateInitialized = false;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        if (hasOriginalTargetCircleType)
            DService.Instance().GameConfig.UiControl.Set("TargetCircleType", originalTargetCircleType);
        hasOriginalTargetCircleType = false;
        combatBufferTimer.Reset();
        wasInCombat = false;
        combatStateInitialized = false;
    }

    public override bool DrawSettings()
    {
        var changed = ImGui.Checkbox("拔出武器时显示目标圈", ref config.ShowWhenWeaponDrawn);
        changed |= ImGui.Checkbox("战斗状态下显示目标圈", ref config.ShowInCombat);
        if (config.ShowInCombat)
        {
            ImGui.SetNextItemWidth(180);
            changed |= ImGui.DragFloat("战斗结束后延迟隐藏（秒）", ref config.CombatEndDelaySeconds, 0.1f, 0, 300, "%.1f");
        }
        config.CombatEndDelaySeconds = Math.Clamp(config.CombatEndDelaySeconds, 0, 300);
        if (changed) SaveConfig();
        return changed;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var services = DService.Instance();
        var inCombat = services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        if (combatStateInitialized && wasInCombat && !inCombat)
            combatBufferTimer.Restart();
        else if (inCombat)
            combatBufferTimer.Restart();
        else if (combatStateInitialized && !wasInCombat && !inCombat && combatBufferTimer.IsRunning
                 && combatBufferTimer.Elapsed.TotalSeconds >= config.CombatEndDelaySeconds)
            combatBufferTimer.Stop();
        wasInCombat = inCombat;
        combatStateInitialized = true;

        var weaponDrawn = services.ObjectTable.LocalPlayer?.StatusFlags.HasFlag(StatusFlags.WeaponOut) == true;
        var show = (config.ShowWhenWeaponDrawn && weaponDrawn)
            || (config.ShowInCombat && (inCombat
                || (combatBufferTimer.IsRunning && combatBufferTimer.Elapsed.TotalSeconds < config.CombatEndDelaySeconds)));
        var targetCircleType = show ? 1u : 0u;
        var uiControl = services.GameConfig.UiControl;
        if (uiControl.GetUInt("TargetCircleType") != targetCircleType)
            uiControl.Set("TargetCircleType", targetCircleType);
    }

    private static string ConfigPath => Path.Combine(
        DalamudServices.PluginInterface.GetPluginConfigDirectory(), "HideTargetCircle.json");

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var saved = JsonSerializer.Deserialize<HideTargetCircleConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            if (saved == null) return;
            config.ShowWhenWeaponDrawn = saved.ShowWhenWeaponDrawn;
            config.ShowInCombat = saved.ShowInCombat;
            config.CombatEndDelaySeconds = Math.Clamp(saved.CombatEndDelaySeconds, 0, 300);
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取隐藏目标圈配置失败"); }
    }

    private void SaveConfig()
    {
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存隐藏目标圈配置失败"); }
    }
}

[Serializable]
public sealed class HideTargetCircleConfig
{
    public bool ShowWhenWeaponDrawn;
    public bool ShowInCombat;
    public float CombatEndDelaySeconds = 3;
}
