using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal static class OutboxExtensions
{
    /// <summary>
    /// A message context flushes its outgoing messages once and silently drops what is enqueued afterwards. Bootstrap and snapshot jobs publish a
    /// place and then its story in one unit of work, so each flush of this outbox must really send.
    /// </summary>
    public static void AllowMultipleFlushes<T>(this IDbContextOutbox<T> outbox)
        where T : DbContext
    {
        if (outbox is MessageContext context)
        {
            context.MultiFlushMode = MultiFlushMode.AllowMultiples;
        }
    }
}
