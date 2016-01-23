﻿using System.Threading.Tasks;

namespace Alluvial.Tests.BankDomain
{
    public class AccountHistoryProjector : IStreamAggregator<AccountHistoryProjection, IDomainEvent>
    {
        public async Task<AccountHistoryProjection> Aggregate(AccountHistoryProjection projection, IStreamBatch<IDomainEvent> events)
        {
            return projection;
        }
    }
}