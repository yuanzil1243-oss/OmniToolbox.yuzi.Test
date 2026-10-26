using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using OmenTools.Interop.Windows.Helpers;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.KeyRepeaterOnline;

public sealed class KeyRepeater : ModuleBase
{
    private const float MinCps = 1f, MaxCps = 30f;
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly KeyRepeaterConfig config = new();
    private long nextTick;
    private bool active;
    private string status = "未运行";
    private string cpsText = "10";

    public override ModuleInfo Info { get; } = new()
    {
        Title = "按键连点", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "按住配置的按键或快捷键组合时，持续发送对应输入。"
    };
    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        LoadConfig();
        cpsText = config.Cps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        DalamudServices.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= OnUpdate;
        Stop();
    }

    public override bool DrawSettings()
    {
        var changed = ImGui.Checkbox("启用按键连点", ref config.Enabled);
        ImGui.TextUnformatted("快捷键（左右 Ctrl/Alt/Shift 视为同一类）");
        changed |= KeyCombo("Ctrl", ref config.Ctrl);
        ImGui.SameLine(); changed |= KeyCombo("Alt", ref config.Alt);
        ImGui.SameLine(); changed |= KeyCombo("Shift", ref config.Shift);
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("普通键", ref config.KeyIndex, KeyNames, KeyNames.Length)) changed = true;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputText("CPS", ref cpsText, 12, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue)
            || ImGui.IsItemDeactivatedAfterEdit())
        {
            if (float.TryParse(cpsText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var cps))
            { config.Cps = Math.Clamp(cps, MinCps, MaxCps); cpsText = config.Cps.ToString("0.##"); changed = true; }
            else cpsText = config.Cps.ToString("0.##");
        }
        ImGui.TextUnformatted($"间隔：{1000f / config.Cps:0.##} ms · 状态：{status}");
        if (!config.Ctrl && !config.Alt && !config.Shift && config.KeyIndex == 0)
            ImGui.TextUnformatted("请选择至少一个普通键；仅修饰键不会触发。");
        if (changed) { Stop(); SaveConfig(); }
        return changed;
    }

    private static bool KeyCombo(string label, ref bool value) => ImGui.Checkbox(label, ref value);

    private void OnUpdate(IFramework framework)
    {
        if (!config.Enabled || config.KeyIndex <= 0) { Stop(); return; }
        if (!Held()) { Stop(); return; }
        var now = Environment.TickCount64;
        if (!active || now >= nextTick)
        {
            SendInput();
            active = true;
            status = "正在连点";
            nextTick = now + Math.Max(1, (long)(1000f / config.Cps));
        }
    }

    private bool Held()
    {
        return (!config.Ctrl || Down(VirtualKey.CONTROL)) && (!config.Alt || Down(VirtualKey.MENU))
            && (!config.Shift || Down(VirtualKey.SHIFT)) && Down((VirtualKey)KeyValues[config.KeyIndex]);
    }

    private static bool Down(VirtualKey key) => DalamudServices.KeyState[key];

    private void SendInput()
    {
        try
        {
            if (config.Ctrl) KeyEmulationHelper.SendKeyDown((int)VirtualKey.CONTROL);
            if (config.Alt) KeyEmulationHelper.SendKeyDown((int)VirtualKey.MENU);
            if (config.Shift) KeyEmulationHelper.SendKeyDown((int)VirtualKey.SHIFT);
            KeyEmulationHelper.SendKeypress(KeyValues[config.KeyIndex]);
        }
        catch (Exception ex) { status = "输入失败"; DalamudServices.PluginLog.Error(ex, "按键连点发送输入失败"); }
        finally
        {
            if (config.Shift) KeyEmulationHelper.SendKeyUp((int)VirtualKey.SHIFT);
            if (config.Alt) KeyEmulationHelper.SendKeyUp((int)VirtualKey.MENU);
            if (config.Ctrl) KeyEmulationHelper.SendKeyUp((int)VirtualKey.CONTROL);
        }
    }

    private void Stop() { active = false; nextTick = 0; status = config.Enabled ? "待触发" : "已停用"; }

    private static readonly int[] KeyValues = { 0, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x20 };
    private static readonly string[] KeyNames = { "未选择", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "Space" };
    private static string ConfigPath => Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "KeyRepeater.json");

    private void LoadConfig() { try { if (File.Exists(ConfigPath) && JsonSerializer.Deserialize<KeyRepeaterConfig>(File.ReadAllText(ConfigPath), JsonOptions) is { } c) { config.Enabled = c.Enabled; config.Ctrl = c.Ctrl; config.Alt = c.Alt; config.Shift = c.Shift; config.KeyIndex = Math.Clamp(c.KeyIndex, 0, KeyValues.Length - 1); config.Cps = Math.Clamp(c.Cps, MinCps, MaxCps); } } catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取按键连点配置失败"); } }
    private void SaveConfig() { try { Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!); File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions)); } catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存按键连点配置失败"); } }
}

[Serializable]
public sealed class KeyRepeaterConfig { public bool Enabled; public bool Ctrl; public bool Alt; public bool Shift; public int KeyIndex; public float Cps = 10f; }
