using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace LootGoblin.Windows;

// Native widgets receive their original English labels/IDs. Only their visible label is painted in the selected locale.
// This also preserves English-derived helper IDs and existing saved window identities.
internal static class UiGui
{
    private static string Visible(string label) => label.Split("##", 2)[0];
    internal static float ButtonWidth(string label, MaterialIcon icon = MaterialIcon.None, string? display = null)
        => MaterialText.Measure(display ?? UiText.T(Visible(label))).X + ImGui.GetStyle().FramePadding.X * 2
            + (icon == MaterialIcon.None ? 0 : 28 * MaterialTheme.Metrics.Scale);
    internal static float CheckboxWidth(string label)
        => ImGui.GetFrameHeight() + (Visible(label).Length == 0 ? 0 : ImGui.GetStyle().ItemInnerSpacing.X + MaterialText.Measure(UiText.T(Visible(label))).X);
    internal static void TextUnformatted(string text) => MaterialText.Text(UiText.T(text));
    private static float WrapPosition()
    {
        var window = ImGuiP.GetCurrentWindow();
        var paneRight = window.InnerRect.Max.X - ImGui.GetWindowPos().X - ImGui.GetStyle().WindowPadding.X;
        return Math.Max(ImGui.GetCursorPosX() + 80 * MaterialTheme.Metrics.Scale,
            Math.Min(paneRight, ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X));
    }
    internal static void TextWrapped(string text)
    {
        ImGui.PushTextWrapPos(WrapPosition());
        try { MaterialText.Text(UiText.T(text)); }
        finally { ImGui.PopTextWrapPos(); }
    }
    internal static void TextDisabled(string text) => TextColored(MaterialTheme.Current.Colors.OnSurfaceVariant, text);
    internal static void Text(string text) => MaterialText.Text(UiText.T(text));
    internal static void BulletText(string text)
    {
        ImGui.BeginGroup();
        try
        {
            ImGui.Bullet();
            ImGui.SameLine();
            TextWrapped(text);
        }
        finally { ImGui.EndGroup(); }
    }
    internal static void TextColored(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(WrapPosition());
        try { MaterialText.TextColored(color, UiText.T(text)); }
        finally { ImGui.PopTextWrapPos(); }
    }
    internal static void SetTooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 440 * MaterialTheme.Metrics.Scale);
        try { MaterialText.Text(UiText.T(text)); }
        finally { ImGui.PopTextWrapPos(); ImGui.EndTooltip(); }
    }
    internal static bool BeginCombo(string label, string preview, ImGuiComboFlags flags = ImGuiComboFlags.None, bool translatePreview = true)
    {
        var translated = translatePreview ? UiText.T(preview) : preview;
        using var height = MaterialText.PushLineHeight(translated, UiText.T(Visible(label)));
        var width = FitField(label, Math.Max(80 * MaterialTheme.Metrics.Scale,
            MaterialText.Measure(translated).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2));
        var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        var window = ImGuiP.GetCurrentWindow(); var previousMax = window.DC.CursorMaxPos;
        var clipped = ClipFieldLabel(label, min, width, draw);
        var open = false;
        try
        {
            try { open = MaterialText.BeginCombo(label, translated, flags); }
            finally { if (clipped) draw.PopClipRect(); }
            FieldLabel(label, min, width, draw, window, previousMax);
            return open;
        }
        catch { if (open) ImGui.EndCombo(); throw; }
    }
    internal static bool Combo(string label, ref int value, string options)
    {
        var items = options.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return Combo(label, ref value, items, items.Length);
    }
    internal static bool BeginTabItem(string label, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
    {
        var display = UiText.T(label);
        using var height = MaterialText.PushLineHeight(display);
        var pad = ImGui.GetStyle().FramePadding;
        var foreground = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(pad.X + Math.Max(0, (MaterialText.Measure(display).X - MaterialText.Measure(label).X) * .5f), pad.Y));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var open = ImGui.BeginTabItem(label, flags);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        foreground.W *= ImGui.GetStyle().Alpha;
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min, max, true);
        try { MaterialText.AddText(dl, min + (max - min - MaterialText.Measure(display)) * .5f, ImGui.ColorConvertFloat4ToU32(foreground), display); }
        catch { if (open) ImGui.EndTabItem(); throw; }
        finally { dl.PopClipRect(); }
        return open;
    }
    internal static bool RadioButton(string label, bool active)
    {
        var display = UiText.T(label);
        using var height = MaterialText.PushLineHeight(display);
        var gap = ImGui.GetStyle().ItemInnerSpacing;
        var foreground = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        MaterialLayout.FitNextItemWidth(0, ImGui.GetFrameHeight() + gap.X + MaterialText.Measure(display).X);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(gap.X + MaterialText.Measure(display).X - MaterialText.Measure(label).X, gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var clicked = ImGui.RadioButton(label, active);
        ImGui.PopStyleColor(); ImGui.PopStyleVar();
        foreground.W *= ImGui.GetStyle().Alpha;
        MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new Vector2(ImGui.GetFrameHeight() + gap.X, ImGui.GetStyle().FramePadding.Y), ImGui.ColorConvertFloat4ToU32(foreground), display);
        return clicked;
    }
    internal static bool Button(string label, Vector2 size, MaterialIcon icon = MaterialIcon.None, string? display = null)
    {
        var translated = display ?? UiText.T(Visible(label));
        using var controls = ImGui.GetStyle().FramePadding.Y == 0 || MaterialControls.Context == MaterialControlContext.Dense
            ? default(MaterialControls.ControlScope) : MaterialControls.Push(MaterialControlContext.Toolbar);
        var padding = ImGui.GetStyle().FramePadding;
        using var height = MaterialText.PushLineHeight(translated);
        var foreground = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var iconWidth = icon == MaterialIcon.None ? 0 : 28 * MaterialTheme.Metrics.Scale;
        size.X = MaterialLayout.FitNextItemWidth(size.X, Math.Max(size.X > 0 ? size.X : 0, ButtonWidth(label, icon, translated)));
        var textSize = MaterialText.Measure(translated);
        size.Y = Math.Max(size.Y, Math.Max(ImGui.GetTextLineHeight(), textSize.Y) + padding.Y * 2);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor();
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(min, max, true);
        try
        {
        foreground.W *= ImGui.GetStyle().Alpha;
        var content = min + new Vector2(Math.Max(padding.X, (max.X - min.X - textSize.X - iconWidth) * .5f), (max.Y - min.Y - textSize.Y) * .5f);
        MaterialText.AddText(dl, ImGui.GetFont(), ImGui.GetFontSize(), content + new Vector2(iconWidth, 0),
            ImGui.ColorConvertFloat4ToU32(foreground), translated);
        if (icon != MaterialIcon.None)
            MaterialIcons.Draw(icon, new Vector2(content.X, min.Y + (max.Y - min.Y - 20 * MaterialTheme.Metrics.Scale) * .5f), 20 * MaterialTheme.Metrics.Scale,
                icon == MaterialIcon.Heart ? new Vector4(1f, .42f, .46f, foreground.W) : foreground);
        }
        finally { dl.PopClipRect(); }
        return clicked;
    }

    // Native controls retain the original ID, hit testing, focus and keyboard behavior.
    internal static bool HeaderToggle(string original, ref bool value)
    {
        var s = MaterialTheme.Metrics.Scale;
        var c = MaterialTheme.Current.Colors;
        var text = UiText.T(original);
        var width = MaterialText.Measure(text).X + 52 * s;
        var height = Math.Max(28 * s, MaterialText.Measure(text).Y + 4 * s);
        MaterialLayout.FitNextItemWidth(0, width);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, Math.Max(0, (height - ImGui.GetTextLineHeight()) * .5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(width - ImGui.GetFrameHeight() - MaterialText.Measure(original).X, 0));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Vector4.Zero);
        var changed = ImGui.Checkbox(original, ref value);
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
        var min = ImGui.GetItemRectMin();
        var track = min + new Vector2(width - 44 * s, Math.Max(0, (height - 26 * s) * .5f));
        var fill = value ? c.Primary : c.Outline;
        if (ImGui.IsItemHovered()) fill = MaterialColor.Layer(fill, c.OnSurface, .08f);
        if (ImGui.IsItemActive()) fill = MaterialColor.Layer(fill, c.OnSurface, .12f);
        var foreground = c.OnSurface;
        fill.W *= ImGui.GetStyle().Alpha;
        foreground.W *= ImGui.GetStyle().Alpha;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(track, track + new Vector2(44, 26) * s, MaterialCanvas.Color(fill), 13 * s);
        dl.AddCircleFilled(track + new Vector2(value ? 31 : 13, 13) * s, 10 * s, MaterialCanvas.Color(foreground), 24);
        if (ImGui.IsItemFocused()) dl.AddRect(min, min + new Vector2(width, height), MaterialCanvas.Color(c.Primary), 4 * s);
        MaterialText.AddText(dl, min + new Vector2(0, Math.Max(0, (height - MaterialText.Measure(text).Y) * .5f)), MaterialCanvas.Color(foreground), text);
        return changed;
    }

    internal static bool SliderInt(string label, ref int value, int min, int max)
    {
        using var height = MaterialText.PushLineHeight(UiText.T(Visible(label)));
        var width = FitField(label, Math.Max(80 * MaterialTheme.Metrics.Scale,
            Math.Max(MaterialText.Measure(min.ToString(CultureInfo.InvariantCulture)).X, MaterialText.Measure(max.ToString(CultureInfo.InvariantCulture)).X)
            + ImGui.GetStyle().FramePadding.X * 2));
        var origin = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var clipped = ClipFieldLabel(label, origin, width, draw);
        var changed = ImGui.SliderInt(label, ref value, min, max);
        if (clipped) draw.PopClipRect();
        FieldLabel(label, origin, width, draw, window, previousMax);
        return changed;
    }
    internal static bool SliderFloat(string label, ref float value, float min, float max, string format = "%.3f")
    {
        using var height = MaterialText.PushLineHeight(UiText.T(Visible(label)));
        var width = FitField(label, FloatMinimum(value, 0));
        var origin = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var clipped = ClipFieldLabel(label, origin, width, draw);
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        if (clipped) draw.PopClipRect();
        FieldLabel(label, origin, width, draw, window, previousMax);
        return changed;
    }
    internal static bool CollapsingHeader(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None, MaterialIcon icon = MaterialIcon.None)
    {
        using var font = UiText.Font(MaterialControls.Metrics.Height <= 32 * MaterialTheme.Metrics.Scale ? UiFontRole.BodyStrong : UiFontRole.PluginName);
        using var shapedHeight = MaterialText.PushLineHeight(UiText.T(label));
        var padding = ImGui.GetStyle().FramePadding;
        var requiredHeight = MaterialText.RequiresShaping(UiText.T(label))
            ? Math.Max(MaterialControls.Metrics.Height, MaterialText.Measure(UiText.T(label)).Y + padding.Y * 2) : MaterialControls.Metrics.Height;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(padding.X, Math.Max(0, (requiredHeight - ImGui.GetTextLineHeight()) * .5f)));
        try
        {
        var position = ImGui.GetCursorScreenPos();
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        if (icon != MaterialIcon.None)
        {
            ImGui.PushStyleColor(ImGuiCol.Header, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        }
        var open = ImGui.CollapsingHeader(label, flags);
        if (icon != MaterialIcon.None) { ImGui.PopStyleColor(); ImGui.PopStyleVar(); }
        ImGui.PopStyleColor();
        var c = MaterialTheme.Current.Colors;
        var foreground = c.OnSurface;
        foreground.W *= ImGui.GetStyle().Alpha;
        var dl = ImGui.GetWindowDrawList();
        var scale = MaterialTheme.Metrics.Scale;
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        var textX = position.X + ImGui.GetStyle().FramePadding.X;
        dl.PushClipRect(min, max, true);
        try
        {
        if (icon != MaterialIcon.None)
        {
            MaterialIcons.Draw(icon, new Vector2(textX, min.Y + (max.Y - min.Y - 24 * scale) * .5f), 24 * scale, c.Primary);
            textX += 36 * scale;
        }
        else textX += ImGui.GetFontSize() + ImGui.GetStyle().FramePadding.X;
        MaterialText.AddText(dl, new Vector2(textX, position.Y + ImGui.GetStyle().FramePadding.Y), MaterialCanvas.Color(foreground), UiText.T(label));
        MaterialIcons.Draw(open ? MaterialIcon.ChevronDown : MaterialIcon.ArrowRight,
            icon == MaterialIcon.None ? position + ImGui.GetStyle().FramePadding : new Vector2(max.X - 24 * scale, min.Y + (max.Y - min.Y - 18 * scale) * .5f),
            icon == MaterialIcon.None ? ImGui.GetFontSize() : 18 * scale, foreground);
        if (open && icon != MaterialIcon.None)
            dl.AddRectFilled(new Vector2(min.X, max.Y - scale), max, MaterialCanvas.Color(c.Primary));
        }
        finally { dl.PopClipRect(); }
        if (textX + MaterialText.Measure(UiText.T(label)).X + 28 * scale > max.X && ImGui.IsItemHovered()) SetTooltip(label);
        return open;
        }
        finally { ImGui.PopStyleVar(); }
    }
    private static void Label(string original,Vector2 position,Vector4 background,Vector4 foreground,Vector2? clip=null,string? display=null)
    {
        var visible=original.Split("##",2)[0];
        var translated=display ?? UiText.T(visible);
        if(translated==visible) return;
        var dl=ImGui.GetWindowDrawList();
        var width=Math.Max(MaterialText.Measure(visible).X,MaterialText.Measure(translated).X);
        if(clip is { } max) dl.PushClipRect(new Vector2(Math.Max(ImGuiP.GetCurrentWindow().ClipRect.Min.X,position.X-ImGui.GetStyle().FramePadding.X),position.Y),max,true);
        try
        {
        dl.AddRectFilled(position,position+new Vector2(width,ImGui.GetTextLineHeight()),ImGui.ColorConvertFloat4ToU32(background));
        foreground.W*=ImGui.GetStyle().Alpha;
        MaterialText.AddText(dl, position,ImGui.ColorConvertFloat4ToU32(foreground),translated);
        }
        finally { if(clip.HasValue) dl.PopClipRect(); }
    }
    internal static bool Button(string label,string? display=null)
    {
        var translated=display ?? UiText.T(label.Split("##",2)[0]);
        using var controls = ImGui.GetStyle().FramePadding.Y == 0 || MaterialControls.Context == MaterialControlContext.Dense
            ? default(MaterialControls.ControlScope) : MaterialControls.Push(MaterialControlContext.Toolbar);
        using var height = MaterialText.PushLineHeight(translated);
        var width=MaterialText.Measure(translated).X+2*ImGui.GetStyle().FramePadding.X;
        width = MaterialLayout.FitNextItemWidth(0, width);
        var foreground=ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        var clicked=ImGui.Button(label,new Vector2(width,0));
        ImGui.PopStyleColor();
        var min=ImGui.GetItemRectMin(); var max=ImGui.GetItemRectMax();
        foreground.W*=ImGui.GetStyle().Alpha;
        ImGui.GetWindowDrawList().PushClipRect(min,max,true);
        try { MaterialText.AddText(ImGui.GetWindowDrawList(), min+(max-min-MaterialText.Measure(translated))*.5f,ImGui.ColorConvertFloat4ToU32(foreground),translated); }
        finally { ImGui.GetWindowDrawList().PopClipRect(); }
        return clicked;
    }
    internal static bool SmallButton(string label,string? display=null)
    {
        // Native small buttons use the same ID and behavior with zero vertical padding.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,new Vector2(ImGui.GetStyle().FramePadding.X,0));
        try { return Button(label,display); }
        finally { ImGui.PopStyleVar(); }
    }
    internal static bool Checkbox(string label,ref bool value)
    {
        var visible=label.Split("##",2)[0];
        var translated=UiText.T(visible);
        using var height = MaterialText.PushLineHeight(translated);
        var foreground=ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var gap=ImGui.GetStyle().ItemInnerSpacing;
        // Native Checkbox sizes its hit area from the original label. Adjust that size for the
        // translated ink while keeping the native widget and its original ID.
        MaterialLayout.FitNextItemWidth(0, CheckboxWidth(label));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing,new Vector2(gap.X+MaterialText.Measure(translated).X-MaterialText.Measure(visible).X,gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        var changed=ImGui.Checkbox(label,ref value);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        var p=ImGui.GetItemRectMin()+new Vector2(ImGui.GetFrameHeight()+gap.X,ImGui.GetStyle().FramePadding.Y);
        foreground.W*=ImGui.GetStyle().Alpha;
        MaterialText.AddText(ImGui.GetWindowDrawList(), p,ImGui.ColorConvertFloat4ToU32(foreground),translated);
        return changed;
    }
    internal static bool Selectable(string original,bool selected = false,string? display=null)
    {
        var translated=display ?? UiText.T(original.Split("##",2)[0]);
        var origin=ImGui.GetCursorScreenPos();
        var width=Math.Max(ImGui.GetContentRegionAvail().X, MaterialText.Measure(translated).X);
        var height=MaterialText.RequiresShaping(translated) ? Math.Max(ImGui.GetTextLineHeight(), MaterialText.Measure(translated).Y) : ImGui.GetTextLineHeight();
        var foreground=ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
        bool clicked;
        try { clicked=ImGui.Selectable(original,selected,ImGuiSelectableFlags.None,new Vector2(width,height)); }
        finally { ImGui.PopStyleColor(); }
        foreground.W*=ImGui.GetStyle().Alpha;
        var dl=ImGui.GetWindowDrawList();
        var clip = ImGuiP.GetCurrentWindow().ClipRect;
        dl.PushClipRect(clip.Min,clip.Max,true);
        try { MaterialText.AddText(dl, origin,ImGui.ColorConvertFloat4ToU32(foreground),translated); }
        finally { dl.PopClipRect(); }
        if(MaterialText.Measure(translated).X>width && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
        return clicked;
    }
    private static float FitField(string label, float? minimumPixels = null)
    {
        var requested = ImGui.CalcItemWidth();
        var minimum = minimumPixels ?? Math.Max(80 * MaterialTheme.Metrics.Scale,
            MaterialText.Measure("0000000000").X + ImGui.GetStyle().FramePadding.X * 2);
        var translated = UiText.T(Visible(label));
        var labelWidth = translated.Length == 0 ? 0 : MaterialText.Measure(translated).X + ImGui.GetStyle().ItemInnerSpacing.X;
        var total = MaterialLayout.FitNextItemWidth(requested + labelWidth, minimum + labelWidth);
        var width = MathF.Ceiling(Math.Max(minimum, total - labelWidth));
        ImGui.SetNextItemWidth(width);
        return width;
    }
    private static bool ClipFieldLabel(string label, Vector2 min, float width, ImDrawListPtr draw)
    {
        if (UiText.T(Visible(label)) == Visible(label)) return false;
        var window = ImGui.GetWindowPos();
        draw.PushClipRect(new Vector2(min.X, window.Y), new Vector2(min.X + width, window.Y + ImGui.GetWindowSize().Y), true);
        return true;
    }
    private static void FieldLabel(string label, Vector2 min, float width, ImDrawListPtr draw, ImGuiWindowPtr window, Vector2 previousMax)
    {
        var visible = Visible(label); var translated = UiText.T(visible);
        if (translated == visible || visible.Length == 0) return;
        var position = min + new Vector2(width + ImGui.GetStyle().ItemInnerSpacing.X, ImGui.GetStyle().FramePadding.Y);
        MaterialText.AddText(draw, position, ImGui.GetColorU32(ImGuiCol.Text), translated);
        var right = position.X + MaterialText.Measure(translated).X;
        window.DC.CursorMaxPos = new Vector2(Math.Max(previousMax.X, right), window.DC.CursorMaxPos.Y);
        window.DC.CursorPosPrevLine = new Vector2(right, window.DC.CursorPosPrevLine.Y);
    }
    internal static float IntMinimum(int value, int step = 0)
        => MathF.Ceiling(Math.Max(80 * MaterialTheme.Metrics.Scale,
            MaterialText.Measure(value.ToString(CultureInfo.InvariantCulture).Length > 6 ? value.ToString(CultureInfo.InvariantCulture) : "-00000").X
            + ImGui.GetStyle().FramePadding.X * 2) + (step > 0 ? 2 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X) : 0));
    private static float FloatMinimum(float value, float step)
        => MathF.Ceiling(Math.Max(80 * MaterialTheme.Metrics.Scale,
            MaterialText.Measure(value.ToString("F3", CultureInfo.InvariantCulture).Length > 9 ? value.ToString("F3", CultureInfo.InvariantCulture) : "-0000.000").X
            + ImGui.GetStyle().FramePadding.X * 2) + (step > 0 ? 2 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X) : 0));
    internal static bool InputText(string label,ref string value,int length, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        using var height = MaterialText.PushLineHeight(value, UiText.T(Visible(label)));
        var width = FitField(label); var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var clipped = ClipFieldLabel(label, min, width, draw);
        bool changed;
        try { changed = MaterialShapedInput.SingleLine(label, string.Empty, ref value, length, flags); }
        finally { if (clipped) draw.PopClipRect(); }
        FieldLabel(label, min, width, draw, window, previousMax); return changed;
    }
    internal static bool InputInt(string label,ref int value, int step = 0, int fastStep = 0)
    {
        using var height = MaterialText.PushLineHeight(UiText.T(Visible(label)));
        var width = FitField(label, IntMinimum(value, step)); var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var clipped = ClipFieldLabel(label, min, width, draw);
        var changed = ImGui.InputInt(label, ref value, step, fastStep);
        if (clipped) draw.PopClipRect();
        FieldLabel(label, min, width, draw, window, previousMax); return changed;
    }
    internal static bool InputFloat(string label,ref float value, float step = 0, float fastStep = 0, string format = "%.3f", ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        using var height = MaterialText.PushLineHeight(UiText.T(Visible(label)));
        var width = FitField(label, FloatMinimum(value, step)); var min = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var clipped = ClipFieldLabel(label, min, width, draw);
        var changed = ImGui.InputFloat(label, ref value, step, fastStep, format, flags);
        if (clipped) draw.PopClipRect();
        FieldLabel(label, min, width, draw, window, previousMax); return changed;
    }
    internal static bool Combo(string label,ref int value,string[] options,int count)
    {
        var changed=false;
        if(BeginCombo(label,value>=0 && value<count?options[value]:""))
        {
            try
            {
            for(var index=0;index<count;index++)
            {
                ImGui.PushID(index);
                try
                {
                    if(Selectable(options[index],value==index)) { changed=value!=index;value=index; }
                    if(value==index) ImGui.SetItemDefaultFocus();
                }
                finally { ImGui.PopID(); }
            }
            }
            finally { ImGui.EndCombo(); }
        }
        return changed;
    }
    internal static void Title(string original,string translated)
        => Title(original, translated, MaterialIcon.None);
    internal static void Title(string original,string translated,MaterialIcon icon)
        => PaintTitle(original, translated, icon, null);
    internal static unsafe void ImageTitle(Window owner, string visibleTitle,
        Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap icon)
    {
        var window = ImGuiP.FindWindowByName(owner.WindowName);
        if (window.Handle == null) return;
        var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
        if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
        var extraRight = count * (ImGuiP.CalcFontSize(window) + ImGui.GetStyle().ItemInnerSpacing.X);
        using var font = UiText.Font(UiFontRole.Body);
        MaterialWindowHeader.PaintTitle(window, visibleTitle, icon.Handle, icon.Size, extraRight, owner.ShowCloseButton);
    }

    internal static void TitleWithButtons(string original, string translated, Window owner)
        => PaintTitle(original, translated, MaterialIcon.None, owner);

    internal static void ReserveTitleSpace(Window owner, string visible, float minimumWidth)
    {
        var style = ImGui.GetStyle();
        var fontSize = ImGui.GetFontSize();
        var collapse = (owner.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0
            && style.WindowMenuButtonPosition != ImGuiDir.None;
        var controls = AdditionalTitleButtonWidth(owner, fontSize)
            + ((owner.ShowCloseButton ? 1 : 0) + (collapse ? 1 : 0)) * (fontSize + style.ItemInnerSpacing.X);
        using var font = UiText.Font(UiFontRole.Body);
        var required = (MaterialText.Measure(visible).X * fontSize / ImGui.GetFontSize()
            + fontSize + controls + style.FramePadding.X * 2 + style.ItemInnerSpacing.X * 2) / ImGui.GetIO().FontGlobalScale;
        var bounds = owner.SizeConstraints ?? new WindowSizeConstraints();
        bounds.MinimumSize = new(Math.Max(minimumWidth, required), bounds.MinimumSize.Y);
        owner.SizeConstraints = bounds;
    }

    private static float AdditionalTitleButtonWidth(Window owner, float fontSize)
    {
        var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
        if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
        return count * (fontSize + ImGui.GetStyle().ItemInnerSpacing.X);
    }

    private static void PaintTitle(string original, string translated, MaterialIcon icon, Window? owner)
    {
        var s=ImGui.GetStyle(); var size=ImGui.GetFontSize();var height=ImGui.GetFrameHeight();
        var window=ImGuiP.GetCurrentWindow();
        var flags=window.Flags;
        var collapseOnLeft=(flags & (ImGuiWindowFlags.NoCollapse|ImGuiWindowFlags.Modal))==0 && s.WindowMenuButtonPosition==ImGuiDir.Left;
        var position=ImGui.GetWindowPos()+new Vector2(s.FramePadding.X+(collapseOnLeft?size+s.ItemInnerSpacing.X:0),s.FramePadding.Y);
        var originalWidth=MaterialText.Measure(original).X;
        using var font=UiText.Font(UiFontRole.Body);
        var translatedWidth=MaterialText.Measure(translated).X*size/ImGui.GetFontSize();
        var iconWidth=icon==MaterialIcon.None?0:size+s.ItemInnerSpacing.X;
        var dl=ImGui.GetWindowDrawList();
        var rightButtons = (owner == null || owner.ShowCloseButton ? size : 0) + s.FramePadding.X * 2
            + (owner == null ? 0 : AdditionalTitleButtonWidth(owner, size));
        if ((flags & ImGuiWindowFlags.NoCollapse) == 0 && s.WindowMenuButtonPosition == ImGuiDir.Right)
            rightButtons += size + s.ItemInnerSpacing.X;
        var titleMin=Vector2.Max(window.OuterRectClipped.Min,new Vector2(ImGui.GetWindowPos().X,position.Y));
        var titleMax=Vector2.Min(window.OuterRectClipped.Max,ImGui.GetWindowPos()+new Vector2(Math.Max(0, ImGui.GetWindowSize().X-rightButtons),height));
        dl.PushClipRect(titleMin,Vector2.Max(titleMin,titleMax),false);
        try
        {
        var bg=s.Colors[(int)(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)?ImGuiCol.TitleBgActive:ImGuiCol.TitleBg)];
        dl.AddRectFilled(position,position+new Vector2(Math.Max(originalWidth,translatedWidth+iconWidth),height-s.FramePadding.Y),ImGui.ColorConvertFloat4ToU32(bg));
        if(icon!=MaterialIcon.None) MaterialIcons.Draw(icon,position,size,MaterialTheme.Current.Colors.Secondary);
        MaterialText.AddText(dl, ImGui.GetFont(),size,position+new Vector2(iconWidth,0),ImGui.ColorConvertFloat4ToU32(s.Colors[(int)ImGuiCol.Text]),translated);
        }
        finally { dl.PopClipRect(); }
    }
    internal static void TableHeadersRow(float height=0)
    {
        var headers = new string[ImGui.TableGetColumnCount()];
        for (var index = 0; index < headers.Length; index++) headers[index] = UiText.T(ImGui.TableGetColumnName(index));
        using var lineHeight = MaterialText.PushLineHeight(headers);
        if (headers.Any(MaterialText.RequiresShaping)) height = Math.Max(height,
            headers.Max(value => MaterialText.Measure(value).Y) + 2 * ImGui.GetStyle().CellPadding.Y);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers,height);
        for(var index=0;index<ImGui.TableGetColumnCount();index++)
        {
            if(!ImGui.TableSetColumnIndex(index)) continue;
            var original=ImGui.TableGetColumnName(index);
            var position=ImGui.GetCursorScreenPos();
            var available=ImGui.GetContentRegionAvail().X;
            var translated=UiText.T(original);
            if (MaterialText.RequiresShaping(translated))
            {
                MaterialText.TableHeader(original, translated);
                if (MaterialText.Measure(translated).X>available-16*MaterialTheme.Metrics.Scale && ImGui.IsItemHovered())
                    MaterialText.SetTooltip(translated);
                continue;
            }
            if(translated!=original) ImGui.PushStyleColor(ImGuiCol.Text,Vector4.Zero);
            ImGui.TableHeader(original);
            if(translated!=original) ImGui.PopStyleColor();
            Label(original,position,ImGui.GetStyle().Colors[(int)ImGuiCol.TableHeaderBg],ImGui.GetStyle().Colors[(int)ImGuiCol.Text], position + new Vector2(Math.Max(1, available), Math.Max(ImGui.GetTextLineHeight(), MaterialText.Measure(translated).Y)));
            if(translated!=original && MaterialText.Measure(translated).X>available-16*AethertekUI.MaterialTheme.Metrics.Scale && ImGui.IsItemHovered())
                MaterialText.SetTooltip(translated);
        }
    }
    internal static unsafe void EnsureColumnMinimum(int column, float minimum)
    {
        // Keep wider saved widths; expand only a column that cannot fit its measured control/header.
        if (ImGuiP.GetCurrentTable().Columns.Data[column].WidthRequest < minimum)
            ImGuiP.TableSetColumnWidth(column, minimum);
    }
}
