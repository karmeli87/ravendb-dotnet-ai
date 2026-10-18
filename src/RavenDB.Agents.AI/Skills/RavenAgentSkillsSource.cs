using Microsoft.Agents.AI;
using Raven.Client.Documents;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// Loads agent skills from RavenDB documents, so you can write, change and roll out skills without redeploying your agents.
/// </summary>
/// <remarks>
/// <para>
/// Each skill is a <see cref="PersistedAgentSkill"/> document. Save skills with <see cref="SaveSkillAsync"/>, or edit them
/// in RavenDB Studio. A skill that breaks the Agent Skills rules, e.g. a name that isn't kebab-case, makes loading fail.
/// </para>
/// <para>
/// Use it with <see cref="AgentSkillsProviderBuilder"/>, which caches the skills, so the database isn't read on every run.
/// </para>
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var skills = new RavenAgentSkillsSource(store);
/// await skills.SaveSkillAsync(new PersistedAgentSkill
/// {
///     Frontmatter = new AgentSkillFrontmatter("refund-policy", "How to handle refund requests."),
///     Instructions = "Refunds are allowed within 30 days.",
/// });
/// var provider = new AgentSkillsProviderBuilder().UseSource(skills).Build();   // add to the agent's context providers
/// ]]></code>
/// </example>
/// <param name="store">An initialized RavenDB document store.</param>
/// <param name="options">Optional settings, such as the document id prefix.</param>
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

    /// <summary>Checks a skill against the Agent Skills rules and saves it, replacing any skill with the same name.</summary>
    /// <param name="skill">The skill. Its document id is <c>{DocumentIdPrefix}/{skill name}</c>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the skill is saved.</returns>
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

/// <summary>Settings for a <see cref="RavenAgentSkillsSource"/>.</summary>
/// <remarks>
/// Each skill is the document <c>{DocumentIdPrefix}/{skill name}</c>; the prefix defaults to <c>AgentSkills</c>. A
/// <see cref="RavenDocumentStoreOptions.TimeToLive"/> applies to skills saved with <see cref="RavenAgentSkillsSource.SaveSkillAsync"/>,
/// e.g. for trial skills.
/// </remarks>
public sealed class RavenAgentSkillsSourceOptions() : RavenDocumentStoreOptions("AgentSkills");

/// <summary>An agent skill stored in RavenDB, read by <see cref="RavenAgentSkillsSource"/>.</summary>
public sealed class PersistedAgentSkill
{
    /// <summary>
    /// The collection of these documents, <c>@AgentSkills</c>. Pass it as <c>collectionName</c> when you query them.
    /// </summary>
    public const string CollectionName = "@AgentSkills";

    /// <summary>
    /// Gets or sets the skill's name, description and other details defined by the Agent Skills specification.
    /// </summary>
    public required AgentSkillFrontmatter Frontmatter { get; set; }

    /// <summary>Gets or sets the instructions the model follows when it uses the skill (the body of a SKILL.md file).</summary>
    public required string Instructions { get; set; }

    /// <summary>Gets or sets extra text the model can read when it needs it, e.g. a price list.</summary>
    public List<PersistedAgentSkillResource> Resources { get; set; } = [];

    /// <summary>Gets or sets whether the skill is hidden from agents, e.g. while you work on it.</summary>
    public bool Disabled { get; set; }
}

/// <summary>Extra text that a <see cref="PersistedAgentSkill"/> offers to the model.</summary>
public sealed class PersistedAgentSkillResource
{
    /// <summary>Gets or sets the name the instructions use to refer to the resource.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets a short description that tells the model what the resource contains.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the text.</summary>
    public required string Content { get; set; }
}
