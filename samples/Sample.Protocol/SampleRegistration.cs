// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// The stack contribution of the sample plugin: protocol, port registration, heuristic parser, and post-parser. Stateless;
/// <see cref="Register"/> may run for every stack on any thread and creates fresh protocol objects each time.
/// </summary>
internal static class SampleRegistration
{
    #region Constants

    /// <summary>Name of the existing TCP heuristic table that the sample's heuristic parser joins.</summary>
    private const string _TcpHeuristicTableName = "tcp.heuristic";

    #endregion

    #region Internal API

    /// <summary>Registers the sample protocol, its UDP port, its heuristic parser, and its post-parser.</summary>
    /// <remarks>
    /// Value texts for <c>sample.gear</c> (enumeration P, R, N, D, S) and <c>sample.speed</c> (special values 254 ERROR, 255 SNA)
    /// are added here with <c>IStackBuilder.SetFieldValueTexts</c> as soon as Core offers it (additive calls).
    /// </remarks>
    /// <param name="stack">The Core builder of the stack under construction.</param>
    internal static void Register(IStackBuilder stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        // Protocol and fields; the protocol keeps its field ids (set before the stack is built, read-only afterwards).
        SampleProtocol protocol = new();
        ProtocolId protocolId = stack.RegisterProtocol(protocol);
        protocol.RegisterFields(stack, protocolId);

        // The UDP port table belongs to the standard protocols. The deferred form registers the port at once when the table
        // exists and as soon as it appears otherwise, so the plugin does not depend on the order in which plugins register,
        // and a stack without UDP simply has no port registration instead of failing the plugin.
        stack.WhenProtocolTableRegistered("udp.port", table => stack.RegisterParserInU64Table(table, SampleWire.DefaultPort, protocolId));

        // The heuristic goes into the TCP table that the standard protocols already registered; a lean stack without that
        // table simply gets no heuristic (UDP has no heuristic table and gets none).
        if (stack.GetHeuristicProtocolTableId(_TcpHeuristicTableName) is HeuristicProtocolTableId heuristicTable)
        {
            stack.RegisterHeuristicParser(heuristicTable, new SampleHeuristicParser(protocolId));
        }

        // Post-parser: sees every frame and marks the offset of the magic.
        SamplePostParser postParser = new();
        ProtocolId postId = stack.RegisterProtocol(postParser);
        postParser.RegisterFields(stack, postId);
        stack.RegisterPostParser(postId, priority: 0, description: "Marks frames that carry the NIS1 magic.");
    }

    #endregion
}
