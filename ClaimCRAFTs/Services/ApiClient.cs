using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaimCRAFTs.Models;

namespace ClaimCRAFTs.Services;

/// <summary>Public ResuBaka REST client. Never requires a developer token or intermediary Worker.</summary>
public sealed class ApiClient : IDisposable
{
    private const string ApiBase = "https://craft-api.resubaka.dev";
    private readonly HttpClient _http = new() { BaseAddress = new Uri(ApiBase), Timeout = TimeSpan.FromSeconds(20) };

    public ApiClient()
    {
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaimCRAFTs/0.3.2");
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken token)
    {
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 96 }, token);
    }

    public async Task<IReadOnlyList<ClaimOption>> SearchClaimsAsync(string query, CancellationToken token)
    {
        var path = "/api/bitcraft/claims?search=" + Uri.EscapeDataString(query.Trim()) + "&page=1&per_page=20";
        using var document = await GetJsonAsync(path, token);
        var root = document.RootElement;
        var claims = JsonData.Property(root, "claims");
        if (claims.ValueKind != JsonValueKind.Array) throw new FormatException("Unexpected claim search response.");
        return claims.EnumerateArray().Select(row => new ClaimOption(
            JsonData.RequireId(row, "entity_id"),
            JsonData.String(row, "name") ?? "Unnamed claim",
            (int)(JsonData.Int64(row, "region") ?? 0),
            JsonData.Int64(row, "tier") is long tier ? (int)tier : null)).ToArray();
    }

    public async Task<CraftSnapshot> FetchSnapshotAsync(string claimId, ClaimDetails? cachedClaim, CancellationToken token)
    {
        var safeId = JsonData.ValidateId(claimId);
        // Claim details can contain large inventory/building data that this overlay never uses.
        // Reuse known membership for routine craft notifications; reload it on global
        // invalidations, manual refresh, reconnect, or first selection.
        if (cachedClaim is not null && cachedClaim.Id == safeId)
        {
            using var craftingOnly = await GetJsonAsync(
                $"/api/bitcraft/claims/{safeId}/crafting?include_private=false&include_passive=false", token);
            return new CraftSnapshot(cachedClaim, ParseCrafts(craftingOnly.RootElement));
        }

        // Initial/full refresh: two read-only requests in parallel, no per-project REST calls.
        using var claimTaskCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var detailsTask = GetJsonAsync($"/api/bitcraft/claims/{safeId}", claimTaskCancel.Token);
        var craftingTask = GetJsonAsync(
            $"/api/bitcraft/claims/{safeId}/crafting?include_private=false&include_passive=false", claimTaskCancel.Token);
        JsonDocument? details = null;
        JsonDocument? crafts = null;
        try
        {
            await Task.WhenAll(detailsTask, craftingTask);
            details = await detailsTask;
            crafts = await craftingTask;
            return new CraftSnapshot(ParseClaim(details.RootElement), ParseCrafts(crafts.RootElement));
        }
        catch
        {
            claimTaskCancel.Cancel();
            // Ensure documents returned before the other request faulted are disposed too.
            if (detailsTask.IsCompletedSuccessfully) detailsTask.Result.Dispose();
            if (craftingTask.IsCompletedSuccessfully) craftingTask.Result.Dispose();
            throw;
        }
        finally
        {
            details?.Dispose();
            crafts?.Dispose();
        }
    }

    internal static ClaimDetails ParseClaim(JsonElement root)
    {
        var members = new Dictionary<string, ClaimMember>(StringComparer.Ordinal);
        var source = JsonData.Property(root, "members");
        if (source.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in source.EnumerateObject())
                AddMember(entry.Value, entry.Name);
        }
        else if (source.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in source.EnumerateArray()) AddMember(entry, null);
        }
        else if (source.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            throw new FormatException("Unexpected claim membership format.");

        return new ClaimDetails(
            JsonData.RequireId(root, "entity_id"),
            JsonData.String(root, "name") ?? "Unnamed claim",
            JsonData.Id(root, "owner_player_entity_id") ?? "0",
            members);

        void AddMember(JsonElement row, string? fallbackId)
        {
            if (row.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid claim member row.");
            var id = JsonData.Id(row, "entity_id") ?? JsonData.Id(row, "player_entity_id") ?? fallbackId;
            if (id is null) throw new FormatException("Claim member ID is missing.");
            members[id] = new ClaimMember(id, JsonData.String(row, "user_name") ?? id,
                JsonData.Boolean(row, "inventory_permission") == true);
        }
    }

    internal static IReadOnlyList<CraftProject> ParseCrafts(JsonElement root)
    {
        JsonElement rows = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var wrapped = JsonData.Property(root, "crafts");
            if (wrapped.ValueKind == JsonValueKind.Undefined) wrapped = JsonData.Property(root, "data");
            if (wrapped.ValueKind != JsonValueKind.Undefined) rows = wrapped;
        }

        if (rows.ValueKind == JsonValueKind.Array)
            return rows.EnumerateArray().Select(ParseCraft).Where(craft => craft is not null)
                .Cast<CraftProject>().ToArray();

        // Some API responses are column-oriented: { "id": [..], "owner_id": [..], ... }.
        if (rows.ValueKind == JsonValueKind.Object && JsonData.Property(rows, "id") is { ValueKind: JsonValueKind.Array } ids)
        {
            var projects = new List<CraftProject>();
            for (var i = 0; i < ids.GetArrayLength(); i++)
            {
                using var objectRow = new System.IO.MemoryStream();
                using (var writer = new Utf8JsonWriter(objectRow, new JsonWriterOptions { SkipValidation = false }))
                {
                    writer.WriteStartObject();
                    foreach (var column in rows.EnumerateObject())
                    {
                        if (column.Value.ValueKind != JsonValueKind.Array || column.Value.GetArrayLength() != ids.GetArrayLength())
                            throw new FormatException($"Malformed craft column: {column.Name}");
                        writer.WritePropertyName(column.Name);
                        column.Value[i].WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                using var oneRow = JsonDocument.Parse(objectRow.ToArray());
                var craft = ParseCraft(oneRow.RootElement);
                if (craft is not null) projects.Add(craft);
            }
            return projects;
        }
        if (rows.ValueKind == JsonValueKind.Object && rows.EnumerateObject().Any() == false)
            return [];
        throw new FormatException("Unexpected crafting response; keeping existing data instead of showing an empty list.");
    }

    private static CraftProject? ParseCraft(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid craft row.");
        var kind = JsonData.String(row, "kind") ?? "Active";
        if (!string.Equals(kind, "Active", StringComparison.OrdinalIgnoreCase)) return null;
        if (JsonData.Boolean(row, "public") == false || JsonData.Boolean(row, "is_public") == false)
            return null;

        var functionIds = new List<long>();
        var functions = JsonData.Property(row, "building_function_types");
        if (functions.ValueKind == JsonValueKind.Array)
            foreach (var element in functions.EnumerateArray())
                if (JsonData.Int64(element) is long number) functionIds.Add(number);
        var id = JsonData.RequireId(row, "id");
        var outputs = JsonData.Property(row, "outputs");
        string? outputName = null;
        if (outputs.ValueKind == JsonValueKind.Array && outputs.GetArrayLength() > 0)
            outputName = JsonData.String(outputs[0], "name");
        var craft = new CraftProject(
            id,
            JsonData.Id(row, "owner_id") ?? JsonData.Id(row, "owner_entity_id") ?? "0",
            JsonData.String(row, "owner_name") ?? "",
            JsonData.String(row, "building_name") ?? "Unknown station",
            functionIds,
            JsonData.String(row, "recipe_name") ?? outputName ?? "Unknown recipe",
            JsonData.Int64(row, "quantity") ?? JsonData.Int64(row, "craft_count") ?? 1,
            JsonData.Int64(row, "progress"),
            JsonData.Int64(row, "progress_total"),
            JsonData.String(row, "status") ?? "Active",
            JsonData.DateTime(row, "timestamp"),
            outputName);
        return craft.IsFinished ? null : craft;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>JSON accessor that retains full decimal precision of BitCraft's 64-bit IDs.</summary>
internal static class JsonData
{
    public static JsonElement Property(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value)
            ? value : default;

    public static string? String(JsonElement row, string name) => String(Property(row, name));

    public static string? String(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    public static string? Id(JsonElement row, string name) => String(Property(row, name));
    public static string RequireId(JsonElement row, string name) => Id(row, name) is { Length: > 0 } id
        ? ValidateId(id) : throw new FormatException($"Required identifier {name} is missing.");

    public static string ValidateId(string id) =>
        id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit) && long.TryParse(id, out var value) && value > 0
            ? id : throw new FormatException("Invalid claim/project identifier.");

    public static long? Int64(JsonElement row, string name) => Int64(Property(row, name));
    public static long? Int64(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    public static bool? Boolean(JsonElement row, string name) => Property(row, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    public static DateTimeOffset? DateTime(JsonElement row, string name) =>
        System.DateTimeOffset.TryParse(String(row, name), out var date) ? date : null;
}
