using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using HarmonyLib;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.ImeCandidateScalingOnline;

public sealed class ImeCandidateScaling : ModuleBase
{
    private const float MinScale = 1f;
    private const float MaxScale = 3f;
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private static readonly Type? ImeType = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly
        .GetType("Dalamud.Interface.Internal.DalamudIme");
    private static readonly FieldInfo? CandidateStringsField = ImeType?.GetField("candidateStrings", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CompositionField = ImeType?.GetField("compositionString", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CursorPositionField = ImeType?.GetField("cursorScreenPos", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? NativeCandidatesField = ImeType?.GetField("immCandNative", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PartialFromField = ImeType?.GetField("partialConversionFrom", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PartialToField = ImeType?.GetField("partialConversionTo", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? AssociatedViewportField = ImeType?.GetField("associatedViewport", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Harmony Harmony = new("Yuzi.Omni.ImeCandidateScaling");
    private static ImeCandidateScaling? active;

    private readonly ImeCandidateScalingConfig config = new();
    private string scaleInput = "1.00";

    public override ModuleInfo Info { get; } = new()
    {
        Title = "输入法候选栏缩放",
        Author = "Yuzi",
        Category = ModuleCategory.Item,
        Description = "独立调整游戏内输入法候选栏的显示倍率。"
    };

    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        LoadConfig();
        scaleInput = config.Scale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        if (ImeType == null || CandidateStringsField == null || CompositionField == null
            || CursorPositionField == null || NativeCandidatesField == null || AssociatedViewportField == null)
            throw new InvalidOperationException("当前 Dalamud 版本未提供兼容的 IME 候选状态。");

        var draw = ImeType.GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(ImeType.FullName, "Draw");
        active = this;
        Harmony.Patch(draw, prefix: new HarmonyMethod(typeof(ImeCandidateScaling), nameof(SkipDalamudDraw)));
        DalamudServices.PluginInterface.UiBuilder.Draw += DrawCandidates;
    }

    protected override void OnDisable()
    {
        DalamudServices.PluginInterface.UiBuilder.Draw -= DrawCandidates;
        Harmony.UnpatchAll(Harmony.Id);
        active = null;
    }

    public override bool DrawSettings()
    {
        var changed = false;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("输入法候选栏缩放", ref config.Scale, MinScale, MaxScale, "%.2f 倍"))
        {
            config.Scale = Clamp(config.Scale);
            scaleInput = config.Scale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            changed = true;
        }

        ImGui.SetNextItemWidth(140);
        if (ImGui.InputText("倍率", ref scaleInput, 16, ImGuiInputTextFlags.EnterReturnsTrue)
            || ImGui.IsItemDeactivatedAfterEdit())
        {
            if (float.TryParse(scaleInput, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                config.Scale = Clamp(parsed);
                scaleInput = config.Scale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                changed = true;
            }
            else
            {
                scaleInput = config.Scale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("恢复默认"))
        {
            config.Scale = 1f;
            scaleInput = "1.00";
            changed = true;
        }

        if (changed) SaveConfig();
        return changed;
    }

    private static bool SkipDalamudDraw() => active != null;

    private void DrawCandidates()
    {
        try
        {
            var ime = GetImeService();
            if (ime == null) return;

            var viewportObject = AssociatedViewportField!.GetValue(ime);
            if (viewportObject == null) return;
            var viewportType = viewportObject.GetType();
            var handle = viewportType.GetProperty("Handle")?.GetValue(viewportObject);
            if (handle is IntPtr pointer && pointer == IntPtr.Zero) return;

            var position = CursorPositionField!.GetValue(ime) is Vector2 cursor ? cursor : Vector2.Zero;
            var composition = CompositionField!.GetValue(ime) as string ?? string.Empty;
            var candidates = CandidateStringsField!.GetValue(ime) as IEnumerable;
            var strings = candidates?.Cast<object>().Select(ReadCandidate).ToArray() ?? Array.Empty<(string Text, bool Supported)>();
            var native = NativeCandidatesField!.GetValue(ime);
            if (native == null) return;

            var count = ReadUInt(native, "dwCount");
            var selection = ReadUInt(native, "dwSelection");
            var pageStart = ReadUInt(native, "dwPageStart");
            var pageSize = Math.Max(1u, ReadUInt(native, "dwPageSize"));
            var partialFrom = ReadInt(PartialFromField, ime);
            var partialTo = ReadInt(PartialToField, ime);
            var hasComposition = strings.Length > 0 || partialFrom != 0 || partialTo != composition.Length;
            if (strings.Length == 0 && !hasComposition) return;

            DrawScaled(position, composition, strings, count, selection, pageStart, pageSize,
                partialFrom, partialTo, config.Scale);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Error(ex, "绘制输入法候选栏失败");
        }
    }

    private static void DrawScaled(Vector2 anchor, string composition, (string Text, bool Supported)[] candidates,
        uint count, uint selection, uint pageStart, uint pageSize, int partialFrom, int partialTo, float scale)
    {
        var viewport = ImGui.GetMainViewport();
        var workMin = viewport.WorkPos;
        var workMax = workMin + viewport.WorkSize;
        var style = ImGui.GetStyle();
        var padding = style.WindowPadding * scale;
        var lineHeight = ImGui.GetTextLineHeight() * scale;
        var lineSpacing = style.ItemSpacing.Y * scale;
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * scale;
        var pageText = $"{Math.Min(selection + 1, Math.Max(count, 1))}/{count} ({pageStart / pageSize + 1}/{Math.Max(1, (count + pageSize - 1) / pageSize)})";
        var maxTextWidth = ImGui.CalcTextSize(pageText).X;
        foreach (var (candidate, index) in candidates.Select((candidate, index) => (candidate, index)))
        {
            var label = $"{index + 1}. {candidate.Text}{(candidate.Supported ? string.Empty : " (x)")}";
            maxTextWidth = Math.Max(maxTextWidth, ImGui.CalcTextSize(label).X);
        }
        maxTextWidth = Math.Max(maxTextWidth, ImGui.CalcTextSize(composition).X) * scale;
        maxTextWidth = Math.Max(1, Math.Min(maxTextWidth, viewport.WorkSize.X - padding.X * 2));
        var measureWidth = maxTextWidth / scale;
        var heights = candidates.Select((candidate, index) => ImGui.CalcTextSize(
            $"{index + 1}. {candidate.Text}{(candidate.Supported ? string.Empty : " (x)")}", false, measureWidth).Y * scale).ToArray();
        var compositionHeight = hasPartial(composition, partialFrom, partialTo)
            ? ImGui.CalcTextSize(composition, false, measureWidth).Y * scale : 0;
        var pageHeight = candidates.Length > 0
            ? ImGui.CalcTextSize(pageText, false, measureWidth).Y * scale : 0;
        var contentHeight = heights.Sum() + compositionHeight + pageHeight
            + Math.Max(0, heights.Length + (compositionHeight > 0 ? 1 : 0) + (pageHeight > 0 ? 1 : 0) - 1) * lineSpacing
            + (candidates.Length > 0 ? lineSpacing : 0);
        var size = new Vector2(maxTextWidth, contentHeight) + padding * 2;
        var flippedUp = anchor.Y + size.Y > workMax.Y;
        var topLeft = anchor - padding;
        if (flippedUp) topLeft.Y -= size.Y - lineHeight - padding.Y;
        if (topLeft.X + size.X > workMax.X) topLeft.X = workMax.X - size.X;
        if (topLeft.Y + size.Y > workMax.Y) topLeft.Y = workMax.Y - size.Y;
        topLeft.X = Math.Max(topLeft.X, workMin.X);
        topLeft.Y = Math.Max(topLeft.Y, workMin.Y);

        var draw = ImGui.GetForegroundDrawList(viewport);
        var bg = ImGui.GetColorU32(ImGuiCol.WindowBg);
        var text = ImGui.GetColorU32(ImGuiCol.Text);
        var selected = ImGui.GetColorU32(ImGuiCol.NavHighlight);
        draw.PushClipRect(workMin, workMax, true);
        draw.AddRectFilled(topLeft, topLeft + size, bg, style.WindowRounding * scale);
        draw.AddRect(topLeft, topLeft + size, ImGui.GetColorU32(ImGuiCol.Border), style.WindowRounding * scale);

        var cursor = topLeft + padding;
        if (!flippedUp && hasPartial(composition, partialFrom, partialTo))
        {
            DrawComposition(draw, font, fontSize, cursor, composition, partialFrom, partialTo, scale, text, maxTextWidth);
            cursor.Y += compositionHeight + lineSpacing;
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            var label = $"{i + 1}. {candidates[i].Text}{(candidates[i].Supported ? string.Empty : " (x)")}";
            draw.AddText(font, fontSize, cursor, i == selection % pageSize ? selected : text, label,
                maxTextWidth, Vector4.Zero);
            cursor.Y += heights[i] + lineSpacing;
        }

        if (candidates.Length > 0)
        {
            draw.AddLine(cursor, cursor + new Vector2(maxTextWidth, 0), ImGui.GetColorU32(ImGuiCol.Separator));
            cursor.Y += lineSpacing;
            draw.AddText(font, fontSize, cursor, text, pageText, maxTextWidth, Vector4.Zero);
            cursor.Y += pageHeight + lineSpacing;
        }

        if (flippedUp && hasPartial(composition, partialFrom, partialTo))
            DrawComposition(draw, font, fontSize, cursor, composition, partialFrom, partialTo, scale, text, maxTextWidth);
        draw.PopClipRect();
    }

    private static void DrawComposition(ImDrawListPtr draw, ImFontPtr font, float fontSize, Vector2 position,
        string composition, int partialFrom, int partialTo, float scale, uint textColor, float wrapWidth)
    {
        var text = composition;
        var textSize = ImGui.CalcTextSize(text, false, wrapWidth / scale) * scale;
        draw.AddRectFilled(position, position + textSize, ImGui.GetColorU32(ImGuiCol.WindowBg));
        if (partialFrom >= 0 && partialTo > partialFrom && partialTo <= text.Length)
        {
            var start = ImGui.CalcTextSize(text[..partialFrom]).X * scale;
            var end = ImGui.CalcTextSize(text[..partialTo]).X * scale;
            draw.AddRectFilled(position + new Vector2(start, 0), position + new Vector2(end, fontSize),
                ImGui.GetColorU32(ImGuiCol.TextSelectedBg));
        }
        draw.AddText(font, fontSize, position, textColor, text, wrapWidth, Vector4.Zero);
    }

    private static (string Text, bool Supported) ReadCandidate(object value)
    {
        var type = value.GetType();
        return (type.GetField("Item1")?.GetValue(value) as string ?? string.Empty,
            type.GetField("Item2")?.GetValue(value) as bool? ?? true);
    }

    private static uint ReadUInt(object value, string name)
        => Convert.ToUInt32(value.GetType().GetField(name)?.GetValue(value) ?? 0u);

    private static int ReadInt(FieldInfo? field, object instance)
        => Convert.ToInt32(field?.GetValue(instance) ?? 0);

    private static bool hasPartial(string text, int from, int to) => text.Length > 0 && (from != 0 || to != text.Length);

    private static object? GetImeService()
    {
        if (ImeType == null) return null;
        var resolver = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetType("Dalamud.ServiceResolver");
        var get = resolver?.GetMethod("Get", BindingFlags.Static | BindingFlags.Public);
        if (get == null) return null;
        return get.Invoke(null, new object[] { ImeType });
    }

    private static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, MinScale, MaxScale) : MinScale;

    private static string ConfigPath => Path.Combine(
        DalamudServices.PluginInterface.GetPluginConfigDirectory(), "ImeCandidateScaling.json");

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var saved = JsonSerializer.Deserialize<ImeCandidateScalingConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            if (saved != null) config.Scale = Clamp(saved.Scale);
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "读取输入法候选栏缩放配置失败"); }
    }

    private void SaveConfig()
    {
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) { DalamudServices.PluginLog.Error(ex, "保存输入法候选栏缩放配置失败"); }
    }
}

[Serializable]
public sealed class ImeCandidateScalingConfig
{
    public float Scale = 1f;
}
