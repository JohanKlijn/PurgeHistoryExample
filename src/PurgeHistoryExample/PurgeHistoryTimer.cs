namespace PurgeHistoryExample
{
    using Azure.Data.Tables;
    using Microsoft.Azure.Functions.Worker;
    using Microsoft.DurableTask.Client;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    /// <summary>
    /// Timer function which purge the history table of Completed orchestrations.
    /// </summary>
    public class PurgeHistoryTimer
    {
        // {second} {minute} {hour} {day} {month} {day-of-week}
        private const string EveryHour = "10 * * * * *";
        private readonly TableServiceClient tableServiceClient;
        private readonly ILogger<PurgeHistoryTimer> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PurgeHistoryTimer"/> class.
        /// </summary>
        /// <param name="tableServiceClient">a instance of <see cref="TableServiceClient"/>.</param>
        /// <param name="logger">an instance of <see cref="ILogger{TCategoryName}"/>.</param>
        public PurgeHistoryTimer(
            TableServiceClient tableServiceClient,
            ILogger<PurgeHistoryTimer> logger)
        {
            this.tableServiceClient = tableServiceClient ?? throw new ArgumentNullException(nameof(tableServiceClient));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Called when the timer expires.
        /// </summary>
        /// <param name="timer">an instance of <see cref="TimerInfo"/>.</param>
        /// <param name="client">an instance of <see cref="DurableTaskClient"/>.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        [Function(nameof(PurgeHistoryTimer))]
        public async Task RunAsync(
            [TimerTrigger(EveryHour)] TimerInfo timer,
            [DurableClient] DurableTaskClient client)
        {
            try
            {
                await ClearCompletedItems(client).ConfigureAwait(false);

                await PurgeFailedItems(client).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this.logger.LogError(exception, "Error running {Function}.", nameof(PurgeHistoryTimer));
                throw;
            }
        }

        private async Task ClearCompletedItems(DurableTaskClient client)
        {
            DateTime fromDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(14)); // 14 days ago, if for some reason the timer was not triggered a long time.
            DateTime toDate = DateTime.UtcNow;
            List<OrchestrationRuntimeStatus> statusesToPurge = new List<OrchestrationRuntimeStatus>
            {
                OrchestrationRuntimeStatus.Completed,
            };

            bool failure = false;
            try
            {
                await ClearItemsAsyncUsingPurgeAllInstances(client, fromDate, toDate, statusesToPurge).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = true;
                this.logger.LogError(exception, "Unable to clear completed items using {FunctionName}.", nameof(ClearItemsAsyncUsingPurgeAllInstances));
            }

            if (failure)
            {
                // If the purge failed, we will try to delete the items one by one.
                await ClearItemsOneByOneAsync(client, fromDate, toDate, statusesToPurge).ConfigureAwait(false);
            }
        }

        private async Task ClearItemsOneByOneAsync(DurableTaskClient client, DateTime fromDate, DateTime toDate, List<OrchestrationRuntimeStatus> statusesToPurge)
        {
            this.logger.LogInformation("Clearing items one by one, because the bulk purge failed.");

            OrchestrationQuery filter = new OrchestrationQuery()
            {
                CreatedFrom = fromDate,
                CreatedTo = toDate,
                Statuses = statusesToPurge,
            };

            var asyncPageable = client.GetAllInstancesAsync(filter);


            await foreach (OrchestrationMetadata instance in asyncPageable)
            {
                this.logger.LogInformation($"Purging instance {instance.InstanceId} (name: {instance.Name})...");
                await client.PurgeInstanceAsync(instance.InstanceId, CancellationToken.None).ConfigureAwait(false);
                this.logger.LogInformation($"Purged instance {instance.InstanceId} (name: {instance.Name}).");
            }
        }

        /// <summary>
        /// PurgeFailedItems:
        /// - re-process and purge 'Orchestrator' items.
        /// - purge all none 'Orchestrator' items.
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        private async Task PurgeFailedItems(DurableTaskClient client)
        {
            DateTime fromDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(14)); // 14 days ago, if for some reason the timer was not triggered a long time.
            DateTime toDate = DateTime.UtcNow;

            IList<OrchestrationRuntimeStatus> statusesToRetrieve = new List<OrchestrationRuntimeStatus>
            {
                OrchestrationRuntimeStatus.Failed,
                OrchestrationRuntimeStatus.Terminated,
            };

            OrchestrationQuery filter = new OrchestrationQuery()
            {
                CreatedFrom = fromDate,
                CreatedTo = toDate,
                Statuses = statusesToRetrieve,
            };

            var asyncPageable = client.GetAllInstancesAsync(filter);
            List<string> itemsToReQueue = new List<string>();
            List<string> itemsToDelete = new List<string>();

            await foreach (OrchestrationMetadata instance in asyncPageable)
            {
                if (instance.Name.Equals(nameof(Function1), StringComparison.OrdinalIgnoreCase))
                {
                    itemsToReQueue.Add(instance.InstanceId);
                }
                else
                {
                    itemsToDelete.Add(instance.InstanceId);
                }
            }

            // First re-queue items which must be re-queued
            foreach (string instanceId in itemsToReQueue)
            {
                try
                {
                    OrchestrationMetadata? instance = await client.GetInstanceAsync(instanceId, true).ConfigureAwait(false);
                    if (instance == null)
                    {
                        this.logger.LogWarning("The orchestration instance {InstanceId} could not be retrieved. The instance will not be purged.", instanceId);
                        continue;
                    }

                    await client.PurgeInstanceAsync(instance.InstanceId).ConfigureAwait(false);
                    this.logger.LogInformation($"Purged error instance {instance.InstanceId} (name: {instance.Name}.");
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception exception)
#pragma warning restore CA1031 // Do not catch general exception types
                {
                    // Only log the exception, because we are not able to process the item (at this moment).
                    this.logger.LogError(exception, $"Error re-queue durable function instance '{instanceId}'.");
                }
            }

            // Second delete items which must NOT be re-queued
            foreach (string instanceId in itemsToDelete)
            {
                OrchestrationMetadata? instance = await client.GetInstanceAsync(instanceId, false).ConfigureAwait(false);
                if (instance == null)
                {
                    this.logger.LogWarning("The orchestration instance {InstanceId} could not be retrieved. The instance will not be purged.", instanceId);
                    continue;
                }

                await client.PurgeInstanceAsync(instance.InstanceId).ConfigureAwait(false);
                this.logger.LogInformation($"Purged error instance {instance.InstanceId} (name: {instance.Name}.");
            }
        }

        private async Task ClearItemsAsyncUsingPurgeAllInstances(DurableTaskClient client, DateTime fromDate, DateTime toDate, IEnumerable<OrchestrationRuntimeStatus> runtimeStatus)
        {
            this.logger.LogInformation($"Purging history and instance table of task hub '{client.Name}' for {runtimeStatus} items created between {fromDate:yyyy-MM-ddTHH:mm:ssZ} and {toDate:yyyy-MM-ddTHH:mm:ssZ}, " +
                $"using client of type {client.GetType().FullName}.");
            this.logger.LogInformation($"DurableTaskClient: {client.GetType().FullName} version: {client.GetType().Assembly.GetName().Version}");

            PurgeInstancesFilter filter = new PurgeInstancesFilter(fromDate, toDate, runtimeStatus);
            PurgeInstanceOptions purgeInstanceOptions = new PurgeInstanceOptions();
            PurgeResult purgeResult = await client.PurgeAllInstancesAsync(filter, CancellationToken.None).ConfigureAwait(true); // ConfigureAwait should be set to true in Azure functions.

            this.logger.LogInformation($"Purged {purgeResult.PurgedInstanceCount} instances.");
        }
    }
}
