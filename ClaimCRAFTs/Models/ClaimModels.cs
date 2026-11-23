namespace ClaimCRAFTs.Models;

public sealed record ClaimOption(string Id, string Name, int Region, int? Tier);
public sealed record ClaimMember(string Id, string Name, bool CanUseStorage);

public sealed record ClaimDetails(
    string Id,
    string Name,
    string OwnerId,
    IReadOnlyDictionary<string, ClaimMember> Members);

public enum OwnerKind { Member, NoPermissions, NonMember }

public sealed record CraftProject(
    string Id,
    string OwnerId,
    string OwnerName,
    string BuildingName,
    IReadOnlyList<long> FunctionIds,
    string RecipeName,
    long Quantity,
    long? Progress,
    long? ProgressTotal,
    string Status,
    DateTimeOffset? LockExpiration,
    string? OutputName)
{
    public int? ProgressPercent => Progress is >= 0 && ProgressTotal is > 0
        ? (int)Math.Clamp(Math.Floor((decimal)Progress.Value * 100 / ProgressTotal.Value + .5m), 0, 100)
        : null;

    public bool IsFinished => string.Equals(Status, "Complete", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase)
        || ProgressPercent == 100;
}

public sealed record CraftSnapshot(ClaimDetails Claim, IReadOnlyList<CraftProject> Projects);

public sealed record LiveMessage(
    string Type, string? ClaimId, string? CraftId, long? Progress, DateTimeOffset? LockExpiration);
