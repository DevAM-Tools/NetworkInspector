// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Errors;

/// <summary>Covers <see cref="FilterError"/> factories, position handling and formatting.</summary>
internal sealed class FilterErrorTests
{
    [Test]
    public async Task Lexer_WithPosition_ExposesSpan()
    {
        FilterError error = FilterError.Lexer("bad char", 3, 1);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.LexerError);
        await Assert.That(error.Position).IsEqualTo(3);
        await Assert.That(error.Length).IsEqualTo(1);
        await Assert.That(error.HasPosition).IsTrue();
        await Assert.That(error.Message).IsEqualTo("bad char");
    }

    [Test]
    public async Task Syntax_Formats_IncludesPosition()
    {
        FilterError error = FilterError.Syntax("unexpected", 7, 2);

        await Assert.That(error.ToString()).IsEqualTo("[SyntaxError] at 7 (length 2): unexpected");
    }

    [Test]
    public async Task StackRequired_HasNoPosition()
    {
        FilterError error = FilterError.StackRequired();

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.StackRequired);
        await Assert.That(error.HasPosition).IsFalse();
        await Assert.That(error.ToString()).StartsWith("[StackRequired]:");
    }

    [Test]
    public async Task InvalidValue_CarriesKind()
    {
        FilterError error = FilterError.InvalidValue("bad literal", 1, 4);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.InvalidValue);
    }

    [Test]
    public async Task UnsupportedFeature_NamesTheConstruct()
    {
        FilterError error = FilterError.UnsupportedFeature("seq", 0, 3);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.UnsupportedFeature);
        await Assert.That(error.Message).Contains("seq");
    }

    [Test]
    public async Task UnknownField_NamesTheField()
    {
        FilterError error = FilterError.UnknownField("nope.field", 0, 10);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.UnknownField);
        await Assert.That(error.Message).Contains("nope.field");
    }

    [Test]
    public async Task UnknownProtocol_NamesTheProtocol()
    {
        FilterError error = FilterError.UnknownProtocol("nope", 0, 4);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.UnknownProtocol);
        await Assert.That(error.Message).Contains("nope");
    }

    [Test]
    public async Task TypeMismatch_CarriesKind()
    {
        FilterError error = FilterError.TypeMismatch("cannot compare", 2, 3);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.TypeMismatch);
    }

    [Test]
    public async Task Compiler_HasNoPosition()
    {
        FilterError error = FilterError.Compiler("boom");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.CompilerError);
        await Assert.That(error.HasPosition).IsFalse();
    }

    [Test]
    public async Task CallbackFailed_WrapsMessage()
    {
        FilterError error = FilterError.CallbackFailed("callback boom");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.CallbackFailed);
        await Assert.That(error.Message).Contains("callback boom");
    }

    [Test]
    public async Task Runtime_CarriesKind()
    {
        FilterError error = FilterError.Runtime("evaluation failed");

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.RuntimeError);
    }

    [Test]
    public async Task OutOfOrder_MentionsBothIds()
    {
        FilterError error = FilterError.OutOfOrder(3, 9);

        await Assert.That(error.Kind).IsEqualTo(FilterErrorKind.OutOfOrder);
        await Assert.That(error.Message).Contains("3");
        await Assert.That(error.Message).Contains("9");
    }
}
