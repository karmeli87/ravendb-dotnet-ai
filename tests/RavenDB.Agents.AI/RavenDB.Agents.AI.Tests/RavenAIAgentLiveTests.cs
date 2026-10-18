using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.AI;
using Raven.Client.Documents.Operations.AI.Agents;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Xunit;

namespace RavenDB.Agents.AI.Tests;

/// <summary>
/// Runs <see cref="RavenAIAgent"/> against a real RavenDB AI Agent backed by a real LLM.
/// Skipped unless a RavenDB license and Azure OpenAI (or OpenAI) credentials are present in the environment.
/// </summary>
public class RavenAIAgentLiveTests : RavenTestBase
{
    private const string AgentId = "store-support";

    private static AiConnectionString? CreateConnectionString()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

        AiConnectionString connectionString = new() { Name = "maf-tests-llm", Identifier = "maf-tests-llm", ModelType = AiModelType.Chat };
        if (Env("RAVEN_AI_INTEGRATION_AZURE_OPENAI_API_KEY") is { } azureKey
            && Env("RAVEN_AI_INTEGRATION_AZURE_OPENAI_DEPLOYMENT_ENDPOINT") is { } endpoint
            && Env("RAVEN_AI_INTEGRATION_AZURE_OPENAI_CHAT_DEPLOYMENT_NAME") is { } deployment)
        {
            connectionString.AzureOpenAiSettings = new AzureOpenAiSettings(azureKey, endpoint, deployment, deployment, dimensions: null, temperature: null);
            return connectionString;
        }

        if (Env("RAVEN_AI_INTEGRATION_OPENAI_API_KEY") is { } openAiKey)
        {
            connectionString.OpenAiSettings = new OpenAiSettings(openAiKey, "https://api.openai.com/v1", "gpt-4o-mini", null, null, null, null, null, null);
            return connectionString;
        }

        return null;
    }

    private async Task CreateRavenAgentAsync(object? sample = null)
    {
        var connectionString = CreateConnectionString();
        Assert.SkipWhen(connectionString is null || !HasLicense,
            "Set RAVEN_LICENSE and Azure OpenAI or OpenAI credentials to run live RavenDB AI Agent tests.");

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Order { Company = "companies/1", Product = "Laptop", Total = 1200 }, "orders/1");
            await session.StoreAsync(new Order { Company = "companies/1", Product = "Mouse", Total = 300 }, "orders/2");
            await session.StoreAsync(new Order { Company = "companies/2", Product = "Desk", Total = 999 }, "orders/3");
            await session.SaveChangesAsync();
        }

        await Store.Maintenance.SendAsync(new PutConnectionStringOperation<AiConnectionString>(connectionString!));

        var configuration = new AiAgentConfiguration(
            "Store support",
            connectionString!.Identifier,
            "You are a support agent for an online store. Answer in one short sentence. " +
            "Use the get-orders query to answer questions about the customer's orders. " +
            "When the customer asks to be emailed, you must call the send-email action. " +
            "When the customer asks to escalate, call BOTH send-email and notify-manager, in the same turn.")
        {
            Identifier = AgentId,
            Parameters = [new AiAgentParameter("company", "The id of the customer's company")],
            Queries =
            [
                new AiAgentToolQuery("get-orders", "Lists the customer's orders with product and total", "from Orders where Company = $company select Product, Total")
                {
                    ParametersSampleObject = "{}", // only uses the agent-level $company parameter
                },
            ],
            Actions =
            [
                new AiAgentToolAction("send-email", "Sends an email to the customer")
                {
                    ParametersSampleObject = """{"subject":"the email subject","body":"the email body"}""",
                },
                new AiAgentToolAction("notify-manager", "Notifies the store manager about an escalation")
                {
                    ParametersSampleObject = """{"reason":"why the customer escalated"}""",
                },
            ],
        };

        // RavenDB agents always answer with a structured object; the sample defines its schema.
        await Store.AI.CreateAgentAsync(configuration, sample ?? new Reply { Answer = "Your answer to the customer" });
    }

    [Fact]
    public async Task A_typed_answer_streams_one_property_and_ends_with_the_answer()
    {
        await CreateRavenAgentAsync(new ScoredReply { Confidence = 0.9, Answer = "Your answer to the customer" });
        var agent = CreateAgent();

        // ScoredReply is the agent's answer shape, and the streamed Answer isn't its first property.
        var updates = await agent.RunStreamingAsync<ScoredReply>(reply => reply.Answer!, "What is the combined total of all my orders? Reply with just the number.", NewSession()).ToListAsync();

        Assert.True(updates.Count(u => !string.IsNullOrEmpty(u.Text)) > 1, "expected several text tokens");
        Assert.Contains("1500", string.Concat(updates.Select(u => u.Text)).Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        var answer = Assert.IsType<ScoredReply>(updates[^1].RawRepresentation);
        Assert.Contains("1500", answer.Answer!.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MAFs_typed_RunAsync_returns_a_nested_answer()
    {
        await CreateRavenAgentAsync(new OrderSummary(1, new ProductPrice("The product name", 1m)));
        var agent = CreateAgent();

        // OrderSummary is the agent's answer shape; RunAsync<TAnswer> goes through MAF's RunAsync<T>, which reads it as OrderSummary.
        AgentResponse<OrderSummary> response = await agent.RunAsync<OrderSummary>("What is the combined total of my orders, and which product was the most expensive and at what price?", NewSession());

        Assert.Equal(1500, response.Result.Total);
        Assert.Equal(("Laptop", 1200m), (response.Result.MostExpensive.Product, response.Result.MostExpensive.Price));
    }

    [Fact]
    public async Task MAFs_own_typed_RunAsync_through_AIAgent_is_refused()
    {
        // Held as AIAgent, MAF's RunAsync<T> is called: it sends only OrderSummary's schema, not the type.
        AIAgent agent = CreateAgent();
        await Assert.ThrowsAsync<NotSupportedException>(() => agent.RunAsync<OrderSummary>("What is the combined total of my orders?", NewSession()));
    }

    private sealed record OrderSummary(int Total, ProductPrice MostExpensive);

    private sealed record ProductPrice(string Product, decimal Price);

    [Fact]
    public async Task A_new_conversation_takes_the_id_it_is_given()
    {
        await CreateRavenAgentAsync();
        var agent = CreateAgent();
        var session = new RavenAIAgentSession { ConversationId = "Chats/tickets/42", Parameters = { ["company"] = "companies/1" } }; // never stored before

        var response = await agent.RunAsync("What is the combined total of all my orders? Reply with just the number.", session);
        Assert.Contains("1500", response.Text.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("Chats/tickets/42", session.ConversationId);

        using var dbSession = Store.OpenAsyncSession();
        Assert.True(await dbSession.Advanced.ExistsAsync("Chats/tickets/42"));
    }

    [Fact]
    public async Task Each_conversation_takes_its_parameters_from_the_session_key()
    {
        await CreateRavenAgentAsync();

        // One shared agent and store, as when hosted. Each user's key partitions (any number of them, the isolation key
        // included) become the parameters their conversation starts with; the agent declares only "company".
        var agent = CreateAgent();
        var sessions = new RavenAgentSessionStore(Store);
        const string Question = "What is the combined total of all my orders? Reply with just the number.";

        var alice = (RavenAIAgentSession)await sessions.GetOrCreateSessionAsync(agent, new AgentSessionStoreKey("chat", new Dictionary<string, string> { ["company"] = "companies/1", ["isolation"] = "alice" }));
        Assert.Equal(new Dictionary<string, string> { ["company"] = "companies/1", ["isolation"] = "alice" }, alice.StateBag.GetValue<Dictionary<string, string>>(RavenAgentSessionStore.PartitionsStateKey));
        Assert.Contains("1500", (await agent.RunAsync(Question, alice)).Text.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var bob = await sessions.GetOrCreateSessionAsync(agent, new AgentSessionStoreKey("chat", new Dictionary<string, string> { ["company"] = "companies/2", ["isolation"] = "bob" }));
        Assert.Contains("999", (await agent.RunAsync(Question, bob)).Text, StringComparison.Ordinal);

        // A conversation keeps the parameters it started with: changing them later doesn't move Alice to companies/2.
        alice.Parameters["company"] = "companies/2";
        Assert.Contains("1500", (await agent.RunAsync("Run get-orders again and give me the combined total, just the number.", alice)).Text.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_cannot_start_without_the_agents_parameters()
    {
        await CreateRavenAgentAsync();
        var agent = CreateAgent();
        await Assert.ThrowsAsync<Raven.Client.Exceptions.MissingAiAgentParameterException>(() => agent.RunAsync("Hi", new RavenAIAgentSession()));
    }

    [Fact]
    public async Task A_stored_session_resumes_the_conversation_by_key()
    {
        await CreateRavenAgentAsync();
        var sessions = new RavenAgentSessionStore(Store);
        var key = new AgentSessionStoreKey("c-123", new Dictionary<string, string> { ["company"] = "companies/1", ["isolation"] = "alice" });
        var agent = CreateAgent();

        var session = (RavenAIAgentSession)await sessions.GetOrCreateSessionAsync(agent, key);
        await agent.RunAsync("What is the combined total of all my orders? Reply with just the number.", session);
        await sessions.SaveSessionAsync(agent, key, session);

        // A guid, not derived from the key: see RavenAIAgent.CreateConversation (id size limit; a new session must start fresh).
        Assert.Matches("^Chats/[0-9a-f]{32}$", session.ConversationId);

        // A restart: the key finds the stored session, which points at the conversation; the history carries over.
        var resumed = (RavenAIAgentSession)(await sessions.GetSessionAsync(agent, key))!;
        Assert.Equal(session.ConversationId, resumed.ConversationId);
        Assert.Contains("Laptop", (await agent.RunAsync("Which product in my orders was the most expensive? Just the name.", resumed)).Text, StringComparison.OrdinalIgnoreCase);

        using var dbSession = Store.OpenAsyncSession();
        var stored = Assert.Single(await dbSession.Query<PersistedAgentSession>(collectionName: PersistedAgentSession.CollectionName).Customize(x => x.WaitForNonStaleResults()).ToListAsync());
        Assert.Equal("AgentSessions/store-support/c-123/company/companies/1/isolation/alice", dbSession.Advanced.GetDocumentId(stored));
        Assert.Equal(session.ConversationId, stored.Session.GetProperty("conversationId").GetString());
    }

    private sealed class ScoredReply
    {
        public double Confidence { get; set; }
        public string? Answer { get; set; }
    }

    private sealed class Reply
    {
        public string? Answer { get; set; }
    }

    [Fact]
    public async Task Tool_responses_from_a_turn_with_caller_side_actions_are_not_lost()
    {
        await CreateRavenAgentAsync();
        var emails = 0;
        var sendEmail = AIFunctionFactory.Create(([Description("Subject")] string subject, [Description("Body")] string body) => { emails++; return "Email sent."; }, "send-email");
        var agent = CreateAgent([sendEmail]); // notify-manager is caller-side
        var session = NewSession();

        var response = await agent.RunAsync("I want to escalate my complaint about my orders.", session);
        var notify = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.Name == "notify-manager");

        // The RavenDB client already sent the send-email response in that turn; answering notify-manager completes it.
        var final = await agent.RunAsync(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(notify.CallId, "Manager notified.")]), session);
        Assert.Empty(final.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.False(string.IsNullOrWhiteSpace(final.Text));
        Assert.Equal(1, emails);
    }

    private RavenAIAgent CreateAgent(IList<AIFunction>? actions = null) => new(Store, AgentId, actions);

    // A session for the customer of companies/1: the value of the agent's "company" parameter.
    private static RavenAIAgentSession NewSession() => new() { Parameters = { ["company"] = "companies/1" } };

    [Fact]
    public async Task Runs_a_ravendb_agent_with_server_side_queries_and_keeps_the_conversation()
    {
        await CreateRavenAgentAsync();
        var agent = CreateAgent();
        var session = NewSession();

        var response = await agent.RunAsync("What is the combined total of all my orders? Reply with just the number.", session);
        Assert.Contains("1500", response.Text.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\"Answer\"", response.Text, StringComparison.Ordinal); // the reply text, not the structured JSON
        Assert.Matches("^Chats/[0-9a-f]{32}$", session.ConversationId); // generated by the client
        Assert.True(response.Usage?.TotalTokenCount > 0);

        // The conversation lives in RavenDB: a follow-up in the same session sees the earlier turn.
        var conversationId = session.ConversationId;
        var followUp = await agent.RunAsync("Which product in my orders was the most expensive?", session);
        Assert.Contains("Laptop", followUp.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(conversationId, session.ConversationId);

        // A restored session continues the same RavenDB conversation, streamed this time.
        var restored = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(session));
        var chunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync("Repeat the name of that product, in lowercase, and nothing else.", restored))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                chunks.Add(update.Text);
            }
        }

        Assert.Contains("laptop", string.Concat(chunks), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ravendb_agent_actions_invoke_matching_maf_tools()
    {
        await CreateRavenAgentAsync();
        var sent = new List<(string Subject, string Body)>();
        var sendEmail = AIFunctionFactory.Create(
            ([Description("Subject")] string subject, [Description("Body")] string body) =>
            {
                sent.Add((subject, body));
                return "Email sent.";
            },
            "send-email");

        var agent = CreateAgent([sendEmail]);
        var response = await agent.RunAsync("Please email me a summary of my orders.", NewSession());

        var email = Assert.Single(sent);
        Assert.False(string.IsNullOrWhiteSpace(email.Body));
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    [Fact]
    public async Task Unhandled_actions_round_trip_through_the_caller()
    {
        await CreateRavenAgentAsync();
        var agent = CreateAgent(); // no actions: the action comes back to us
        var session = NewSession();

        var response = await agent.RunAsync("Please email me a summary of my orders.", session);
        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("send-email", call.Name);

        var final = await agent.RunAsync(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "Email sent.")]), session);
        Assert.Empty(final.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.False(string.IsNullOrWhiteSpace(final.Text));
    }

    [Fact]
    public async Task Plain_text_agent_returns_and_streams_text()
    {
        await CreateRavenAgentAsync();
        var agent = new RavenAIAgent(Store, AgentId);
        var session = NewSession();

        var response = await agent.RunAsync("What is the combined total of all my orders? Reply with just the number.", session);
        Assert.Contains("1500", response.Text.Replace(",", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var streamed = string.Concat(await agent.RunStreamingAsync("Which product in my orders was the most expensive? Just the name.", session).Select(u => u.Text).ToListAsync());
        Assert.Contains("Laptop", streamed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stopping_a_stream_early_lets_the_turn_finish()
    {
        await CreateRavenAgentAsync();
        var agent = new RavenAIAgent(Store, AgentId);
        var session = NewSession();

        await foreach (var update in agent.RunStreamingAsync("Describe each of my orders in a few words.", session))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                break; // first token only
            }
        }

        // The RavenDB turn keeps going in the background and stores the reply when it ends; a stuck callback never would.
        var deadline = DateTime.UtcNow.AddMinutes(1);
        var stored = false;
        while (!stored && DateTime.UtcNow < deadline)
        {
            using var dbSession = Store.OpenAsyncSession();
            var conversation = await dbSession.LoadAsync<Newtonsoft.Json.Linq.JObject>(session.ConversationId);
            stored = conversation?.SelectTokens("$..[?(@.role == 'assistant')].content").Any(c => c.HasValues || c.Type == Newtonsoft.Json.Linq.JTokenType.String) == true;
            await Task.Delay(stored ? 0 : 200);
        }

        Assert.True(stored);
    }

    [Fact]
    public async Task Tool_results_reach_ravendb_as_given()
    {
        await CreateRavenAgentAsync();
        var sendEmail = AIFunctionFactory.Create((string subject, string body) => "Email sent.", "send-email");
        var agent = CreateAgent([sendEmail]);
        var session = NewSession();
        await agent.RunAsync("Please email me a summary of my orders.", session);

        using var dbSession = Store.OpenAsyncSession();
        var conversation = await dbSession.LoadAsync<Newtonsoft.Json.Linq.JObject>(session.ConversationId);
        var toolMessages = conversation.SelectTokens("$..[?(@.role == 'tool')].content").Select(t => (string?)t).ToList();
        Assert.Contains("\"Email sent.\"", toolMessages);
    }

    [Fact]
    public async Task Files_in_a_user_message_reach_the_model_as_conversation_attachments()
    {
        await CreateRavenAgentAsync();
        var agent = CreateAgent();
        var session = NewSession();
        var photo = new DataContent(Convert.FromBase64String(RedSquarePng), "image/png") { Name = "photo.png" };
        var claim = new DataContent(Pdf("Claim number 4711."), "application/pdf"); // unnamed

        var response = await agent.RunAsync(new ChatMessage(ChatRole.User, [new TextContent("What is the main color of the attached photo, and what is the claim number in the attached PDF?"), photo, claim]), session);

        Assert.Contains("red", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4711", response.Text, StringComparison.Ordinal);

        using var dbSession = Store.OpenAsyncSession();
        var conversation = await dbSession.LoadAsync<Newtonsoft.Json.Linq.JObject>(session.ConversationId);
        var attachments = dbSession.Advanced.Attachments.GetNames(conversation);
        Assert.Equal(["application/pdf", "image/png"], attachments.Select(a => a.ContentType).Order(StringComparer.Ordinal));
        Assert.Contains(attachments, a => a.Name == "photo.png");
    }

    [Fact]
    public async Task A_document_attachment_is_copied_into_the_conversation()
    {
        await CreateRavenAgentAsync();
        using (var dbSession = Store.OpenAsyncSession())
        {
            await dbSession.StoreAsync(new Order { Company = "companies/1", Product = "Bicycle", Total = 450 }, "orders/claimed");
            dbSession.Advanced.Attachments.Store("orders/claimed", "claim.pdf", new MemoryStream(Pdf("Claim number 4711.")), "application/pdf");
            await dbSession.SaveChangesAsync();
        }

        var agent = CreateAgent();
        var session = NewSession();
        var claim = RavenAttachment.Copy("orders/claimed", "claim.pdf"); // the bytes stay in RavenDB; a provider-hosted file (no RavenDB scope) is not copied

        var response = await agent.RunAsync(new ChatMessage(ChatRole.User, [new TextContent("What is the claim number in the attached PDF?"), claim, new HostedFileContent("file-abc123")]), session);

        Assert.Contains("4711", response.Text, StringComparison.Ordinal);
        using var verify = Store.OpenAsyncSession();
        Assert.True(await verify.Advanced.Attachments.ExistsAsync(session.ConversationId, "claim.pdf"));
    }

    [Fact]
    public async Task Follows_a_tool_calling_agent_in_a_sequential_workflow()
    {
        await CreateRavenAgentAsync(); // the connection string
        await Store.AI.CreateAgentAsync(
            new AiAgentConfiguration("Forecaster", "maf-tests-llm", "Rewrite the weather report you are given as a one-line forecast.") { Identifier = "forecaster" },
            new Reply { Answer = "The forecast" });

        // The first agent calls its own tool; MAF forwards its call, the tool result and its reply to the next agent.
        var weather = new ChatClientAgent(new ToolCallingChatClient(), new ChatClientAgentOptions
        {
            Name = "weather",
            ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "sunny, 24°C", "get-weather")] },
        });
        var workflow = AgentWorkflowBuilder.BuildSequential(weather, new RavenAIAgent(Store, "forecaster"));

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, new List<ChatMessage> { new(ChatRole.User, "What's the weather in Paris?") });
        await run.TrySendMessageAsync(new TurnToken(emitEvents: false));
        var failures = new List<string>();
        List<ChatMessage>? output = null;
        await foreach (var workflowEvent in run.WatchStreamAsync())
        {
            switch (workflowEvent)
            {
                case ExecutorFailedEvent failed:
                    failures.Add($"{failed.ExecutorId}: {failed.Data}");
                    break;
                case WorkflowErrorEvent error:
                    failures.Add($"workflow: {error.Exception}");
                    break;
                case WorkflowOutputEvent { Data: List<ChatMessage> messages }:
                    output = messages;
                    break;
            }
        }

        Assert.Empty(failures);
        Assert.Contains("sunny", output![^1].Text, StringComparison.OrdinalIgnoreCase);
    }

    // First call: ask for the get-weather tool. Next call (with the tool result): report it.
    private sealed class ToolCallingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Paris: {result.Result}."))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_weather_1", "get-weather")])));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // A 256×256 solid red square (much smaller images give the model too little to see).
    private const string RedSquarePng = "iVBORw0KGgoAAAANSUhEUgAAAQAAAAEACAIAAADTED8xAAAE2UlEQVR4nO3cyQ3AMAzEQPXfdNKA//vgAC4gIBj4kFb33VkIZAnsv8BCwA9AAgTsACRAwBGIBAi4A5AAAZdgEiDgFYgECHgGJQEC6gAkQEAhjAQIqASTAAGtECRAQC8QCRDQDEcCBHSDkgAB7dAkQEAegAQICMSQAAGJMBIgIBJJAgRkgkmAgFA8CRAwFYIECBiLQgIEzAUiAQIGY5GgTmD/BRYCfgASIGAHIAECjkAkQMAdgAQIuASTAAGvQCRAwDMoCRBQByABAgphJEBAJZgECGiFIAECeoFIgIBmOBIgoBuUBAhohyYBAvIAJEBAIIYECEiEkQABkUgSICATTAIEhOJJgICpECRAwFgUEiBgLhAJEDAYiwR1AvsvsBDwA5AAATsACRBwBCIBAu4AJEDAJZgECHgFIgECnkFJgIA6AAkQUAgjAQIqwSRAQCsECRDQC0QCBDTDkQAB3aAkQEA7NAkQkAcgAQICMSRAQCKMBAiIRJIAAZlgEiAgFE8CBEyFIAECxqKQAAFzgUiAgMFYJKgT2H+BhYAfgAQI2AFIgIAjEAkQcAcgAQIuwSRAwCsQCRDwDEoCBNQBSICAQhgJEFAJJgECWiFIgIBeIBIgoBmOBAjoBiUBAtqhSYCAPAAJEBCIIQECEmEkQEAkkgQIyASTAAGheBIgYCoECRAwFoUECJgLRAIEDMYiQZ3A/gssBPwAJEDADkACBByBSICAOwAJEHAJJgECXoFIgIBnUBIgoA5AAgQUwkiAgEowCRDQCkECBPQCkQABzXAkQEA3KAkQ0A5NAgTkAUiAgEAMCRCQCCMBAiKRJEBAJpgECAjFkwABUyFIgICxKCRAwFwgEiBgMBYJ6gT2X2Ah4AcgAQJ2ABIg4AhEAgTcAUiAgEswCRDwCkQCBDyDkgABdQASIKAQRgIEVIJJgIBWCBIgoBeIBAhohiMBArpBSYCAdmgSICAPQAIEBGJIgIBEGAkQEIkkAQIywSRAQCieBAiYCkECBIxFIQEC5gKRAAGDsUhQJ7D/AgsBPwAJELADkAABRyASIOAOQAIEXIJJgIBXIBIg4BmUBAioA5AAAYUwEiCgEkwCBLRCkAABvUAkQEAzHAkQ0A1KAgS0Q5MAAXkAEiAgEEMCBCTCSICASCQJEJAJJgECQvEkQMBUCBIgYCwKCRAwF4gECBiMRYI6gf0XWAj4AUiAgB2ABAg4ApEAAXcAEiDgEkwCBLwCkQABz6AkQEAdgAQIKISRAAGVYBIgoBWCBAjoBSIBAprhSICAblASIKAdmgQIyAOQAAGBGBIgIBFGAgREIkmAgEwwCRAQiicBAqZCkAABY1FIgIC5QCRAwGAsEtQJ7L/AQsAPQAIE7AAkQMARiAQIuAOQAAGXYBIg4BWIBAh4BiUBAuoAJEBAIYwECKgEkwABrRAkQEAvEAkQ0AxHAgR0g5IAAe3QJEBAHoAECAjEkAABiTASICASSQIEZIJJgIBQPAkQMBWCBAgYi0ICBMwFIgECBmORoE5g/wUWAn4AEiBgByABAo5AJEDAHYAECLgEkwABr0AkQMAzKAkQUAcgAQIKYSRAQCWYBAhohSABAnqBSICAZjgSIKAblAQIaIcmAQLyACRAQCCGBAhIhJEAAZFIEiAgE0wCBO4div8BWUwO8l9vrLsAAAAASUVORK5CYII=";

    // A one-page PDF showing the text.
    private static byte[] Pdf(string text)
    {
        var content = $"BT /F1 18 Tf 50 700 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        offsets.ForEach(offset => pdf.Append($"{offset:D10} 00000 n \n"));
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private sealed class Order
    {
        public string? Company { get; set; }
        public string? Product { get; set; }
        public decimal Total { get; set; }
    }
}
