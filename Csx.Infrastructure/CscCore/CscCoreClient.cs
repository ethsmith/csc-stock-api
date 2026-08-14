using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Csx.Domain.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Csx.Infrastructure.CscCore;

public sealed class CscTeamDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public CscFranchiseDto? Franchise { get; set; }
    public CscTierDto? Tier { get; set; }
    public List<CscPlayerDto> Players { get; set; } = [];
}

public sealed class CscFranchiseDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Prefix { get; set; }
    public bool Active { get; set; }
    public CscLogoDto? Logo { get; set; }
    public List<CscTeamDto> Teams { get; set; } = [];
}

public sealed class CscLogoDto
{
    public string? Url { get; set; }
}

public sealed class CscTierDto
{
    public string? Name { get; set; }
    public int? MmrMin { get; set; }
    public int? MmrMax { get; set; }
}

public sealed class CscPlayerDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DiscordId { get; set; }
    public string? Type { get; set; }
    public int? Mmr { get; set; }
}

public sealed class CscMatchDto
{
    public string Id { get; set; } = "";
    public DateTimeOffset? ScheduledDate { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool IsBo3 { get; set; }
    public CscTeamDto? Home { get; set; }
    public CscTeamDto? Away { get; set; }
    public List<CscMapStatDto> Stats { get; set; } = [];
}

public sealed class CscMapStatDto
{
    public int MapNumber { get; set; }
    public string? MapName { get; set; }
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public bool IsForfeit { get; set; }
}

public sealed class CscCoreClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly CscCoreOptions _options;
    private readonly ILogger<CscCoreClient> _log;

    public CscCoreClient(HttpClient http, IOptions<CscCoreOptions> options, ILogger<CscCoreClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
        _http.BaseAddress = new Uri(_options.GraphQlUrl);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);
    }

    public async Task<int> GetActiveSeasonAsync(CancellationToken ct)
    {
        if (_options.Season is { } configured) return configured;
        var data = await QueryAsync<SeasonWrap>(
            "{ latestActiveSeason { number isActiveSeason } }",
            null,
            ct);
        return data.LatestActiveSeason?.Number ?? 21;
    }

    public async Task<IReadOnlyList<CscFranchiseDto>> GetActiveFranchisesAsync(CancellationToken ct)
    {
        const string q = """
            query {
              franchises(active: true) {
                id name prefix active
                logo { url }
                teams {
                  id name active
                  tier { name mmrMin mmrMax }
                  players { id name discordId type mmr }
                }
              }
            }
            """;
        var data = await QueryAsync<FranchisesWrap>(q, null, ct);
        return data.Franchises ?? [];
    }

    public async Task<IReadOnlyList<CscMatchDto>> GetMatchesAsync(
        int season, string status, int limit, CancellationToken ct)
    {
        const string q = """
            query ($season: Int!, $status: MatchStatus, $limit: Int) {
              matches(season: $season, status: $status, limit: $limit) {
                id scheduledDate completedAt isBo3
                home {
                  id name
                  franchise { id name prefix }
                  tier { name mmrMin mmrMax }
                  players { id name discordId type mmr }
                }
                away {
                  id name
                  franchise { id name prefix }
                  tier { name mmrMin mmrMax }
                  players { id name discordId type mmr }
                }
                stats { mapNumber mapName homeScore awayScore isForfeit }
              }
            }
            """;
        var data = await QueryAsync<MatchesWrap>(
            q,
            new { season, status, limit },
            ct);
        return data.Matches ?? [];
    }

    public async Task<IReadOnlyList<CscMatchDto>> GetCompletedMatchesAsync(int season, CancellationToken ct)
    {
        const string q = """
            query ($season: Int!) {
              matches(season: $season, status: COMPLETED, limit: 0) {
                id scheduledDate completedAt isBo3
                home {
                  id
                  franchise { prefix }
                  tier { name }
                }
                away {
                  id
                  franchise { prefix }
                  tier { name }
                }
                stats { mapNumber mapName homeScore awayScore isForfeit }
              }
            }
            """;
        var data = await QueryAsync<MatchesWrap>(q, new { season }, ct);
        return data.Matches ?? [];
    }

    public async Task<IReadOnlyList<CscMatchDto>> GetUpcomingMatchesAsync(int season, CancellationToken ct)
    {
        const string q = """
            query ($season: Int!) {
              matches(season: $season, afterToday: true, limit: 80) {
                id scheduledDate completedAt isBo3
                home { id name franchise { id name prefix } tier { name } }
                away { id name franchise { id name prefix } tier { name } }
              }
            }
            """;
        var data = await QueryAsync<MatchesWrap>(q, new { season }, ct);
        return data.Matches ?? [];
    }

    private async Task<T> QueryAsync<T>(string query, object? variables, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { query, variables }, JsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("CSC Core GraphQL HTTP {Status}: {Body}", (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        var parsed = JsonSerializer.Deserialize<GraphQlResponse<T>>(body, JsonOptions)
                     ?? throw new InvalidOperationException("Empty GraphQL response");
        if (parsed.Errors is { Count: > 0 })
        {
            var msg = string.Join("; ", parsed.Errors.Select(e => e.Message));
            _log.LogError("CSC Core GraphQL errors: {Errors}", msg);
            throw new InvalidOperationException(msg);
        }
        return parsed.Data ?? throw new InvalidOperationException("GraphQL data was null");
    }

    private sealed class GraphQlResponse<T>
    {
        public T? Data { get; set; }
        public List<GraphQlError>? Errors { get; set; }
    }

    private sealed class GraphQlError
    {
        public string Message { get; set; } = "";
    }

    private sealed class SeasonWrap
    {
        public SeasonNode? LatestActiveSeason { get; set; }
    }

    private sealed class SeasonNode
    {
        public int Number { get; set; }
        public bool IsActiveSeason { get; set; }
    }

    private sealed class FranchisesWrap
    {
        public List<CscFranchiseDto>? Franchises { get; set; }
    }

    private sealed class MatchesWrap
    {
        public List<CscMatchDto>? Matches { get; set; }
    }
}
