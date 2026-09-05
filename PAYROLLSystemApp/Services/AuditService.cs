using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

public interface IAuditService
{
    Task WriteAsync(string action, string entity, int? entityId, bool success, string details,
        string? actor = null, int? actorUserId = null);

    Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 200);
}

/// <summary>
/// FR-091 / FR-092: writes the append-only audit trail. There is deliberately
/// no update or delete method here — the interface itself is the guarantee.
/// </summary>
public sealed class AuditService : IAuditService
{
    private readonly PayrollDatabase _database;

    public AuditService(PayrollDatabase database) => _database = database;

    public async Task WriteAsync(string action, string entity, int? entityId, bool success,
        string details, string? actor = null, int? actorUserId = null)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        await connection.InsertAsync(new AuditEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Actor = actor ?? "anonymous",
            ActorUserId = actorUserId,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Success = success,
            Details = details
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 200)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<AuditEntry>()
            .OrderByDescending(e => e.Id)
            .Take(take)
            .ToListAsync()
            .ConfigureAwait(false);
    }
}
