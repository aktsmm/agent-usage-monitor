using System.Text.Json.Serialization;

namespace AiUsageTray.Core;

/// <summary>
/// One connection to one account of a service (a contract). Several profiles of the same service coexist and are never summed.
/// The primary account of each service is synthesized from the legacy single-account settings; extra accounts are stored as profiles.
/// </summary>
public sealed record AccountProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public Provider Provider { get; init; }
    public bool Enabled { get; init; } = true;
    public string? PinnedAccount { get; init; }

    // Copilot
    public string Login { get; init; } = "";
    public string? CredentialTarget { get; init; }
    public string Executable { get; init; } = "";

    // Codex
    public string Email { get; init; } = "";
    public string Home { get; init; } = "";
    public bool CliFallback { get; init; }

    // Claude
    public string Label { get; init; } = "";
    public string Snapshot { get; init; } = "";

    /// <summary>What the connection itself knows about who it is (GitHub login, Codex email, Claude label).</summary>
    [JsonIgnore]
    public string Identity => Provider switch { Provider.Copilot => Login, Provider.Codex => Email, _ => Label };

    /// <summary>The account this connection is bound to, when known: the pinned account, or for Claude the one derived from its label.</summary>
    [JsonIgnore]
    public string? ExpectedAccount => Provider == Provider.Claude
        ? (string.IsNullOrWhiteSpace(Label) ? null : AccountKey.FromIdentity(Provider.Claude, Label).Id)
        : PinnedAccount;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 || !Enum.IsDefined(Provider) ||
            PinnedAccount is { } pin && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)) ||
            Enabled && string.IsNullOrWhiteSpace(Identity))
            throw new UsageException(FailureKind.Schema, "account_profile_invalid");
        if (Provider == Provider.Codex && Enabled && string.IsNullOrWhiteSpace(Home) ||
            Provider == Provider.Claude && Enabled && string.IsNullOrWhiteSpace(Snapshot))
            throw new UsageException(FailureKind.Schema, "account_profile_incomplete");
    }
}

/// <summary>Display choices that belong to the user, not to the connection: they never change the account identity or history.</summary>
public sealed record AccountPreference
{
    public string? Alias { get; init; }
    public bool Visible { get; init; } = true;
    public bool ShowInStrip { get; init; }
}

public sealed record AccountEntry(AccountProfile Profile, AccountPreference Preference, string Name, bool IsPrimary)
{
    public bool InStrip => Profile.Enabled && Preference.Visible && Preference.ShowInStrip;
}

public static class AccountNames
{
    public const int MaximumAlias = 24;
    public static string PrimaryId(Provider provider) => "primary-" + provider.ToString().ToLowerInvariant();

    /// <summary>Automatic name: the part of the identity before '@' (an e-mail is not shown in full), else the identity, else the service.</summary>
    public static string Auto(AccountProfile profile)
    {
        var identity = profile.Identity.Trim();
        var at = identity.IndexOf('@');
        var name = at > 0 ? identity[..at] : identity;
        if (name.Length == 0 && profile.Provider == Provider.Codex && profile.Home.Length > 0)
            name = Path.GetFileName(profile.Home.TrimEnd('\\', '/'));
        return Shorten(name.Length == 0 ? profile.Provider.ToString() : name);
    }

    public static string Shorten(string value) => value.Length <= MaximumAlias ? value : value[..(MaximumAlias - 1)] + "…";

    public static string? NormalizeAlias(string? alias)
    {
        var trimmed = alias?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Any(char.IsControl)) throw new UsageException(FailureKind.Schema, "account_alias_invalid");
        return Shorten(trimmed);
    }

    /// <summary>User alias first, then the automatic name. Accounts of one service that would show the same name get a short, stable suffix.</summary>
    public static IReadOnlyList<AccountEntry> Resolve(IEnumerable<AccountProfile> profiles,
        IReadOnlyDictionary<string, AccountPreference>? preferences)
    {
        var list = profiles.ToList();
        var raw = list.Select(p =>
        {
            var preference = preferences is not null && preferences.TryGetValue(p.Id, out var value) ? value : new AccountPreference();
            return (Profile: p, Preference: preference, Base: NormalizeAlias(preference.Alias) ?? Auto(p));
        }).ToList();
        var entries = new List<AccountEntry>();
        foreach (var item in raw)
        {
            var sameName = raw.Where(o => o.Profile.Provider == item.Profile.Provider &&
                string.Equals(o.Base, item.Base, StringComparison.OrdinalIgnoreCase)).ToList();
            var name = sameName.Count == 1 ? item.Base : Shorten(item.Base) + " #" + (sameName.IndexOf(item) + 1);
            entries.Add(new(item.Profile, item.Preference, name, item.Profile.Id == PrimaryId(item.Profile.Provider)));
        }
        return entries;
    }
}
