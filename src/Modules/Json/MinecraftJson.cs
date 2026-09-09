using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Modules.Json;

/// <summary>
/// The app's one JSON reading/writing layer for files it did not author itself: Bedrock
/// manifests, terrain_texture.json, .texture_set.json, fog files, third-party API payloads.
///
/// <para><b>Why this exists.</b> Every module used to carry its own parser, and they disagreed
/// on what "valid JSON" means. Real pack files in the wild routinely carry <c>//</c> and
/// <c>/* */</c> comments, trailing commas, <b>duplicate keys</b>, quoted numbers, and
/// mixed-type fields - Bedrock's own reader tolerates all of it, so ours has to. Newtonsoft
/// swallowed most of it silently; System.Text.Json throws by default, and its
/// <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/> does something
/// worse than throw on duplicate keys - it *succeeds*, then throws <c>ArgumentException</c>
/// later, on the first enumeration, when <see cref="JsonObject"/> materialises its lazy
/// dictionary. A crash at an arbitrary later call site, from a file that "parsed fine".</para>
///
/// <para><b>How.</b> Everything goes through <see cref="ParseNode"/>, which walks a
/// <see cref="JsonDocument"/> (duplicate-tolerant natively - no lazy dictionary) and rebuilds
/// a fresh mutable node tree by indexer assignment, so a later duplicate key simply overwrites
/// the earlier one. Last-one-wins, matching what Bedrock does in practice.</para>
///
/// <para><b>Trimming.</b> Release publishes trimmed. Nothing here reflects: no
/// <c>JsonSerializer.Deserialize&lt;T&gt;</c>, no generic <c>JsonValue.Create&lt;T&gt;</c>, no
/// generic <c>JsonArray.Add&lt;T&gt;</c>. Values are read through <see cref="JsonElement"/> or
/// the non-generic <see cref="JsonValue"/> overloads, both of which the trimmer is happy with.
/// Anything that genuinely needs a POCO gets a source-generated context instead.</para>
/// </summary>
public static class MinecraftJson
{
    /// <summary>Comments and trailing commas are not errors in a pack file.</summary>
    public static readonly JsonDocumentOptions TolerantDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions IndentedWriteOptions = new() { WriteIndented = true };

    // ─────────────────────────── Parsing ───────────────────────────

    /// <summary>
    /// Parses any JSON text into a mutable node tree, tolerating comments, trailing commas and
    /// duplicate keys. Returns null instead of throwing on anything it can't read at all.
    /// </summary>
    public static JsonNode? ParseNode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var document = JsonDocument.Parse(text, TolerantDocumentOptions);
            return RebuildNode(document.RootElement);
        }
        catch (JsonException)
        {
            // Second chance for the one malformation JsonDocumentOptions has no switch for:
            // raw control characters inside a string literal. See EscapeControlCharsInStrings.
            var repaired = EscapeControlCharsInStrings(text);
            if (repaired != null)
            {
                try
                {
                    using var document = JsonDocument.Parse(repaired, TolerantDocumentOptions);
                    Trace.WriteLine("[JSON] Accepted a file containing unescaped control characters inside strings.");
                    return RebuildNode(document.RootElement);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[JSON] Could not parse JSON text: {ex.Message}");
                    return null;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JSON] Could not parse JSON text: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Escapes raw control characters (a literal newline, most often) that appear *inside* a
    /// JSON string literal, leaving every character outside a string exactly as it was. Returns
    /// null when there is nothing to fix, so the caller can tell a real syntax error from this.
    ///
    /// <para>This is not pedantry about the spec - it is the one real-world malformation
    /// <see cref="JsonDocumentOptions"/> offers no switch for, and it is common. Pack authors
    /// write a multi-line <c>header.description</c> by pressing Enter inside the quotes.
    /// RFC 8259 forbids an unescaped U+0000-U+001F in a string, so System.Text.Json refuses the
    /// whole document; Newtonsoft accepted it silently, and so does Bedrock - these packs load
    /// and play in game. Measured across a 127-archive sample corpus: 2 of 123 manifests are
    /// like this, and without this pass both of those packs simply vanish from the pack browser,
    /// with no error the user could act on.</para>
    ///
    /// <para>The value produced is identical to what Newtonsoft read - a raw line feed becomes
    /// an escaped one, which decodes back to the same character. Comments are skipped rather
    /// than scanned, so a quote inside <c>// like "this"</c> cannot desynchronise the
    /// in-string state.</para>
    /// </summary>
    private static string? EscapeControlCharsInStrings(string text)
    {
        StringBuilder? builder = null;
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '/' && i + 1 < text.Length)
                {
                    // Comments are copied through wholesale: their contents are not JSON, and a
                    // quote inside one must not be read as a string delimiter.
                    if (text[i + 1] == '/')
                    {
                        var end = text.IndexOfAny(LineBreaks, i);
                        var stop = end < 0 ? text.Length : end;
                        builder?.Append(text, i, stop - i);
                        i = stop - 1;
                        continue;
                    }
                    if (text[i + 1] == '*')
                    {
                        var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        var stop = end < 0 ? text.Length : end + 2;
                        builder?.Append(text, i, stop - i);
                        i = stop - 1;
                        continue;
                    }
                }

                builder?.Append(c);
                continue;
            }

            // Inside a string literal.
            if (c == '\\')
            {
                builder?.Append(c);
                if (i + 1 < text.Length)
                {
                    builder?.Append(text[i + 1]);
                    i++;
                }
                continue;
            }

            if (c == '"')
            {
                inString = false;
                builder?.Append(c);
                continue;
            }

            if (c < 0x20)
            {
                builder ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
                builder.Append(c switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ => "\\u" + ((int)c).ToString("x4"),
                });
                continue;
            }

            builder?.Append(c);
        }

        return builder?.ToString();
    }

    private static readonly char[] LineBreaks = { '\r', '\n' };

    /// <summary>Like <see cref="ParseNode"/>, but null unless the root is a JSON object.</summary>
    public static JsonObject? ParseObject(string? text) => ParseNode(text) as JsonObject;

    /// <summary>Like <see cref="ParseNode"/>, but null unless the root is a JSON array.</summary>
    public static JsonArray? ParseArray(string? text) => ParseNode(text) as JsonArray;

    /// <summary>Reads and parses a file. Null on a missing/unreadable/unparseable file.</summary>
    public static JsonObject? ParseObjectFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return ParseObject(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JSON] Could not read '{path}': {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc cref="ParseObjectFile"/>
    public static async Task<JsonObject?> ParseObjectFileAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return ParseObject(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JSON] Could not read '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses from a stream without taking ownership of it.</summary>
    public static JsonObject? ParseObjectStream(Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            return ParseObject(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JSON] Could not read JSON stream: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rebuilds a <see cref="JsonElement"/> as a mutable node. Scalars are kept as cloned
    /// elements rather than converted to CLR values, so a number that nothing touches is
    /// written back out with exactly the digits it came in with.
    /// </summary>
    private static JsonNode? RebuildNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var prop in element.EnumerateObject())
                    obj[prop.Name] = RebuildNode(prop.Value); // a later duplicate key overwrites the earlier one
                return obj;

            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in element.EnumerateArray())
                    arr.Add(RebuildNode(item));
                return arr;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            default:
                return JsonValue.Create(element.Clone());
        }
    }

    // ─────────────────────────── Writing ───────────────────────────

    /// <summary>Indented JSON text, the formatting every file this app rewrites uses.</summary>
    public static string ToIndentedString(JsonNode node) => node.ToJsonString(IndentedWriteOptions);

    /// <summary>Writes <paramref name="node"/> to <paramref name="path"/>, indented.</summary>
    public static void WriteIndented(string path, JsonNode node)
        => File.WriteAllText(path, ToIndentedString(node));

    // ─────────────────────── Value coercion ────────────────────────
    //
    // Every reader below is lenient in the same direction Bedrock is: a value of the wrong
    // JSON kind is "missing", never an exception. Quoted numbers ("format_version": "2") are
    // accepted because packs in the wild ship them.

    /// <summary>The backing element of a parsed scalar, when there is one.</summary>
    private static bool TryGetElement(JsonNode? node, out JsonElement element)
    {
        element = default;
        return node is JsonValue value && value.TryGetValue(out element);
    }

    /// <summary>
    /// A scalar's string value. Strings come back verbatim; numbers and booleans come back as
    /// written. Objects and arrays are not scalars, so they come back null rather than as a
    /// blob of JSON text.
    /// </summary>
    public static string? GetString(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        try
        {
            if (TryGetElement(value, out var element))
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
            }

            // A node this app assigned itself rather than parsed.
            return value.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// An int, coercing a quoted number ("2") the way real manifests need. Null if the value
    /// is missing, not a scalar, or not a whole number.
    /// </summary>
    public static int? GetInt(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        try
        {
            if (TryGetElement(value, out var element))
            {
                if (element.ValueKind == JsonValueKind.Number)
                    return element.TryGetInt32(out var i) ? i : null;
                if (element.ValueKind == JsonValueKind.String)
                    return int.TryParse(element.GetString(), out var parsed) ? parsed : null;
                return null;
            }

            return value.TryGetValue<int>(out var direct)
                ? direct
                : int.TryParse(value.ToString(), out var fallback) ? fallback : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A double, coercing a quoted number. Returns false (leaving <paramref name="value"/> at
    /// 0) for anything missing or non-numeric.
    /// </summary>
    public static bool TryGetDouble(JsonNode? node, out double value)
    {
        value = 0.0;
        if (node is not JsonValue jsonValue) return false;

        try
        {
            if (TryGetElement(jsonValue, out var element))
            {
                if (element.ValueKind == JsonValueKind.Number)
                    return element.TryGetDouble(out value);
                if (element.ValueKind == JsonValueKind.String)
                    return double.TryParse(element.GetString(), out value);
                return false;
            }

            if (jsonValue.TryGetValue<double>(out value)) return true;
            return double.TryParse(jsonValue.ToString(), out value);
        }
        catch
        {
            value = 0.0;
            return false;
        }
    }

    /// <summary>
    /// A bool. Only a real JSON boolean counts - a string "true" does not, because nothing in
    /// this app has ever needed it to and silently accepting it would hide a malformed file.
    /// </summary>
    public static bool? GetBool(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        try
        {
            if (TryGetElement(value, out var element))
            {
                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null,
                };
            }

            return value.TryGetValue<bool>(out var direct) ? direct : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// An array of whole numbers, e.g. a manifest's <c>"version": [1, 2, 3]</c>. Every element
    /// must be a real integer; one that isn't makes the whole array null, because a partially
    /// understood version is worse than no version. Pass <paramref name="requiredLength"/> to
    /// also require an exact length.
    /// </summary>
    public static int[]? GetIntArray(JsonNode? node, int? requiredLength = null)
    {
        if (node is not JsonArray array) return null;
        if (requiredLength is int expected && array.Count != expected) return null;

        var result = new int[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            // Deliberately stricter than GetInt: a quoted "1" is not an integer version
            // component, and neither is 1.5. Anything but a genuine JSON integer disqualifies.
            if (array[i] is not JsonValue value) return null;
            if (TryGetElement(value, out var element))
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out result[i]))
                    return null;
            }
            else if (!value.TryGetValue(out result[i]))
            {
                return null;
            }
        }
        return result;
    }

    /// <summary>
    /// Every string in a JSON array, skipping entries of any other kind. Empty (never null)
    /// when the node is missing or isn't an array.
    ///
    /// <para>Genuine JSON strings only - deliberately stricter than <see cref="GetString"/>,
    /// which renders a number or boolean as text. Every caller of this reads a list of *names*
    /// (capabilities, blacklist patterns, authors), and a number in one of those is malformed
    /// data to drop, not a name to invent.</para>
    /// </summary>
    public static List<string> GetStringArray(JsonNode? node)
    {
        var result = new List<string>();
        if (node is not JsonArray array) return result;

        foreach (var item in array)
        {
            if (item is not JsonValue value || value.GetValueKind() != JsonValueKind.String) continue;
            if (GetString(value) is string s) result.Add(s);
        }
        return result;
    }

    /// <summary>
    /// Walks a dotted path (<c>"minecraft:fog_settings.volumetric"</c>) through nested objects,
    /// the one thing Newtonsoft's <c>SelectToken</c> was actually used for here. Null the moment
    /// any segment is missing or isn't an object. Keys containing a literal '.' are not
    /// reachable this way - none of the files this app reads has one.
    /// </summary>
    public static JsonNode? SelectPath(JsonNode? node, string dottedPath)
    {
        if (node == null || string.IsNullOrEmpty(dottedPath)) return null;

        var current = node;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(segment, out current) || current == null) return null;
        }
        return current;
    }

    /// <summary>
    /// Replaces an array's contents with <paramref name="values"/> as JSON strings. Written as
    /// its own helper because the obvious spelling - <c>array.Add(someString)</c> - binds to the
    /// reflection-based generic <c>Add&lt;T&gt;</c> and trips the trim analyzer.
    /// </summary>
    public static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }
}
