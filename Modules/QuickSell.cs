using System;
using System.IO;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using OmenTools;
using OmenTools.OmenService;
using OmenTools.Interop.Game.AddonEvent;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.Lifecycle;

namespace OmniToolbox.SellOnClickOnline;

public sealed unsafe class SellOnClick : ModuleBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private readonly SellOnClickConfig config = new();
    public override ModuleInfo Info { get; } = new()
    {
        Title = "快捷出售", Author = "Yuzi", Category = ModuleCategory.Item,
        Description = "按住配置的修饰键右键单击物品，执行当前商店或雇员菜单的系统出售动作。"
    };
    public override bool HasSettings => true;
    private Hook<AgentInventoryContext.Delegates.OpenForItemSlot>? hook;
    private string vendorLabel = "";
    private string retainerLabel = "";
    private bool dispatching;
    private AddonEventRegistry? addonEvents;
    private bool pendingSellConfirmation;

    protected override void OnEnable()
    {
        LoadConfig();
        var sheet = DalamudServices.DataManager.GetExcelSheet<Addon>();
        vendorLabel = sheet.GetRow(93).Text.ExtractText();
        retainerLabel = sheet.GetRow(5480).Text.ExtractText();
        try
        {
            hook = DService.Instance().Hook.HookFromAddress<AgentInventoryContext.Delegates.OpenForItemSlot>(
                AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot, OpenContext);
            hook.Enable();
            addonEvents = new AddonEventRegistry(DalamudServices.AddonLifecycle);
            addonEvents.Register(AddonEvent.PostSetup, "SelectYesno", OnSelectYesno);
        }
        catch { hook?.Dispose(); hook = null; throw; }
    }
    protected override void OnDisable()
    {
        addonEvents?.Dispose();
        addonEvents = null;
        pendingSellConfirmation = false;
        hook?.Dispose();
        hook = null;
    }

    public override bool DrawSettings()
    {
        var changed = ImGui.Checkbox("Alt", ref config.Alt);
        changed |= ImGui.Checkbox("Ctrl", ref config.Ctrl);
        changed |= ImGui.Checkbox("Shift", ref config.Shift);
        changed |= ImGui.Checkbox("自动确认贵重物品", ref config.AutoConfirmValuable);
        ImGui.TextUnformatted("按住全部选中的修饰键＋鼠标右键单击物品。\n仅执行游戏菜单中可用的系统出售。");
        if (!config.Alt && !config.Ctrl && !config.Shift)
            ImGui.TextUnformatted("请至少选择一个修饰键；当前不会触发出售。");
        if (changed) SaveConfig();
        return changed;
    }

    private static string ConfigPath => Path.Combine(
        DalamudServices.PluginInterface.GetPluginConfigDirectory(), "QuickSell.json");

    private void LoadConfig()
    {
        try
        {
            var path = ConfigPath;
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<SellOnClickConfig>(File.ReadAllText(path), JsonOptions);
            if (saved == null) return;
            config.Alt = saved.Alt;
            config.Ctrl = saved.Ctrl;
            config.Shift = saved.Shift;
            config.AutoConfirmValuable = saved.AutoConfirmValuable;
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取快捷出售配置失败"); }
    }

    private void SaveConfig()
    {
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存快捷出售配置失败"); }
    }
    private bool Held() => (config.Alt || config.Ctrl || config.Shift)
        && (!config.Alt || DalamudServices.KeyState[VirtualKey.MENU])
        && (!config.Ctrl || DalamudServices.KeyState[VirtualKey.CONTROL])
        && (!config.Shift || DalamudServices.KeyState[VirtualKey.SHIFT]);

    private static bool Visible(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible;
    }

    private void OpenContext(AgentInventoryContext* context, InventoryType inventory, int slot, int flags, uint owner)
    {
        hook!.Original(context, inventory, slot, flags, owner);
        if (dispatching || !Held() || context == null) return;
        try
        {
            // The native context menu is the authority: no cached item ID or guessed slot is sold.
            var vendor = Visible("Shop");
            var retainer = Visible("RetainerGrid0") || Visible("RetainerSellList");
            if (!vendor && !retainer) return;
            var container = InventoryManager.Instance()->GetInventoryContainer(inventory);
            var item = container == null ? null : container->GetInventorySlot(slot);
            if (item == null || item->ItemId == 0 || item->Quantity == 0) return;
            var menu = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)context->GetAddonId());
            if (menu == null || !menu->IsVisible) return;
            var label = vendor ? vendorLabel : retainerLabel;
            if (string.IsNullOrEmpty(label)) return;
            for (var i = 0; i < context->ContextItemCount && i < 32; i++)
            {
                var index = context->ContexItemStartIndex + i;
                if (index < 0 || index >= context->EventParams.Length || context->IsContextItemDisabled(i)) continue;
                ref var value = ref context->EventParams[index];
                if (value.Type != AtkValueType.String || value.String.ToString() != label) continue;
                AtkValue* args = stackalloc AtkValue[5];
                args[0].SetInt(0); args[1].SetInt(i); args[2].SetUInt(0); args[3].SetInt(0); args[4].SetInt(0);
                dispatching = true;
                menu->FireCallback(5, args);
                pendingSellConfirmation = config.AutoConfirmValuable;
                context->Hide();
                menu->Close(false);
                break;
            }
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "按键出售未完成"); }
        finally { dispatching = false; }
    }

    private void OnSelectYesno(AddonEvent type, AddonArgs args)
    {
        if (!pendingSellConfirmation)
            return;

        pendingSellConfirmation = false;
        DalamudServices.Framework.RunOnFrameworkThread(() => AddonSelectYesnoEvent.ClickYes());
    }
}

[Serializable]
public sealed class SellOnClickConfig
{
    public bool Alt;
    public bool Ctrl;
    public bool Shift = true;
    public bool AutoConfirmValuable;
}

