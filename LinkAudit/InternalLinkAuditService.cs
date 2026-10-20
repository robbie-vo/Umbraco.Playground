using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.LinkAudit;

namespace Umbraco.Playground.LinkAudit;

/// <summary>
/// Replaces the LinkAudit crawler with <see cref="InternalLinkAuditService"/>, which decorates the original.
/// </summary>
[ComposeAfter(typeof(LinkAuditComposer))]
public class InternalLinkAuditComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<LinkAuditService>();
        builder.Services.AddUnique<ILinkAuditService, InternalLinkAuditService>();
    }
}

/// <summary>
/// Wraps the LinkAudit crawler and adds <see cref="LinkFindingKind.BrokenInternal"/> findings for internal
/// links (content pickers, multi URL pickers and rich text links) whose target page is no longer published,
/// is in the recycle bin or has been deleted. Such links render as <c>href="#"</c> on the front end, which
/// the external check never sees.
/// </summary>
public sealed partial class InternalLinkAuditService : ILinkAuditService
{
    private readonly LinkAuditService _inner;
    private readonly IUmbracoContextFactory _umbracoContextFactory;
    private readonly IDocumentNavigationQueryService _navigation;
    private readonly IVariationContextAccessor _variationContextAccessor;
    private readonly IContentService _contentService;
    private readonly IOptionsMonitor<LinkAuditSettings> _options;

    public InternalLinkAuditService(
        LinkAuditService inner,
        IUmbracoContextFactory umbracoContextFactory,
        IDocumentNavigationQueryService navigation,
        IVariationContextAccessor variationContextAccessor,
        IContentService contentService,
        IOptionsMonitor<LinkAuditSettings> options)
    {
        _inner = inner;
        _umbracoContextFactory = umbracoContextFactory;
        _navigation = navigation;
        _variationContextAccessor = variationContextAccessor;
        _contentService = contentService;
        _options = options;
    }

    // Pickers store "umb://document/<guid>", rich text stores "/{localLink:<guid>}" (or the legacy UDI form).
    [GeneratedRegex(@"umb://document/(?<key>[0-9a-f]{32})", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentUdiRegex();

    [GeneratedRegex(@"\{localLink:(?<key>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\}", RegexOptions.IgnoreCase)]
    private static partial Regex LocalLinkRegex();

    public async Task<LinkAuditReport> RunAuditAsync(CancellationToken cancellationToken)
    {
        LinkAuditReport report = await _inner.RunAuditAsync(cancellationToken);
        (List<LinkAuditFinding> findings, int linksScanned) = await FindBrokenInternalLinksAsync(cancellationToken);

        return report with
        {
            LinksScanned = report.LinksScanned + linksScanned,
            Findings = [.. report.Findings, .. findings],
        };
    }

    private async Task<(List<LinkAuditFinding> Findings, int LinksScanned)> FindBrokenInternalLinksAsync(CancellationToken cancellationToken)
    {
        LinkAuditSettings settings = _options.CurrentValue;
        var findings = new List<LinkAuditFinding>();
        var seen = new HashSet<string>();
        var targetProblems = new Dictionary<Guid, string?>();
        int linksScanned = 0;

        using UmbracoContextReference contextRef = _umbracoContextFactory.EnsureUmbracoContext();
        IPublishedContentCache? contentCache = contextRef.UmbracoContext.Content;
        IPublishedMediaCache? mediaCache = contextRef.UmbracoContext.Media;
        if (contentCache is null)
        {
            return (findings, linksScanned);
        }

        IEnumerable<Guid> rootKeys = [];
        bool hasRoots = string.IsNullOrWhiteSpace(settings.RootDocumentTypeAlias)
            ? _navigation.TryGetRootKeys(out rootKeys)
            : _navigation.TryGetRootKeysOfType(settings.RootDocumentTypeAlias, out rootKeys);
        if (!hasRoots)
        {
            return (findings, linksScanned);
        }

        foreach (Guid rootKey in rootKeys)
        {
            if (!_navigation.TryGetDescendantsKeysOrSelfKeys(rootKey, out IEnumerable<Guid> pageKeys))
            {
                continue;
            }

            foreach (Guid pageKey in pageKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IPublishedContent? page = await contentCache.GetByIdAsync(pageKey);
                if (page is null)
                {
                    continue;
                }

                IEnumerable<string> cultures = page.Cultures.Count > 0 ? page.Cultures.Keys : [string.Empty];
                foreach (string culture in cultures)
                {
                    _variationContextAccessor.VariationContext = new VariationContext(culture);
                    string? pageUrl = SafeUrl(page, culture);

                    foreach (IPublishedProperty property in page.Properties)
                    {
                        string? source = SafeGetSourceValue(property, culture);
                        if (string.IsNullOrEmpty(source))
                        {
                            continue;
                        }

                        IEnumerable<(Match Match, bool MayBeMedia)> matches =
                            DocumentUdiRegex().Matches(source).Select(m => (m, false))
                                .Concat(LocalLinkRegex().Matches(source).Select(m => (m, true)));

                        foreach ((Match match, bool mayBeMedia) in matches)
                        {
                            Guid targetKey = Guid.Parse(match.Groups["key"].Value);
                            if (!seen.Add($"{pageKey}|{culture}|{property.Alias}|{targetKey}"))
                            {
                                continue;
                            }

                            linksScanned++;

                            if (!targetProblems.TryGetValue(targetKey, out string? problem))
                            {
                                problem = await GetTargetProblemAsync(targetKey, mayBeMedia, contentCache, mediaCache);
                                targetProblems[targetKey] = problem;
                            }

                            if (problem is not null)
                            {
                                findings.Add(new LinkAuditFinding(
                                    page.Name, pageKey, pageUrl, culture, property.Alias,
                                    $"umb://document/{targetKey:N}", LinkFindingKind.BrokenInternal, null, problem));
                            }
                        }
                    }
                }
            }
        }

        return (findings, linksScanned);
    }

    /// <summary>Returns why the link target can't be shown, or null when it resolves to published content.</summary>
    private async Task<string?> GetTargetProblemAsync(Guid key, bool mayBeMedia, IPublishedContentCache contentCache, IPublishedMediaCache? mediaCache)
    {
        if (await contentCache.GetByIdAsync(key) is not null)
        {
            return null;
        }

        // A bare {localLink:guid} can also point at a media item.
        if (mayBeMedia && mediaCache is not null && await mediaCache.GetByIdAsync(key) is not null)
        {
            return null;
        }

        var content = _contentService.GetById(key);
        if (content is null)
        {
            return mayBeMedia ? "Target page or media no longer exists" : "Target page no longer exists";
        }

        return content.Trashed
            ? $"Target page '{content.Name}' is in the recycle bin"
            : $"Target page '{content.Name}' is not published";
    }

    private static string? SafeUrl(IPublishedContent page, string culture)
    {
        try
        {
            return page.Url(culture);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetSourceValue(IPublishedProperty property, string culture)
    {
        try
        {
            return property.GetSourceValue(culture)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
