using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vanilla_RTX_App.Core;

// Add caching if it isn't fast enough

/// <summary>
/// Read-only lookup for app-wide constants stored in Assets/constants.json
/// (resolved relative to AppContext.BaseDirectory — works for both packaged
/// and unpackaged builds). Parsed once, kept alive for the process lifetime.
///
/// Three ways to call it, all backed by the same lookup/search logic:
///
///   1) Fully explicit (original behavior, unchanged):
///        Constants.RetrieveConstant&lt;string&gt;("apiBaseUrl")
///
///   2) Explicit type, inferred key — key comes from the name of the field
///      or property you're initializing, via [CallerMemberName]:
///        public string ApiBaseUrl = Constants.RetrieveConstant&lt;string&gt;();
///
///   3) Inferred type AND inferred key — relies on the implicit conversions
///      on ConstantValue, so the field's declared type drives the lookup:
///        public bool EnableLogging = Constants.RetrieveConstant();
///
/// IMPORTANT CALLERMEMBERNAME CAVEAT: this only reliably infers the right
/// name for FIELD initializers and PROPERTY initializers/expression bodies
/// (`public bool X = ...;` / `public bool X => ...;` / `public bool X { get; } = ...;`).
/// Inside an ordinary method body, CallerMemberName resolves to the
/// *enclosing method's* name, not a local variable's name — so
/// `var x = Constants.RetrieveConstant<bool>();` inside a method will look
/// up the method's name, not "x". For locals (or `var` in general — see
/// below), pass the key explicitly: `Constants.RetrieveConstant<bool>("x")`.
///
/// VAR CAVEAT for form (3): `var x = Constants.RetrieveConstant();` gives you
/// a raw ConstantValue, not a usable value — implicit conversions only fire
/// when there's a known target type to convert to, and `var` has none. Use
/// form (2) (`Constants.RetrieveConstant<T>()`) whenever you're using `var`
/// or otherwise want to be explicit about the type.
///
/// NULLABLE CAVEAT for form (3): ConstantValue's conversions resolve through
/// the non-nullable types internally, so assigning to a nullable target
/// (e.g. `bool? x = Constants.RetrieveConstant();`) will NOT give you a true
/// null on a missing/mismatched key — it'll give you the wrapped default
/// (false/0/etc). If you need to tell "missing" apart from "found and it's
/// the default value," use form (2) with a nullable type argument:
/// `Constants.RetrieveConstant<bool?>()`, which preserves null-on-not-found.
///
/// Matching is now case-insensitive (was case-sensitive before).
/// </summary>
public static class Constants
{
    private const string folderName = "Core";
    private const string fileName = "constants.json";

    private static readonly Lazy<JsonDocument?> _document =
        new(LoadDocument, isThreadSafe: true);

    /// <summary>
    /// Explicit-type form. Pass a key to behave exactly as before; omit it
    /// and the name of the field/property being initialized is used instead
    /// (see CallerMemberName caveat above).
    /// </summary>
    public static T? RetrieveConstant<T>([CallerMemberName] string? key = null)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
                return default;

            var root = _document.Value?.RootElement;
            if (root is null)
                return default;

            foreach (var candidate in FindMatches(root.Value, key))
            {
                if (TryConvert<T>(candidate, out var value))
                    return value;
            }

            return default;
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Type-inferring form. Returns a ConstantValue whose implicit
    /// conversions perform the actual lookup once the compiler knows what
    /// type you're assigning it to (see VAR and NULLABLE caveats above).
    /// </summary>
    public static ConstantValue RetrieveConstant([CallerMemberName] string? key = null)
        => new(key);

    private static JsonDocument? LoadDocument()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, folderName, fileName);
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            return JsonDocument.Parse(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> FindMatches(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                yield return prop.Value;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in FindMatches(prop.Value, key))
                    yield return nested;
            }
        }
    }

    private static bool TryConvert<T>(JsonElement element, out T? value)
    {
        value = default;

        try
        {
            var targetType = typeof(T);
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var s = element.GetString();
                    if (underlying == typeof(string)) { value = (T)(object)s!; return true; }
                    if (underlying == typeof(Guid) && Guid.TryParse(s, out var g)) { value = (T)(object)g; return true; }
                    if (underlying == typeof(DateTime) && DateTime.TryParse(s, out var dt)) { value = (T)(object)dt; return true; }
                    if (underlying == typeof(DateTimeOffset) && DateTimeOffset.TryParse(s, out var dto)) { value = (T)(object)dto; return true; }
                    if (underlying == typeof(TimeSpan) && TimeSpan.TryParse(s, out var ts)) { value = (T)(object)ts; return true; }
                    if (underlying == typeof(char) && s?.Length == 1) { value = (T)(object)s[0]; return true; }
                    if (underlying.IsEnum && s is not null && Enum.TryParse(underlying, s, ignoreCase: true, out var ev)) { value = (T)ev; return true; }
                    return false;

                case JsonValueKind.Number:
                    if (underlying == typeof(int) && element.TryGetInt32(out var i)) { value = (T)(object)i; return true; }
                    if (underlying == typeof(long) && element.TryGetInt64(out var l)) { value = (T)(object)l; return true; }
                    if (underlying == typeof(short) && element.TryGetInt16(out var sh)) { value = (T)(object)sh; return true; }
                    if (underlying == typeof(byte) && element.TryGetByte(out var by)) { value = (T)(object)by; return true; }
                    if (underlying == typeof(uint) && element.TryGetUInt32(out var ui)) { value = (T)(object)ui; return true; }
                    if (underlying == typeof(ulong) && element.TryGetUInt64(out var ul)) { value = (T)(object)ul; return true; }
                    if (underlying == typeof(double) && element.TryGetDouble(out var d)) { value = (T)(object)d; return true; }
                    if (underlying == typeof(float) && element.TryGetSingle(out var f)) { value = (T)(object)f; return true; }
                    if (underlying == typeof(decimal) && element.TryGetDecimal(out var dec)) { value = (T)(object)dec; return true; }
                    return false;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    if (underlying == typeof(bool)) { value = (T)(object)element.GetBoolean(); return true; }
                    return false;

                default:
                    // null, array, or object — not a scalar we know how to hand back.
                    return false;
            }
        }
        catch
        {
            value = default;
            return false;
        }
    }
}

/// <summary>
/// Deferred, type-inferring wrapper returned by the zero-generic
/// Constants.RetrieveConstant() overload. Carries only the resolved key;
/// the actual JSON lookup and type conversion happen in whichever implicit
/// operator the compiler selects, based on the assignment's target type.
/// Each operator delegates back into Constants.RetrieveConstant&lt;T&gt;, so
/// there's exactly one place the real lookup logic lives.
/// </summary>
public readonly struct ConstantValue
{
    private readonly string? _key;

    internal ConstantValue(string? key) => _key = key;

    public static implicit operator string?(ConstantValue cv) => Constants.RetrieveConstant<string>(cv._key);
    public static implicit operator bool(ConstantValue cv) => Constants.RetrieveConstant<bool>(cv._key);
    public static implicit operator int(ConstantValue cv) => Constants.RetrieveConstant<int>(cv._key);
    public static implicit operator long(ConstantValue cv) => Constants.RetrieveConstant<long>(cv._key);
    public static implicit operator short(ConstantValue cv) => Constants.RetrieveConstant<short>(cv._key);
    public static implicit operator byte(ConstantValue cv) => Constants.RetrieveConstant<byte>(cv._key);
    public static implicit operator uint(ConstantValue cv) => Constants.RetrieveConstant<uint>(cv._key);
    public static implicit operator ulong(ConstantValue cv) => Constants.RetrieveConstant<ulong>(cv._key);
    public static implicit operator double(ConstantValue cv) => Constants.RetrieveConstant<double>(cv._key);
    public static implicit operator float(ConstantValue cv) => Constants.RetrieveConstant<float>(cv._key);
    public static implicit operator decimal(ConstantValue cv) => Constants.RetrieveConstant<decimal>(cv._key);
    public static implicit operator Guid(ConstantValue cv) => Constants.RetrieveConstant<Guid>(cv._key);
    public static implicit operator DateTime(ConstantValue cv) => Constants.RetrieveConstant<DateTime>(cv._key);
    public static implicit operator DateTimeOffset(ConstantValue cv) => Constants.RetrieveConstant<DateTimeOffset>(cv._key);
    public static implicit operator TimeSpan(ConstantValue cv) => Constants.RetrieveConstant<TimeSpan>(cv._key);
    public static implicit operator char(ConstantValue cv) => Constants.RetrieveConstant<char>(cv._key);
}
