using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vanilla_RTX_App.Modules.Alchitex.Core;
using Vanilla_RTX_App.Modules.Json;

namespace Vanilla_RTX_App.Modules.Alchitex.Tools;

/// <summary>
/// Collapses a materials.json down to the properties that actually change what the pipeline
/// produces, and rewrites it in place.
///
/// The file is hand-tuned on top of a bootstrap (MaterialsBootstrapper), and a bootstrapped
/// entry spells out every property it can derive so there is something in front of the artist
/// to edit. Most of those end up restating the "default" entry - emissive 0 to 0, three invert
/// flags that were already going to be false, an empty recursive list, a heightmap and normal
/// section identical to the fallback. All of it parses to the same material either way, and
/// all of it is file the artist has to read past to find the entries they did tune.
///
/// The intended cycle is bootstrap -> hand-tune -> optimize, repeated as the pack grows: the
/// bootstrapper only ever appends, so a new game version adds its blocks in full form beside
/// the collapsed ones, and running this again folds those away too.
///
/// <para><b>How a property is judged redundant, and why there is no second copy of the
/// rules.</b> This file contains no idea of its own about what any property means, what its
/// default is, or how it falls back. It removes a property, asks
/// <see cref="MaterialsConfig.ResolveEntry"/> what the entry now resolves to, and keeps the
/// removal only if the answer is identical to what the entry resolved to before. The pipeline
/// is the judge of its own semantics. A change to fallback behaviour moves both sides of that
/// comparison together, so this cannot begin collapsing something that has begun to matter,
/// and a property added to the schema needs nothing here.</para>
///
/// <para>Two smaller pieces of the same discipline. The set of properties it is allowed to
/// consider is derived by round-tripping each entry through the very deserializer
/// MaterialsConfig uses, so a key the schema does not model - a hand-written note, a field
/// from a later version - is never a candidate and survives untouched. And the comparison is
/// made on the JSON of the resolved material rather than field by field, so a property added
/// to <see cref="ResolvedMaterial"/> joins the comparison on its own.</para>
///
/// Destructive and in place, like the rest of Tools/. It is also idempotent: a second run
/// over its own output finds nothing left to remove.
/// </summary>
public static class MaterialsOptimizer
{
    public sealed record OptimizeResult(
        int EntriesRead,
        int EntriesCollapsed,
        int PropertiesRemoved,
        int EntriesSkipped,
        long BytesBefore,
        long BytesAfter,
        string OutputPath);

    private const string DefaultKey = "default";

    // Only used for JsonNode.ToJsonString over an already-built tree, which reflects over
    // nothing - same reason MaterialsBootstrapper keeps its own copy of this.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Rewrites <paramref name="materialsJsonPath"/> with every redundant property removed.
    ///
    /// Nothing is written until the whole file has been processed, so a failure part way
    /// through leaves the original exactly as it was. An entry the pipeline itself cannot
    /// read is copied through verbatim rather than dropped: MaterialsConfig already skips
    /// such an entry at load, and rewriting a file is no occasion to delete the artist's
    /// only copy of what they typed.
    /// </summary>
    public static OptimizeResult Optimize(string materialsJsonPath)
    {
        if (!File.Exists(materialsJsonPath))
            throw new FileNotFoundException($"materials.json not found: '{materialsJsonPath}'");

        var bytesBefore = new FileInfo(materialsJsonPath).Length;
        var raw = File.ReadAllText(materialsJsonPath);

        // Same tolerance MaterialsConfig.Load and MaterialsBootstrapper apply - comments,
        // trailing commas, and a duplicate texture name resolved last-one-wins rather than
        // abandoning the run over a typo in a file this size.
        var root = MinecraftJson.ParseObject(raw)
            ?? throw new InvalidDataException($"'{materialsJsonPath}' isn't a JSON object of texture names - refusing to rewrite it.");

        // Loaded from the same path, so the "default" every comparison resolves against is
        // the file's own.
        var config = MaterialsConfig.Load(materialsJsonPath);

        var output = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        var entriesRead = 0;
        var entriesCollapsed = 0;
        var propertiesRemoved = 0;
        var entriesSkipped = 0;

        foreach (var property in root)
        {
            var key = property.Key;
            var node = property.Value?.DeepClone();

            if (node == null) continue;
            entriesRead++;

            // The default entry is the thing everything else is measured against, so
            // collapsing it against the built-in fallbacks would quietly change every entry
            // that leans on it. It is completed instead - see TopUpDefault.
            if (string.Equals(key, DefaultKey, StringComparison.OrdinalIgnoreCase))
            {
                output[key] = node is JsonObject defaultObject ? TopUpDefault(defaultObject) : node;
                continue;
            }

            if (node is not JsonObject entryObject)
            {
                entriesSkipped++;
                output[key] = node;
                continue;
            }

            try
            {
                var removed = Collapse(config, key, entryObject, out var collapsed);
                output[key] = collapsed;
                propertiesRemoved += removed;
                if (removed > 0) entriesCollapsed++;
            }
            catch (Exception ex)
            {
                // Unreadable by the schema, so the pipeline is already ignoring it. Keep the
                // text exactly as written and say so.
                Trace.WriteLine($"[ALCHITEX] MaterialsOptimizer: entry '{key}' couldn't be read ({ex.Message}) - left untouched.");
                entriesSkipped++;
                output[key] = entryObject;
            }
        }

        WriteOrdered(output, materialsJsonPath);

        var bytesAfter = new FileInfo(materialsJsonPath).Length;
        Trace.WriteLine($"[ALCHITEX] MaterialsOptimizer: {propertiesRemoved} properties removed across {entriesCollapsed}/{entriesRead} entries, {bytesBefore} -> {bytesAfter} bytes.");

        return new OptimizeResult(entriesRead, entriesCollapsed, propertiesRemoved, entriesSkipped, bytesBefore, bytesAfter, materialsJsonPath);
    }

    /// <summary>
    /// Removes every property of one entry that the pipeline would have arrived at anyway,
    /// and reports how many went.
    ///
    /// Each candidate is tested against the entry's ORIGINAL resolution, not the previous
    /// step's, which is what makes a single greedy pass sound: every accepted removal has
    /// been shown to leave the whole entry resolving exactly as it started, so any number of
    /// them compose.
    ///
    /// Candidates are ordered deepest first so that a container is only considered once the
    /// leaves inside it are gone - that is how `"mer": {}` disappears in the same pass that
    /// emptied it, without needing a second sweep for empty sections.
    /// </summary>
    private static int Collapse(MaterialsConfig config, string entryName, JsonObject entry, out JsonObject collapsed)
    {
        var baseline = Fingerprint(config, entryName, entry);

        var working = (JsonObject)entry.DeepClone();
        var removed = 0;

        foreach (var path in CandidatePaths(entry).OrderByDescending(p => p.Count))
        {
            var trial = (JsonObject)working.DeepClone();
            if (!RemoveAt(trial, path)) continue;

            if (Fingerprint(config, entryName, trial) != baseline) continue;

            working = trial;
            removed++;
        }

        collapsed = working;
        return removed;
    }

    /// <summary>
    /// What the pipeline makes of an entry, as a string that differs whenever anything about
    /// the resolved material differs.
    ///
    /// JSON rather than a field-by-field comparison on purpose. <see cref="ResolvedMaterial"/>
    /// is a class (reference equality) holding a list (also reference equality), so an
    /// equality check here would have to be written by hand and would then silently stop
    /// covering any property added to it later - and a comparison that quietly returns "same"
    /// is a comparison that deletes an artist's work. Serializing puts every property in
    /// scope automatically.
    /// </summary>
    private static string Fingerprint(MaterialsConfig config, string entryName, JsonObject entry)
    {
        var material = entry.Deserialize(AlchitexJsonContext.Default.MaterialEntry)
            ?? throw new InvalidDataException("entry deserialized to null");

        return JsonSerializer.Serialize(config.ResolveEntry(material, entryName), AlchitexJsonContext.Default.ResolvedMaterial);
    }

    /// <summary>
    /// Every property this tool may consider removing, as a path of keys from the entry root.
    ///
    /// Derived by round-tripping the entry through the same deserializer the pipeline uses
    /// and keeping only what survives: anything the schema doesn't model is dropped on the way
    /// through and so never becomes a candidate. That is what protects a hand-written comment
    /// key, or a property belonging to a version of the schema this build doesn't have, from
    /// being deleted merely because removing it changes nothing today.
    ///
    /// <b>A container holding one is not a candidate either</b>, at any depth, and that is the
    /// half that is easy to miss: not offering `mer.artist_note` for removal buys nothing if
    /// `mer` itself can be removed, because it goes with the section. An entry whose whole
    /// `mer` is redundant apart from a hand-written note is exactly the case - every modelled
    /// value in it resolves to the default, so the section collapses and takes the note with
    /// it. The walk therefore reports upward whether it saw anything unmodelled, and a
    /// container that did keeps its place while its own modelled children stay removable.
    ///
    /// Array elements are not candidates, only the array itself and the keys inside its
    /// elements. A recursive pass is art direction; the question worth asking about one is
    /// whether its channel or a MER bound is redundant, never whether the pass is.
    /// </summary>
    private static List<List<string>> CandidatePaths(JsonObject entry)
    {
        var known = JsonSerializer.SerializeToNode(
            entry.Deserialize(AlchitexJsonContext.Default.MaterialEntry)!,
            AlchitexJsonContext.Default.MaterialEntry) as JsonObject;

        var paths = new List<List<string>>();
        if (known != null) Walk(entry, known, new List<string>(), paths);
        return paths;

        // Returns true when this subtree holds anything the schema didn't model, so the
        // caller knows not to offer the node containing it.
        static bool Walk(JsonNode? actual, JsonNode? recognised, List<string> prefix, List<List<string>> into)
        {
            if (actual is JsonObject actualObject && recognised is JsonObject recognisedObject)
            {
                var holdsUnmodelled = false;

                foreach (var child in actualObject)
                {
                    if (!recognisedObject.ContainsKey(child.Key))
                    {
                        // A key the schema understood but whose value was written as JSON null
                        // also fails to come back (nulls are suppressed on write), and removing
                        // its container would be removing something the schema does model. It
                        // counts as unmodelled here for the same conservative reason.
                        holdsUnmodelled = true;
                        continue;
                    }

                    var path = new List<string>(prefix) { child.Key };

                    if (Walk(child.Value, recognisedObject[child.Key], path, into))
                        holdsUnmodelled = true;
                    else
                        into.Add(path);
                }

                return holdsUnmodelled;
            }

            if (actual is JsonArray actualArray && recognised is JsonArray recognisedArray)
            {
                var holdsUnmodelled = false;

                // Index-aligned: the round-trip preserves element order and count, so element
                // i of one is element i of the other.
                for (var i = 0; i < actualArray.Count && i < recognisedArray.Count; i++)
                {
                    var path = new List<string>(prefix) { i.ToString() };

                    if (Walk(actualArray[i], recognisedArray[i], path, into))
                        holdsUnmodelled = true;
                }

                return holdsUnmodelled;
            }

            return false;
        }
    }

    /// <summary>Removes the node at a key path, addressing array elements by their index as
    /// a path segment. False when the path no longer resolves, which happens whenever a
    /// container was collapsed before its own children came up.</summary>
    private static bool RemoveAt(JsonObject root, List<string> path)
    {
        JsonNode? current = root;

        for (var i = 0; i < path.Count - 1; i++)
        {
            current = current switch
            {
                JsonObject o => o.TryGetPropertyValue(path[i], out var next) ? next : null,
                JsonArray a when int.TryParse(path[i], out var index) && index >= 0 && index < a.Count => a[index],
                _ => null,
            };

            if (current == null) return false;
        }

        var leaf = path[^1];
        return current is JsonObject parent && parent.Remove(leaf);
    }

    /// <summary>
    /// Adds any property of the built-in default entry that the file's own "default" is
    /// missing, leaving every property it does define exactly as the artist set it.
    ///
    /// Purely additive and, by construction, output-neutral: an absent property already
    /// resolves to the built-in value being written in its place. What it buys is a file that
    /// states its own fallbacks instead of implying them - and, because every other entry is
    /// collapsed against this one, a default that spells out a property is a property every
    /// entry below it can then drop.
    /// </summary>
    private static JsonObject TopUpDefault(JsonObject existing)
    {
        var full = JsonSerializer.SerializeToNode(
            MaterialDefaults.BuildFullDefaultEntry(),
            AlchitexJsonContext.Default.MaterialEntry) as JsonObject;

        if (full == null) return existing;

        foreach (var section in full)
        {
            if (!existing.TryGetPropertyValue(section.Key, out var mine) || mine == null)
            {
                existing[section.Key] = section.Value?.DeepClone();
                continue;
            }

            // One level down is as deep as this goes, which is all the schema has: every
            // section is a flat bag of scalars apart from "recursive", whose contents are
            // per-texture art direction that no built-in default has an opinion about.
            if (mine is not JsonObject mineObject || section.Value is not JsonObject fullObject) continue;

            foreach (var property in fullObject)
            {
                if (!mineObject.ContainsKey(property.Key))
                    mineObject[property.Key] = property.Value?.DeepClone();
            }
        }

        return existing;
    }

    /// <summary>"default" first, then alphabetical - the same ordering
    /// MaterialsBootstrapper writes, so bootstrapping and optimizing don't reshuffle the
    /// file against each other.</summary>
    private static void WriteOrdered(Dictionary<string, JsonNode> entries, string outputPath)
    {
        var ordered = new JsonObject();

        if (entries.TryGetValue(DefaultKey, out var defaultEntry))
            ordered[DefaultKey] = defaultEntry;

        foreach (var key in entries.Keys
                     .Where(k => !string.Equals(k, DefaultKey, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            ordered[key] = entries[key];
        }

        File.WriteAllText(outputPath, ordered.ToJsonString(WriteOptions));
    }
}
