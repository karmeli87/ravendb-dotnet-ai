using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RavenDB.Agents.AI.Tests;

public class DocumentSearchTests : RavenTestBase
{
    public sealed class Article
    {
        public string? Title { get; set; }
        public string? Body { get; set; }
    }

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new Article { Title = "Returns", Body = "Customers may return any product within 30 days for a full refund." }, "articles/returns");
        await session.StoreAsync(new Article { Title = "Shipping", Body = "Orders ship within two business days using express couriers." }, "articles/shipping");
        await session.StoreAsync(new Article { Title = "Careers", Body = "We are hiring backend engineers in Tel Aviv." }, "articles/careers");
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_returns_relevant_documents()
    {
        await SeedAsync();
        var search = new RavenDocumentSearch(Store, new RavenDocumentSearchOptions
        {
            Collection = "Articles",
            TextField = nameof(Article.Body),
            TitleField = nameof(Article.Title),
            MaxResults = 1,
            MinimumSimilarity = 0.3f,
            WaitForNonStaleResults = true,
        });

        var result = Assert.Single(await search.SearchAsync("Can I get my money back for an item?"));
        Assert.Equal("Returns", result.SourceName);
        Assert.Contains("30 days", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_search_provider_injects_rag_context_into_the_agent()
    {
        await SeedAsync();
        var search = new RavenDocumentSearch(Store, new RavenDocumentSearchOptions
        {
            Collection = "Articles",
            TextField = nameof(Article.Body),
            TitleField = nameof(Article.Title),
            MaxResults = 1,
            MinimumSimilarity = 0.3f,
            WaitForNonStaleResults = true,
        });

        var client = new RecordingChatClient();
        var agent = CreateAgent(client, null, search.CreateTextSearchProvider());
        await agent.RunAsync("How fast do orders ship?", await agent.CreateSessionAsync());

        Assert.Contains("two business days", client.LastPromptText, StringComparison.Ordinal);
    }
}

public class RavenAIAgentTests : RavenTestBase
{
    [Fact]
    public void A_tool_becomes_a_ravendb_action_with_its_schema()
    {
        var sendEmail = AIFunctionFactory.Create(([System.ComponentModel.Description("The email subject")] string subject, string body) => "sent", "send-email", "Sends an email to the customer");

        var action = sendEmail.ToAgentToolAction();

        Assert.Equal(("send-email", "Sends an email to the customer"), (action.Name, action.Description));
        Assert.Equal(sendEmail.JsonSchema.GetRawText(), action.ParametersSchema);
        Assert.Contains("The email subject", action.ParametersSchema, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_is_built_from_its_configuration_and_its_functions_must_be_actions()
    {
        var sendEmail = AIFunctionFactory.Create((string subject) => "sent", "send-email", "Sends an email");
        var notify = AIFunctionFactory.Create((string reason) => "done", "notify-manager", "Notifies the manager");
        var configuration = new Raven.Client.Documents.Operations.AI.Agents.AiAgentConfiguration("Store support", "llm", "prompt")
        {
            Identifier = "store-support",
            Actions = [sendEmail.ToAgentToolAction()],
        };

        var options = new RavenAIAgentOptions();
        var agent = configuration.ToRavenAIAgent(Store, [sendEmail], options);
        Assert.Equal(("store-support", "Store support"), (agent.Id, agent.Name));
        Assert.Null(options.Name); // the caller's options are not changed
        Assert.Equal("Support bot", configuration.ToRavenAIAgent(Store, options: new RavenAIAgentOptions { Name = "Support bot" }).Name);

        // A function the RavenDB agent has no action for would never be called.
        var missing = Assert.Throws<ArgumentException>(() => configuration.ToRavenAIAgent(Store, [sendEmail, notify]));
        Assert.Contains("'notify-manager'", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_round_trips_the_conversation_id_and_state()
    {
        var agent = new RavenAIAgent(Store, "support-agent");
        Assert.Equal("support-agent", agent.Name);
        Assert.Equal("support-agent", agent.Id);

        var session = (RavenAIAgentSession)await agent.CreateSessionAsync();
        Assert.Null(session.ConversationId);
        session.ConversationId = "Chats/0000000000000000001-A";
        session.StateBag.SetValue("user", "users/1-A");
        session.Parameters["company"] = "companies/1";

        var restored = (RavenAIAgentSession)await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));
        Assert.Equal(session.ConversationId, restored.ConversationId);
        Assert.Equal("users/1-A", restored.StateBag.GetValue<string>("user"));
        Assert.Equal("companies/1", restored.Parameters["company"]);
    }

    [Fact]
    public void A_copied_attachment_serializes_with_mafs_default_options()
    {
        // Workflow checkpoints and history stores serialize messages with MAF's own options; a custom AIContent would throw.
        var message = new ChatMessage(ChatRole.User, [new TextContent("Is it covered?"), RavenAttachment.Copy("Claims/1", "damage.jpg")]);

        var restored = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions), AIJsonUtilities.DefaultOptions)!;

        var file = Assert.IsType<HostedFileContent>(restored.Contents[1]);
        Assert.Equal(("Claims/1", "damage.jpg", RavenAttachment.CopyScope), (file.FileId, file.Name, file.Scope));
    }

    [Fact]
    public async Task Foreign_sessions_are_rejected()
    {
        var agent = new RavenAIAgent(Store, "support-agent");
        var otherSession = await CreateAgent(new RecordingChatClient()).CreateSessionAsync();
        await Assert.ThrowsAsync<InvalidCastException>(() => agent.RunAsync("hi", otherSession));
    }
}
