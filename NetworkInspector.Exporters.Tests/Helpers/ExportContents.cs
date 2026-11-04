// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Ready-made <see cref="ExportContent"/> values for the exporter tests, so a test names what it needs ("hierarchy with info and
/// frame bytes") instead of building the record each time. Every factory returns a new, unshared value.
/// </summary>
internal static class ExportContents
{
    #region Content presets

    /// <summary>
    /// The hierarchy shape with every optional member set except the additional buffers (the frame bytes included; both are off in
    /// <c>new ExportContent()</c>, so this is not what an exporter writes without WithContent; use the exporter's
    /// <c>DefaultContent</c> for that).
    /// </summary>
    internal static ExportContent Full => new() { FrameBytes = true };

    /// <summary>The hierarchy shape with exactly the named optional members; everything not named is off.</summary>
    /// <param name="info">Write the packet summary line.</param>
    /// <param name="frameBytes">Write the raw frame bytes.</param>
    /// <param name="customRepresentation">Write each value's custom representation.</param>
    /// <param name="customText">Write each field's custom text.</param>
    /// <returns>The content.</returns>
    internal static ExportContent Hierarchy(
        bool info = false, bool frameBytes = false, bool customRepresentation = false, bool customText = false) =>
        new()
        {
            Shape = ExportShape.Hierarchy,
            Info = info,
            FrameBytes = frameBytes,
            CustomRepresentation = customRepresentation,
            CustomText = customText,
        };

    /// <summary>
    /// The all-fields shape with exactly the named optional members. Used where a test needs a batch or exporter that captures no
    /// topology (only the hierarchy shape does).
    /// </summary>
    /// <param name="info">Write the packet summary line.</param>
    /// <param name="frameBytes">Write the raw frame bytes.</param>
    /// <param name="customRepresentation">Write each value's custom representation.</param>
    /// <param name="customText">Write each field's custom text.</param>
    /// <returns>The content.</returns>
    internal static ExportContent AllFields(
        bool info = false, bool frameBytes = false, bool customRepresentation = false, bool customText = false) =>
        new()
        {
            Shape = ExportShape.AllFields,
            Info = info,
            FrameBytes = frameBytes,
            CustomRepresentation = customRepresentation,
            CustomText = customText,
        };

    /// <summary>The selected-fields shape for the named fields (all occurrences unless a request says otherwise).</summary>
    /// <param name="requests">The requested fields; at least one.</param>
    /// <returns>The content with the optional members at their defaults (info and texts on, frame bytes off).</returns>
    /// <exception cref="InvalidOperationException">A name is unknown or has no value.</exception>
    internal static ExportContent Selected(params FieldRequest[] requests)
    {
        if (!FieldSelection.TryCreate(TestHarness.GetStack(), requests, Occurrences.All, out FieldSelection? selection, out FieldSelectionError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        return new ExportContent { Shape = ExportShape.SelectedFields, Fields = selection };
    }

    #endregion
}
