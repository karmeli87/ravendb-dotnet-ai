using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RavenDB.Extensions.VectorData;
using Xunit;

namespace RavenDB.Agents.AI.Tests;

/// <summary>Regression tests for the code-review findings.</summary>
public class ReviewFixTests : RavenTestBase
{
    [Fact]
    public async Task File_listing_and_search_work_from_paths_and_load_only_candidates()
    {
        var files = new RavenAgentFileStore(Store, root: "big");
        var bulky = new string('x', 64 * 1024);
        for (var i = 0; i < 20; i++)
        {
            await files.WriteAsync($"logs/day{i}.log", bulky);
        }

        await files.WriteAsync("notes/todo.md", "ship it");
        await files.WriteAsync("notes/deep/plan.md", "ship later");

        Assert.Equal([("logs", FileStoreEntry.Directory), ("notes", FileStoreEntry.Directory)], (await files.ListChildrenAsync("")).Select(e => (e.Name, e.Type)));
        Assert.Equal(20, (await files.ListChildrenAsync("logs")).Count);

        // RavenDB returns only files whose content matches: the 20 bulky logs never cross the wire.
        var hits = await files.SearchAsync("", "ship", "notes/*.md", recursive: true);
        Assert.Equal(["notes/todo.md"], hits.Select(h => h.FileName));
        Assert.Equal(["deep/plan.md", "todo.md"], (await files.SearchAsync("notes", "ship", recursive: true)).Select(h => h.FileName).Order());
    }

    [Fact]
    public void Malformed_action_arguments_become_a_function_call_with_an_error()
    {
        var call = RavenAIAgent.CreateFunctionCall("tool-1", "notify-manager", "{not json");
        Assert.Equal("notify-manager", call.Name);
        Assert.NotNull(call.Exception);

        var ok = RavenAIAgent.CreateFunctionCall("tool-2", "notify-manager", """{"reason":"angry customer"}""");
        Assert.Null(ok.Exception);
        Assert.Equal("angry customer", ((JsonElement)ok.Arguments!["reason"]!).GetString());
    }
}
