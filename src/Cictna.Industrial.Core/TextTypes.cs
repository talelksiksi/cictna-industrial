using System.Globalization;

namespace Cictna.Industrial.Core;

/// <summary>
/// A name qualified by a namespace index, used for browse names (for example <c>2:Speed</c>).
/// </summary>
/// <param name="NamespaceIndex">Namespace index of the name.</param>
/// <param name="Name">The name; may be null.</param>
public readonly record struct QualifiedName(ushort NamespaceIndex, string? Name)
{
    /// <summary>Text form: <c>ns:name</c>, or just the name for namespace 0.</summary>
    public override string ToString() =>
        NamespaceIndex == 0 ? Name ?? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{NamespaceIndex}:{Name}");
}

/// <summary>Human-readable text with an optional locale such as "en-US" or "ar".</summary>
/// <param name="Locale">Locale identifier; null when not specified.</param>
/// <param name="Text">The text; may be null.</param>
public readonly record struct LocalizedText(string? Locale, string? Text)
{
    /// <summary>Creates text without a locale.</summary>
    /// <param name="text">The text.</param>
    public static LocalizedText FromText(string? text) => new(null, text);

    /// <summary>The text, or an empty string.</summary>
    public override string ToString() => Text ?? string.Empty;
}

/// <summary>An XML fragment carried as a value (built-in type XmlElement). Stored as text.</summary>
/// <param name="Value">The XML text; may be null.</param>
public readonly record struct XmlElement(string? Value)
{
    /// <summary>The XML text, or an empty string.</summary>
    public override string ToString() => Value ?? string.Empty;
}
