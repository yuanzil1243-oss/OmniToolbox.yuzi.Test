using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Windows.Helpers;
using OmenTools;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.KeyRepeaterOnline;

public sealed unsafe class KeyRepeater : ModuleBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly Dictionary<int, long> due = new();
    private readonly HashSet<int> bindings = new();
    private KeyRepeaterConfig config = new();
    private string status = "已停用", error = "";
    private long refreshAt;
    private int running;
    private bool faulted;
    public override ModuleInfo Info { get; } = new()
    {
        Title = "按键连点", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "自动读取游戏技能栏快捷键，按住任意已绑定按键即可持续连点。"
    };
    public override bool HasSettings => true;
    protected override void OnEnable()
    {
        try
        {
            if (File.Exists(ConfigPath)) config = JsonSerializer.Deserialize<KeyRepeaterConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new();
        }
        catch (Exception ex) { error = "配置读取失败，已使用默认停用配置"; DalamudServices.PluginLog.Error(ex, "按键连点配置读取失败"); }
        if (!float.IsFinite(config.Cps) || config.Cps < 1 || config.Cps > 30) config.Cps = 10;
        faulted = false;
        refreshAt = 0;
        DalamudServices.Framework.Update += OnUpdate;
    }
    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= OnUpdate;
        due.Clear(); bindings.Clear(); running = 0; status = "已停用";
    }
    public override bool DrawSettings()
    {
        var changed = ImGui.Checkbox("启用技能栏按住连点", ref config.Enabled);
        changed |= ImGui.SliderFloat("每秒次数（CPS）", ref config.Cps, 1, 30, "%.1f");
        ImGui.TextWrapped("自动使用游戏技能栏已设置的主、副快捷键，无需逐键配置。左右 Ctrl/Alt/Shift 同义，修饰键必须完全匹配。修改游戏绑定后约 1 秒更新。");
        ImGui.TextUnformatted($"间隔 {1000 / config.Cps:0.#} ms · 已识别 {bindings.Count} 组 · 连点中 {running} 组 · {status}");
        ImGui.TextWrapped("聊天或插件文字输入时、游戏不在前台时暂停。速度受游戏帧率限制，不补发积压输入。");
        ImGui.TextWrapped("模拟输入可能不被部分应用支持，使用时请遵守对应服务条款。本模块不保存或上传实际键盘输入。");
        if (error.Length > 0) ImGui.TextWrapped(error);
        if (ImGui.Button("重新读取技能栏绑定")) { faulted = false; refreshAt = 0; due.Clear(); }
        ImGui.SameLine();
        if (ImGui.Button("恢复默认（停用）")) { config = new(); changed = true; }
        if (changed)
        {
            due.Clear(); running = 0; faulted = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var temp = ConfigPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
                File.Move(temp, ConfigPath, true);
                error = "";
            }
            catch (Exception ex) { error = "配置保存失败，本次设置仅在当前会话有效"; DalamudServices.PluginLog.Error(ex, "按键连点配置保存失败"); }
        }
        return changed;
    }
    private void OnUpdate(IFramework framework)
    {
        running = 0;
        if (faulted || !config.Enabled) { due.Clear(); status = faulted ? "异常暂停" : "已停用"; return; }
        try
        {
            var ui = UIModule.Instance();
            if (ui == null || !DService.Instance().ClientState.IsLoggedIn) { Pause("等待角色登录"); return; }
            if (Environment.TickCount64 >= refreshAt)
            {
                var input = ui->GetUIInputData();
                if (input == null) { Pause("技能栏绑定不可用"); return; }
                var current = new HashSet<int>();
                // Named InputId boundaries: bars 1–10, pet bar, duty actions; never UI/movement bindings.
                for (var id = (int)InputId.HOTBAR_1_1; id <= (int)InputId.HOTBAR_CONTENTS_ACT_R; id++)
                {
                    var bind = input->GetKeybind((InputId)id);
                    if (bind == null) continue;
                    foreach (var setting in bind->KeySettings)
                    {
                        var key = (int)setting.Key;
                        if (key < 8 || key > 254 || key is 16 or 17 or 18 or 91 or 92 or >= 160 and <= 165) continue;
                        if (((int)setting.KeyModifier & ~7) != 0) continue;
                        current.Add(key | ((int)setting.KeyModifier << 8));
                    }
                }
                if (!bindings.SetEquals(current)) { bindings.Clear(); bindings.UnionWith(current); due.Clear(); }
                refreshAt = Environment.TickCount64 + 1000;
            }
            GetWindowThreadProcessId(GetForegroundWindow(), out var process);
            if (process != Environment.ProcessId) { Pause("游戏未在前台"); return; }
            if (RaptureAtkModule.Instance()->IsTextInputActive() || ImGui.GetIO().WantTextInput) { Pause("文字输入中"); return; }
            var modifiers = (Down(16) ? 1 : 0) | (Down(17) ? 2 : 0) | (Down(18) ? 4 : 0);
            var now = Environment.TickCount64;
            foreach (var binding in bindings)
            {
                var key = binding & 255;
                if ((binding >> 8) != modifiers || !Down(key) || Down(91) || Down(92)) { due.Remove(binding); continue; }
                running++;
                if (due.TryGetValue(binding, out var next) && now < next) continue;
                // Physical modifiers remain held; never inject modifier releases that corrupt the user's chord.
                KeyEmulationHelper.SendKeypress(key);
                due[binding] = now + Math.Max(1, (long)(1000 / config.Cps));
            }
            status = running > 0 ? "正在连点" : "等待按住技能栏快捷键";
        }
        catch (Exception ex)
        {
            faulted = true; Pause("异常暂停"); error = "读取或发送输入失败，请查看日志并重新读取绑定";
            DalamudServices.PluginLog.Error(ex, "按键连点运行失败，已暂停");
        }
    }
    private void Pause(string reason) { due.Clear(); running = 0; status = reason; }
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    private static string ConfigPath => Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "KeyRepeater.json");
}
[Serializable]
public sealed class KeyRepeaterConfig { public bool Enabled; public float Cps = 10; }
