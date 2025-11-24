using Koware.Domain.Models;

namespace Koware.Application.Abstractions;

public interface IAnimeCatalog
{
    Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Episode>> GetEpisodesAsync(Anime anime, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<StreamLink>> GetStreamsAsync(Episode episode, CancellationToken cancellationToken = default);
}
