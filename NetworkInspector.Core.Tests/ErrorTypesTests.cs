// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-point coverage for Core error types and factories.</summary>
internal sealed class ErrorTypesTests
{
    [Test]
    public async Task FieldAppendException_FromError_WrapsParseError()
    {
        ParseError error = ParseError.FieldAppendFailed();
        FieldAppendException ex = FieldAppendException.FromError(error);

        await Assert.That(ex.ParseError.Kind).IsEqualTo(ParseErrorKind.FieldAppendFailed);
        await Assert.That(ex.Message).IsEqualTo(error.ToString());
    }

    [Test]
    public async Task InvalidNameSettingsException_ForUiName_ContainsName()
    {
        InvalidNameSettingsException ex = InvalidNameSettingsException.ForUiName("bad name");

        await Assert.That(ex.Name).IsEqualTo("bad name");
        await Assert.That(ex.Message).Contains("Invalid UI name");
    }

    [Test]
    public async Task ParseError_FactoryMethods_ProduceExpectedKinds()
    {
        ParseError internalError = ParseError.InternalError("boom");
        ParseError appendFailed = ParseError.FieldAppendFailed();
        ParseError mismatch = ParseError.FieldTypeMismatch("f", FieldType.U64, FieldType.String);

        await Assert.That(internalError.Kind).IsEqualTo(ParseErrorKind.InternalError);
        await Assert.That(appendFailed.Kind).IsEqualTo(ParseErrorKind.FieldAppendFailed);
        await Assert.That(mismatch.Kind).IsEqualTo(ParseErrorKind.FieldTypeMismatch);
        await Assert.That(mismatch.Message).Contains("Field type mismatch");
    }

    [Test]
    public async Task ParseError_Default_ToString_ReturnsKindName()
    {
        ParseError error = default;

        await Assert.That(error.ToString()).IsEqualTo(ParseErrorKind.InsufficientData.ToString());
        await Assert.That(error.Kind).IsEqualTo(ParseErrorKind.InsufficientData);
    }

    [Test]
    public async Task PersistenceSettingsException_ForIo_WrapsInnerException()
    {
        IOException io = new("disk full");
        PersistenceSettingsException ex = PersistenceSettingsException.ForIo(io);

        await Assert.That(ex.InnerException).IsSameReferenceAs(io);
        await Assert.That(ex.Message).Contains("I/O error");
    }

    [Test]
    public async Task PersistenceSettingsException_ForJson_WrapsInnerException()
    {
        JsonException json = new("bad json");
        PersistenceSettingsException ex = PersistenceSettingsException.ForJson(json);

        await Assert.That(ex.InnerException).IsSameReferenceAs(json);
        await Assert.That(ex.Message).Contains("JSON error");
    }

    [Test]
    public async Task RegistrationException_InnerConstructor_IsReachable()
    {
        InvalidOperationException inner = new("root");
        RegistrationException ex = new TestRegistrationException("wrapped", inner);

        await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
        await Assert.That(ex.Message).IsEqualTo("wrapped");
    }

    [Test]
    public async Task SettingsException_InnerConstructor_IsReachable()
    {
        InvalidOperationException inner = new("root");
        SettingsException ex = new TestSettingsException("wrapped", inner);

        await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
        await Assert.That(ex.Message).IsEqualTo("wrapped");
    }

    private sealed class TestRegistrationException : RegistrationException
    {
        internal TestRegistrationException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    private sealed class TestSettingsException : SettingsException
    {
        internal TestSettingsException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
