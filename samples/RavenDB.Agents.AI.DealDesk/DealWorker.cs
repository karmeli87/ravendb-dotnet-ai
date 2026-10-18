using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI.Workflows;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;
using RavenDB.Agents.AI;

// A RavenDB data subscription feeds the deal desk: a new deal starts a run, and a customer's reply or a manager's
// decision resumes the deal's run from its latest checkpoint. The workflow's own writes move a deal out of the filter.
internal sealed class DealWorker(IDocumentStore store, DealDesk desk) : BackgroundService
{
    private readonly CheckpointManager _checkpoints =
        CheckpointManager.CreateJson(new RavenCheckpointStore(store), new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var worker = store.Subscriptions.GetSubscriptionWorker<DealRequest>(Setup.DealSubscription);
        await worker.Run(async batch =>
        {
            foreach (var item in batch.Items)
            {
                try
                {
                    if (item.Result.Status == DealStatus.New)
                    {
                        await StartAsync(item.Id, stoppingToken);
                    }
                    else
                    {
                        await ResumeAsync(item.Result, stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One broken deal must not stop the desk.
                    await MarkFailedAsync(item.Id, $"failed: {exception.Message}");
                }
            }
        }, stoppingToken);
    }

    // A run goes until it waits for a person or ends; the waiting step is checkpointed, so the run itself can go away.
    private async Task StartAsync(string dealId, CancellationToken cancellationToken)
    {
        await using var run = await InProcessExecution.RunStreamingAsync(desk.Build(), new DealRef(dealId), _checkpoints, dealId, cancellationToken);
        await FollowAsync(run, dealId, cancellationToken);
    }

    private async Task ResumeAsync(DealRequest deal, CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.GetLatestCheckpointAsync(deal.Id!, cancellationToken)
            ?? throw new InvalidOperationException($"No checkpoint for {deal.Id}.");
        await using var run = await InProcessExecution.ResumeStreamingAsync(desk.Build(), checkpoint, _checkpoints, cancellationToken);

        // The pending request is raised again on resume: answer it from the deal, then follow the run on.
        await foreach (var workflowEvent in run.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken))
        {
            if (workflowEvent is RequestInfoEvent { Request: var request })
            {
                await run.SendResponseAsync(request.PortInfo.PortId == DealDesk.ManagerPort
                    ? request.CreateResponse(new DealStep(deal.Id!, deal.Escalation?.Approved == true ? Decisions.Accept : Decisions.Counter))
                    : request.CreateResponse(new DealRef(deal.Id!)));
            }
            else
            {
                await ReportFailureAsync(workflowEvent, deal.Id!);
            }
        }

        await FollowAsync(run, deal.Id!, cancellationToken);
    }

    private async Task FollowAsync(StreamingRun run, string dealId, CancellationToken cancellationToken)
    {
        await foreach (var workflowEvent in run.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken))
        {
            await ReportFailureAsync(workflowEvent, dealId);
        }
    }

    // A failing step doesn't throw out of the stream: the workflow reports it as an event, and the page shows it.
    private async Task ReportFailureAsync(WorkflowEvent workflowEvent, string dealId)
    {
        var error = workflowEvent switch
        {
            ExecutorFailedEvent { Data: { } exception } failed => $"{failed.ExecutorId} failed: {exception.Message}",
            WorkflowErrorEvent { Exception: { } exception } => $"failed: {exception.Message}",
            _ => null,
        };
        if (error is not null)
        {
            await MarkFailedAsync(dealId, error);
        }
    }

    private async Task MarkFailedAsync(string dealId, string error)
    {
        Console.WriteLine($"[{dealId}] {error}");
        using var session = store.OpenAsyncSession();
        session.Advanced.Patch<DealRequest, string>(dealId, d => d.Status, DealStatus.Failed);
        session.Advanced.Patch<DealRequest, string>(dealId, d => d.Log, log => log.Add($"{DateTime.UtcNow:HH:mm:ss} {error}"));
        await session.SaveChangesAsync();
    }
}
