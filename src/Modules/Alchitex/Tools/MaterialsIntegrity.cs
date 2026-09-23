using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Vanilla_RTX_App.Modules.Alchitex.Core;
using Vanilla_RTX_App.Modules.Json;

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

/// <summary>
/// Reads a materials.json and reports everything about it the generation pipeline would
/// quietly work around, as a text file the artist can read away from the app.
///
/// It exists because every leniency in <see cref="MaterialsConfig"/> is deliberate and
/// silent by design (§4.5): an out-of-range value is clamped, an unrecognised padding_type
/// falls back, a misspelt field is ignored outright, an entry with a wrong-typed value is
/// skipped and that texture inherits "default". None of that fails a run, and none of it is
/// visible to someone hand-tuning thirty thousand lines. This is the pass that says it out
/// loud, once, at the end of the session where it can still be acted on.
///
/// <para><b>It knows nothing about the schema, on purpose</b> - the same discipline
/// <see cref="MaterialsOptimizer"/> follows, and for the same reason: a checker carrying its
/// own copy of the rules is a second thing to update, and the first time someone forgets, it
/// reports confidently on a schema that no longer exists. There are three mechanisms here
/// and not one of them contains a list of property names, valid values or ranges.</para>
///
/// <list type="number">
///   <item><b>The loader reports itself.</b> Every clamp, fallback and skipped entry in
///   MaterialsConfig already writes a <see cref="Trace"/> line naming the entry, the property
///   path and what it did instead. This runs the real <see cref="MaterialsConfig.Load"/> and
///   then the real <see cref="MaterialsConfig.Resolve"/> over every name in the file with a
///   listener attached, and copies those lines into the report verbatim. A leniency added to
///   the loader later appears here with no edit to this file.</item>
///
///   <item><b>The deserializer reports what it did not understand.</b> An entry is
///   round-tripped through the very converter the pipeline deserializes with; any key present
///   in the file but missing from what comes back is a key the schema does not model. That is
///   how a misspelt field and a field written into the wrong section are both found, and
///   neither case needs to be enumerated here.</item>
///
///   <item><b>The document reports its own duplicates.</b> Names repeated in one object are
///   resolved last-one-wins by both System.Text.Json and Bedrock (§6), so an earlier one is
///   dead text that reads as a live setting. Nothing downstream is in a position to notice -
///   by the time anything else sees the document, the loser is already gone.</item>
/// </list>
///
/// <para><b>Run this BEFORE the optimizer, never after.</b> The optimizer keeps a property
/// only when removing it changes what the entry resolves to - and a value the loader had to
/// fall back on resolves to the fallback, so the optimizer correctly removes it and the
/// evidence goes with it. A <c>"padding_type": "tiled"</c> typo resolves to Tile, which is
/// what the default already says, so an optimize pass deletes the misspelling and the file
/// looks clean from then on. Reading the file as the artist actually wrote it is the only
/// moment that mistake is visible.</para>
///
/// Read-only with respect to materials.json. The only thing it writes is the report, and
/// <see cref="WriteReport"/> is a separate call so checking and writing can be judged apart.
/// </summary>
public static class MaterialsIntegrity
{
    /// <summary>
    /// What a finding actually costs, which is a different question from how unusual it is.
    /// <see cref="Ignored"/> is its own bucket rather than a kind of problem because a key
    /// the schema does not model is also how a deliberate hand-written note survives in this
    /// file (§5.4) - the report states what the pipeline does with it and lets the artist
    /// decide which of the two it was.
    /// </summary>
    public enum Severity
    {
        /// <summary>The pipeline could not use what the file says, and used something else.</summary>
        Problem,

        /// <summary>The pipeline never reads this at all.</summary>
        Ignored,

        /// <summary>Used exactly as written; worth knowing anyway.</summary>
        Note,
    }

    /// <summary><paramref name="Kind"/> is the report's own group heading as well as the
    /// grouping key, so findings that belong together arrive together without the writer
    /// classifying anything a second time.</summary>
    public sealed record Finding(Severity Severity, string Kind, string Entry, string Detail);

    public sealed record IntegrityReport(
        string MaterialsJsonPath,
        long Bytes,
        int Lines,
        int Entries,
        DateTime CheckedAtLocal,
        IReadOnlyList<Finding> Findings)
    {
        public int ProblemCount => Findings.Count(f => f.Severity == Severity.Problem);
        public int IgnoredCount => Findings.Count(f => f.Severity == Severity.Ignored);
        public int NoteCount => Findings.Count(f => f.Severity == Severity.Note);

        /// <summary>Nothing at all to say - not merely "nothing fatal". The weaker reading
        /// would let a report headed "clean" sit directly above a list of ignored keys.</summary>
        public bool IsClean => Findings.Count == 0;
    }

    // Group headings, which are also the grouping key. Declared together so the shape of the
    // report is readable in one place.
    private const string KindLoader = "What the pipeline's own loader had to work around";
    private const string KindDuplicate = "Names written more than once (only the last one is live)";
    private const string KindUnmodelled = "Written, but not part of the schema - never read";
    private const string KindNull = "Written as null - read exactly as if absent";
    private const string KindRange = "Accepted as written, worth a second look";

    // Every line MaterialsConfig writes about this file carries both of these. Matching on
    // them rather than on a phrasing keeps the capture from going quiet the first time a
    // message is reworded; a new message about materials.json mentioning neither the module
    // nor the file would be a worse log line on its own terms.
    private const string LoaderTag = "[ALCHITEX]";
    private const string LoaderSubject = "materials.json";

    /// <summary>Pulls the entry name out of a loader line so findings can be grouped and
    /// sorted by texture. Deliberately tolerant: a line it cannot read is still reported in
    /// full, it just sorts under the file rather than under its own entry.</summary>
    private static readonly Regex EntryInLoaderLine =
        new(@"entry '([^']*)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads <paramref name="materialsJsonPath"/> and returns everything worth reporting
    /// about it. Never writes to that file, and never throws for anything the file itself
    /// contains - a document that cannot be parsed at all comes back as a report saying so,
    /// because that is the single most important thing this could ever tell anyone.
    /// </summary>
    public static IntegrityReport Check(string materialsJsonPath)
    {
        if (!File.Exists(materialsJsonPath))
            throw new FileNotFoundException($"materials.json not found: '{materialsJsonPath}'");

        var raw = File.ReadAllText(materialsJsonPath);
        var findings = new List<Finding>();

        // Parsed once here for the structural passes. A null root means the loader could not
        // read it either, and it says so through the capture below - there is nothing to add.
        var root = MinecraftJson.ParseObject(raw);

        CollectLoaderReport(materialsJsonPath, root, findings);
        CollectDuplicateNames(raw, findings);

        if (root != null)
        {
            var schema = BuildSchemaMap();

            foreach (var property in root)
            {
                if (property.Value is not JsonObject entry) continue;

                CollectUnmodelled(entry, property.Key, schema, findings);
                CollectReversedRanges(entry, property.Key, findings);
            }
        }

        return new IntegrityReport(
            materialsJsonPath,
            new FileInfo(materialsJsonPath).Length,
            CountLines(raw),
            root?.Count ?? 0,
            DateTime.Now,
            Sorted(findings));
    }

    // ── 1. What the loader says about itself ──────────────────────────────────────────

    /// <summary>
    /// Runs the real load and the real per-texture resolve with a listener on
    /// <see cref="Trace"/>, and keeps every line the loader wrote about this file.
    ///
    /// Resolving each name individually is what makes this complete: the merge, the clamping
    /// and all of its logging happen inside <see cref="MaterialsConfig.Resolve"/>, once per
    /// entry, and are cached afterwards - so an entry nothing asks about is an entry whose
    /// problems are never reported. Asking about all of them is the only way to see them all.
    ///
    /// Trace listeners are process-wide, so this briefly sees whatever else the app writes.
    /// Both filters have to match for a line to be kept, and generation is locked out while a
    /// dev tool runs, so in practice the only writer is the loader below.
    /// </summary>
    private static void CollectLoaderReport(string materialsJsonPath, JsonObject? root, List<Finding> into)
    {
        var listener = new CaptureListener();
        Trace.Listeners.Add(listener);

        try
        {
            var config = MaterialsConfig.Load(materialsJsonPath);

            if (root != null)
                foreach (var property in root)
                    config.Resolve(property.Key);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        foreach (var line in listener.Lines)
        {
            if (!line.Contains(LoaderTag, StringComparison.Ordinal) ||
                !line.Contains(LoaderSubject, StringComparison.OrdinalIgnoreCase))
                continue;

            // Only the module tag comes off. The rest is reproduced exactly as the pipeline
            // phrased it, so the report cannot end up describing the loader's behaviour
            // differently from the loader.
            var detail = line.Replace(LoaderTag, string.Empty, StringComparison.Ordinal).Trim();
            var match = EntryInLoaderLine.Match(detail);

            into.Add(new Finding(Severity.Problem, KindLoader, match.Success ? match.Groups[1].Value : string.Empty, detail));
        }
    }

    /// <summary>Collects <see cref="Trace"/> output for as long as it is attached. Write and
    /// WriteLine are the only two members Trace.WriteLine reaches, and Trace holds a global
    /// lock across listeners, so this needs no synchronisation of its own.</summary>
    private sealed class CaptureListener : TraceListener
    {
        private readonly StringBuilder _partial = new();

        public List<string> Lines { get; } = new();

        public override void Write(string? message) => _partial.Append(message);

        public override void WriteLine(string? message)
        {
            _partial.Append(message);
            Lines.Add(_partial.ToString());
            _partial.Clear();
        }
    }

    // ── 2. What the deserializer did not understand ───────────────────────────────────

    /// <summary>
    /// Reports every key in one entry that the schema does not model, by round-tripping the
    /// entry through the pipeline's own converter and comparing key sets at each level.
    /// Whatever comes back is by definition everything the pipeline understood, so whatever
    /// went in and did not come back is everything it did not.
    ///
    /// An entry that throws on the way in is left alone: the loader has already reported it
    /// as unreadable, and a second finding about the same entry would only describe wreckage.
    /// </summary>
    private static void CollectUnmodelled(
        JsonObject entry, string entryName, IReadOnlyDictionary<string, List<string>> schema, List<Finding> into)
    {
        JsonObject? recognised;

        try
        {
            var typed = entry.Deserialize(AlchitexJsonContext.Default.MaterialEntry);
            if (typed == null) return;

            recognised = JsonSerializer.SerializeToNode(typed, AlchitexJsonContext.Default.MaterialEntry) as JsonObject;
        }
        catch
        {
            return;
        }

        if (recognised == null) return;

        Walk(entry, recognised, string.Empty);

        void Walk(JsonNode? actual, JsonNode? survived, string prefix)
        {
            if (actual is JsonObject actualObject && survived is JsonObject survivedObject)
            {
                foreach (var child in actualObject)
                {
                    var path = prefix.Length == 0 ? child.Key : $"{prefix}.{child.Key}";

                    if (survivedObject.ContainsKey(child.Key))
                    {
                        Walk(child.Value, survivedObject[child.Key], path);
                        continue;
                    }

                    // A key the converter DID understand but whose value was JSON null comes
                    // back missing too, because the schema is all-nullable and writing nulls
                    // back out is suppressed (§4.5). That is a different thing to say.
                    if (child.Value is null || child.Value.GetValueKind() == JsonValueKind.Null)
                    {
                        into.Add(new Finding(Severity.Ignored, KindNull, entryName,
                            $"{path} is null. The pipeline reads a null exactly as it reads an absent property, so this inherits \"default\" like any other unset value."));
                        continue;
                    }

                    into.Add(new Finding(Severity.Ignored, KindUnmodelled, entryName,
                        $"{path} {Whereabouts(child.Key, prefix, schema)}"));
                }

                return;
            }

            if (actual is JsonArray actualArray && survived is JsonArray survivedArray)
            {
                // Index-aligned: the round-trip preserves element order and count, so element
                // i of one is element i of the other.
                for (var i = 0; i < actualArray.Count && i < survivedArray.Count; i++)
                    Walk(actualArray[i], survivedArray[i], $"{prefix}[{i}]");
            }
        }
    }

    /// <summary>
    /// Says what the schema knows about a key that is not valid where it was written - either
    /// the section (or sections) it does belong in, or that the schema has never heard of it.
    ///
    /// This is the whole of "a parameter in the wrong section", and it is an answer the schema
    /// gives rather than one written down here: the map it consults is built from the
    /// converter's own property metadata.
    /// </summary>
    private static string Whereabouts(string key, string prefix, IReadOnlyDictionary<string, List<string>> schema)
    {
        var here = SectionLabel(prefix);

        if (!schema.TryGetValue(key, out var sections) || sections.Count == 0)
            return "is not a property the schema has, anywhere, so nothing reads it. A note to yourself is fine - the optimizer preserves it - and if it was meant to be a setting, the spelling is what to check.";

        var elsewhere = sections.Where(s => !string.Equals(s, here, StringComparison.Ordinal)).ToList();

        if (elsewhere.Count == 0)
            return $"is not read in this position, although the schema does have a '{key}'.";

        return $"is not a property of {here} - '{key}' belongs in {string.Join(" or ", elsewhere)}. Written here it does nothing.";
    }

    private static string SectionLabel(string prefix) => prefix.Length == 0 ? "the entry itself" : $"'{prefix}'";

    /// <summary>
    /// Every property name the schema models, mapped to the section label(s) it is valid in.
    ///
    /// Walked off <see cref="AlchitexJsonContext"/>'s own type metadata - the same metadata
    /// the deserializer binds with - so a property added to, moved between or removed from the
    /// DTOs changes this map without anyone touching it. That is also why it uses the
    /// source-generated context rather than plain reflection: the DTOs' members are stripped
    /// under PublishTrimmed, and reflecting over them would answer differently in Release
    /// (§6).
    ///
    /// Depth-limited rather than cycle-tracked by type, so a shape reached twice - MerParams,
    /// which is both 'mer' and each recursive pass's 'mer' - is reported as belonging to both.
    /// </summary>
    private static Dictionary<string, List<string>> BuildSchemaMap()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Walk(AlchitexJsonContext.Default.MaterialEntry, string.Empty, 0);
        return map;

        void Walk(JsonTypeInfo? info, string prefix, int depth)
        {
            // The schema is three levels deep; this only has to stop a hypothetical cycle
            // from running forever.
            if (info == null || depth > 8) return;

            foreach (var property in info.Properties)
            {
                var label = SectionLabel(prefix);

                if (!map.TryGetValue(property.Name, out var sections))
                    map[property.Name] = sections = new List<string>();

                if (!sections.Contains(label, StringComparer.Ordinal))
                    sections.Add(label);

                var type = property.PropertyType;
                var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";

                var element = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
                    ? type.GetGenericArguments()[0]
                    : null;

                if (element != null)
                    Walk(AlchitexJsonContext.Default.GetTypeInfo(element), $"{path}[]", depth + 1);
                else
                    Walk(AlchitexJsonContext.Default.GetTypeInfo(type), path, depth + 1);
            }
        }
    }

    // ── 3. What the document says twice ───────────────────────────────────────────────

    /// <summary>
    /// Reports any name written more than once inside the same object, at any depth.
    ///
    /// Case-insensitive because the pipeline is - entries are keyed by an OrdinalIgnoreCase
    /// dictionary and property binding is case-insensitive - so "Stone" and "stone" are one
    /// name here as well.
    ///
    /// Reads through <see cref="JsonDocument"/>, the only reader that still has both copies:
    /// <see cref="MinecraftJson"/>'s node tree has already applied last-one-wins by the time
    /// anyone can look. A document JsonDocument cannot parse is one MaterialsConfig cannot
    /// parse either, and that has already been reported.
    /// </summary>
    private static void CollectDuplicateNames(string raw, List<Finding> into)
    {
        try
        {
            using var document = JsonDocument.Parse(raw, MinecraftJson.TolerantDocumentOptions);
            Scan(document.RootElement, string.Empty, string.Empty, 0);
        }
        catch (JsonException)
        {
        }

        // depth distinguishes the root - whose names ARE the entry names, and which is the one
        // level with no entry to attribute a finding to - from everything inside an entry. An
        // empty prefix does not: an entry object is itself reached with one.
        void Scan(JsonElement element, string entryName, string prefix, int depth)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var property in element.EnumerateObject())
                {
                    counts[property.Name] = counts.GetValueOrDefault(property.Name) + 1;

                    Scan(property.Value,
                        depth == 0 ? property.Name : entryName,
                        depth == 0 ? string.Empty : Join(prefix, property.Name),
                        depth + 1);
                }

                foreach (var pair in counts.Where(c => c.Value > 1).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var where = depth == 0
                        ? "at the top level"
                        : prefix.Length == 0 ? "in this entry" : $"in '{prefix}'";

                    into.Add(new Finding(Severity.Problem, KindDuplicate, depth == 0 ? pair.Key : entryName,
                        $"'{pair.Key}' is written {pair.Value} times {where}. Only the last one has any effect - the rest is dead text that reads as live settings."));
                }

                return;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Scan(item, entryName, $"{prefix}[{index++}]", depth + 1);
            }
        }

        static string Join(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}.{name}";
    }

    // ── 4. Accepted as written ────────────────────────────────────────────────────────

    /// <summary>
    /// Reports a min/max pair written the wrong way round.
    ///
    /// Not an error, and the report says so: PbrGeneration's Stretch takes the target range as
    /// given, so a reversed one runs the ramp backwards - arithmetically the same thing the
    /// matching invert flag does. It earns a line because the two ways of saying it compose,
    /// and a transposed pair plus an invert is a double negative that reads as neither.
    ///
    /// The pairs are found by the schema's own naming - 'x_min' beside 'x_max', or a bare
    /// 'min' beside 'max' - so a range added later is covered without being listed here. Only
    /// values written in the same object are compared: a bound inherited from "default" while
    /// the other is set locally is a legitimate thing to do, and guessing at intent across
    /// entries is how a report starts being wrong.
    /// </summary>
    private static void CollectReversedRanges(JsonObject entry, string entryName, List<Finding> into)
    {
        Scan(entry, string.Empty);

        void Scan(JsonNode? node, string prefix)
        {
            if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                    Scan(array[i], $"{prefix}[{i}]");
                return;
            }

            if (node is not JsonObject obj) return;

            foreach (var child in obj)
            {
                var path = prefix.Length == 0 ? child.Key : $"{prefix}.{child.Key}";

                if (child.Value is JsonObject or JsonArray)
                {
                    Scan(child.Value, path);
                    continue;
                }

                if (!child.Key.EndsWith("min", StringComparison.OrdinalIgnoreCase)) continue;

                var maxKey = child.Key[..^3] + "max";
                if (!obj.TryGetPropertyValue(maxKey, out var maxNode)) continue;

                if (MinecraftJson.GetInt(child.Value) is not int lo ||
                    MinecraftJson.GetInt(maxNode) is not int hi ||
                    lo <= hi)
                    continue;

                var maxPath = prefix.Length == 0 ? maxKey : $"{prefix}.{maxKey}";

                into.Add(new Finding(Severity.Note, KindRange, entryName,
                    $"{path} is {lo} and {maxPath} is {hi}, so the range runs backwards. The pipeline accepts it - the texture's darkest pixel maps to {lo} and its brightest to {hi}, which is what the matching invert flag does. Worth checking the two are not both set."));
            }
        }
    }

    // ── The report ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="report"/> into <paramref name="directory"/> and returns the path
    /// it landed on.
    ///
    /// The file name carries the moment it was taken, so a run never overwrites the previous
    /// report. That matters more than tidiness here: the destination is somewhere the artist
    /// chose, usually their desktop, and silently replacing a file there is not this tool's to
    /// do.
    /// </summary>
    public static string WriteReport(IntegrityReport report, string directory)
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"materials-integrity-{report.CheckedAtLocal:yyyyMMdd-HHmmss}.txt");

        File.WriteAllText(path, BuildReportText(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>The report as text. Public so the whole of it can be asserted on without a
    /// file system.</summary>
    public static string BuildReportText(IntegrityReport report)
    {
        var text = new StringBuilder();

        text.AppendLine("Alchitex - materials.json integrity report");
        text.AppendLine("==========================================");
        text.AppendLine();
        text.AppendLine($"File     : {report.MaterialsJsonPath}");
        text.AppendLine($"Size     : {report.Bytes / 1024:N0} KB, {report.Lines:N0} lines, {report.Entries:N0} entries");
        text.AppendLine($"Checked  : {report.CheckedAtLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        text.AppendLine();

        if (report.IsClean)
        {
            text.AppendLine("RESULT: clean.");
            text.AppendLine();
            text.AppendLine("Every entry parsed, every value was used exactly as written, and there is");
            text.AppendLine("nothing in this file the pipeline has to work around. Nothing to do.");
            AppendMethod(text);
            return text.ToString();
        }

        text.AppendLine(report.ProblemCount > 0
            ? $"RESULT: {Count(report.ProblemCount, "thing")} the pipeline does not do as written."
            : "RESULT: nothing here stops the pipeline reading this file as intended.");

        text.AppendLine();
        text.AppendLine($"  {report.ProblemCount,6:N0}  not used as written");
        text.AppendLine($"  {report.IgnoredCount,6:N0}  never read at all");
        text.AppendLine($"  {report.NoteCount,6:N0}  used as written, worth a look");

        foreach (var group in report.Findings.GroupBy(f => f.Kind).OrderBy(g => g.First().Severity))
        {
            text.AppendLine();
            text.AppendLine();
            text.AppendLine(group.Key);
            text.AppendLine(new string('-', group.Key.Length));
            text.AppendLine();

            foreach (var byEntry in group.GroupBy(f => f.Entry).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine(byEntry.Key.Length == 0 ? "  (the file as a whole)" : $"  {byEntry.Key}");

                foreach (var finding in byEntry)
                    text.AppendLine($"      {finding.Detail}");

                text.AppendLine();
            }
        }

        AppendMethod(text);
        return text.ToString();
    }

    /// <summary>
    /// What was actually checked, in the report itself.
    ///
    /// A report whose only clean state is the word "clean" asks to be trusted; saying which
    /// three questions were asked lets it be judged instead - and makes plain what it does not
    /// cover, which is anything that needs a texture in front of it.
    /// </summary>
    private static void AppendMethod(StringBuilder text)
    {
        text.AppendLine();
        text.AppendLine();
        text.AppendLine("What was checked");
        text.AppendLine("----------------");
        text.AppendLine();
        text.AppendLine("  The file was loaded and every entry in it resolved through the same code a");
        text.AppendLine("  generation run uses, and everything that code reported about a value it could");
        text.AppendLine("  not use is reproduced above in its own words.");
        text.AppendLine();
        text.AppendLine("  Every entry was round-tripped through the same converter the pipeline");
        text.AppendLine("  deserializes with. Anything that went in and did not come back is a key the");
        text.AppendLine("  schema does not model, whether that is a misspelling, a setting written into");
        text.AppendLine("  the wrong section, or a deliberate note.");
        text.AppendLine();
        text.AppendLine("  Every object was read for names written more than once. Only the last of");
        text.AppendLine("  those ever takes effect, here and in the game.");
        text.AppendLine();
        text.AppendLine("  Not checked: whether the textures named here exist in any pack, and whether");
        text.AppendLine("  the values are the right ones artistically. Neither is answerable from this");
        text.AppendLine("  file alone.");
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static int CountLines(string raw) => raw.Length == 0 ? 0 : raw.Count(c => c == '\n') + 1;

    /// <summary>Problems first, then by entry, so the file reads in the order it would be
    /// acted on.</summary>
    private static List<Finding> Sorted(List<Finding> findings) => findings
        .OrderBy(f => f.Severity)
        .ThenBy(f => f.Entry, StringComparer.OrdinalIgnoreCase)
        .ThenBy(f => f.Detail, StringComparer.Ordinal)
        .ToList();
}
