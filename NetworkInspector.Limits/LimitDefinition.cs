// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>
/// The single declaration of a limit: its key, unit, fixed default, hard range, whether it may be switched off or changed, and
/// the seven end-user texts. The setting, the command-line option, the help, the hit text, and any user interface all read
/// this one object. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// Declare a definition as a static property of the catalog class of the project that enforces the limit
/// (<c>public static LimitDefinition MaxEntrySize { get; } = new(...)</c>) and list it in the catalog's <c>All</c>. The
/// constructor rejects an inconsistent declaration (a default outside the range, a key that names its unit, a missing text) so
/// that a self-contradicting limit never reaches a user; a failure there is a bug of the declaring code, not an expected failure.
/// <para>The default is one number the declaring code chose for the machine it sized the application for; nothing here reads
/// the memory or the processors of the machine the code runs on.</para>
/// <para>Hard range: <see cref="Minimum"/> and <see cref="Maximum"/> are the only accepted numbers. <see cref="LimitValue.Off"/>
/// is accepted unless <see cref="AllowOff"/> is <see langword="false"/> (a ceiling that protects other users or the system).
/// <see cref="ReadOnly"/> says the value cannot change after creation: only the default is valid.</para>
/// <para>Enforcing code does not read the definition per item: it reads <see cref="Limit.Ceiling"/> of a <see cref="Limit"/>.
/// Members that build text allocate and belong on cold paths (describe, hit, parse).</para>
/// <para>Architecture: docs/architecture.md § Limits.</para>
/// </remarks>
/// <seealso cref="ILimitUnit"/>
/// <seealso cref="LimitValue"/>
/// <seealso cref="LimitTexts"/>
/// <seealso cref="Limit"/>
/// <seealso cref="LimitCatalog"/>
public sealed class LimitDefinition
{
    #region Declaration

    /// <summary>Product tuning: a title must fit one table cell and one help heading.</summary>
    private const int _MaxTitleLength = 60;

    /// <summary>Declares a limit.</summary>
    /// <param name="key">
    /// The settings key <c>group.localName</c>: a lower-case group (dot-separated segments of letters and digits) and a local
    /// name that starts with a lower-case letter followed by letters, digits, or <c>_</c>, and does not end with a word of the
    /// unit (<see cref="ILimitUnit.ForbiddenKeySuffixes"/>).
    /// </param>
    /// <param name="unit">What the values measure; parses and formats the text form.</param>
    /// <param name="defaultValue">The default in the base unit; inside [<paramref name="minimum"/>, <paramref name="maximum"/>].</param>
    /// <param name="minimum">The smallest accepted value in the base unit; at least 0 and at least <see cref="ILimitUnit.SmallestMinimum"/>.</param>
    /// <param name="maximum">The largest accepted value in the base unit; not below <paramref name="minimum"/>.</param>
    /// <param name="texts">The seven end-user texts; none may be empty; <see cref="LimitTexts.Title"/> is the UI name.</param>
    /// <param name="allowOff">
    /// Whether the user may switch the limit off with <c>off</c>. <see langword="false"/> for a ceiling that protects other
    /// users or the system; the value <c>off</c> is then refused like a number outside the range.
    /// </param>
    /// <param name="readOnly">Whether the value is fixed after creation: the default is the only valid value.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key, range, default, or a text is invalid; the message names the key and the field.</exception>
    public LimitDefinition(string key, ILimitUnit unit, long defaultValue, long minimum, long maximum, LimitTexts texts,
        bool allowOff = true, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(texts);

        // The key first: every later message names it. The default is checked against the range after the range itself is sound.
        _CheckKey(key, unit);
        _CheckRange(key, unit, minimum, maximum);
        _CheckDefault(key, defaultValue, minimum, maximum);
        _CheckTexts(key, texts);

        int lastDot = key.LastIndexOf('.');
        Key = key;
        Group = key[..lastDot];
        LocalName = key[(lastDot + 1)..];
        Unit = unit;
        Default = defaultValue;
        DefaultValue = LimitValue.Of(defaultValue);
        Minimum = minimum;
        Maximum = maximum;
        Texts = texts;
        AllowOff = allowOff;
        ReadOnly = readOnly;
    }

    #endregion

    #region Declaration data

    /// <summary>The settings key, for example <c>archive.maxEntrySize</c>.</summary>
    public string Key { get; }

    /// <summary>The settings group: the part of <see cref="Key"/> before the last dot, for example <c>archive</c>.</summary>
    public string Group { get; }

    /// <summary>
    /// The name inside the group: the part of <see cref="Key"/> after the last dot, for example <c>maxEntrySize</c>; also
    /// the name of an option that exposes the limit.
    /// </summary>
    public string LocalName { get; }

    /// <summary>What the values measure; the text form is parsed and formatted through it.</summary>
    public ILimitUnit Unit { get; }

    /// <summary>The default in the base unit.</summary>
    public long Default { get; }

    /// <summary>The default as a value; inside the range by construction.</summary>
    public LimitValue DefaultValue { get; }

    /// <summary>The smallest accepted value in the base unit.</summary>
    public long Minimum { get; }

    /// <summary>The largest accepted value in the base unit.</summary>
    public long Maximum { get; }

    /// <summary>The seven texts that explain the limit to the user; <see cref="LimitTexts.Title"/> is the UI name.</summary>
    public LimitTexts Texts { get; }

    /// <summary>Whether the user may switch the limit off; when not, <see cref="LimitValue.Off"/> is refused everywhere a value is checked.</summary>
    public bool AllowOff { get; }

    /// <summary>Whether the value is fixed after creation; a store refuses every value but <see cref="DefaultValue"/>.</summary>
    public bool ReadOnly { get; }

    #endregion

    #region Validation

    /// <summary>Checks <paramref name="value"/> against the hard range.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="problem">The key and the allowed range when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> for a value inside the range, and for <see cref="LimitValue.Off"/> when <see cref="AllowOff"/> is
    /// <see langword="true"/>. Read-only is not checked here: it belongs to the holder of the value.
    /// </returns>
    public bool TryValidate(LimitValue value, [NotNullWhen(false)] out string? problem)
    {
        if (TryValidateRange(value, out string? rangeProblem))
        {
            problem = null;
            return true;
        }

        problem = string.Create(CultureInfo.InvariantCulture, $"{Key}: {rangeProblem}");
        return false;
    }

    /// <summary>
    /// The range check without the key in front of the message, for a store that names the setting in its own messages
    /// (a settings library prefixes the key itself). <see cref="TryValidate"/> is the same check with the key in front.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="problem">
    /// <c>allowed {range}</c> when the method returns <see langword="false"/>; for <see cref="LimitValue.Off"/> of a limit that
    /// cannot be switched off, <c>off is not accepted; allowed {range}</c>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> for a value inside the range, and for <see cref="LimitValue.Off"/> when <see cref="AllowOff"/> is
    /// <see langword="true"/>.
    /// </returns>
    public bool TryValidateRange(LimitValue value, [NotNullWhen(false)] out string? problem)
    {
        // Off passes only when the definition allows it (switching off is the user's decision, not every limit's);
        // every other value must lie in the hard range.
        if (value.IsOff)
        {
            problem = AllowOff
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"off is not accepted; allowed {FormatRange()}");
            return AllowOff;
        }

        if (value.Value >= Minimum && value.Value <= Maximum)
        {
            problem = null;
            return true;
        }

        problem = string.Create(CultureInfo.InvariantCulture, $"allowed {FormatRange()}");
        return false;
    }

    /// <summary>Throws when <paramref name="value"/> is outside the hard range; for <see langword="init"/> accessors of option properties.</summary>
    /// <param name="value">The value to check.</param>
    /// <param name="parameterName">The name of the property or parameter that carries the value.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the range; the message holds the key and the allowed range.</exception>
    public void ThrowIfInvalid(LimitValue value, string parameterName)
    {
        if (!TryValidate(value, out string? problem))
        {
            throw new ArgumentOutOfRangeException(parameterName, value.ToString(), problem);
        }
    }

    #endregion

    #region Text form

    /// <summary>Parses the text form of a value: a number in the form of the <see cref="Unit"/>, or <c>off</c> when allowed.</summary>
    /// <param name="text">The text, for example <c>256MiB</c>, <c>90s</c>, <c>10_000</c>, or <c>off</c>.</param>
    /// <param name="value">The value when the method returns <see langword="true"/>.</param>
    /// <param name="problem">The key, what is wrong, and what to write instead, when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the text is exactly representable and inside the range.</returns>
    public bool TryParse(ReadOnlySpan<char> text, out LimitValue value, [NotNullWhen(false)] out string? problem)
    {
        // Shape first (the grammar of the unit), then range, so both problems speak in the words of this definition.
        if (!TryParseShape(text, out value, out string? shapeProblem))
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"{Key}: {shapeProblem}");
            return false;
        }

        return TryValidate(value, out problem);
    }

    /// <summary>
    /// Parses the shape of a value text without the range check and without the key in front of the problem. A store with
    /// codecs of its own uses it, then applies the range with <see cref="TryValidateRange"/> and names the setting itself.
    /// </summary>
    /// <param name="text">The text to parse; white space around it is ignored.</param>
    /// <param name="value">The value when the method returns <see langword="true"/>.</param>
    /// <param name="problem">What is wrong and what to write instead, when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the text is exactly representable (it may still be outside the range); the text <c>off</c>
    /// is refused here when <see cref="AllowOff"/> is <see langword="false"/>.
    /// </returns>
    /// <remarks>Sync: <see cref="ILimitUnit.TryParse"/> receives only the trimmed text that is neither empty nor <c>off</c>.</remarks>
    public bool TryParseShape(ReadOnlySpan<char> text, out LimitValue value, [NotNullWhen(false)] out string? problem)
    {
        value = default;

        // White space is never significant at either end; empty and "off" are decided here so that no unit repeats the rule.
        ReadOnlySpan<char> trimmed = text.Trim();
        if (trimmed.IsEmpty)
        {
            string offTail = AllowOff
                ? ", or off"
                : "";
            problem = string.Create(CultureInfo.InvariantCulture, $"empty value; write a whole number such as {Unit.Example}{offTail}");
            return false;
        }

        if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (!AllowOff)
            {
                problem = string.Create(CultureInfo.InvariantCulture, $"off is not accepted for this limit; write a whole number such as {Unit.Example}");
                return false;
            }

            value = LimitValue.Off;
            problem = null;
            return true;
        }

        if (!Unit.TryParse(trimmed, Maximum, out long number, out problem))
        {
            return false;
        }

        value = LimitValue.Of(number);
        return true;
    }

    /// <summary>Formats <paramref name="value"/> in the largest unit word that represents it exactly.</summary>
    /// <param name="value">The value to format.</param>
    /// <returns><c>256 MiB</c>, <c>90 s</c>, <c>10000</c>, or <c>off</c>; the text parses back to the value.</returns>
    public string Format(LimitValue value) => value.IsOff
        ? "off"
        : Unit.Format(value.Value);

    /// <summary>Describes the hard range, for example <c>1 KiB – 8 GiB, or off</c>.</summary>
    /// <returns>
    /// The smallest and the largest value in the unit form, followed by <c>, or off</c> when <see cref="AllowOff"/> is
    /// <see langword="true"/>.
    /// </returns>
    public string FormatRange()
    {
        string offTail = AllowOff
            ? ", or off"
            : "";
        return string.Create(CultureInfo.InvariantCulture, $"{Unit.Format(Minimum)} – {Unit.Format(Maximum)}{offTail}");
    }

    #endregion

    #region Declaration checks

    /// <summary>Checks the key: group and name present, group lower case, name without a word of its unit at the end.</summary>
    /// <param name="key">The key to check.</param>
    /// <param name="unit">The unit whose forbidden key suffixes the name must not end with.</param>
    /// <exception cref="ArgumentException">The key is malformed.</exception>
    private static void _CheckKey(string key, ILimitUnit unit)
    {
        int lastDot = key.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == key.Length - 1)
        {
            throw _Invalid(key, nameof(key), "the key needs a group and a name, written group.name");
        }

        ReadOnlySpan<char> group = key.AsSpan(0, lastDot);
        ReadOnlySpan<char> name = key.AsSpan(lastDot + 1);

        // Each group segment: lower-case letter first, then lower-case letters and digits. This library keeps no reserved group
        // name: a store that reserves one (the settings library reserves "default") refuses the key where it becomes a setting.
        int segmentStart = 0;
        for (int index = 0; index <= group.Length; index++)
        {
            if (index == group.Length || group[index] == '.')
            {
                if (!_IsGroupSegment(group[segmentStart..index]))
                {
                    throw _Invalid(key, nameof(key), "the group is lower case: segments of letters and digits that start with a letter, separated by dots");
                }

                segmentStart = index + 1;
            }
        }

        if (!_IsLocalName(name))
        {
            throw _Invalid(key, nameof(key), "the name starts with a lower-case letter and has letters, digits, and _ only");
        }

        // A key never says the unit (it is part of the value and shown by the definition), so that changing the unit of a limit
        // never makes a key lie. The unit knows which endings say it: maxItems is fine, waitMs is not.
        IReadOnlyList<string> suffixes = unit.ForbiddenKeySuffixes;
        for (int index = 0; index < suffixes.Count; index++)
        {
            if (name.EndsWith(suffixes[index], StringComparison.Ordinal))
            {
                string detail = string.Create(CultureInfo.InvariantCulture,
                    $"the name must not end with the unit word '{suffixes[index]}'; the unit belongs to the value, not the key");
                throw _Invalid(key, nameof(key), detail);
            }
        }
    }

    /// <summary>Checks one segment of a group name.</summary>
    /// <param name="segment">The text between two dots.</param>
    /// <returns><see langword="true"/> for a lower-case ASCII letter followed by lower-case letters and digits.</returns>
    private static bool _IsGroupSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || !char.IsAsciiLetterLower(segment[0]))
        {
            return false;
        }

        for (int index = 1; index < segment.Length; index++)
        {
            if (!char.IsAsciiLetterLower(segment[index]) && !char.IsAsciiDigit(segment[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks the local name of a key (lowerCamel or snake_case).</summary>
    /// <param name="name">The text after the last dot.</param>
    /// <returns><see langword="true"/> for a lower-case ASCII letter followed by ASCII letters, digits, and underscores.</returns>
    private static bool _IsLocalName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || !char.IsAsciiLetterLower(name[0]))
        {
            return false;
        }

        for (int index = 1; index < name.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(name[index]) && name[index] != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks the hard range.</summary>
    /// <param name="key">The key, for messages.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="minimum">The smallest value.</param>
    /// <param name="maximum">The largest value.</param>
    /// <exception cref="ArgumentException">The range is negative, reversed, or may be below the smallest minimum of the unit.</exception>
    private static void _CheckRange(string key, ILimitUnit unit, long minimum, long maximum)
    {
        if (minimum < 0)
        {
            throw _Invalid(key, nameof(minimum), "the minimum is negative");
        }

        if (minimum > maximum)
        {
            throw _Invalid(key, nameof(minimum), "the minimum is above the maximum");
        }

        // A zero duration would make every wait fail at once; "no wait" is spelled off, not 0. The unit says which of its values
        // are like that, so a domain unit with the same trap declares it once.
        if (minimum < unit.SmallestMinimum)
        {
            string detail = string.Create(CultureInfo.InvariantCulture, $"the minimum for this unit is at least {unit.SmallestMinimum}");
            throw _Invalid(key, nameof(minimum), detail);
        }
    }

    /// <summary>Checks that the default lies inside the range.</summary>
    /// <param name="key">The key, for messages.</param>
    /// <param name="defaultValue">The default in the base unit.</param>
    /// <param name="minimum">The smallest value.</param>
    /// <param name="maximum">The largest value.</param>
    /// <exception cref="ArgumentException">The default is below the minimum or above the maximum.</exception>
    private static void _CheckDefault(string key, long defaultValue, long minimum, long maximum)
    {
        // A default that is clamped silently would hide a wrong number in the declaration; it is refused instead.
        if (defaultValue < minimum || defaultValue > maximum)
        {
            throw _Invalid(key, nameof(defaultValue), "the default is outside the range of the limit");
        }
    }

    /// <summary>Checks the seven texts.</summary>
    /// <param name="key">The key, for messages.</param>
    /// <param name="texts">The texts to check.</param>
    /// <exception cref="ArgumentException">A text is empty, contains a control character, or has the wrong ending.</exception>
    private static void _CheckTexts(string key, LimitTexts texts)
    {
        _CheckText(key, nameof(texts.Title), texts.Title);
        if (texts.Title.Length > _MaxTitleLength || texts.Title.EndsWith('.'))
        {
            string detail = string.Create(CultureInfo.InvariantCulture, $"the title has at most {_MaxTitleLength} characters and no final period");
            throw _Invalid(key, nameof(texts), nameof(texts.Title), detail);
        }

        _CheckSentence(key, nameof(texts.Description), texts.Description);
        _CheckSentence(key, nameof(texts.Reason), texts.Reason);
        _CheckSentence(key, nameof(texts.Benefit), texts.Benefit);
        _CheckSentence(key, nameof(texts.SmallerValues), texts.SmallerValues);
        _CheckSentence(key, nameof(texts.LargerValues), texts.LargerValues);
        _CheckSentence(key, nameof(texts.OffEffect), texts.OffEffect);
    }

    /// <summary>Checks a text that is a sentence: valid text that ends with a period.</summary>
    /// <param name="key">The key, for messages.</param>
    /// <param name="field">The name of the text field, for messages.</param>
    /// <param name="text">The text to check.</param>
    /// <exception cref="ArgumentException">The text is invalid.</exception>
    private static void _CheckSentence(string key, string field, string text)
    {
        _CheckText(key, field, text);
        if (!text.EndsWith('.'))
        {
            throw _Invalid(key, nameof(LimitTexts), field, "the text is a sentence and ends with a period");
        }
    }

    /// <summary>Checks that a text is present and free of control characters (line breaks would break tables and one-line help).</summary>
    /// <param name="key">The key, for messages.</param>
    /// <param name="field">The name of the text field, for messages.</param>
    /// <param name="text">The text to check.</param>
    /// <exception cref="ArgumentException">The text is missing, empty, or contains a control character.</exception>
    private static void _CheckText(string key, string field, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw _Invalid(key, nameof(LimitTexts), field, "the text is missing or empty");
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsControl(text[index]))
            {
                throw _Invalid(key, nameof(LimitTexts), field, "the text contains a control character");
            }
        }
    }

    /// <summary>Creates the exception for a broken declaration whose field is a constructor parameter.</summary>
    /// <param name="key">The key, for the message.</param>
    /// <param name="parameter">The constructor parameter that is wrong; also named in the message.</param>
    /// <param name="detail">What is wrong.</param>
    /// <returns>The exception to throw.</returns>
    private static ArgumentException _Invalid(string key, string parameter, string detail) => _Invalid(key, parameter, parameter, detail);

    /// <summary>Creates the exception for a broken declaration.</summary>
    /// <param name="key">The key, for the message.</param>
    /// <param name="parameter">The constructor parameter that holds the wrong data.</param>
    /// <param name="field">The field inside the parameter that is wrong; named in the message so a person finds it.</param>
    /// <param name="detail">What is wrong.</param>
    /// <returns>The exception to throw.</returns>
    private static ArgumentException _Invalid(string key, string parameter, string field, string detail) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Limit '{key}': {detail} ({field})."), parameter);

    #endregion
}
