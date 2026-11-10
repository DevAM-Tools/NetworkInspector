// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>The limit policy code of NetworkInspector: the hit sentence with the change hint of <c>ni</c>.</summary>
internal sealed class CoreLimitPolicyTests
{
    private const long _MiB = 1L << 20;

    [Test]
    public async Task TheHitTextNamesTheValueTheKeyAndBothWaysToChangeItWithNi()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: "blf.maxContainerSize");

        string message = definition.HitMessage("Container is 300 MiB", LimitValue.Of(128 * _MiB));

        await Assert.That(message).IsEqualTo(
            "Container is 300 MiB. Limit: 128 MiB (setting blf.maxContainerSize). "
            + "Change it with ni settings set blf.maxContainerSize <value> or --set blf.maxContainerSize=<value>.");
    }

    [Test]
    public async Task TheHitTextOfALimitThatIsOffSaysOff()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: "blf.maxContainerSize");

        string message = definition.HitMessage("Container is 300 MiB", LimitValue.Off);

        await Assert.That(message).Contains("Limit: off (setting blf.maxContainerSize).");
    }

    [Test]
    public async Task TheExtensionRefusesANullDefinitionAndAnEventTextWithoutWords()
    {
        LimitDefinition? missing = null;
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        await Assert.That(() => missing!.HitMessage("Entry is 300 MiB", LimitValue.Of(1))).Throws<ArgumentNullException>(); // null! is the input under test
        await Assert.That(() => definition.HitMessage("...", LimitValue.Of(1))).Throws<ArgumentException>();
    }

    [Test]
    public async Task ThePolicyTypeHasNoReferenceMachineBudgetOrCatalogPolicyMember()
    {
        string[] members = [.. typeof(CoreLimitPolicy).GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)];

        await Assert.That(members).IsEquivalentTo(["HitMessage"]);
    }
}
