using System.DirectoryServices;
using System.Runtime.Versioning;
using System.Text;
using IntuneDeviceActions.Workers.AdObjectCleanup.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntuneDeviceActions.Workers.AdObjectCleanup.Services;

/// <summary>
/// Deletes on-prem AD computer objects via <see cref="System.DirectoryServices"/>
/// (LDAP). Scoped to the configured OU subtree; applies the exclusion list and
/// the per-message delete cap before any deletion.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActiveDirectoryCleanupService : IActiveDirectoryCleanupService
{
    private readonly AdCleanupOptions _opts;
    private readonly ILogger<ActiveDirectoryCleanupService> _log;

    public ActiveDirectoryCleanupService(IOptions<AdCleanupOptions> opts, ILogger<ActiveDirectoryCleanupService> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    public AdCleanupResult DeleteByName(string targetName, CancellationToken ct)
    {
        var matches = FindComputers(targetName);

        var exclusion = new HashSet<string>(
            _opts.ExclusionNames.Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        var toDelete = matches.Where(m => !exclusion.Contains(m.Name)).ToList();
        var excluded = matches.Count - toDelete.Count;

        if (toDelete.Count == 0)
        {
            return new AdCleanupResult { Found = matches.Count, Excluded = excluded, DryRun = _opts.DryRun };
        }

        if (toDelete.Count > _opts.MaxDeletePerMessage)
        {
            return new AdCleanupResult
            {
                Found = matches.Count,
                Excluded = excluded,
                CapExceeded = true,
                DryRun = _opts.DryRun,
            };
        }

        var deleted = 0;
        var skipped = 0;
        var report = new List<string>(toDelete.Count);

        foreach (var m in toDelete)
        {
            ct.ThrowIfCancellationRequested();

            if (_opts.DryRun)
            {
                report.Add($"WOULDDELETE:{m.DistinguishedName}");
                skipped++;
                continue;
            }

            try
            {
                using var entry = new DirectoryEntry($"LDAP://{Escape(m.DistinguishedName)}");
                // Computer objects can have child objects — delete the whole subtree.
                entry.DeleteTree();
                entry.CommitChanges();
                report.Add($"DELETED:{m.DistinguishedName}");
                deleted++;
            }
            catch (DirectoryServicesCOMException ex) when (IsTransient(ex))
            {
                throw new TransientAdException($"Transient AD failure deleting {m.DistinguishedName}: {ex.Message}", ex);
            }
        }

        return new AdCleanupResult
        {
            Found = matches.Count,
            Excluded = excluded,
            Deleted = deleted,
            Skipped = skipped,
            DryRun = _opts.DryRun,
            Objects = report,
        };
    }

    private List<(string Name, string DistinguishedName)> FindComputers(string targetName)
    {
        var results = new List<(string, string)>();
        DirectoryEntry root;
        try
        {
            root = string.IsNullOrWhiteSpace(_opts.SearchBase)
                ? new DirectoryEntry()                                   // default naming context (whole domain)
                : new DirectoryEntry($"LDAP://{Escape(_opts.SearchBase!)}");
        }
        catch (DirectoryServicesCOMException ex)
        {
            throw new TransientAdException($"Cannot bind to AD root: {ex.Message}", ex);
        }

        using (root)
        using (var searcher = new DirectorySearcher(root))
        {
            // (&(objectCategory=computer)(cn=<escaped>)) — cn == the computer name.
            searcher.Filter = $"(&(objectCategory=computer)(cn={EscapeLdapFilter(targetName)}))";
            searcher.SearchScope = SearchScope.Subtree;
            searcher.PageSize = 100;
            searcher.PropertiesToLoad.Add("distinguishedName");
            searcher.PropertiesToLoad.Add("cn");

            try
            {
                using var found = searcher.FindAll();
                foreach (SearchResult sr in found)
                {
                    var dn = GetProp(sr, "distinguishedName");
                    var cn = GetProp(sr, "cn");
                    if (!string.IsNullOrEmpty(dn) && !string.IsNullOrEmpty(cn))
                    {
                        results.Add((cn, dn));
                    }
                }
            }
            catch (DirectoryServicesCOMException ex)
            {
                throw new TransientAdException($"AD search failed: {ex.Message}", ex);
            }
        }

        return results;
    }

    private static string GetProp(SearchResult sr, string name)
        => sr.Properties.Contains(name) && sr.Properties[name].Count > 0
            ? sr.Properties[name][0]?.ToString() ?? string.Empty
            : string.Empty;

    // COM/LDAP errors that are worth a retry (server down, busy, timeout).
    private static bool IsTransient(DirectoryServicesCOMException ex)
    {
        // 0x8007200E = SERVER_DOWN(ish)/busy, 0x8007203A = SERVER_DOWN,
        // 0x80072035 = UNAVAILABLE, 0x800705B4 = TIMEOUT. Treat these as transient.
        var code = unchecked((uint)ex.ErrorCode);
        return code is 0x8007200E or 0x8007203A or 0x80072035 or 0x800705B4;
    }

    // Escape a DN embedded in an LDAP:// path (space/# handled by ADSI; guard slashes).
    private static string Escape(string dn) => dn.Replace("/", "\\/");

    // RFC 4515 escaping for a value used inside an LDAP search filter.
    private static string EscapeLdapFilter(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*':  sb.Append("\\2a"); break;
                case '(':  sb.Append("\\28"); break;
                case ')':  sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                case '/':  sb.Append("\\2f"); break;
                default:   sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
