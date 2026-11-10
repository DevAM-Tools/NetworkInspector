// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Utility;

/// <summary>
/// The words a unit accepts after a number, smallest first, built from composable parts: a base word, the binary or decimal
/// prefix ladder, or single words added with <see cref="With"/>. Immutable and thread-safe; every builder returns a new set.
/// </summary>
/// <remarks>
/// <c>UnitWordSet.Binary("B")</c> gives <c>B KiB MiB GiB TiB</c>; <c>UnitWordSet.Decimal("bit/s")</c> gives
/// <c>bit/s kbit/s Mbit/s Gbit/s Tbit/s</c>; <c>UnitWordSet.Of("ms").With("s", 1000)</c> gives two words. A set is data of a
/// <see cref="TableUnit"/>; the core of the library never uses it.
/// <para>Words compare ignoring case, so <c>Mbit/s</c> and <c>mbit/s</c> are the same word of a decimal set. A prefix whose
/// case matters (milli against mega) cannot be told apart by a parser that ignores case; declare such a unit with words of its own.</para>
/// <para>Security: the word alphabet (ASCII letters, <c>%</c>, <c>/</c>) keeps a word from being confused with the digits, the
/// <c>_</c> separator, the sign, or the fraction point, which the parser decides before it reads a word. A word that starts
/// with <c>e</c> would read as an exponent first and could never be written, so it is refused at the declaration.</para>
/// <para>Cost: a declaration allocates once; a set is a small array that the parser scans linearly.</para>
/// </remarks>
/// <seealso cref="TableUnit"/>
/// <seealso cref="UnitWord"/>
public sealed class UnitWordSet
{
    #region Configuration and state

    /// <summary>The longest word; <c>Mbit/s</c> and <c>kframes</c> fit with room.</summary>
    private const int _MaxWordLength = 16;

    /// <summary>The reason a decimal spelling of a binary unit is refused; the replacement follows it in the message.</summary>
    internal const string DecimalRefusedReason = "is a decimal unit and is not accepted";

    /// <summary>The accepted words, smallest first.</summary>
    private readonly UnitWord[] _Words;

    /// <summary>The spellings refused with the accepted word to write instead.</summary>
    private readonly RefusedSpelling[] _Refused;

    /// <summary>The words as a read-only view; built once so that reading it allocates nothing.</summary>
    private readonly IReadOnlyList<UnitWord> _WordsView;

    /// <summary>A spelling that is never accepted, with the accepted word that replaces it.</summary>
    /// <param name="Text">The refused spelling, for example <c>MB</c>.</param>
    /// <param name="Replacement">The accepted word to write instead, for example <c>MiB</c>.</param>
    internal readonly record struct RefusedSpelling(string Text, string Replacement);

    /// <summary>Wraps checked arrays.</summary>
    /// <param name="words">The accepted words; already checked.</param>
    /// <param name="refused">The refused spellings; already checked.</param>
    private UnitWordSet(UnitWord[] words, RefusedSpelling[] refused)
    {
        _Words = words;
        _Refused = refused;
        _WordsView = Array.AsReadOnly(words);
    }

    #endregion

    #region Builders

    /// <summary>A set without words: the value is a plain number (counts).</summary>
    public static UnitWordSet None { get; } = new([], []);

    /// <summary>A set with one word, the base unit.</summary>
    /// <param name="baseWord">The word with the factor 1, for example <c>ms</c>.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentException">The word breaks a rule of <see cref="With"/>.</exception>
    public static UnitWordSet Of(string baseWord) => None.With(baseWord, 1);

    /// <summary>The binary ladder: the base word and the words with the prefixes <c>Ki Mi Gi Ti</c> (factors 2^10 to 2^40).</summary>
    /// <param name="baseWord">The base word, for example <c>B</c>.</param>
    /// <returns>
    /// The new set. The decimal spellings (<c>K</c>, <c>M</c>, <c>G</c>, <c>T</c> before the base word) are refused with the
    /// binary word to write instead.
    /// </returns>
    /// <exception cref="ArgumentException">The base word or a derived word breaks a rule of <see cref="With"/>.</exception>
    public static UnitWordSet Binary(string baseWord)
    {
        string[] prefixes = ["Ki", "Mi", "Gi", "Ti"];
        string[] decimals = ["K", "M", "G", "T"];

        UnitWordSet set = Of(baseWord);
        RefusedSpelling[] refused = new RefusedSpelling[prefixes.Length];
        for (int index = 0; index < prefixes.Length; index++)
        {
            string word = prefixes[index] + baseWord;
            set = set.With(word, 1L << (10 * (index + 1)));
            refused[index] = new RefusedSpelling(decimals[index] + baseWord, word);
        }

        return new UnitWordSet(set._Words, refused);
    }

    /// <summary>The decimal ladder: the base word and the words with the prefixes <c>k M G T</c> (factors 10^3 to 10^12).</summary>
    /// <param name="baseWord">The base word, for example <c>bit/s</c>.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentException">The base word or a derived word breaks a rule of <see cref="With"/>.</exception>
    public static UnitWordSet Decimal(string baseWord)
    {
        string[] prefixes = ["k", "M", "G", "T"];

        UnitWordSet set = Of(baseWord);
        long factor = 1;
        for (int index = 0; index < prefixes.Length; index++)
        {
            factor *= 1_000;
            set = set.With(prefixes[index] + baseWord, factor);
        }

        return set;
    }

    /// <summary>Adds a word that is larger than every word of the set.</summary>
    /// <param name="word">The word: 1 to 16 ASCII letters, <c>%</c>, or <c>/</c>; not starting with <c>e</c>; not equal to another word ignoring case.</param>
    /// <param name="factor">Base units per word; above the factor of the last word; exactly 1 for the first word of a set.</param>
    /// <returns>A new set; this set is unchanged.</returns>
    /// <exception cref="ArgumentException">The word or its factor breaks a rule; the message names the word.</exception>
    public UnitWordSet With(string word, long factor)
    {
        ArgumentNullException.ThrowIfNull(word);

        _CheckWord(word, factor);

        UnitWord[] words = new UnitWord[_Words.Length + 1];
        Array.Copy(_Words, words, _Words.Length);
        words[^1] = new UnitWord(word, factor);
        return new UnitWordSet(words, _Refused);
    }

    #endregion

    #region Data

    /// <summary>The accepted words, smallest first; the first is the base unit. Empty for <see cref="None"/>.</summary>
    public IReadOnlyList<UnitWord> Words => _WordsView;

    /// <summary>The spellings that are refused with a replacement; used by <see cref="TableUnit"/>.</summary>
    internal IReadOnlyList<RefusedSpelling> Refused => _Refused;

    #endregion

    #region Declaration checks

    /// <summary>Checks a word and its factor against the words already in the set.</summary>
    /// <param name="word">The word to add.</param>
    /// <param name="factor">Its factor.</param>
    /// <exception cref="ArgumentException">A rule is broken.</exception>
    private void _CheckWord(string word, long factor)
    {
        if (word.Length is 0 or > _MaxWordLength)
        {
            throw _Invalid(word, string.Create(CultureInfo.InvariantCulture, $"a word has 1 to {_MaxWordLength} characters"));
        }

        for (int index = 0; index < word.Length; index++)
        {
            if (!TableUnit.IsWordChar(word[index]))
            {
                throw _Invalid(word, "a word has ASCII letters, '%', or '/' only");
            }
        }

        // Security: the parser refuses a number followed by 'e' as an exponent before it reads a word, so a word that starts
        // with 'e' could never be written; declaring one is a bug of the unit.
        if (word[0] is 'e' or 'E')
        {
            throw _Invalid(word, "the word starts with 'e', which reads as an exponent");
        }

        if (factor < 1)
        {
            throw _Invalid(word, "the factor is below 1");
        }

        // The first word is the base unit; every later factor is larger, so formatting finds the largest exact word by scanning back.
        if (_Words.Length == 0 && factor != 1)
        {
            throw _Invalid(word, "the first word is the base unit and has the factor 1");
        }

        if (_Words.Length > 0 && factor <= _Words[^1].Factor)
        {
            throw _Invalid(word, "the factor does not rise");
        }

        for (int index = 0; index < _Words.Length; index++)
        {
            if (string.Equals(_Words[index].Text, word, StringComparison.OrdinalIgnoreCase))
            {
                throw _Invalid(word, "the word appears twice (words compare ignoring case)");
            }
        }
    }

    /// <summary>Creates the exception for a broken word declaration.</summary>
    /// <param name="word">The word, for the message.</param>
    /// <param name="detail">What is wrong.</param>
    /// <returns>The exception to throw.</returns>
    private static ArgumentException _Invalid(string word, string detail) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Unit word '{word}': {detail}."), nameof(word));

    #endregion
}
