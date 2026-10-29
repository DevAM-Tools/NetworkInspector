// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Payload;

/// <summary>
/// Registers the fields of the configured payload types: one field per usage site, in one index group per member direction.
/// <para>
/// <b>Why one field per usage site:</b> a type is defined once per service but used at many places, and every place is its own
/// filterable value (<c>request.target_c</c> and <c>response.target_c</c> are different questions). The compiler already derived a
/// unique registered name for every site; this class turns the names into registered fields and writes the resulting
/// <see cref="FieldId"/> back to the site, so the decoder reads the id from the site and never looks a name up while decoding.
/// </para>
/// <para>
/// <b>Why one index group per member direction:</b> a packet holds the group when it holds a message of that member in that
/// direction, so a filter on any parameter of the member narrows the candidates to those packets without expanding a single
/// payload. The group is a superset by design (a TLV member that is absent, a payload that was cut off): the filter verifies each
/// candidate on the tree, and the group never excludes a packet that holds the field. Rejected alternative: one group per field,
/// which would give the index one bitmap per parameter and make the memory of the index grow with the size of the description
/// instead of with the number of members.
/// </para>
/// <para>
/// <b>Failure is a warning, not a gap:</b> a name that the host refuses (a filter name that equals a built-in field, a display
/// name with a control character) must not make a parameter vanish from the decode. The site then decodes into the generic typed
/// field of its kind and the registration reports the cause; the value stays visible and filterable under the generic name.
/// </para>
/// <para><b>Thread-safety:</b> runs once on the single-threaded stack build; it writes the field and group ids that the model
/// documents as assigned exactly once. Not safe to call a second time for the same model.</para>
/// </summary>
internal static class SomeIpPayloadFieldRegistry
{
    #region Public API

    /// <summary>
    /// Registers every usage site of the model and assigns the groups and populator slots of every member.
    /// </summary>
    /// <param name="builder">The stack builder.</param>
    /// <param name="protocolId">The id of the SOME/IP protocol.</param>
    /// <param name="model">The compiled model; its sites and members receive their ids.</param>
    /// <param name="ids">The fixed payload fields, for the fallback of a site that cannot be registered.</param>
    /// <param name="warn">Receives the text of each registration problem.</param>
    /// <returns>The number of populator slots the model needs (two per member: request and response direction).</returns>
    /// <exception cref="InvalidOperationException">The model is already registered on a stack.</exception>
    internal static int Register(
        IStackBuilder builder,
        ProtocolId protocolId,
        SomeIpCompiledModel model,
        SomeIpPayloadFieldIds ids,
        Action<string> warn)
    {
        // A model carries the field ids of the stack it was registered on. Registering it on a second stack would overwrite them, and
        // the first stack would then decode into fields of the second. A misuse like this is a programming error and fails loudly;
        // the description is compiled again for each stack (every Register overload does).
        foreach (SomeIpServiceInfo registered in model.Services.Span)
        {
            foreach (SomeIpMemberInfo member in registered.Members.Span)
            {
                if (member.PayloadSlot >= 0)
                {
                    throw new InvalidOperationException(
                        "The compiled SOME/IP model is already registered on a stack; compile the description again for another stack.");
                }
            }
        }

        int slots = 0;
        ReadOnlySpan<SomeIpServiceInfo> services = model.Services.Span;
        for (int s = 0; s < services.Length; s++)
        {
            SomeIpServiceInfo service = services[s];
            string servicePath = string.Concat(SomeIpNameTokens.Prefix, ".", service.Token);
            ReadOnlySpan<SomeIpMemberInfo> members = service.Members.Span;
            for (int m = 0; m < members.Length; m++)
            {
                SomeIpMemberInfo member = members[m];

                // Getters, setters and methods have a request and a response and therefore two groups; events and notifiers have
                // one direction, and both lists share the group so a lookup by direction needs no special case.
                bool twoDirections = member.Kind is SomeIpMemberKind.Method or SomeIpMemberKind.FieldGetter or SomeIpMemberKind.FieldSetter;
                string? inDirection = null;
                string? outDirection = null;
                if (twoDirections)
                {
                    inDirection = "in";
                    outDirection = "out";
                }

                string inGroup = SomeIpNameTokens.Member(servicePath, member.Token, inDirection);
                string outGroup = SomeIpNameTokens.Member(servicePath, member.Token, outDirection);
                member.InGroup = builder.GetOrCreateIndexGroup(inGroup);
                member.OutGroup = twoDirections
                    ? builder.GetOrCreateIndexGroup(outGroup)
                    : member.InGroup;
                member.PayloadSlot = slots;
                slots += 2;

                _RegisterRange(builder, protocolId, model, ids, warn, member.In.SitesStart, member.In.SitesCount, inGroup, member.InGroup);
                _RegisterRange(builder, protocolId, model, ids, warn, member.Out.SitesStart, member.Out.SitesCount, outGroup, member.OutGroup);
            }
        }

        return slots;
    }

    #endregion

    #region Registration

    /// <summary>Registers the sites of one contiguous range and, below each, its children.</summary>
    private static void _RegisterRange(
        IStackBuilder builder,
        ProtocolId protocolId,
        SomeIpCompiledModel model,
        SomeIpPayloadFieldIds ids,
        Action<string> warn,
        int start,
        int count,
        string groupName,
        IndexGroupId groupId)
    {
        ReadOnlySpan<SomeIpUsageSite> sites = model.Sites.Span;
        for (int i = start; i < start + count; i++)
        {
            SomeIpUsageSite site = sites[i];
            FieldType fieldType = FieldTypeOf(model, site);
            string uiName = site.RegisteredName;
            if (site.UiName.Length > 0)
            {
                uiName = site.UiName;
            }

            try
            {
                site.FieldId = builder.RegisterFieldInGroup(
                    protocolId,
                    site.RegisteredName,
                    uiName,
                    fieldType,
                    groupName);
            }
            catch (RegistrationException ex)
            {
                // The site keeps working under the generic field of its kind. The reason is the message of the host, which names
                // the offending name; the registration list shows it next to the setting of the description file.
                site.FieldId = FallbackOf(ids, fieldType);
                warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The payload field '{site.RegisteredName}' could not be registered ({ex.Message}); its value is shown under the generic field of its kind."));
            }

            site.GroupId = groupId;
            if (site.ChildrenCount > 0)
            {
                _RegisterRange(builder, protocolId, model, ids, warn, site.ChildrenStart, site.ChildrenCount, groupName, groupId);
            }
        }
    }

    #endregion

    #region Field type mapping

    /// <summary>
    /// The field type a site is registered with: the typed value for a primitive, the integer of a bitfield, and no value for a
    /// container, an unavailable type and a dangling reference (such a node carries no value; its children or its marker do).
    /// </summary>
    /// <param name="model">The model that holds the types.</param>
    /// <param name="site">The site.</param>
    internal static FieldType FieldTypeOf(SomeIpCompiledModel model, SomeIpUsageSite site)
    {
        if (site.TypeIndex < 0)
        {
            return FieldType.None;
        }

        SomeIpTypeInfo type = model.Types.Span[site.TypeIndex];
        switch (type.Category)
        {
            case SomeIpTypeCategory.Bitfield:
                return FieldType.U64;
            case SomeIpTypeCategory.Base:
                return type.Primitive switch
                {
                    SomeIpPrimitive.UInt8 or SomeIpPrimitive.UInt16 or SomeIpPrimitive.UInt32 or SomeIpPrimitive.UInt64 => FieldType.U64,
                    SomeIpPrimitive.Int8 or SomeIpPrimitive.Int16 or SomeIpPrimitive.Int32 or SomeIpPrimitive.Int64 => FieldType.I64,
                    SomeIpPrimitive.Float or SomeIpPrimitive.Double => FieldType.F64,
                    SomeIpPrimitive.Bool => FieldType.Bool,
                    SomeIpPrimitive.String => FieldType.String,
                    SomeIpPrimitive.Uuid => FieldType.Uuid,
                    _ => FieldType.None,
                };
            default:
                return FieldType.None;
        }
    }

    /// <summary>The generic field for a field type; the field of a site whose own field could not be registered.</summary>
    internal static FieldId FallbackOf(SomeIpPayloadFieldIds ids, FieldType fieldType) => fieldType switch
    {
        FieldType.U64 => ids.ValueU64,
        FieldType.I64 => ids.ValueI64,
        FieldType.F64 => ids.ValueF64,
        FieldType.Bool => ids.ValueBool,
        FieldType.String => ids.ValueString,
        FieldType.Uuid => ids.ValueUuid,
        _ => ids.ValueNone,
    };

    #endregion
}
