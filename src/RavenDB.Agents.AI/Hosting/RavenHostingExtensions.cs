using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;

namespace RavenDB.Agents.AI;

/// <summary>Hosting extensions for wiring RavenDB into MAF hosted agents.</summary>
public static class RavenHostingExtensions
{
    /// <summary>
    /// Persists the hosted agent's sessions in RavenDB, using the <see cref="IDocumentStore"/> registered in DI.
    /// </summary>
    /// <remarks>
    /// Isolation works as in MAF's own session stores: when on (the default), MAF adds the key from the registered
    /// <see cref="AgentIsolationKeyProvider"/> (e.g. the signed-in user) to every session key, so a caller who knows another
    /// user's session id still can't load that session. It is strict: without a registered
    /// <see cref="AgentIsolationKeyProvider"/>, every session load and save throws.
    /// </remarks>
    /// <param name="builder">The hosted agent builder.</param>
    /// <param name="configure">Optional store options; <see cref="RavenAgentSessionStoreOptions.AgentNamespace"/> defaults to the hosted agent name.</param>
    /// <param name="withIsolation">Whether sessions are scoped by the host's isolation key (e.g. the signed-in user). Turn it off only for a single-user or development host.</param>
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
