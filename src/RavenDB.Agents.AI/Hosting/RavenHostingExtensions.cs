using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;

namespace RavenDB.Agents.AI;

/// <summary>Adds RavenDB to agents hosted with the Agent Framework's hosting.</summary>
public static class RavenHostingExtensions
{
    /// <summary>
    /// Saves the hosted agent's sessions in RavenDB, with the <see cref="IDocumentStore"/> registered in dependency injection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With isolation (the default), each session belongs to the key from the registered <see cref="AgentIsolationKeyProvider"/>,
    /// e.g. the signed-in user, so one user can't load another user's session even with its id. Register an
    /// <see cref="AgentIsolationKeyProvider"/>, or every session load and save fails.
    /// </para>
    /// </remarks>
    /// <param name="builder">The hosted agent.</param>
    /// <param name="configure">Optional settings for the store. The agent namespace defaults to the hosted agent's name.</param>
    /// <param name="withIsolation">Whether each user (or whatever the isolation key is) has their own sessions. Turn it off
    /// only for a single-user or development host.</param>
    /// <returns>The same builder.</returns>
    /// <example>
    /// <code><![CDATA[
    /// builder.Services.AddSingleton<IDocumentStore>(store);
    /// builder.Services.AddSingleton<AgentIsolationKeyProvider, SignedInUserKeyProvider>();
    /// builder.AddAIAgent("support", "You are a support agent.").WithRavenSessionStore();
    /// ]]></code>
    /// </example>
    public static IHostedAgentBuilder WithRavenSessionStore(this IHostedAgentBuilder builder, Action<RavenAgentSessionStoreOptions>? configure = null, bool withIsolation = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithSessionStore(
            (services, agentName) =>
            {
                var options = new RavenAgentSessionStoreOptions { AgentNamespace = agentName };
                configure?.Invoke(options);
                return new RavenAgentSessionStore(services.GetRequiredService<IDocumentStore>(), options);
            },
            ServiceLifetime.Singleton,
            withIsolation);
    }
}
