// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp;

/// <summary>
/// The identity of one segmented message: the key under which its segments are collected.
/// <para>
/// The six parts are what the specification says identifies a transfer plus two that keep unrelated transfers apart: the base type
/// (a segmented request and its segmented response share client and session id, and must not be mixed into one message) and the
/// interface version (two generations of a service share the rest). The message type is the <em>base</em> type, so the TP and ACK
/// flags do not split one transfer into several.
/// </para>
/// <para><b>Thread-safety:</b> immutable value type.</para>
/// </summary>
/// <param name="ServiceId">Service id of the message.</param>
/// <param name="MethodId">Method or event id of the message.</param>
/// <param name="ClientId">Client id of the request id.</param>
/// <param name="SessionId">Session id of the request id.</param>
/// <param name="BaseType">The message type with the TP and ACK flags cleared.</param>
/// <param name="InterfaceVersion">The interface version of the message.</param>
internal readonly record struct SomeIpTpKey(
    ushort ServiceId,
    ushort MethodId,
    ushort ClientId,
    ushort SessionId,
    byte BaseType,
    byte InterfaceVersion);
