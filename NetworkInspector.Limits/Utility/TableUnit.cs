// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Utility;

/// <summary>
/// An <see cref="ILimitUnit"/> made from a <see cref="UnitWordSet"/>: parses a whole number with an optional or required unit
/// word and formats a value in the largest word that represents it exactly. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// Grammar: <c>value := number ws? word?</c>; <c>number := digit ( "_"? digit )*</c> (no sign, fraction, exponent, or
/// hexadecimal form); <c>word</c> is one word of the set, ignoring case; a unit whose word is required refuses a bare number,
/// a unit without words takes only the number. Every ambiguous form is refused with the exact text to write instead.
/// <para>Security: the text comes from files, the command line, and later a user interface or a remote client. Mitigation: a
/// hand-written scanner (no regular expression, so no backtracking), a single forward pass, overflow checks instead of
/// exceptions, and a refusal that quotes at most <see cref="_MaxQuotedLength"/> characters of the input, so a hostile value
/// cannot amplify into a huge message. Rejected alternative: lenient parsing of ambiguous words (<c>MB</c> as 10^6): the same text would mean
/// different amounts in different tools. Residual risk: none beyond the linear scan of the input.</para>
/// <para>Cost: success allocates nothing; a refusal allocates the problem text (and a <see cref="BigInteger"/> for the
/// suggestion of a fraction, with at most <see cref="_MaxFractionDigits"/> fraction digits).</para>
/// <para>Sync: the grammar is described in the Limits README § Value grammar.</para>
/// </remarks>
/// <seealso cref="ILimitUnit"/>
/// <seealso cref="UnitWordSet"/>
/// <seealso cref="LimitUnits"/>
public sealed class TableUnit : ILimitUnit
{
    #region Configuration and state

    /// <summary>The refusal of a digit separator that is not between two digits.</summary>
    private const string _MisplacedSeparator = "misplaced digit separator; use _ only between two digits, for example 1_000";

    /// <summary>Security: a refusal quotes the offending text of the input; this cap keeps the message short for any input size.</summary>
    private const int _MaxQuotedLength = 32;

    /// <summary>Security: the suggestion for a fraction uses exact big-number arithmetic; this cap bounds its cost for hostile input.</summary>
    private const int _MaxFractionDigits = 18;

    /// <summary>Product tuning: a unit name must fit one refusal sentence.</summary>
    private const int _MaxNameLength = 32;

    /// <summary>The accepted words, smallest first.</summary>
    private readonly IReadOnlyList<UnitWord> _Words;

    /// <summary>The refused spellings with their replacements.</summary>
    private readonly IReadOnlyList<UnitWordSet.RefusedSpelling> _Refused;

    /// <summary>Whether a number without a word is refused.</summary>
    private readonly bool _WordRequired;

    /// <summary>What a refusal says the value takes, for example <c>bytes: B, KiB, MiB, GiB, TiB</c>.</summary>
    private readonly string _FamilyText;

    /// <summary>Declares a unit.</summary>
    /// <param name="name">
    /// What the values measure, plural and lower case, for example <c>bytes</c>; shown in the refusals. At most 32 characters.
    /// </param>
    /// <param name="example">A valid value of this unit, for example <c>256 MiB</c>; checked: it must parse as a value of this unit.</param>
    /// <param name="words">The accepted words; <see cref="UnitWordSet.None"/> for a unit that takes a plain number.</param>
    /// <param name="wordRequired">
    /// Whether a number without a word is refused, because a bare number would not say its scale (a duration). Needs at least one word.
    /// </param>
    /// <param name="smallestMinimum">
    /// The smallest minimum a definition of this unit may declare (<see cref="ILimitUnit.SmallestMinimum"/>); 1 where a zero value
    /// would refuse everything at once.
    /// </param>
    /// <param name="forbiddenKeySuffixes">
    /// Endings a key of a limit in this unit must not have (<see cref="ILimitUnit.ForbiddenKeySuffixes"/>); ASCII letters, digits, and <c>_</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/>, <paramref name="example"/>, or <paramref name="words"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The name is empty, too long, or has a control character; a required word has no word to be required; a suffix has a
    /// character outside the key alphabet; the example is not a value of the unit. The message names the unit.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="smallestMinimum"/> is negative.</exception>
    public TableUnit(string name, string example, UnitWordSet words, bool wordRequired = false, long smallestMinimum = 0,
        IReadOnlyList<string>? forbiddenKeySuffixes = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(example);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentOutOfRangeException.ThrowIfNegative(smallestMinimum);

        // Checks first, assignments last: a unit that fails a check never exists half-built.
        _CheckName(name);
        if (wordRequired && words.Words.Count == 0)
        {
            throw _Invalid(name, nameof(wordRequired), "a unit without words cannot require a word");
        }

        string[] suffixes = _CopySuffixes(name, forbiddenKeySuffixes);

        Name = name;
        Example = example;
        SmallestMinimum = smallestMinimum;
        ForbiddenKeySuffixes = Array.AsReadOnly(suffixes);
        _Words = words.Words;
        _Refused = words.Refused;
        _WordRequired = wordRequired;
        _FamilyText = _Words.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name}: whole numbers without a unit")
            : string.Create(CultureInfo.InvariantCulture, $"{name}: {_JoinWords(_Words)}");

        // The example is what every refusal teaches; an example that the unit itself refuses would teach a wrong spelling.
        if (!TryParse(example, long.MaxValue, out _, out string? exampleProblem))
        {
            throw _Invalid(
                name,
                nameof(example),
                string.Create(CultureInfo.InvariantCulture, $"the example '{example}' is not a value of the unit: {exampleProblem}"));
        }
    }

    /// <summary>What the values measure, plural and lower case, for example <c>bytes</c>.</summary>
    public string Name { get; }

    /// <inheritdoc/>
    public string Example { get; }

    /// <inheritdoc/>
    public long SmallestMinimum { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> ForbiddenKeySuffixes { get; }

    /// <summary>The unit name.</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;

    #endregion

    #region Parsing

    /// <inheritdoc/>
    public bool TryParse(ReadOnlySpan<char> text, long maximum, out long value, [NotNullWhen(false)] out string? problem)
    {
        value = 0;

        // The definition hands over trimmed text; trimming again is cheap and keeps a direct caller safe.
        ReadOnlySpan<char> trimmed = text.Trim();
        if (trimmed.IsEmpty)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"empty value; write a whole number such as {Example}");
            return false;
        }

        // Forms that look like numbers but are not part of the grammar get their own refusal before the scan starts.
        if (trimmed[0] is '+' or '-')
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"no sign; write a whole number such as {Example}");
            return false;
        }

        if (trimmed.Length >= 2 && trimmed[0] == '0' && trimmed[1] is 'x' or 'X')
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"whole number in decimal digits expected, no hexadecimal form; write {Example}");
            return false;
        }

        if (!_TryReadDigits(trimmed, out long number, out bool overflow, out int consumed, out problem))
        {
            return false;
        }

        ReadOnlySpan<char> rest = trimmed[consumed..];
        if (!rest.IsEmpty && !_AcceptsAfterDigits(rest, number, overflow, out problem))
        {
            return false;
        }

        // A number beyond long cannot be represented whatever the unit says.
        if (overflow)
        {
            problem = _TooLarge(maximum);
            return false;
        }

        if (!_TryResolveWord(rest, number, out long factor, out problem))
        {
            return false;
        }

        // Check before multiplying so that no product wraps around (Section 4.1, integer operations).
        if (number > long.MaxValue / factor)
        {
            problem = _TooLarge(maximum);
            return false;
        }

        value = number * factor;
        problem = null;
        return true;
    }

    /// <summary>Reads the leading whole number: digits with single <c>_</c> separators between digits.</summary>
    /// <param name="text">Trimmed, non-empty text.</param>
    /// <param name="number">The number read; meaningless when <paramref name="overflow"/> is <see langword="true"/>.</param>
    /// <param name="overflow">
    /// Becomes <see langword="true"/> once the number exceeds <see cref="long.MaxValue"/>; the scan continues so that the rest
    /// of the text is classified, but the number is no longer accumulated.
    /// </param>
    /// <param name="consumed">Index of the first character that is not part of the number.</param>
    /// <param name="problem">The refusal when the text does not start with a number or a separator is misplaced.</param>
    /// <returns><see langword="true"/> when a number was read.</returns>
    private bool _TryReadDigits(ReadOnlySpan<char> text, out long number, out bool overflow, out int consumed,
        [NotNullWhen(false)] out string? problem)
    {
        number = 0;
        overflow = false;
        consumed = 0;

        if (!char.IsAsciiDigit(text[0]))
        {
            string wanted = _WordRequired
                ? "a whole number with a unit"
                : "a whole number with an optional unit";
            problem = text[0] == '_'
                ? _MisplacedSeparator
                : string.Create(CultureInfo.InvariantCulture, $"expected {wanted}; for example {Example}");
            return false;
        }

        bool previousSeparator = false;
        int index = 0;
        for (; index < text.Length; index++)
        {
            char c = text[index];
            if (char.IsAsciiDigit(c))
            {
                previousSeparator = false;
                int digit = c - '0';
                if (!overflow)
                {
                    if (number > (long.MaxValue - digit) / 10)
                    {
                        overflow = true;
                    }
                    else
                    {
                        number = (number * 10) + digit;
                    }
                }

                continue;
            }

            if (c != '_')
            {
                break;
            }

            // A separator is only valid between two digits: not first (checked above), not doubled, not last (checked below).
            if (previousSeparator)
            {
                problem = _MisplacedSeparator;
                return false;
            }

            previousSeparator = true;
        }

        if (previousSeparator)
        {
            problem = _MisplacedSeparator;
            return false;
        }

        consumed = index;
        problem = null;
        return true;
    }

    /// <summary>Refuses the characters that can follow a number but never belong to the grammar: a fraction, a decimal comma, an exponent.</summary>
    /// <param name="rest">The non-empty text after the number.</param>
    /// <param name="number">The whole number before the fraction.</param>
    /// <param name="overflow">Whether the number exceeded <see cref="long.MaxValue"/>.</param>
    /// <param name="problem">The refusal when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the text after the number may still be a unit word.</returns>
    private bool _AcceptsAfterDigits(ReadOnlySpan<char> rest, long number, bool overflow, [NotNullWhen(false)] out string? problem)
    {
        switch (rest[0])
        {
            case '.':
                problem = _FractionProblem(rest, number, overflow);
                return false;
            case ',':
                problem = "no decimal comma or thousands separator; separate digits with _, for example 1_000";
                return false;
            case 'e' or 'E':
                problem = "whole number expected, no exponent form; write all digits, for example 1_000_000";
                return false;
            default:
                problem = null;
                return true;
        }
    }

    /// <summary>Reads the optional unit word after the number and returns its factor.</summary>
    /// <param name="rest">The text after the number, possibly empty.</param>
    /// <param name="number">The number before the unit, for the suggestions of a refusal.</param>
    /// <param name="factor">Base units per unit word; 1 when no word is given.</param>
    /// <param name="problem">The refusal when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the text is empty (base unit; a unit with a required word refuses it, because a bare 30 does
    /// not say seconds or milliseconds) or one known unit word.
    /// </returns>
    private bool _TryResolveWord(ReadOnlySpan<char> rest, long number, out long factor, [NotNullWhen(false)] out string? problem)
    {
        factor = 1;

        ReadOnlySpan<char> unitText = rest.TrimStart(" \t");
        if (unitText.IsEmpty)
        {
            if (_WordRequired)
            {
                problem = _MissingWordProblem(number);
                return false;
            }

            problem = null;
            return true;
        }

        int length = 0;
        while (length < unitText.Length && IsWordChar(unitText[length]))
        {
            length++;
        }

        // Something that is neither a word character nor white space after the number, such as a second number.
        if (length == 0)
        {
            problem = _Unexpected(unitText);
            return false;
        }

        ReadOnlySpan<char> word = unitText[..length];
        if (!_TryFindWord(word, out factor))
        {
            problem = _UnitProblem(word, number);
            return false;
        }

        ReadOnlySpan<char> after = unitText[length..];
        if (!after.IsEmpty)
        {
            problem = _Unexpected(after.TrimStart(" \t"));
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Looks an accepted word up, ignoring case. At most six entries: a linear scan beats any lookup structure.</summary>
    /// <param name="word">The word to find.</param>
    /// <param name="factor">Base units per word when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the unit has the word.</returns>
    private bool _TryFindWord(ReadOnlySpan<char> word, out long factor)
    {
        for (int index = 0; index < _Words.Count; index++)
        {
            if (word.Equals(_Words[index].Text, StringComparison.OrdinalIgnoreCase))
            {
                factor = _Words[index].Factor;
                return true;
            }
        }

        factor = 0;
        return false;
    }

    /// <summary>Tells whether a character may be part of a unit word.</summary>
    /// <param name="c">The character.</param>
    /// <returns><see langword="true"/> for an ASCII letter, <c>%</c>, or <c>/</c>.</returns>
    internal static bool IsWordChar(char c) => char.IsAsciiLetter(c) || c == '%' || c == '/';

    #endregion

    #region Refusal texts

    /// <summary>Explains a number without the word that its unit requires, with the two smallest spellings as the suggestion.</summary>
    /// <param name="number">The number the user wrote.</param>
    /// <returns>For example <c>add a unit: 30 s or 30 ms</c>.</returns>
    private string _MissingWordProblem(long number) =>
        _Words.Count >= 2
            ? string.Create(CultureInfo.InvariantCulture, $"add a unit: {number} {_Words[1].Text} or {number} {_Words[0].Text}")
            : string.Create(CultureInfo.InvariantCulture, $"add a unit: {number} {_Words[0].Text}");

    /// <summary>Explains a unit word that does not belong to the unit, with the replacement where one exists.</summary>
    /// <param name="word">The offending word.</param>
    /// <param name="number">The number before the word; part of the suggestion for a refused word with a replacement.</param>
    /// <returns>The refusal text.</returns>
    private string _UnitProblem(ReadOnlySpan<char> word, long number)
    {
        // A refused spelling is ambiguous on purpose (10^6 or 2^20 bytes); say why, and what to write.
        for (int index = 0; index < _Refused.Count; index++)
        {
            if (word.Equals(_Refused[index].Text, StringComparison.OrdinalIgnoreCase))
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"'{_Quote(word)}' {UnitWordSet.DecimalRefusedReason}; use {number} {_Refused[index].Replacement}");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"unknown unit '{_Quote(word)}'; this value takes {_FamilyText}");
    }

    /// <summary>
    /// Explains the refusal of a fraction. A fraction is always refused: it is not exactly representable in every case, and
    /// accepting some would hide which. When the value is whole in a smaller unit the text names that spelling
    /// (<c>1.5GiB</c> gives <c>1536 MiB</c>).
    /// </summary>
    /// <param name="rest">The text after the whole number; starts with the dot.</param>
    /// <param name="number">The whole part.</param>
    /// <param name="overflow">Whether the whole part exceeded <see cref="long.MaxValue"/>.</param>
    /// <returns>The refusal text.</returns>
    private string _FractionProblem(ReadOnlySpan<char> rest, long number, bool overflow)
    {
        const string noSuggestion = "whole number expected; write a whole number in a smaller unit";

        int end = 1;
        while (end < rest.Length && char.IsAsciiDigit(rest[end]))
        {
            end++;
        }

        ReadOnlySpan<char> fraction = rest[1..end];
        if (overflow || fraction.IsEmpty || fraction.Length > _MaxFractionDigits)
        {
            return noSuggestion;
        }

        // The unit after the fraction decides the factor; an unusable unit is reported instead of a suggestion.
        if (!_TryFractionFactor(rest[end..], number, out long factor, out string? unitProblem))
        {
            return unitProblem;
        }

        // Exact arithmetic: (whole.fraction) * factor must be a whole number of base units. BigInteger is allowed here:
        // this is the refusal path and the digit count is capped.
        BigInteger scale = BigInteger.Pow(10, fraction.Length);
        BigInteger digits = (new BigInteger(number) * scale) + BigInteger.Parse(fraction, NumberStyles.None, CultureInfo.InvariantCulture);
        BigInteger total = digits * factor;
        if (!(total % scale).IsZero)
        {
            return noSuggestion;
        }

        BigInteger whole = total / scale;
        if (whole > long.MaxValue)
        {
            return noSuggestion;
        }

        return string.Create(CultureInfo.InvariantCulture, $"whole number expected; use {Format((long)whole)}");
    }

    /// <summary>Resolves the unit that follows a fraction.</summary>
    /// <param name="afterFraction">The text after the fraction digits.</param>
    /// <param name="number">The whole part, for the suggestions of a refusal.</param>
    /// <param name="factor">Base units per unit.</param>
    /// <param name="problem">The refusal when a required word is missing, the word is unknown, or text follows it.</param>
    /// <returns><see langword="true"/> when a factor exists, so a suggestion can be computed.</returns>
    private bool _TryFractionFactor(ReadOnlySpan<char> afterFraction, long number, out long factor, [NotNullWhen(false)] out string? problem)
    {
        ReadOnlySpan<char> unitText = afterFraction.TrimStart(" \t");
        if (unitText.IsEmpty)
        {
            // Without a word a unit with a required word has no scale at all; for the other units the base unit is meant.
            factor = 1;
            problem = _WordRequired
                ? string.Create(CultureInfo.InvariantCulture, $"whole number with a unit expected, for example {Example}")
                : null;
            return !_WordRequired;
        }

        return _TryResolveWord(afterFraction, number, out factor, out problem);
    }

    /// <summary>The refusal of a number that does not fit.</summary>
    /// <param name="maximum">The largest value of the definition in the base unit.</param>
    /// <returns>The refusal text that names the largest value.</returns>
    private string _TooLarge(long maximum) =>
        string.Create(CultureInfo.InvariantCulture, $"too large; the largest value is {Format(maximum)}");

    /// <summary>The refusal of text that follows a complete value.</summary>
    /// <param name="text">The leftover text; quoted shortened.</param>
    /// <returns>The refusal text.</returns>
    private static string _Unexpected(ReadOnlySpan<char> text) =>
        string.Create(CultureInfo.InvariantCulture, $"unexpected text '{_Quote(text)}' after the value");

    /// <summary>Shortens input text that a refusal quotes and replaces its control characters.</summary>
    /// <param name="text">The text from the input.</param>
    /// <returns>
    /// At most <see cref="_MaxQuotedLength"/> characters, each control character replaced by U+FFFD, followed by an ellipsis
    /// when shortened.
    /// </returns>
    /// <remarks>
    /// Security: the text comes from users and remote clients and ends up in terminals and logs. Threat: forged lines and
    /// terminal control sequences. Mitigation: a length cap and U+FFFD for each control character. Rejected: quoting as given.
    /// Residual risk: none beyond a shortened quote.
    /// </remarks>
    private static string _Quote(ReadOnlySpan<char> text)
    {
        int length = Math.Min(text.Length, _MaxQuotedLength);
        Span<char> buffer = stackalloc char[_MaxQuotedLength + 1];
        for (int index = 0; index < length; index++)
        {
            buffer[index] = char.IsControl(text[index])
                ? '�'
                : text[index];
        }

        if (text.Length > _MaxQuotedLength)
        {
            buffer[length++] = '…';
        }

        return new string(buffer[..length]);
    }

    #endregion

    #region Formatting

    /// <inheritdoc/>
    public string Format(long value)
    {
        if (_Words.Count == 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // Largest word first; zero stays in the base word ("0 B") because every word divides zero.
        if (value > 0)
        {
            for (int index = _Words.Count - 1; index > 0; index--)
            {
                if (value % _Words[index].Factor == 0)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"{value / _Words[index].Factor} {_Words[index].Text}");
                }
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value} {_Words[0].Text}");
    }

    #endregion

    #region Declaration checks

    /// <summary>Checks the name of the unit.</summary>
    /// <param name="name">The name.</param>
    /// <exception cref="ArgumentException">The name is empty, too long, or has a control character.</exception>
    private static void _CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > _MaxNameLength || _HasControlCharacter(name))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Limit unit '{name}': the name has 1 to {_MaxNameLength} characters and no control character."),
                nameof(name));
        }
    }

    /// <summary>Checks the forbidden key suffixes and copies them.</summary>
    /// <param name="name">The unit name, for messages.</param>
    /// <param name="suffixes">The suffixes, or <see langword="null"/>.</param>
    /// <returns>A copy of the suffixes; empty when there are none.</returns>
    /// <exception cref="ArgumentException">A suffix is empty or is not made of the characters of a key name.</exception>
    private static string[] _CopySuffixes(string name, IReadOnlyList<string>? suffixes)
    {
        if (suffixes is null)
        {
            return [];
        }

        string[] copy = new string[suffixes.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            string suffix = suffixes[index];
            if (string.IsNullOrEmpty(suffix) || !_IsKeyText(suffix))
            {
                throw _Invalid(name, nameof(suffixes), "a key suffix is made of ASCII letters, digits, and _");
            }

            copy[index] = suffix;
        }

        return copy;
    }

    /// <summary>Tells whether a text contains a control character; a line break would break tables and one-line help.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> when a character is a control character.</returns>
    private static bool _HasControlCharacter(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsControl(text[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tells whether a text is made of the characters of a key name.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for ASCII letters, digits, and <c>_</c> only.</returns>
    private static bool _IsKeyText(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(text[index]) && text[index] != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Joins the canonical spellings of the words for a refusal.</summary>
    /// <param name="words">The words, smallest first.</param>
    /// <returns>For example <c>B, KiB, MiB</c>.</returns>
    private static string _JoinWords(IReadOnlyList<UnitWord> words)
    {
        StringBuilder builder = new();
        for (int index = 0; index < words.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(words[index].Text);
        }

        return builder.ToString();
    }

    /// <summary>Creates the exception for a broken unit declaration.</summary>
    /// <param name="name">The unit name, for the message.</param>
    /// <param name="parameter">The constructor parameter that is wrong.</param>
    /// <param name="detail">What is wrong.</param>
    /// <returns>The exception to throw.</returns>
    private static ArgumentException _Invalid(string name, string parameter, string detail) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Limit unit '{name}': {detail} ({parameter})."), parameter);

    #endregion
}
