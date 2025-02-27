using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elsa.ActivityResults;
using Elsa.Events;
using Elsa.Models;
using Elsa.Providers.WorkflowStorage;
using Elsa.Services.Models;
using Elsa.Services.WorkflowStorage;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elsa.Services.Workflows
{
    public class WorkflowRunner : IWorkflowRunner
    {
        private delegate ValueTask<IActivityExecutionResult> ActivityOperation(ActivityExecutionContext activityExecutionContext, IActivity activity);

        private static readonly ActivityOperation Execute = (context, activity) => activity.ExecuteAsync(context);
        private static readonly ActivityOperation Resume = (context, activity) => activity.ResumeAsync(context);

        private readonly IWorkflowContextManager _workflowContextManager;
        private readonly IMediator _mediator;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger _logger;
        private readonly IGetsStartActivities _startingActivitiesProvider;
        private readonly IWorkflowStorageService _workflowStorageService;

        public WorkflowRunner(
            IWorkflowContextManager workflowContextManager,
            IMediator mediator,
            IServiceScopeFactory serviceScopeFactory,
            IGetsStartActivities startingActivitiesProvider,
            IWorkflowStorageService workflowStorageService,
            ILogger<WorkflowRunner> logger)
        {
            _mediator = mediator;
            _serviceScopeFactory = serviceScopeFactory;
            _startingActivitiesProvider = startingActivitiesProvider;
            _workflowStorageService = workflowStorageService;
            _logger = logger;
            _workflowContextManager = workflowContextManager;
        }

        public virtual async Task<RunWorkflowResult> RunWorkflowAsync(
            IWorkflowBlueprint workflowBlueprint,
            WorkflowInstance workflowInstance,
            string? activityId = default,
            WorkflowInput? input = default,
            CancellationToken cancellationToken = default)
        {
            using var loggingScope = _logger.BeginScope(new Dictionary<string, object> { ["WorkflowInstanceId"] = workflowInstance.Id });
            using var workflowExecutionScope = _serviceScopeFactory.CreateScope();

            if (input?.Input != null)
            {
                var workflowStorageContext = new WorkflowStorageContext(workflowInstance, workflowBlueprint.Id);
                var inputStorageProvider = _workflowStorageService.GetProviderByNameOrDefault(input.StorageProviderName);
                await inputStorageProvider.SaveAsync(workflowStorageContext, nameof(WorkflowInstance.Input), input.Input, cancellationToken);
                workflowInstance.Input = new WorkflowInputReference(inputStorageProvider.Name);
                await _mediator.Publish(new WorkflowInputUpdated(workflowInstance), cancellationToken);
            }

            var workflowExecutionContext = new WorkflowExecutionContext(workflowExecutionScope.ServiceProvider, workflowBlueprint, workflowInstance, input?.Input);
            var result = await RunWorkflowInternalAsync(workflowExecutionContext, activityId, cancellationToken);
            await workflowExecutionContext.WorkflowExecutionLog.FlushAsync(cancellationToken);
            return result;
        }
        
        private async Task<RunWorkflowResult> RunWorkflowInternalAsync(WorkflowExecutionContext workflowExecutionContext, string? activityId = default, CancellationToken cancellationToken = default)
        {
            var workflowInstance = workflowExecutionContext.WorkflowInstance;

            if (!string.IsNullOrWhiteSpace(workflowInstance.ContextId))
            {
                var loadContext = new LoadWorkflowContext(workflowExecutionContext);
                workflowExecutionContext.WorkflowContext = await _workflowContextManager.LoadContext(loadContext, cancellationToken);
            }

            // If the workflow instance has a CurrentActivity, it means the workflow instance is being retried.
            var currentActivity = workflowInstance.CurrentActivity;

            if (activityId == null && currentActivity != null)
                activityId = currentActivity.ActivityId;

            var workflowBlueprint = workflowExecutionContext.WorkflowBlueprint;
            var activity = activityId != null ? workflowBlueprint.GetActivity(activityId) : default;

            // Give application a chance to prevent workflow from executing.
            var validateWorkflowExecution = new ValidateWorkflowExecution(workflowExecutionContext, activity);
            await _mediator.Publish(validateWorkflowExecution, cancellationToken);

            if (!validateWorkflowExecution.CanExecuteWorkflow)
            {
                _logger.LogInformation("Workflow execution prevented for workflow {WorkflowInstanceId}", workflowInstance.Id);
                return new RunWorkflowResult(workflowInstance, activityId, false);
            }

            await _mediator.Publish(new WorkflowExecuting(workflowExecutionContext), cancellationToken);
            RunWorkflowResult runWorkflowResult;

            switch (workflowExecutionContext.Status)
            {
                case WorkflowStatus.Idle:
                    runWorkflowResult = await BeginWorkflow(workflowExecutionContext, activity, cancellationToken);

                    if (!runWorkflowResult.Executed)
                    {
                        if (workflowInstance.WorkflowStatus != WorkflowStatus.Faulted)
                        {
                            _logger.LogDebug("Workflow {WorkflowInstanceId} cannot begin from an idle state (perhaps it needs a specific input)", workflowInstance.Id);
                            return runWorkflowResult;
                        }
                    }

                    break;

                case WorkflowStatus.Running:
                    await RunWorkflowAsync(workflowExecutionContext, cancellationToken);
                    runWorkflowResult = new RunWorkflowResult(workflowInstance, activityId, true);
                    break;

                case WorkflowStatus.Suspended:
                    runWorkflowResult = await ResumeWorkflowAsync(workflowExecutionContext, activity!, cancellationToken);

                    if (!runWorkflowResult.Executed)
                    {
                        _logger.LogDebug("Workflow {WorkflowInstanceId} cannot be resumed from a suspended state (perhaps it needs a specific input)", workflowInstance.Id);
                        return runWorkflowResult;
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            await _mediator.Publish(new WorkflowExecuted(workflowExecutionContext), cancellationToken);

            var statusEvents = workflowExecutionContext.Status switch
            {
                WorkflowStatus.Cancelled => new INotification[] { new WorkflowCancelled(workflowExecutionContext), new WorkflowInstanceCancelled(workflowInstance) },
                WorkflowStatus.Finished => new INotification[] { new WorkflowCompleted(workflowExecutionContext) },
                WorkflowStatus.Faulted => new INotification[] { new WorkflowFaulted(workflowExecutionContext) },
                WorkflowStatus.Suspended => new INotification[] { new WorkflowSuspended(workflowExecutionContext) },
                _ => Array.Empty<INotification>()
            };

            foreach (var statusEvent in statusEvents)
                await _mediator.Publish(statusEvent, cancellationToken);

            await _mediator.Publish(new WorkflowExecutionFinished(workflowExecutionContext), cancellationToken);
            return runWorkflowResult;
        }

        private async Task<RunWorkflowResult> BeginWorkflow(WorkflowExecutionContext workflowExecutionContext, IActivityBlueprint? activity, CancellationToken cancellationToken)
        {
            if (activity == null)
                activity = _startingActivitiesProvider.GetStartActivities(workflowExecutionContext.WorkflowBlueprint).FirstOrDefault() ?? workflowExecutionContext.WorkflowBlueprint.Activities.First();

            try
            {
                if (!await CanExecuteAsync(workflowExecutionContext, activity, false, cancellationToken))
                    return new RunWorkflowResult(workflowExecutionContext.WorkflowInstance, activity.Id, false);

                workflowExecutionContext.Begin();
                workflowExecutionContext.ScheduleActivity(activity.Id);
                await RunAsync(workflowExecutionContext, Execute, cancellationToken);
                return new RunWorkflowResult(workflowExecutionContext.WorkflowInstance, activity.Id, true);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to run workflow {WorkflowInstanceId}", workflowExecutionContext.WorkflowInstance.Id);
                workflowExecutionContext.Fault(e, activity.Id, null, false);
                workflowExecutionContext.AddEntry(activity, "Faulted",  null, SimpleException.FromException(e));
            }

            return new RunWorkflowResult(workflowExecutionContext.WorkflowInstance, activity.Id, false);
        }

        private async Task RunWorkflowAsync(WorkflowExecutionContext workflowExecutionContext, CancellationToken cancellationToken)
        {
            await RunAsync(workflowExecutionContext, Execute, cancellationToken);
        }

        private async Task<RunWorkflowResult> ResumeWorkflowAsync(WorkflowExecutionContext workflowExecutionContext, IActivityBlueprint activityBlueprint, CancellationToken cancellationToken)
        {
            if (!await CanExecuteAsync(workflowExecutionContext, activityBlueprint, true, cancellationToken))
                return new RunWorkflowResult(workflowExecutionContext.WorkflowInstance, activityBlueprint.Id, false);

            var blockingActivities = workflowExecutionContext.WorkflowInstance.BlockingActivities.Where(x => x.ActivityId == activityBlueprint.Id).ToList();

            foreach (var blockingActivity in blockingActivities)
                await workflowExecutionContext.RemoveBlockingActivityAsync(blockingActivity);

            workflowExecutionContext.Resume();
            workflowExecutionContext.ScheduleActivity(activityBlueprint.Id);
            await RunAsync(workflowExecutionContext, Resume, cancellationToken);
            return new RunWorkflowResult(workflowExecutionContext.WorkflowInstance, activityBlueprint.Id, true);
        }

        private async ValueTask<bool> CanExecuteAsync(WorkflowExecutionContext workflowExecutionContext, IActivityBlueprint activityBlueprint, bool resuming, CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();

            var activityExecutionContext = new ActivityExecutionContext(
                scope.ServiceProvider,
                workflowExecutionContext,
                activityBlueprint,
                workflowExecutionContext.Input,
                resuming,
                cancellationToken);

            using var executionScope = AmbientActivityExecutionContext.EnterScope(activityExecutionContext);
            var runtimeActivityInstance = await activityExecutionContext.ActivateActivityAsync(cancellationToken);
            var activityType = runtimeActivityInstance.ActivityType;
            var activity = await activityType.ActivateAsync(activityExecutionContext);
            var canExecute = await activityType.CanExecuteAsync(activityExecutionContext, activity);

            if (canExecute)
            {
                var canExecuteMessage = new ValidateWorkflowActivityExecution(activityExecutionContext, runtimeActivityInstance);
                await _mediator.Publish(canExecuteMessage, cancellationToken);
                canExecute = canExecuteMessage.CanExecuteActivity;
            }

            return canExecute;
        }

        private async ValueTask RunAsync(WorkflowExecutionContext workflowExecutionContext, ActivityOperation activityOperation, CancellationToken cancellationToken = default)
        {
            try
            {
                await RunCoreAsync(workflowExecutionContext, activityOperation, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to run workflow {WorkflowInstanceId}", workflowExecutionContext.WorkflowInstance.Id);
                workflowExecutionContext.Fault(e, null, null, activityOperation == Resume);
            }
        }

        private async ValueTask RunCoreAsync(WorkflowExecutionContext workflowExecutionContext, ActivityOperation activityOperation, CancellationToken cancellationToken = default)
        {
            var scope = workflowExecutionContext.ServiceProvider;
            var workflowBlueprint = workflowExecutionContext.WorkflowBlueprint;
            var workflowInstance = workflowExecutionContext.WorkflowInstance;
            var burstStarted = false;

            while (workflowExecutionContext.HasScheduledActivities)
            {
                var scheduledActivity = workflowInstance.CurrentActivity = workflowExecutionContext.PopScheduledActivity();
                var currentActivityId = scheduledActivity.ActivityId;
                var activityBlueprint = workflowBlueprint.GetActivity(currentActivityId)!;
                var resuming = activityOperation == Resume;
                var outputReference = workflowInstance.Output;
                var output = outputReference != null ? await _workflowStorageService.LoadAsync(outputReference.ProviderName, new WorkflowStorageContext(workflowInstance, outputReference.ActivityId), "Output", cancellationToken) : null;
                var input = !burstStarted ? workflowExecutionContext.Input : scheduledActivity.Input ?? output;
                var activityExecutionContext = new ActivityExecutionContext(scope, workflowExecutionContext, activityBlueprint, input, resuming, cancellationToken);

                try
                {
                    var runtimeActivityInstance = await activityExecutionContext.ActivateActivityAsync(cancellationToken);
                    var activityType = runtimeActivityInstance.ActivityType;
                    using var executionScope = AmbientActivityExecutionContext.EnterScope(activityExecutionContext);
                    await _mediator.Publish(new ActivityActivating(activityExecutionContext), cancellationToken);
                    var activity = await activityType.ActivateAsync(activityExecutionContext);

                    if (!burstStarted)
                    {
                        await _mediator.Publish(new WorkflowExecutionBurstStarting(workflowExecutionContext, activityExecutionContext), cancellationToken);
                        burstStarted = true;
                    }

                    if (resuming)
                        await _mediator.Publish(new ActivityResuming(activityExecutionContext, activity), cancellationToken);

                    await _mediator.Publish(new ActivityExecuting(activityExecutionContext, activity), cancellationToken);
                    var result = await TryExecuteActivityAsync(activityOperation, activityExecutionContext, activity, cancellationToken);

                    if (result == null)
                        return;

                    await _mediator.Publish(new ActivityExecuted(activityExecutionContext, activity), cancellationToken);
                    await _mediator.Publish(new ActivityExecutionResultExecuting(result, activityExecutionContext), cancellationToken);
                    await result.ExecuteAsync(activityExecutionContext, cancellationToken);
                    workflowExecutionContext.CompletePass();
                    workflowInstance.LastExecutedActivityId = currentActivityId;
                    await _mediator.Publish(new ActivityExecutionResultExecuted(result, activityExecutionContext), cancellationToken);
                    await _mediator.Publish(new WorkflowExecutionPassCompleted(workflowExecutionContext, activityExecutionContext), cancellationToken);

                    if (!workflowExecutionContext.HasScheduledActivities)
                        await _mediator.Publish(new WorkflowExecutionBurstCompleted(workflowExecutionContext, activityExecutionContext), cancellationToken);

                    activityOperation = Execute;
                }
                catch (Exception e)
                {
                    await _mediator.Publish(new ActivityExecutionResultFailed(e, activityExecutionContext), cancellationToken);
                    throw;
                }
            }

            workflowInstance.CurrentActivity = null;

            if (workflowExecutionContext.HasBlockingActivities)
                workflowExecutionContext.Suspend();

            if (workflowExecutionContext.Status == WorkflowStatus.Running)
                await workflowExecutionContext.CompleteAsync();
        }

        private async ValueTask<IActivityExecutionResult?> TryExecuteActivityAsync(
            ActivityOperation activityOperation,
            ActivityExecutionContext activityExecutionContext,
            IActivity activity,
            CancellationToken cancellationToken)
        {
            try
            {
                return await activityOperation(activityExecutionContext, activity);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to run activity {ActivityId} of workflow {WorkflowInstanceId}", activity.Id, activityExecutionContext.WorkflowInstance.Id);
                activityExecutionContext.Fault(e);
                await _mediator.Publish(new ActivityFaulted(e, activityExecutionContext, activity), cancellationToken);
            }

            return null;
        }
    }
}