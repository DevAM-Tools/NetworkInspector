// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>
/// The owner of one live limit and the only object that changes it. Give <see cref="Limit"/> to enforcing code and keep the
/// writer in the store (a settings manager). Thread-safe: any thread may call <see cref="TrySet"/>; the last write wins.
/// </summary>
/// <remarks>
/// Design: reading and changing are two types so that a component with read access cannot change a limit (a protection could
/// be switched off by code that only wanted to look). Create one writer per consumer, not per definition. Every published value is
/// valid: <see cref="TrySet"/> checks the range and the flags of the definition first.
/// </remarks>
/// <seealso cref="Limit"/>
/// <seealso cref="LimitDefinition"/>
public sealed class LimitWriter
{
    #region Construction

    /// <summary>Creates a writer with a new holder that starts at the default of <paramref name="definition"/>.</summary>
    /// <param name="definition">The declaration; its range and flags rule every later change.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public LimitWriter(LimitDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Limit = new Limit(definition);
    }

    /// <summary>The read side; the same object for the life of this writer.</summary>
    public Limit Limit { get; }

    #endregion

    #region Changing the value

    /// <summary>Changes the value.</summary>
    /// <param name="value">The new value.</param>
    /// <param name="problem">The key and the reason when the method returns <see langword="false"/>; the value stays as it was.</param>
    /// <returns>
    /// <see langword="true"/> when the value is now current: inside the range (or <c>off</c> when allowed), or equal to the
    /// current value. A read-only limit accepts only its default.
    /// </returns>
    public bool TrySet(LimitValue value, [NotNullWhen(false)] out string? problem)
    {
        // The same value changes nothing; this also keeps an idempotent apply or reset of a read-only limit valid.
        if (value.Bits == Limit.Bits)
        {
            problem = null;
            return true;
        }

        if (Limit.Definition.ReadOnly)
        {
            problem = string.Create(CultureInfo.InvariantCulture, $"{Limit.Definition.Key}: the limit is read-only");
            return false;
        }

        if (!Limit.Definition.TryValidate(value, out problem))
        {
            return false;
        }

        // Concurrent writers: the last write wins; every published value is valid, so a reader never sees an invalid one.
        Limit.Publish(value.Bits);
        return true;
    }

    #endregion
}
