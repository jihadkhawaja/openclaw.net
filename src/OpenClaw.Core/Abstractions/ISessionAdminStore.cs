using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface ISessionAdminStore
{
    ValueTask<PagedSessionList> ListSessionsAsync(int page, int pageSize, SessionListQuery query, CancellationToken ct);
}
