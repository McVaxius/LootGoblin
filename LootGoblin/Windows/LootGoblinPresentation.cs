using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace LootGoblin.Windows;

internal enum UiFontRole { Body, BodyStrong, Title, PluginName, Counter, Action }

internal static class LootGoblinPresentation
{
    // Approved LootGoblin-review-v3: about 1054px regular envelope. Content uses
    // 52px actions, 38px queue headings, 44px rows and 12px panel gaps at 100%.
    // Compact actions/rows/gaps are 40/36/8px; body text remains 16px.
    internal const uint ReferenceAccent = 0xEAB321;
    internal static readonly float[] FontSizes = [16, 16, 32, 20, 22, 18];
    internal static readonly string[] FontFiles = ["segoeui.ttf", "seguisb.ttf", "segoeuib.ttf", "seguisb.ttf", "seguisb.ttf", "seguisb.ttf"];
    internal static float AtlasHeight(UiFontRole role) => FontSizes[(int)role] * 4 / 3;
    internal static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    internal static MaterialControlMetrics Controls(float height, float icon = 20)
    {
        var s = MaterialTheme.Metrics.Scale;
        return new() { Height = height * s, Padding = new(12 * s, Math.Max(0, (height * s - ImGui.GetTextLineHeight()) * .5f)),
            Gap = 8 * s, IconSize = icon * s, Rounding = 4 * s, ItemSpacing = new(10 * s, 4 * s), CellPadding = new(12 * s, 8 * s) };
    }
    internal static float DetailColumn(params string[] labels)
        => ImGui.GetCursorPosX() + MathF.Ceiling(labels.Max(label => MaterialText.Measure(UiText.T(label)).X)) + 12 * MaterialTheme.Metrics.Scale;
    internal static void DetailLabel(string label, float valueColumn)
    {
        UiGui.Text(label);
        ImGui.SameLine(valueColumn);
    }
    internal static void AccentRule()
    {
        ImGui.PushStyleColor(ImGuiCol.Separator, MaterialTheme.Current.Colors.Primary);
        ImGui.Separator();
        ImGui.PopStyleColor();
    }
    internal static float[] QueueWidths(IEnumerable<string> mapNames)
    {
        var s = MaterialTheme.Metrics.Scale;
        var padding = ImGui.GetStyle().CellPadding.X * 2 + 10 * s;
        var labels = new[] { "Map", "Enabled", "Run count", "Inventory", "Saddlebag", "Retainer", "Gather", "Buy", "Max gil" };
        float[] defaults = [270, 76, 122, 86, 96, 86, 74, 72, 130];
        using (UiText.Font(UiFontRole.BodyStrong))
            for (var index = 0; index < labels.Length; index++)
                defaults[index] = MathF.Ceiling(Math.Max(defaults[index] * s, MaterialText.Measure(UiText.T(labels[index])).X + padding));
        using (UiText.Font(UiFontRole.BodyStrong))
            defaults[0] = Math.Max(defaults[0], MathF.Ceiling(mapNames.Select(name => MaterialText.Measure(name).X + padding).DefaultIfEmpty(0).Max()));
        defaults[2] = Math.Max(defaults[2], UiGui.IntMinimum(99999) + UiGui.ButtonWidth("Max") + ImGui.GetStyle().ItemSpacing.X + padding);
        defaults[8] = Math.Max(defaults[8], UiGui.IntMinimum(int.MaxValue) + UiGui.ButtonWidth("Use") + ImGui.GetStyle().ItemSpacing.X + padding);
        return defaults;
    }
    internal static float SummaryWidth(string label, string value, bool compact)
    {
        float labelWidth, valueWidth;
        using (UiText.Font(UiFontRole.Body)) labelWidth = MaterialText.Measure(UiText.T(label)).X;
        using (UiText.Font(UiFontRole.BodyStrong)) valueWidth = MaterialText.Measure(UiText.T(value)).X;
        return MathF.Ceiling((compact ? labelWidth + valueWidth + 12 * MaterialTheme.Metrics.Scale : Math.Max(labelWidth, valueWidth))
            + 56 * MaterialTheme.Metrics.Scale + ImGui.GetStyle().CellPadding.X * 2);
    }
    internal static void Summary(string label, string value, MaterialIcon icon, bool compact)
    {
        var s = MaterialTheme.Metrics.Scale;
        ImGui.BeginGroup();
        MaterialIcons.Draw(icon, ImGui.GetCursorScreenPos(), 32 * s, MaterialTheme.Current.Colors.Secondary);
        ImGui.Dummy(new Vector2(36, 32) * s); ImGui.SameLine();
        ImGui.BeginGroup();
        using (UiText.Font(UiFontRole.Body)) UiGui.TextUnformatted(label);
        if (compact) ImGui.SameLine();
        using (UiText.Font(UiFontRole.BodyStrong)) UiGui.TextUnformatted(value);
        ImGui.EndGroup();
        ImGui.EndGroup();
    }
    internal static MaterialTheme Theme(uint accent)
    {
        accent &= 0xFFFFFF;
        var reference = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new Vector3(Rgb(ReferenceAccent).X, Rgb(ReferenceAccent).Y, Rgb(ReferenceAccent).Z)));
        var selected = Rgb(accent);
        var seed = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(selected.X, selected.Y, selected.Z)));
        var hueShift = seed.Y < .001f ? 0 : seed.Z - reference.Z;
        var chromaScale = seed.Y < .001f ? 0 : seed.Y / reference.Y;
        Vector4 Relative(uint rgb)
        {
            var color = Rgb(rgb);
            if (accent == ReferenceAccent) return color;
            var lch = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(color.X, color.Y, color.Z)));
            return new(MaterialColor.GamutMap(lch.X, lch.Y * chromaScale, lch.Z + hueShift), 1);
        }
        var palette = new OklchPaletteGenerator().Generate(new(selected.X, selected.Y, selected.Z));
        var background = Relative(0x0D1820);
        var foreground = Relative(0xE8EEF2);
        var primary = Relative(ReferenceAccent);
        var colors = new MaterialColorScheme(palette)
        {
            Background = background, OnBackground = foreground, Surface = Relative(0x0F1B23), OnSurface = foreground,
            SurfaceContainerLowest = Relative(0x0C1720), SurfaceContainerLow = Relative(0x101E27),
            SurfaceContainer = Relative(0x13232E), SurfaceContainerHigh = Relative(0x16232F), SurfaceContainerHighest = Relative(0x14212B),
            SurfaceVariant = Relative(0x253642), OnSurfaceVariant = Relative(0xB7C7CD),
            Outline = Relative(0x425B68), OutlineVariant = Relative(0x304551),
            Primary = primary, OnPrimary = MaterialColor.Contrast(primary, background) >= MaterialColor.Contrast(primary, foreground) ? background : foreground,
            PrimaryContainer = Relative(0x806116), OnPrimaryContainer = foreground,
            Secondary = Relative(0xB9C4D3), OnSecondary = background, SecondaryContainer = Relative(0x1F2B36), OnSecondaryContainer = foreground,
            Tertiary = Relative(0xDDC99A), OnTertiary = background, TertiaryContainer = Relative(0x443B26), OnTertiaryContainer = foreground,
            InverseSurface = foreground, InverseOnSurface = background, InversePrimary = Relative(0x866913),
        };
        return new(colors) { SurfaceOpacity = 1 };
    }

    internal static void Chest(Vector2 origin, float size, Vector4 color)
    {
        // Loot Goblin owns its treasure-chest mark. Native vector meshes need no texture lifetime.
        var dl = ImGui.GetWindowDrawList();
        var ink = MaterialCanvas.Color(color);
        var cut = MaterialCanvas.Color(MaterialTheme.Current.Colors.Background);
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * size;
        dl.AddRectFilled(P(.10f,.38f), P(.91f,.93f), ink, size*.06f);
        dl.AddQuadFilled(P(.08f,.31f), P(.23f,.11f), P(.82f,.11f), P(.94f,.31f), ink);
        dl.AddLine(P(.12f,.38f), P(.91f,.38f), cut, size*.055f);
        dl.AddLine(P(.32f,.40f), P(.32f,.90f), cut, size*.075f);
        dl.AddLine(P(.72f,.40f), P(.72f,.90f), cut, size*.075f);
        dl.AddRectFilled(P(.45f,.32f), P(.59f,.58f), ink, size*.025f);
        dl.AddCircleFilled(P(.52f,.43f), size*.035f, cut, 12);
        dl.AddLine(P(.52f,.43f), P(.52f,.52f), cut, size*.025f);
    }

    internal static void People(Vector2 origin, float size, Vector4 color)
    {
        // Plugin-owned branding; no font icons or shared-library plugin assets.
        var dl = ImGui.GetWindowDrawList();
        var ink = MaterialCanvas.Color(color);
        dl.AddCircleFilled(origin + new Vector2(.36f, .24f) * size, .16f * size, ink, 24);
        dl.AddCircleFilled(origin + new Vector2(.78f, .34f) * size, .12f * size, ink, 24);
        dl.AddRectFilled(origin + new Vector2(.12f, .45f) * size, origin + new Vector2(.60f, .91f) * size, ink, size * .15f);
        dl.AddRectFilled(origin + new Vector2(.65f, .52f) * size, origin + new Vector2(.97f, .91f) * size, ink, size * .11f);
    }

    internal static void Person(Vector2 origin, float size, Vector4 color)
    {
        var dl = ImGui.GetWindowDrawList();
        var ink = MaterialCanvas.Color(color);
        dl.AddCircleFilled(origin + new Vector2(.5f, .24f) * size, .2f * size, ink, 24);
        dl.AddRectFilled(origin + new Vector2(.12f, .48f) * size, origin + new Vector2(.88f, .94f) * size, ink, size * .2f);
    }

    internal static void Flag(Vector2 origin, float size, Vector4 color)
    {
        var dl = ImGui.GetWindowDrawList();
        var ink = MaterialCanvas.Color(color);
        dl.AddLine(origin + new Vector2(.15f, .05f) * size, origin + new Vector2(.15f, .96f) * size, ink, size * .08f);
        dl.AddTriangleFilled(origin + new Vector2(.2f, .1f) * size, origin + new Vector2(.92f, .25f) * size, origin + new Vector2(.2f, .58f) * size, ink);
    }
}
