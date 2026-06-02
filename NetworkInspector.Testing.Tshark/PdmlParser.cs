// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Internal helpers for parsing Wireshark PDML (Packet Details Markup Language) XML.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="XName.LocalName"/>-based matching?</b>
/// Wireshark 4.x introduced a default XML namespace declaration on the PDML root element
/// (e.g. <c>xmlns="http://www.wireshark.org/pdml/2.0"</c>). Once that namespace is present
/// every child element's fully-qualified <see cref="XName"/> includes the namespace URI, so
/// the common unqualified lookup <c>element.Element("packet")</c> returns <see langword="null"/>
/// because the supplied literal <c>"packet"</c> resolves to the no-namespace name.
/// Matching on <see cref="XName.LocalName"/> instead ignores the namespace component and
/// works correctly across all tshark versions regardless of whether a namespace is declared.
/// </para>
/// </remarks>
internal static class PdmlParser
{
    /// <summary>
    /// Parses a PDML XML string and extracts the <see cref="PdmlField"/> entries whose
    /// <c>name</c> attribute matches one of <paramref name="fieldNames"/>. Searches
    /// recursively through all nested <c>&lt;field&gt;</c> elements.
    /// </summary>
    /// <param name="pdmlXml">Raw PDML XML produced by <c>tshark -T pdml</c>.</param>
    /// <param name="fieldNames">
    /// Field names to locate. Must be validated lowercase ASCII by the caller
    /// (enforced upstream by <c>ValidateTsharkFieldName</c>); <see cref="StringComparer.Ordinal"/>
    /// comparison is therefore both correct and maximally efficient.
    /// </param>
    /// <returns>Matched fields in document order.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="pdmlXml"/> is not well-formed XML.</exception>
    internal static List<PdmlField> ParseFields(string pdmlXml, string[] fieldNames)
    {
        List<PdmlField> result = [];
        // StringComparer.Ordinal is correct: ValidateTsharkFieldName enforces an
        // all-lowercase ASCII whitelist, and tshark PDML output uses lowercase names.
        HashSet<string> requested = new(fieldNames, StringComparer.Ordinal);

        try
        {
            XDocument doc = XDocument.Parse(pdmlXml);
            XElement? packet = ChildByLocalName(doc.Root, "packet");
            if (packet is null)
            {
                return result;
            }

            foreach (XElement fieldElement in DescendantsWithLocalName(packet, "field"))
            {
                string? name = fieldElement.Attribute("name")?.Value;
                if (name is not null && requested.Contains(name))
                {
                    result.Add(ParseFieldElement(fieldElement));
                    if (result.Count >= fieldNames.Length)
                    {
                        // Found everything the caller asked for.
                        break;
                    }
                }
            }
        }
        catch (System.Xml.XmlException ex)
        {
            string snippet = pdmlXml.Length > 200 ? pdmlXml[..200] : pdmlXml;
            throw new InvalidOperationException(
                $"tshark returned malformed PDML XML: {ex.Message}\nXML snippet: {snippet}", ex);
        }

        return result;
    }

    /// <summary>
    /// Parses a PDML XML string and extracts every <c>&lt;field&gt;</c> belonging to the
    /// <c>&lt;proto&gt;</c> element with the given name.
    /// </summary>
    /// <param name="pdmlXml">Raw PDML XML produced by <c>tshark -T pdml</c>.</param>
    /// <param name="protocolName">tshark protocol name (for example <c>eth</c>, <c>ip</c>, <c>tcp</c>).</param>
    /// <returns>All fields inside the matching protocol element.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="pdmlXml"/> is not well-formed XML.</exception>
    internal static List<PdmlField> ParseProtocolFields(string pdmlXml, string protocolName)
    {
        List<PdmlField> result = [];

        try
        {
            XDocument doc = XDocument.Parse(pdmlXml);
            XElement? packet = ChildByLocalName(doc.Root, "packet");
            if (packet is null)
            {
                return result;
            }

            foreach (XElement proto in DescendantsWithLocalName(packet, "proto"))
            {
                string? name = proto.Attribute("name")?.Value;
                if (!string.Equals(name, protocolName, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (XElement fieldElement in DescendantsWithLocalName(proto, "field"))
                {
                    if (fieldElement.Attribute("name")?.Value is not null)
                    {
                        result.Add(ParseFieldElement(fieldElement));
                    }
                }
            }
        }
        catch (System.Xml.XmlException ex)
        {
            string snippet = pdmlXml.Length > 200 ? pdmlXml[..200] : pdmlXml;
            throw new InvalidOperationException(
                $"tshark returned malformed PDML XML: {ex.Message}\nXML snippet: {snippet}", ex);
        }

        return result;
    }

    /// <summary>
    /// Builds a <see cref="PdmlField"/> from a single PDML <c>&lt;field&gt;</c> element.
    /// </summary>
    internal static PdmlField ParseFieldElement(XElement fieldElement)
    {
        string name = fieldElement.Attribute("name")?.Value ?? string.Empty;
        string? value = fieldElement.Attribute("value")?.Value;
        string? show = fieldElement.Attribute("show")?.Value;
        string? showName = fieldElement.Attribute("showname")?.Value;
        _ = int.TryParse(fieldElement.Attribute("pos")?.Value, out int pos);
        _ = int.TryParse(fieldElement.Attribute("size")?.Value, out int size);
        return new PdmlField(name, value, show, showName, pos, size);
    }

    /// <summary>
    /// Returns the first direct child of <paramref name="parent"/> whose
    /// <see cref="XName.LocalName"/> equals <paramref name="localName"/>, ignoring XML
    /// namespace, or <see langword="null"/> when no match exists.
    /// </summary>
    internal static XElement? ChildByLocalName(XElement? parent, string localName)
    {
        if (parent is null)
        {
            return null;
        }

        foreach (XElement c in parent.Elements())
        {
            if (string.Equals(c.Name.LocalName, localName, StringComparison.Ordinal))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates direct children of <paramref name="parent"/> whose
    /// <see cref="XName.LocalName"/> equals <paramref name="localName"/>, ignoring XML namespace.
    /// </summary>
    internal static IEnumerable<XElement> ChildrenWithLocalName(XElement parent, string localName)
    {
        foreach (XElement c in parent.Elements())
        {
            if (string.Equals(c.Name.LocalName, localName, StringComparison.Ordinal))
            {
                yield return c;
            }
        }
    }

    /// <summary>
    /// Enumerates all descendants of <paramref name="root"/> whose
    /// <see cref="XName.LocalName"/> equals <paramref name="localName"/>, ignoring XML namespace.
    /// </summary>
    internal static IEnumerable<XElement> DescendantsWithLocalName(XElement root, string localName)
    {
        foreach (XElement e in root.Descendants())
        {
            if (string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal))
            {
                yield return e;
            }
        }
    }
}
