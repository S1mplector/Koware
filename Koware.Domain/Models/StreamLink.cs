namespace Koware.Domain.Models;

public sealed record StreamLink(Uri Url, string Quality, string Provider, string? Referrer)
{
    public override string ToString() => $"{Quality} - {Provider}";
}
