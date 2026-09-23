using System.Globalization;
using System.Text.Json;
using HandlebarsDotNet;
using Markdig;

namespace reeldeel_new.Cms;

/// <summary>
/// Renders the page from the "index" Handlebars template, content blocks and variables,
/// all stored in the CMS database. The rendered HTML is cached and only rebuilt when
/// <see cref="Invalidate"/> is called (i.e. after an admin save), so the public site
/// reflects edits immediately without a restart.
/// </summary>
public sealed class SiteRenderer
{
    public const string IndexTemplateName = "index";

    // Advanced extensions turn on pipe tables, autolinks, footnotes, etc. so content-block
    // Markdown (tables in particular) renders instead of passing through as plain text.
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // Named formats for the {{date}} helper -> .NET custom date/time format strings.
    private static readonly Dictionary<string, string> DateFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["short-date"] = "M/d/yyyy",
        ["long-date"] = "dddd, MMMM d, yyyy",
        ["short-time"] = "h:mm tt",
        ["long-time"] = "h:mm:ss tt",
        ["long-date+short-time"] = "dddd, MMMM d, yyyy h:mm tt",
        ["long-date+long-time"] = "dddd, MMMM d, yyyy h:mm:ss tt",
        ["date-time"] = "M/d/yyyy h:mm tt",
        ["month-day"] = "MMMM d",
        ["year-month"] = "MMMM yyyy",
    };

    private readonly CmsRepository _repo;
    private readonly IReadOnlyDictionary<string, string> _manifest;
    private readonly object _gate = new();

    private string? _cachedHtml;
    private long _version;        // bumped on each invalidate
    private long _renderedVersion = -1;

    public SiteRenderer(CmsRepository repo, IReadOnlyDictionary<string, string> manifest)
    {
        _repo = repo;
        _manifest = manifest;
    }

    /// <summary>Marks the cached page stale; the next request re-renders from the DB.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    public string Render()
    {
        var current = Interlocked.Read(ref _version);
        if (_cachedHtml is not null && _renderedVersion == current)
            return _cachedHtml;

        lock (_gate)
        {
            current = Interlocked.Read(ref _version);
            if (_cachedHtml is not null && _renderedVersion == current)
                return _cachedHtml;

            _cachedHtml = Build(_repo.GetContentMap(), _repo.GetTemplate(IndexTemplateName)?.Source ?? "", _repo.GetVariableMap());
            _renderedVersion = current;
            return _cachedHtml;
        }
    }

    /// <summary>Trial-renders the page with a proposed edit applied (without saving or
    /// touching the cache). Returns null on success or the Handlebars error message, so
    /// the admin API can reject a change that would break the public site.</summary>
    public string? Validate(string? indexSource = null, string? contentKey = null, string? contentMarkdown = null)
    {
        var content = _repo.GetContentMap();
        if (contentKey is not null)
            content[contentKey] = contentMarkdown ?? "";

        try
        {
            Build(content,
                  indexSource ?? _repo.GetTemplate(IndexTemplateName)?.Source ?? "",
                  _repo.GetVariableMap());

            // The page only renders blocks it references; compile the edited one too so a
            // new, not-yet-used block still gets its template syntax checked.
            if (contentKey is not null)
                Handlebars.Create().Compile(content[contentKey]);
            return null;
        }
        catch (HandlebarsException ex)
        {
            // Runtime errors are wrapped ("Runtime error while rendering..."); the
            // innermost message is the one that says what's wrong.
            Exception inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            return inner.Message;
        }
    }

    private string Build(Dictionary<string, string> content, string indexSource, Dictionary<string, string> variables)
    {
        // Each variable is available as {{name}}; all_data is the same set as a JSON object
        // for window.reeldeel_data. The default encoder escapes < > & so a value can't close
        // the surrounding <script> tag.
        var templateData = variables.ToDictionary(v => v.Key, v => (object)v.Value);
        templateData["all_data"] = JsonSerializer.Serialize(variables);

        var handlebars = Handlebars.Create();

        // Content blocks are Handlebars templates too, so {{variables}} and helpers work
        // inside the Markdown. Compiled lazily, once per build; `rendering` stops a block
        // that (directly or indirectly) includes itself from recursing forever.
        var compiledBlocks = new Dictionary<string, HandlebarsTemplate<object, object>>(StringComparer.OrdinalIgnoreCase);
        var rendering = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // {{{ContentBlocks "key"}}} renders the DB-backed Markdown block with that key.
        handlebars.RegisterHelper("ContentBlocks", (writer, _, args) =>
        {
            var key = args.Length > 0 ? args[0]?.ToString() : null;
            if (string.IsNullOrEmpty(key) || !content.TryGetValue(key, out var markdown) || !rendering.Add(key))
                return;
            try
            {
                if (!compiledBlocks.TryGetValue(key, out var template))
                    compiledBlocks[key] = template = handlebars.Compile(markdown);
                writer.WriteSafeString(Markdown.ToHtml(template(templateData), MarkdownPipeline));
            }
            finally
            {
                rendering.Remove(key);
            }
        });

        // {{date value "long-date"}} formats a date/time string with one of the named
        // formats above (default "short-date" when omitted). Writes nothing if the value
        // is missing or can't be parsed, or if the format name isn't recognized.
        handlebars.RegisterHelper("date", (writer, _, args) =>
        {
            var value = args.Length > 0 ? args[0]?.ToString() : null;
            if (string.IsNullOrEmpty(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return;

            var formatName = args.Length > 1 ? args[1]?.ToString() : "short-date";
            if (string.IsNullOrEmpty(formatName) || !DateFormats.TryGetValue(formatName, out var netFormat))
                return;

            writer.Write(parsed.ToString(netFormat, CultureInfo.InvariantCulture));
        });

        handlebars.RegisterHelper("file-link", (writer, _, args) =>
        {
            var fileName = args.Length > 0 ? args[0]?.ToString() : null;
            if (string.IsNullOrEmpty(fileName))
                return;
            if (_manifest.TryGetValue(fileName, out var mapped))
                fileName = mapped;
            writer.Write($"/{fileName}");
        });

        var compiled = handlebars.Compile(indexSource);
        return compiled(templateData);
    }
}
