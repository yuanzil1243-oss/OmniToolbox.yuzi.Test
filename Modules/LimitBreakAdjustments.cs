using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.LimitBreakAdjustmentsOnline;

public sealed unsafe class LimitBreakAdjustments : ModuleBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly LimitBreakConfig config = new();
    private bool preview;
    private byte previewFrames;
    private byte previousBarCount;

    public override ModuleInfo Info { get; } = new()
    {
        Title = "极限技调整（测试）", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "调整极限技条的旋转、间距和文本显示。"
    };
    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        LoadConfig();
        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        if (previewFrames > 0)
            LimitBreakController.Instance()->BarCount = previousBarCount;
        previewFrames = 0;
        preview = false;
        Apply(LimitBreakConfig.Default);
    }

    public override bool DrawSettings()
    {
        var changed = ImGui.DragFloat("极限技条旋转", ref config.BarRotation, 0.5f, -360, 720, "%.1f°");
        config.BarRotation %= 360;
        if (config.BarRotation < 0) config.BarRotation += 360;
        changed |= ImGui.DragFloat2("极限技条间距", ref config.BarSpacing);
        changed |= ImGui.Checkbox("隐藏文本", ref config.HideText);
        changed |= ImGui.DragFloat2("文本位置", ref config.TextPosition);
        if (changed) SaveConfig();
        if (ImGui.Checkbox("预览三条极限技", ref preview))
        {
            if (!preview && previewFrames > 0)
                LimitBreakController.Instance()->BarCount = previousBarCount;
            previewFrames = 0;
        }
        if (preview)
        {
            if (previewFrames == 0) previousBarCount = LimitBreakController.Instance()->BarCount;
            LimitBreakController.Instance()->BarCount = 3;
            previewFrames = 2;
        }
        return changed;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (previewFrames > 0 && --previewFrames == 0)
        {
            preview = false;
            LimitBreakController.Instance()->BarCount = previousBarCount;
        }
        Apply(config);
    }

    private static string ConfigPath => Path.Combine(
        DalamudServices.PluginInterface.GetPluginConfigDirectory(), "LimitBreakAdjustments.json");

    private void LoadConfig()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<LimitBreakConfig>(File.ReadAllText(path), JsonOptions);
            if (saved == null) return;
            config.BarRotation = saved.BarRotation;
            config.BarSpacing = saved.BarSpacing;
            config.TextPosition = saved.TextPosition;
            config.HideText = saved.HideText;
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取极限技调整配置失败"); }
    }

    private void SaveConfig()
    {
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存极限技调整配置失败"); }
    }

    private static void Apply(LimitBreakConfig value)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("_LimitBreak");
        if (addon == null || !addon->IsVisible) return;
        var text = addon->GetTextNodeById(2);
        if (text == null) return;
        text->AtkResNode.ToggleVisibility(!value.HideText && LimitBreakController.Instance()->BarCount > 0);
        text->AtkResNode.SetPositionFloat(value.TextPosition.X, value.TextPosition.Y);
        var position = Vector2.Zero;
        foreach (var id in new ushort[] { 6, 5, 4 })
        {
            var bar = (AtkComponentNode*)addon->GetNodeById(id);
            if (bar == null) continue;
            bar->AtkResNode.Rotation = value.BarRotation * MathF.PI / 180f;
            bar->AtkResNode.SetPositionFloat(position.X, position.Y);
            bar->AtkResNode.SetScale(1, 1);
            bar->AtkResNode.DrawFlags |= 1;
            position += value.BarSpacing;
        }
    }
}

[Serializable]
public sealed class LimitBreakConfig
{
    public static readonly LimitBreakConfig Default = new();
    public float BarRotation;
    public Vector2 BarSpacing = new(164, 0);
    public Vector2 TextPosition = new(12, 1);
    public bool HideText;
}
