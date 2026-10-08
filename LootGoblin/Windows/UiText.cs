using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using AethertekUI;

namespace LootGoblin.Windows;

internal sealed class UiText : IDisposable
{
    [ThreadStatic] private static UiText? current;
    internal static UiText Current => current ?? throw new InvalidOperationException("Enter the LootGoblin UI frame before drawing.");
    internal static readonly (string Code,string Name)[] Languages=[("en","English"),("de","Deutsch"),("fr","Français"),
        ("es","Español"),("it","Italiano"),("ru","Русский"),("ja","日本語"),("ko","한국어"),("zh-Hans","简体中文"),
        ("vi","Tiếng Việt"),("pt-BR","Português (Brasil)"),("id","Bahasa Indonesia"),("pl","Polski"),("tr","Türkçe"),("hi","हिन्दी")];
    internal static IEnumerable<string> CjkLanguages(string selected) => new[]{"ja","ko","zh-Hans"}.OrderBy(code=>code==selected?0:1);
    private readonly ResourceManager manager;
    internal ResourceSet Resources { get; }
    internal CultureInfo Culture { get; }
    internal string Language { get; }
    private readonly Func<UiFontRole,IDisposable> pushFont;
    internal string[] RequiredText { get; }
    private readonly (string Key, string[] Parts, int[] ArgumentIndexes, int ArgumentCount, int MinimumLength)[] messageTemplates;
    internal UiText(string language, Func<UiFontRole,IDisposable> pushFont)
    {
        Language=Languages.Any(l=>l.Code==language)?language:"en";
        Culture=CultureInfo.GetCultureInfo(Language);
        manager=new ResourceManager("LootGoblin.Localization.Strings_"+Language.Replace('-','_'),typeof(UiText).Assembly);
        Resources=manager.GetResourceSet(CultureInfo.InvariantCulture,true,false) ?? throw new MissingManifestResourceException(Language);
        this.pushFont=pushFont;
        var englishManager = new ResourceManager("LootGoblin.Localization.Strings_en", typeof(UiText).Assembly);
        var english = englishManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)
            ?? throw new MissingManifestResourceException("en");
        RequiredText = Values(Resources).Concat(Values(english)).Concat(Languages.Where(l => l.Code != "hi").Select(l => l.Name))
            .Append("⚠•—").Append(Culture.NumberFormat.NumberGroupSeparator).Distinct().ToArray();
        englishManager.ReleaseAllResources();
        // Service messages remain English in logs; only their UI copies are localized.
        var parameter = new Regex(@"\{(\d+)(?::([^}]+))?\}");
        messageTemplates = Resources.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)
            .Where(key => parameter.IsMatch(key) && parameter.Replace(key, "").Any(char.IsLetter)
                && key is not "{0}m" and not "{0}h {1:D2}m" and not "{0}m{1:D2}s")
            .OrderByDescending(key => parameter.Replace(key, "").Length).Select(key =>
            {
                var patternKey = key.Trim();
                var parts = new List<string>();
                var argumentIndexes = new List<int>();
                var offset = 0;
                var holes = parameter.Matches(patternKey);
                foreach (Match hole in holes)
                {
                    parts.Add(patternKey[offset..hole.Index]);
                    argumentIndexes.Add(int.Parse(hole.Groups[1].Value, CultureInfo.InvariantCulture));
                    offset = hole.Index + hole.Length;
                }
                parts.Add(patternKey[offset..]);
                return (key, parts.ToArray(), argumentIndexes.ToArray(), argumentIndexes.Max() + 1, parts.Sum(part => part.Length));
            }).ToArray();
    }
    internal static string T(string english)
    {
        if (Current.Resources.GetString(english, true) is { } exact) return exact;
        foreach (var template in Current.messageTemplates)
        {
            // The previous single-line regex's end anchor also accepted a final newline.
            var length = english.Length > 0 && english[^1] == '\n' ? english.Length - 1 : english.Length;
            if (length < template.MinimumLength || !english.StartsWith(template.Parts[0], StringComparison.Ordinal)
                || !english.AsSpan(0, length).EndsWith(template.Parts[^1], StringComparison.Ordinal)) continue;
            var end = length - template.Parts[^1].Length;
            var offset = template.Parts[0].Length;
            // Captured service values are already formatted; preserve names and leading zeroes.
            var args = Enumerable.Repeat<object>(string.Empty, template.ArgumentCount).ToArray();
            var matched = true;
            for (var index = 0; index < template.ArgumentIndexes.Length; index++)
            {
                var next = index == template.ArgumentIndexes.Length - 1 ? end
                    : english.IndexOf(template.Parts[index + 1], offset, end - offset, StringComparison.Ordinal);
                if (next < 0) { matched = false; break; }
                args[template.ArgumentIndexes[index]] = english[offset..next];
                offset = next + template.Parts[index + 1].Length;
            }
            if (!matched) continue;
            // These arguments come from EmptorIPC.GetScopeLabel, rather than game data.
            if (template.Key is "Requesting {0} Emptor price hint(s) for {1}..."
                or "Loaded {0} Emptor price hint(s) for {1}."
                or "Loaded {0} Emptor price hint(s) for {1}; {2} unavailable.")
                args[1] = T((string)args[1]);
            else if (template.Key == "Emptor returned no {0} price level.")
                args[0] = T((string)args[0]);
            // These slots carry this plugin's authored integration-status messages.
            else if (template.Key is "Skipping market purchase for map {0}: {1}"
                or "Cannot gather {0}: {1}"
                or "Cannot gather {0}: {1}."
                or "Gathering {0}: {1}"
                or "Could not gather {0}: {1}"
                or "Waiting to gather {0}: {1}"
                or "Could not retrieve {0} from saddlebag: {1}"
                or "Could not move retainer map from {0}: {1}"
                or "Could not plan retainer map retrieval from {0}: {1}"
                or "Gathered {0}, but could not switch back to combat job: {1}"
                or "Treasure map key item '{0}' is active after error '{1}', but no AgentMap flag, TreasureSpot capture, or cached target is available. Manual intervention required."
                or "Teleport to {0} failed: {1}"
                or "Could not return to {0}: {1}"
                or "Could not teleport to {0} for ADS repair: {1}"
                or "Teleport to {0} for ADS repair failed: {1}"
                or "Error #{0} (recovered): {1}"
                or "Error #{0}: {1}")
                args[1] = T((string)args[1]);
            else if (template.Key is "Lifestream is required for off-world map purchasing: {0}"
                or "Could not retrieve retainer map: {0}"
                or "Navigation error: {0}"
                or "Could not switch to configured combat job: {0}"
                or "Could not switch to gather job: {0}"
                or "Party cannot be safely disbanded for world travel: {0}"
                or "Could not plan safe saddlebag storage: {0}"
                or "The guarded saddlebag move was not issued: {0}"
                or "InventoryBuddy closed before move confirmation: {0}"
                or "{0} Could not read equipped gear durability."
                or "{0} Manual world return and party restoration may be required."
                or "Market batch warning: {0} Continuing with {1} secured {2} map(s)."
                or "An additional batch step failed: {0}"
                or "{0} Retrying ADS repair in {1:F1}s ({2}/{3})..."
                or "{0} Durability {1}%/{2}%. Retrying in {3:F1}s ({4}/{5})...")
                args[0] = T((string)args[0]);
            else if (template.Key is "ADS unloaded before repair retry. {0}"
                or "Map allowance: {0}"
                or "Map allowance status unavailable: {0}"
                or "Timeout in state {0} after {1}s."
                or "{0} timed out for {1}."
                or "FATE/combat cleared - resuming portal after {0}..."
                or "FATE/combat cleared - resuming captured portal after {0}...")
                args[0] = T((string)args[0]);
            else if (template.Key == "Prepared {0} of {1} planned {2} map(s). {3}")
                args[3] = T((string)args[3]);
            else if (template.Key == "Lowest durability {0}%, threshold {1}%, ADS mode {2}, ADS {3}, retry count {4}/{5}, territory {6}.")
            {
                args[2] = T((string)args[2]);
                args[3] = T((string)args[3]);
            }
            else if (template.Key is "{0} Lowest durability {1}%, threshold {2}%, ADS mode {3}, ADS {4}, retry count {5}/{6}, territory {7}."
                or "{0} Lowest durability {1}%, threshold {2}%, ADS mode {3}, ADS {4}, retry count {5}/{6}, territory {7}. ADS is not loaded.")
            {
                args[0] = T((string)args[0]);
                args[3] = T((string)args[3]);
                args[4] = T((string)args[4]);
            }
            else if (template.Key == "Current job changed before GatherBuddy start; currentJob={0}; expectedJob={1}.")
            {
                if ((string)args[0] == "unavailable (0)") args[0] = T((string)args[0]);
                if ((string)args[1] == "unavailable (0)") args[1] = T((string)args[1]);
            }
            else if (template.Key == "Waiting for gather job switch to {0}; current job {1}..."
                && (string)args[1] == "unavailable (0)")
                args[1] = T((string)args[1]);
            // Readiness reasons and recovery target kinds have closed, authored vocabularies.
            else if (template.Key is "Holding teleport while {0} ({1:F0}s)..."
                or "Outdoor map flow paused while {0} ({1:F0}s)..."
                or "Recovering {0}: re-pathing ({1:F1}y)..."
                or "Waiting to open saddlebag: {0}"
                or "Waiting for saddlebag UI: {0}"
                or "Waiting for stable saddlebag UI: {0}"
                or "Waiting to move saddlebag map: {0}"
                or "Waiting to open saddlebag for map batching: {0}"
                or "Waiting for saddlebag UI during map batching: {0}"
                or "Waiting for stable saddlebag UI during map batching: {0}"
                or "Waiting to store the purchased map: {0}"
                or "Waiting to switch to combat job: {0}"
                or "Waiting to switch to gather job: {0}"
                or "Waiting to start GatherBuddy Reborn: {0}"
                or "Waiting to switch back after gathering: {0}"
                or "Timed out {0} after {1:F0} seconds."
                or "ADS repair stopped before durability reached threshold; ADS {0}.")
                args[0] = T((string)args[0]);
            else if (template.Key == "{0}: stuck {1} - teleporting to nearest aetheryte...")
                args[1] = T((string)args[1]);
            else if (template.Key is "Waiting to interact with '{0}' ({1})..."
                or "Treasure dungeon territory {0} detected - waiting for ADS-safe handoff seam... ({1})")
                args[1] = Readiness((string)args[1]);
            else if (template.Key is "Waiting to retry portal after close nudge ({0})..."
                or "Waiting to interact with portal ({0})..."
                or "Alexandrite: waiting to open Mysterious Map ({0})...")
                args[0] = Readiness((string)args[0]);
            else if (template.Key is "Map allowance cooldown: {0}"
                or "Map allowance locked for {0}."
                or "Waiting for map allowance ({0} remaining).")
                args[0] = Allowance((string)args[0]);
            else if (template.Key == "Map allowance for {0} is locked for {1}, longer than the {2}m wait limit.")
                args[1] = Allowance((string)args[1]);
            else if (template.Key is "{0} Map allowance status is unavailable."
                or "{0} Click the seedling to change the missing-map gather selection while allowance is ready.")
                args[0] = T((string)args[0]);
            else if (template.Key == "{0} Allowance cooldown: {1} remaining. Selection is saved for the next allowance.")
            {
                args[0] = T((string)args[0]);
                args[1] = Allowance((string)args[1]);
            }
            else if (template.Key is "Waiting for party zone load ({0}/{1} mounted, {2:F0}s elapsed{3})..."
                or "Thief-map remount: waiting for party zone load ({0}/{1} mounted, {2:F0}s elapsed{3})...")
                args[3] = T((string)args[3]);
            else if (template.Key == "Waiting for party ({0}/{1} nearby within {2:F0}y, need {3} before map content{4})...")
                args[4] = string.Concat(((string)args[4]).Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => T(", " + part)));
            else if (template.Key is "Waiting for player during {0} flag fallback..."
                or "In combat - pausing {0} flag fallback ({1:F1}y)..."
                or "Mounting for {0} flag fallback ({1:F1}y)..."
                or "Flying to {0} flag fallback ({1:F1}y)..."
                or "ADS repair requested ({0}) - waiting for ADS status... ({1:F0}s)"
                or "ADS repair requested ({0}) - waiting for utility start... ({1:F0}s)"
                or "ADS repair requested ({0}); durability {1}% below threshold {2}%..."
                or "ADS repair running ({0})... ({1:F0}s, {2})"
                or "No nearby repair NPC for ADS {0}; continuing map flow."
                or "Waiting for Higher/Lower puzzle UI ({0})..."
                or "Waiting for targetable Higher/Lower world object for {0}..."
                or "Waiting to retry Higher/Lower world interaction for {0}..."
                or "Duty completed - {0} exit in {1:F0}s...")
                args[0] = T((string)args[0]);
            else if (template.Key == "Playing Higher/Lower stage {0}: {1}...")
                args[1] = T((string)args[1]);
            else if (template.Key == "Cannot gather a map while LootGoblin is busy ({0}).")
                args[0] = T((string)args[0]);
            else if (template.Key is "{0} timed out while gathering {1}; targetMap={2} ({3}); currentJob={4}; expectedJob={5}; GatherBuddy={6}."
                or "{0} timed out while gathering {1}; targetMap={2} ({3}); currentJob={4}; expectedJob={5}; GatherBuddy={6}; closeGate={7}; closeAttempts={8}; cancelIssued={9}.")
            {
                args[0] = T((string)args[0]);
                if ((string)args[4] == "unavailable (0)") args[4] = T((string)args[4]);
                if ((string)args[5] == "unavailable (0)") args[5] = T((string)args[5]);
                args[6] = T((string)args[6]);
                if (args.Length > 7) args[7] = T((string)args[7]);
            }
            else if (template.Key == "addons={0}; conditions=Gathering:{1}, ExecutingGatheringAction:{2}"
                && (string)args[0] == "none")
                args[0] = T((string)args[0]);
            else if (template.Key == "{0} Market batch warning: {1} Continuing with {2} secured {3} map(s).")
            {
                args[0] = T((string)args[0]);
                args[1] = T((string)args[1]);
            }
            else if (template.Key == "Emptor rejected the order ({0})." && (string)args[0] == "unknown state")
                args[0] = T((string)args[0]);
            else if (template.Key == "ADS unavailable/pending: {0}.")
                args[0] = string.Join(", ", Regex.Split((string)args[0], @"(?<=\)), (?=(?:range(?: reset)?|hunts(?: reset)?) \()")
                    .Select(T));
            else if (template.Key is "range ({0})" or "range reset ({0})" or "hunts ({0})" or "hunts reset ({0})")
            {
                if ((string)args[0] == "ADS returned false") args[0] = T((string)args[0]);
            }
            return string.Format(Current.Culture, Current.Resources.GetString(template.Key, false)!, args);
        }
        var trimmed = english.Trim();
        if (trimmed.Length != english.Length && trimmed.Length > 0)
        {
            var offset = english.IndexOf(trimmed, StringComparison.Ordinal);
            return english[..offset] + T(trimmed) + english[(offset + trimmed.Length)..];
        }
        // Cycle-location waits prepend one of these two fixed presentation badges.
        if (english.StartsWith("[Flying] ", StringComparison.Ordinal))
            return T("[Flying]") + " " + T(english[9..]);
        if (english.StartsWith("[Ground] ", StringComparison.Ordinal))
            return T("[Ground]") + " " + T(english[9..]);
        // Compose state/detail pairs from localized pieces without changing the service/log strings.
        var separator = english.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) return T(english[..separator]) + " - " + T(english[(separator + 3)..]);
        return english; // Names, game data and raw diagnostic values are consumer data.
    }
    internal static string F(string english,params object?[] args) => string.Format(Current.Culture,T(english),args);
    internal static string F(FormattableString text) => string.Format(Current.Culture, T(text.Format), text.GetArguments());
    // Only source-verified readiness slots contain comma-separated authored tokens.
    private static string Readiness(string text) => string.Join(", ", text.Split(", ").Select(T));
    internal static string Allowance(string text)
    {
        // Keep the service's already formatted digits, including zero padding.
        var hours = Regex.Match(text, @"^(\d+)h (\d{2})m$", RegexOptions.CultureInvariant);
        if (hours.Success) return F("{0}h {1:D2}m", hours.Groups[1].Value, hours.Groups[2].Value);
        var minutes = Regex.Match(text, @"^(\d+)m$", RegexOptions.CultureInvariant);
        return minutes.Success ? F("{0}m", minutes.Groups[1].Value) : T(text);
    }
    internal string Format(string key, params object?[] args) => string.Format(Culture, Resources.GetString(key, false) ?? key, args);
    internal string Label(string key) => Resources.GetString(key, false) ?? key;
    internal static string SearchTerms(string englishTerms) => englishTerms + " " + string.Join(" ",
        Current.Resources.Cast<DictionaryEntry>()
            .Where(entry => englishTerms.Contains((string)entry.Key, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (string)entry.Value!));
    internal static IDisposable Font(UiFontRole role) => Current.pushFont(role);
    internal Scope Enter() => new(this);
    internal readonly struct Scope : IDisposable
    {
        private readonly UiText? previous;
        internal Scope(UiText value) { previous=current; current=value; }
        public void Dispose() => current=previous;
    }
    internal static string Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("g",Current.Culture) ?? T("Never");
    internal ushort[] GlyphRanges()
    {
        var chars=RequiredText.Select(MaterialText.NativeGlyphText).SelectMany(t=>t).Where(c=>!char.IsControl(c))
            .Concat(Enumerable.Range(0x20,0x024F-0x20+1).Select(i=>(char)i))
            .Concat(Enumerable.Range(0x0400,0x052F-0x0400+1).Select(i=>(char)i)).Concat("—").Distinct().Order().ToArray();
        var result=new List<ushort>();
        for(var index=0;index<chars.Length;index++)
        {
            var first=chars[index]; var last=first;
            while(index+1<chars.Length && chars[index+1]==last+1) last=chars[++index];
            result.Add(first); result.Add(last);
        }
        result.Add(0); return result.ToArray();
    }
    internal static IEnumerable<string> Values(ResourceSet set) => set.Cast<DictionaryEntry>().Select(e=>(string)e.Value!);
    public void Dispose() => manager.ReleaseAllResources();
}
