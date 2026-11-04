using HomeRobot.Server.Communication;

namespace HomeRobot.Server.AI;

internal sealed record RobotPlanActionStatus(
    string Command, object Parameters, string Status, string? CommandId = null, string? Message = null);

internal sealed record RobotPlanStatus(
    string PlanId, string RobotId, string Status, IReadOnlyList<RobotPlanActionStatus> Actions,
    IReadOnlyList<string> ValidationErrors, string? Message = null);

internal sealed class RobotPlanExecutor(RobotRegistry registry, ILogger<RobotPlanExecutor> logger)
    : IHostedService, IDisposable
{
    private const int ActiveLimit = 32;
    private const int HistoryLimit = 256;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<string, Task> running = new();
    private readonly Dictionary<string, RobotPlanStatus> plans = new();
    private readonly Queue<string> history = new();

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (gate)
        {
            shutdown.Cancel();
            tasks = running.Values.ToArray();
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    public void Dispose() => shutdown.Dispose();

    internal RobotPlanStatus? Find(string robotId, string planId)
    {
        lock (gate)
            return plans.TryGetValue(planId, out var plan) && plan.RobotId == robotId ? plan : null;
    }

    internal RobotPlanStatus? TryStart(string robotId, RobotPlanLease lease, IReadOnlyList<RobotCommand> commands)
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested || running.Count >= ActiveLimit)
                return null;

            var plan = new RobotPlanStatus(RobotProtocol.NewId(), robotId, "pending",
                commands.Select(command => new RobotPlanActionStatus(command.Command, command.Parameters, "pending")).ToArray(), []);
            plans.Add(plan.PlanId, plan);
            running.Add(plan.PlanId, Task.Run(() => ExecuteAsync(plan, lease, commands, shutdown.Token)));
            return plan;
        }
    }

    private async Task ExecuteAsync(RobotPlanStatus plan, RobotPlanLease lease,
        IReadOnlyList<RobotCommand> commands, CancellationToken cancellationToken)
    {
        var actions = plan.Actions.ToArray();
        var status = "completed";
        string? message = null;
        try
        {
            for (var index = 0; index < commands.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var submission = registry.SubmitPlanAction(plan.RobotId, lease, commands[index]);
                if (submission.Command is not { } command || submission.Completion is not { } completion)
                {
                    status = "failed";
                    message = submission.Error;
                    break;
                }

                actions[index] = actions[index] with { Status = "running", CommandId = command.CommandId };
                lock (gate)
                    plans[plan.PlanId] = plan with { Status = "running", Actions = actions.ToArray() };

                var result = await completion.WaitAsync(cancellationToken);
                actions[index] = actions[index] with { Status = result.Status, Message = result.Message };
                if (result.Status != "completed")
                {
                    status = result.Status;
                    message = result.Message;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            registry.StopPlan(plan.RobotId, lease);
            status = "cancelled";
            message = "server_stopping";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Robot action plan {PlanId} failed", plan.PlanId);
            registry.StopPlan(plan.RobotId, lease);
            status = "failed";
            message = "execution_error";
        }
        finally
        {
            lock (gate)
            {
                var reason = registry.ReleasePlan(plan.RobotId, lease);
                if (reason is not null && message is not "command_timeout")
                {
                    status = "cancelled";
                    message ??= reason;
                }

                for (var index = 0; index < actions.Length; index++)
                {
                    if (actions[index].Status is "pending" or "running")
                        actions[index] = actions[index] with { Status = "cancelled", Message = message };
                }

                plans[plan.PlanId] = plan with { Status = status, Actions = actions, Message = message };
                history.Enqueue(plan.PlanId);
                if (history.Count > HistoryLimit)
                    plans.Remove(history.Dequeue());
                running.Remove(plan.PlanId);
            }
        }
    }
}
