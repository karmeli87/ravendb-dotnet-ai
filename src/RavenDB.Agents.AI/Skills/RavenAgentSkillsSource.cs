using Microsoft.Agents.AI;
using Raven.Client.Documents;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// An <see cref="AgentSkillsSource"/> that reads agent skills from RavenDB documents, so skills can be authored,
/// versioned and rolled out centrally without redeploying agents.
/// </summary>
/// <remarks>
/// Each <see cref="PersistedAgentSkill"/> becomes an <see cref="AgentInlineSkill"/>. A stored skill whose frontmatter
/// breaks the Agent Skills rules (e.g. edited in Studio) fails the load with RavenDB's error. MAF's
/// <see cref="AgentSkillsProviderBuilder"/> caches sources by default, so the database is not hit on every run.
/// </remarks>
/// <param name="store">An initialized RavenDB document store.</param>
/// <param name="options">Optional configuration.</param>
public sealed class RavenAgentSkillsSource(IDocumentStore store, RavenAgentSkillsSourceOptions? options = null) : AgentSkillsSource
{
    private readonly IDocumentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly RavenAgentSkillsSourceOptions _options = options ?? new RavenAgentSkillsSourceOptions();

    /// <inheritdoc />
    public override async Task<IList<AgentSkill>> GetSkillsAsync(AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
    {
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        var skills = await RavenHelpers.LoadAllStartingWithAsync<PersistedAgentSkill>(session, $"{_options.DocumentIdPrefix}/", cancellationToken).ConfigureAwait(false);
        return [.. skills.Where(s => !s.Disabled).Select(ToSkill)];
    }

    /// <summary>Validates a skill, then stores or replaces it as <c>{DocumentIdPrefix}/{skill name}</c>.</summary>
    public async Task SaveSkillAsync(PersistedAgentSkill skill, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ToSkill(skill);

        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        await session.StoreAsync(skill, $"{_options.DocumentIdPrefix}/{skill.Frontmatter.Name}", cancellationToken).ConfigureAwait(false);
        RavenHelpers.SetMetadata(session, skill, PersistedAgentSkill.CollectionName, _options.TimeToLive);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AgentInlineSkill ToSkill(PersistedAgentSkill stored)
    {
        var skill = new AgentInlineSkill(stored.Frontmatter, stored.Instructions);
        foreach (var resource in stored.Resources)
        {
            skill.AddResource(resource.Name, resource.Content, resource.Description);
        }

        return skill;
    }
}

/// <summary>Options for <see cref="RavenAgentSkillsSource"/>.</summary>
/// <remarks>
/// Documents are <c>{DocumentIdPrefix}/{skill name}</c>; default prefix <c>AgentSkills</c>. <see cref="RavenDocumentStoreOptions.TimeToLive"/>
/// applies to skills saved with <see cref="RavenAgentSkillsSource.SaveSkillAsync"/> (e.g. temporary or trial skills).
/// </remarks>
public sealed class RavenAgentSkillsSourceOptions() : RavenDocumentStoreOptions("AgentSkills");

/// <summary>An agent skill stored in RavenDB (collection <c>@AgentSkills</c>, ids <c>{DocumentIdPrefix}/{skill name}</c>).</summary>
public sealed class PersistedAgentSkill
{
    /// <summary>The collection these documents are stored in, <c>@AgentSkills</c>; pass it as <c>collectionName</c> to query them.</summary>
    public const string CollectionName = "@AgentSkills";

    /// <summary>Gets or sets the skill's frontmatter (name, description, license, compatibility, allowed tools, metadata),
    /// as defined by the Agent Skills specification.</summary>
    public required AgentSkillFrontmatter Frontmatter { get; set; }

    /// <summary>Gets or sets the skill instructions (the SKILL.md body).</summary>
    public required string Instructions { get; set; }

    /// <summary>Gets or sets static resources the model can read on demand.</summary>
    public List<PersistedAgentSkillResource> Resources { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the skill is hidden from agents.</summary>
    public bool Disabled { get; set; }
}

/// <summary>A static resource of a <see cref="PersistedAgentSkill"/>.</summary>
public sealed class PersistedAgentSkillResource
{
    /// <summary>Gets or sets the resource name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets the resource description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the resource content.</summary>
    public required string Content { get; set; }
}
