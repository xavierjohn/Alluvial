using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Alluvial.Distributors;

namespace Alluvial
{
    /// <summary>
    /// An persistent query over a stream of data, which updates one or more stream aggregators.
    /// </summary>
    /// <typeparam name="TData">The type of the data that the catchup pushes to the aggregators.</typeparam>
    /// <typeparam name="TCursor">The type of the cursor.</typeparam>
    /// <typeparam name="TPartition">The type of the partition.</typeparam>
    [DebuggerDisplay("{ToString()}")]
    internal class DistributedSingleStreamCatchup<TData, TCursor, TPartition> : StreamCatchupBase<TData, TCursor>
    {
        private static readonly string catchupTypeDescription = typeof (DistributedSingleStreamCatchup<TData, TCursor, TPartition>).ReadableName();

        protected readonly IPartitionedStream<TData, TCursor, TPartition> partitionedStream;

        // FIX: (DistributedSingleStreamCatchup) need a way to dispose this distributor
        private readonly IDistributor<IStreamQueryPartition<TPartition>> distributor;
        protected readonly FetchAndSaveProjection<ICursor<TCursor>> fetchAndSavePartitionCursor;

        public DistributedSingleStreamCatchup(
            IPartitionedStream<TData, TCursor, TPartition> partitionedStream,
            IEnumerable<IStreamQueryPartition<TPartition>> partitions,
            int? batchSize = null,
            FetchAndSaveProjection<ICursor<TCursor>> fetchAndSavePartitionCursor = null,
            IDistributor<IStreamQueryPartition<TPartition>> distributor = null) : base(batchSize)
        {
            if (partitions == null)
            {
                throw new ArgumentNullException("partitions");
            }

            this.partitionedStream = partitionedStream;
            this.distributor = distributor ?? partitions.DistributeQueriesInProcess();
            this.fetchAndSavePartitionCursor = fetchAndSavePartitionCursor ??
                                               new InMemoryProjectionStore<ICursor<TCursor>>(id => Cursor.New<TCursor>()).AsHandler();

            this.distributor
#if DEBUG
                .Trace() // TODO: (DistributedSingleStreamCatchup) figure out a way to let people Trace this distributor
#endif
                .OnReceive(OnReceiveLease);
        }

        protected virtual async Task OnReceiveLease(Lease<IStreamQueryPartition<TPartition>> lease)
        {
            await fetchAndSavePartitionCursor(
                lease.ResourceName,
                async cursor =>
                {
                    var upstreamCatchup = new SingleStreamCatchup<TData, TCursor>(
                        await partitionedStream.GetStream(lease.Resource),
                        initialCursor: cursor,
                        batchSize: BatchSize,
                        subscriptions: new ConcurrentDictionary<Type, IAggregatorSubscription>(aggregatorSubscriptions));

                    return await upstreamCatchup.RunSingleBatch();
                });
        }

        /// <summary>
        /// Consumes a single batch from the source stream and updates the subscribed aggregators.
        /// </summary>
        /// <returns>
        /// The updated cursor position after the batch is consumed.
        /// </returns>
        public override async Task<ICursor<TCursor>> RunSingleBatch()
        {
            var resources = distributor as IEnumerable<IStreamQueryPartition<TPartition>>;
            if (resources != null)
            {
                var partitions = new ConcurrentDictionary<IStreamQueryPartition<TPartition>, Unit>(
                    resources.Select(r => new KeyValuePair<IStreamQueryPartition<TPartition>, Unit>(r, Unit.Default)));

                while (partitions.Any())
                {
                    var acquired = await distributor.Distribute(1);

                    Unit _;
                    partitions.TryRemove(acquired.Single(), out _);
                }
            }
            else
            {
                // not all distributors have a finite set of known leasable resources
                await distributor.Distribute(1);
            }

            return Cursor.New<TCursor>();
        }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            return string.Format("{0}->{1}->{2}",
                                 catchupTypeDescription,
                                 partitionedStream,
                                 string.Join(" + ",
                                             aggregatorSubscriptions.Select(s => s.Value.ProjectionType.ReadableName())));
        }
    }
}