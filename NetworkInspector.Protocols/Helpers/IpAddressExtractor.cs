// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Shared helper for extracting IP addresses from the field tree via previous-sibling
/// navigation. Used as a fallback when the protocol-layer address caches (IPv4/IPv6 protocols)
/// do not contain cached addresses — for example in edge cases or custom stacks without standard IP protocols.
/// <para>
/// <b>Thread-safety:</b> All methods are stateless and safe for concurrent use.
/// </para>
/// </summary>
internal static class IpAddressExtractor
{
    /// <summary>
    /// Walks backwards through previous siblings from <paramref name="startField"/>
    /// to find the innermost IP container (IPv4 or IPv6), extracting source and
    /// destination addresses as typed nullable tuples. Exactly one of the out
    /// parameters will be non-null when the method returns <see langword="true"/>.
    /// <para>
    /// Handles tunnel scenarios (IP-in-IP, GRE) by returning the innermost (most
    /// recent) IP layer's addresses rather than the outermost.
    /// </para>
    /// </summary>
    internal static bool TryFindPreviousIpAddresses(
        Field startField,
        FieldId ipContainerFieldId,
        FieldId ipv6ContainerFieldId,
        FieldId ipSrcFieldId,
        FieldId ipDstFieldId,
        FieldId ipv6SrcFieldId,
        FieldId ipv6DstFieldId,
        out (IPv4Address Src, IPv4Address Dst)? ipv4,
        out (IPv6Address Src, IPv6Address Dst)? ipv6)
    {
        ipv4 = null;
        ipv6 = null;
        Field current = startField;

        // Walk backwards through previous siblings. The first IP container
        // encountered is the innermost layer encapsulating this transport protocol.
        do
        {
            FieldId fid = current.FieldId;

            if (fid == ipContainerFieldId && ipContainerFieldId.IsValid)
            {
                ipv4 = TryExtractIPv4ChildAddresses(current, ipSrcFieldId, ipDstFieldId);
                if (ipv4.HasValue)
                {
                    return true;
                }
            }

            if (fid == ipv6ContainerFieldId && ipv6ContainerFieldId.IsValid)
            {
                ipv6 = TryExtractIPv6ChildAddresses(current, ipv6SrcFieldId, ipv6DstFieldId);
                if (ipv6.HasValue)
                {
                    return true;
                }
            }
        }
        while (current.TryGetPrev(out current));

        return false;
    }

    /// <summary>
    /// Extracts IPv4 source and destination addresses from the eagerly-appended
    /// children of an IPv4 container field. Uses <c>materialize: false</c> to
    /// avoid triggering lazy field population.
    /// </summary>
    private static (IPv4Address Src, IPv4Address Dst)? TryExtractIPv4ChildAddresses(
        Field container, FieldId srcFieldId, FieldId dstFieldId)
    {
        IPv4Address src = default;
        IPv4Address dst = default;
        bool foundSrc = false;
        bool foundDst = false;

        foreach (Field child in container.Children(materialize: false))
        {
            if (!foundSrc && child.FieldId == srcFieldId
                && child.Value.Data.TryGetAsIPv4(out src))
            {
                foundSrc = true;
            }
            else if (!foundDst && child.FieldId == dstFieldId
                && child.Value.Data.TryGetAsIPv4(out dst))
            {
                foundDst = true;
            }

            if (foundSrc && foundDst)
            {
                return (src, dst);
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts IPv6 source and destination addresses from the eagerly-appended
    /// children of an IPv6 container field. Uses <c>materialize: false</c> to
    /// avoid triggering lazy field population.
    /// </summary>
    private static (IPv6Address Src, IPv6Address Dst)? TryExtractIPv6ChildAddresses(
        Field container, FieldId srcFieldId, FieldId dstFieldId)
    {
        IPv6Address src = default;
        IPv6Address dst = default;
        bool foundSrc = false;
        bool foundDst = false;

        foreach (Field child in container.Children(materialize: false))
        {
            if (!foundSrc && child.FieldId == srcFieldId
                && child.Value.Data.TryGetAsIPv6(out src))
            {
                foundSrc = true;
            }
            else if (!foundDst && child.FieldId == dstFieldId
                && child.Value.Data.TryGetAsIPv6(out dst))
            {
                foundDst = true;
            }

            if (foundSrc && foundDst)
            {
                return (src, dst);
            }
        }

        return null;
    }
}
