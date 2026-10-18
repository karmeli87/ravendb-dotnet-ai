using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace RavenDB.Agents.AI.Tests;

public class ChatHistoryProviderTests : RavenTestBase
{
    [Fact]
    public async Task History_is_persisted_and_survives_a_new_agent_instance()
    {
        var client = new RecordingChatClient();
        var agent = CreateAgent(client, new RavenChatHistoryProvider(Store));
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("Hello", session);
        await agent.RunAsync("How are you?", session);

        // Second call saw the first exchange.
        Assert.Equal(["Hello", "reply 1", "How are you?"], client.LastRequest.Select(m => m.Text));

        // A brand-new agent + provider, restored from the serialized session, picks up the stored history.
        var serialized = await agent.SerializeSessionAsync(session);
        var client2 = new RecordingChatClient();
        var agent2 = CreateAgent(client2, new RavenChatHistoryProvider(Store));
        var restored = await agent2.DeserializeSessionAsync(serialized);
        await agent2.RunAsync("Still there?", restored);

        Assert.Equal(["Hello", "reply 1", "How are you?", "reply 2", "Still there?"], client2.LastRequest.Select(m => m.Text));

        // The document holds the messages as-is.
        using var dbSession = Store.OpenAsyncSession();
        var conversation = Assert.Single(await dbSession.Advanced.LoadStartingWithAsync<ChatConversation>("ChatConversations/"));
        Assert.Equal("@ChatConversations", dbSession.Advanced.GetMetadataFor(conversation)[Raven.Client.Constants.Documents.Metadata.Collection]);
        var messages = conversation.Messages;
        Assert.Equal(6, messages.Count);
        Assert.Equal((ChatRole.User, "Hello"), (messages[0].Role, messages[0].Text));
    }

    [Fact]
    public async Task Messages_round_trip_as_is()
    {
        var client = new RecordingChatClient();
        var agent = CreateAgent(client, new RavenChatHistoryProvider(Store));
        var session = await agent.CreateSessionAsync();
        var asked = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

        await agent.RunAsync(
        [
            new ChatMessage(ChatRole.User, [new TextContent("What's the weather here?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])
            {
                CreatedAt = asked,
                AuthorName = "dana",
                AdditionalProperties = new() { ["channel"] = "mobile" },
            },
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_weather", new Dictionary<string, object?> { ["city"] = "Paris" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "sunny")]),
        ], session);
        await agent.RunAsync("Thanks", session);

        var call = client.LastRequest.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal(("call-1", "get_weather", "Paris"), (call.CallId, call.Name, call.Arguments!["city"]?.ToString()));
        Assert.Equal("sunny", client.LastRequest.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result?.ToString());

        var first = client.LastRequest[0];
        Assert.Equal(asked, first.CreatedAt);
        Assert.Equal("dana", first.AuthorName);
        Assert.Equal("mobile", first.AdditionalProperties?["channel"]?.ToString());
        var image = Assert.Single(first.Contents.OfType<DataContent>());
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data.ToArray());
    }

    [Fact]
    public async Task State_initializer_binds_a_known_conversation()
    {
        var provider = new RavenChatHistoryProvider(Store, new RavenChatHistoryProviderOptions
        {
            StateInitializer = _ => new RavenChatHistoryProvider.State("support-ticket-42"),
        });
        var agent = CreateAgent(new RecordingChatClient(), provider);

        await agent.RunAsync("Printer is on fire", await agent.CreateSessionAsync());

        Assert.Equal(2, (await LoadMessagesAsync("ChatConversations/support-ticket-42")).Count);
    }

    [Fact]
    public async Task Concurrent_writes_to_one_conversation_do_not_lose_messages()
    {
        var provider = new RavenChatHistoryProvider(Store, new RavenChatHistoryProviderOptions
        {
            StateInitializer = _ => new RavenChatHistoryProvider.State("shared"),
        });
        var agent = CreateAgent(new RecordingChatClient(), provider);

        // Every run's first write races to create the conversation; each append is atomic on the server.
        await Task.WhenAll(Enumerable.Range(0, 5).Select(async i => await agent.RunAsync($"msg {i}", await agent.CreateSessionAsync())));

        Assert.Equal(10, (await LoadMessagesAsync("ChatConversations/shared")).Count);
    }

    [Fact]
    public async Task A_custom_store_filter_is_enumerated_once_and_never_duplicates_history()
    {
        // A custom store filter that passes everything through: it may be one-shot (enumerated once), and it no longer drops
        // the loaded history (a custom filter replaces MAF's default), which the provider must not store again.
        var provider = new RavenChatHistoryProvider(Store, new RavenChatHistoryProviderOptions
        {
            StoreInputRequestMessageFilter = messages => new OneShot<ChatMessage>(messages),
            StateInitializer = _ => new RavenChatHistoryProvider.State("once"),
        });
        var agent = CreateAgent(new RecordingChatClient(), provider);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("first", session);
        await agent.RunAsync("second", session);

        Assert.Equal(["first", "reply 1", "second", "reply 2"], (await LoadMessagesAsync("ChatConversations/once")).Select(m => m.Text));
    }

    private sealed class OneShot<T>(IEnumerable<T> items) : IEnumerable<T>
    {
        private bool _enumerated;

        public IEnumerator<T> GetEnumerator()
        {
            Assert.False(_enumerated, "enumerated twice");
            _enumerated = true;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private async Task<List<ChatMessage>> LoadMessagesAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<ChatConversation>(id)).Messages;
    }
}

public class AgentSessionStoreTests : RavenTestBase
{
    [Fact]
    public async Task Sessions_round_trip_and_are_isolated_by_key()
    {
        var store = new RavenAgentSessionStore(Store);
        var client = new RecordingChatClient();
        var agent = CreateAgent(client, new RavenChatHistoryProvider(Store));

        var key = new AgentSessionStoreKey("conv-1", new Dictionary<string, string> { ["user"] = "alice" });
        Assert.Null(await store.GetSessionAsync(agent, key));

        var session = await store.GetOrCreateSessionAsync(agent, key);
        await agent.RunAsync("Remember me", session);
        await store.SaveSessionAsync(agent, key, session);

        // Same session id, different partition => different session.
        Assert.Null(await store.GetSessionAsync(agent, key.WithPartition("user", "bob")));

        var restored = await store.GetSessionAsync(agent, key);
        Assert.NotNull(restored);
        await agent.RunAsync("Do you?", restored);
        Assert.Equal(["Remember me", "reply 1", "Do you?"], client.LastRequest.Select(m => m.Text));

        await store.DeleteSessionAsync(agent, key);
        Assert.Null(await store.GetSessionAsync(agent, key));
    }
}

public class CheckpointStoreTests : RavenTestBase
{
    [Fact]
    public async Task Checkpoints_are_indexed_by_session_and_parent()
    {
        var store = new RavenCheckpointStore(Store);
        var value = JsonDocument.Parse("""{"step":1,"note":"2026-10-14T00:00:00Z"}""").RootElement;

        var first = await store.CreateCheckpointAsync("run-a", value);
        var second = await store.CreateCheckpointAsync("run-a", value, first);
        await store.CreateCheckpointAsync("run-a/other", value); // shares the id prefix, must not leak in

        Assert.Equal([first, second], await store.RetrieveIndexAsync("run-a"));
        Assert.Equal([second], await store.RetrieveIndexAsync("run-a", withParent: first));
        Assert.Equal(value.GetRawText(), (await store.RetrieveCheckpointAsync("run-a", second)).GetRawText());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.RetrieveCheckpointAsync("run-a", new CheckpointInfo("run-a", "missing")));
    }

    [Fact]
    public async Task A_checkpoint_value_is_a_compressed_attachment_and_the_document_keeps_the_index_fields()
    {
        var store = new RavenCheckpointStore(Store);
        var history = string.Join(",", Enumerable.Range(0, 500).Select(i => $$"""{"role":"assistant","text":"Round {{i % 5}}: the deal needs a lower discount."}"""));
        var value = JsonDocument.Parse("""{"stateData":{"history":[""" + history + "]}}").RootElement;

        var checkpoint = await store.CreateCheckpointAsync("run-a", value);

        using var session = Store.OpenAsyncSession();
        var id = $"WorkflowCheckpoints/run-a/{checkpoint.CheckpointId}";
        var document = await session.LoadAsync<Dictionary<string, object>>(id);
        Assert.Equal(["CheckpointId", "CreatedAt", "ParentCheckpointId", "SessionId"], document.Keys.Where(k => k != "@metadata").Order(StringComparer.Ordinal));
        var attachment = Assert.Single(session.Advanced.Attachments.GetNames(document));
        Assert.Equal(("value.json.br", "application/x-brotli"), (attachment.Name, attachment.ContentType));
        Assert.True(attachment.Size * 20 < value.GetRawText().Length, $"{attachment.Size} bytes for {value.GetRawText().Length}");
        Assert.Equal(value.GetRawText(), (await store.RetrieveCheckpointAsync("run-a", checkpoint)).GetRawText());
    }

    [Fact]
    public async Task A_real_workflow_checkpoints_to_ravendb_and_resumes()
    {
        static Workflow BuildWorkflow()
        {
            var upper = ((Func<string, string>)(s => s.ToUpperInvariant())).BindAsExecutor("upper");
            var exclaim = ((Func<string, string>)(s => s + "!")).BindAsExecutor("exclaim");
            return new WorkflowBuilder(upper).AddEdge(upper, exclaim).WithOutputFrom(exclaim).Build();
        }

        var checkpointStore = new RavenCheckpointStore(Store);
        var manager = CheckpointManager.CreateJson(checkpointStore);

        await using var run = await InProcessExecution.RunAsync(BuildWorkflow(), "hello", manager, "wf-session");
        Assert.Equal(RunStatus.Idle, await run.GetStatusAsync());
        Assert.Contains(run.OutgoingEvents.OfType<WorkflowOutputEvent>(), e => Equals(e.Data, "HELLO!"));

        Assert.NotEmpty(run.Checkpoints);
        var stored = (await checkpointStore.RetrieveIndexAsync("wf-session")).ToList();
        Assert.Equal(run.Checkpoints.Select(c => c.CheckpointId), stored.Select(c => c.CheckpointId));

        // Simulate a restart: fresh workflow + fresh store, resume from the first checkpoint using only what RavenDB holds.
        await using var resumed = await InProcessExecution.ResumeAsync(BuildWorkflow(), stored[0], CheckpointManager.CreateJson(new RavenCheckpointStore(Store)));
        Assert.Equal(RunStatus.Idle, await resumed.GetStatusAsync());
        Assert.Contains(resumed.OutgoingEvents.OfType<WorkflowOutputEvent>(), e => Equals(e.Data, "HELLO!"));
    }
}

public class AgentFileStoreTests : RavenTestBase
{
    [Fact]
    public async Task Files_support_write_read_list_search_and_delete()
    {
        var files = new RavenAgentFileStore(Store, root: "user-1");

        await files.WriteAsync("notes/todo.md", "- buy milk\n- ship RavenDB integration");
        await files.WriteAsync("notes/archive/2025.md", "- old stuff");
        await files.WriteAsync("readme.md", "hello");

        Assert.Equal("hello", await files.ReadAsync("readme.md"));
        Assert.Equal("hello", await files.ReadAsync("./README.md")); // normalized + case-insensitive
        Assert.Null(await files.ReadAsync("missing.md"));
        Assert.True(await files.FileExistsAsync("notes/todo.md"));

        var root = await files.ListChildrenAsync("");
        Assert.Equal([("notes", FileStoreEntry.Directory), ("readme.md", FileStoreEntry.File)], root.Select(e => (e.Name, e.Type)));

        var notes = await files.ListChildrenAsync("notes");
        Assert.Equal([("archive", FileStoreEntry.Directory), ("todo.md", FileStoreEntry.File)], notes.Select(e => (e.Name, e.Type)));

        var direct = await files.SearchAsync("notes", "stuff|ravendb");
        Assert.Equal(["todo.md"], direct.Select(r => r.FileName));
        var recursive = await files.SearchAsync("notes", "stuff|ravendb", "**/*.md", recursive: true);
        Assert.Equal(["archive/2025.md", "todo.md"], recursive.Select(r => r.FileName).Order());
        Assert.Equal(2, Assert.Single(direct).MatchingLines[0].LineNumber);

        Assert.True(await files.DeleteAsync("readme.md"));
        Assert.False(await files.DeleteAsync("readme.md"));

        // Roots are isolated.
        Assert.Empty(await new RavenAgentFileStore(Store, root: "user-2").ListChildrenAsync(""));
    }

    [Fact]
    public async Task Path_traversal_is_rejected()
    {
        var files = new RavenAgentFileStore(Store);
        await Assert.ThrowsAsync<ArgumentException>(() => files.WriteAsync("../escape.md", "x"));
    }

    [Fact]
    public async Task Search_runs_the_regex_in_ravendb_right_after_writing()
    {
        var files = new RavenAgentFileStore(Store, root: "alice");
        await files.WriteAsync("notes/todo.md", "first line\nship it today\nlast line");
        await files.WriteAsync("notes/plan.md", "nothing here\nSHIP later");
        await new RavenAgentFileStore(Store, root: "bob").WriteAsync("notes/todo.md", "ship it too");

        // Searched immediately: the query waits for its index, so the files just written are found.
        Assert.Equal(["todo.md"], (await files.SearchAsync("notes", @"ship\s+it")).Select(r => r.FileName));
        Assert.Equal(["todo.md"], (await files.SearchAsync("notes", "(?m)^ship it")).Select(r => r.FileName));
        Assert.Equal(["plan.md", "todo.md"], (await files.SearchAsync("notes", "ship")).Select(r => r.FileName).Order(StringComparer.Ordinal)); // case-insensitive
        Assert.Empty(await files.SearchAsync("notes", "too")); // bob's file is another root
    }
}

public class SkillsSourceTests : RavenTestBase
{
    [Fact]
    public async Task Skills_are_loaded_from_documents()
    {
        var source = new RavenAgentSkillsSource(Store);
        await source.SaveSkillAsync(new PersistedAgentSkill
        {
            Frontmatter = new AgentSkillFrontmatter("refund-policy", "How to handle refund requests.")
            {
                License = "MIT",
                Metadata = new AdditionalPropertiesDictionary { ["owner"] = "support-team" },
            },
            Instructions = "Refunds are allowed within 30 days. Read the 'limits' resource for amounts.",
            Resources = [new PersistedAgentSkillResource { Name = "limits", Content = "Max refund: $500" }],
        });
        await source.SaveSkillAsync(new PersistedAgentSkill { Frontmatter = new("hidden-skill", "Hidden."), Instructions = "x", Disabled = true });

        var agent = CreateAgent(new RecordingChatClient());
        var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(agent, await agent.CreateSessionAsync()));

        var skill = Assert.Single(skills);
        Assert.Equal(("refund-policy", "MIT", "support-team"), (skill.Frontmatter.Name, skill.Frontmatter.License, skill.Frontmatter.Metadata!["owner"]));
        Assert.Contains("30 days", await skill.GetContentAsync());
        Assert.NotNull(await skill.GetResourceAsync("limits"));
    }

    [Fact]
    public async Task A_stored_skill_with_invalid_frontmatter_fails_the_load()
    {
        // Written outside SaveSkillAsync (e.g. edited in Studio): the name must be kebab-case.
        using (var session = Store.OpenAsyncSession())
        {
            var invalid = new Newtonsoft.Json.Linq.JObject
            {
                ["Frontmatter"] = new Newtonsoft.Json.Linq.JObject { ["Name"] = "Not Valid!", ["Description"] = "d" },
                ["Instructions"] = "i",
            };
            await session.StoreAsync(invalid, "AgentSkills/bad");
            await session.SaveChangesAsync();
        }

        var agent = CreateAgent(new RecordingChatClient());
        var failure = await Assert.ThrowsAnyAsync<Exception>(async () => await new RavenAgentSkillsSource(Store).GetSkillsAsync(new AgentSkillsSourceContext(agent, await agent.CreateSessionAsync())));
        Assert.Contains("Skill name must use only lowercase letters", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skills_plug_into_the_maf_skills_provider()
    {
        var source = new RavenAgentSkillsSource(Store);
        await source.SaveSkillAsync(new PersistedAgentSkill { Frontmatter = new("greeting", "Greets users warmly."), Instructions = "Say hi." });

        var client = new RecordingChatClient();
        var agent = CreateAgent(client, null, new AgentSkillsProviderBuilder().UseSource(source).Build());
        await agent.RunAsync("hello", await agent.CreateSessionAsync());

        // The skill catalog (name + description) is advertised to the model.
        Assert.Contains("greeting", client.LastPromptText, StringComparison.Ordinal);
    }
}
