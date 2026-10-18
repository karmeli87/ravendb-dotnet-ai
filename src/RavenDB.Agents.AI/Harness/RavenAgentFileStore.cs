using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Raven.Client.Documents;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// Gives agents files that are saved in RavenDB, one <see cref="AgentFile"/> document per file, so notes and working files
/// survive restarts and can be shared between machines.
/// </summary>
/// <remarks>
/// <para>
/// Use it with <see cref="FileMemoryProvider"/> (the agent keeps notes) or <see cref="FileAccessProvider"/> (the agent reads
/// and writes files).
/// </para>
/// <list type="bullet">
/// <item><description>Each <paramref name="root"/> is a separate set of files, e.g. one per user or tenant.</description></item>
/// <item><description>Folders exist as long as they contain files; you don't create them.</description></item>
/// <item><description>Paths are case-insensitive: <c>Notes/A.md</c> and <c>notes/a.md</c> are the same file.</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var files = new RavenAgentFileStore(store, root: "user-1");
/// var memory = new FileMemoryProvider(files);   // add to the agent's context providers
///
/// await files.WriteAsync("notes/todo.md", "- call the bank");
/// string? todo = await files.ReadAsync("notes/todo.md");
/// ]]></code>
/// </example>
/// <param name="store">An initialized RavenDB document store.</param>
/// <param name="root">The name of this set of files, e.g. a user or tenant id. It can't contain <c>/</c>.</param>
/// <param name="options">Optional settings, such as how long files are kept.</param>
public sealed class RavenAgentFileStore(IDocumentStore store, string root = "default", RavenAgentFileStoreOptions? options = null) : AgentFileStore
{
    private readonly IDocumentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly RavenAgentFileStoreOptions _options = options ?? new RavenAgentFileStoreOptions();
    private readonly string _root = string.IsNullOrWhiteSpace(root) || root.Contains('/')
        ? throw new ArgumentException("Root is required and must not contain '/'.", nameof(root))
        : root;

    /// <inheritdoc />
    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = NormalizeFilePath(path);
        var file = new AgentFile { Root = _root, Path = normalized, Content = content, LastModified = DateTime.UtcNow };

        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        await session.StoreAsync(file, GetDocumentId(normalized), cancellationToken).ConfigureAwait(false);
        RavenHelpers.SetMetadata(session, file, AgentFile.CollectionName, _options.TimeToLive);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        var file = await session.LoadAsync<AgentFile>(GetDocumentId(NormalizeFilePath(path)), cancellationToken).ConfigureAwait(false);
        return file?.Content;
    }

    /// <inheritdoc />
    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var id = GetDocumentId(NormalizeFilePath(path));
        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        if (!await session.Advanced.ExistsAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        session.Delete(id);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public override async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        return await session.Advanced.ExistsAsync(GetDocumentId(NormalizeFilePath(path)), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
    {
        var directories = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        var prefix = DirectoryPrefix(directory);
        foreach (var relative in (await ListPathsAsync(prefix, cancellationToken).ConfigureAwait(false)).Select(p => p[prefix.Length..]))
        {
            var separator = relative.IndexOf('/');
            if (separator < 0)
            {
                files.Add(relative);
            }
            else if (seenDirectories.Add(relative[..separator]))
            {
                directories.Add(relative[..separator]);
            }
        }

        return
        [
            .. directories.Select(d => new FileStoreEntry(d, FileStoreEntry.Directory)),
            .. files.Select(f => new FileStoreEntry(f, FileStoreEntry.File)),
        ];
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(string directory, string regexPattern, string? globPattern = null, bool recursive = false, CancellationToken cancellationToken = default)
    {
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        Matcher? matcher = null;
        if (globPattern is not null)
        {
            matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(globPattern);
        }

        var prefix = DirectoryPrefix(directory);
        var idPrefix = GetDocumentId(prefix);
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        var files = await session.Query<AgentFile>(collectionName: AgentFile.CollectionName)
            .Customize(query => query.WaitForNonStaleResults())
            .Where(file => file.Id.StartsWith(idPrefix) && Regex.IsMatch(file.Content, regexPattern))
            .Select(file => new { file.Path, file.Content })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return files
            .Select(file => (Relative: file.Path[prefix.Length..], file.Content))
            .Where(file => (recursive || !file.Relative.Contains('/')) && (matcher is null || matcher.Match(file.Relative).HasMatches))
            .Select(file => ScanContent(file.Relative, file.Content, regex))
            .OfType<FileSearchResult>()
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>Does nothing: folders exist as long as they contain files.</remarks>
    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private async Task<List<string>> ListPathsAsync(string pathPrefix, CancellationToken cancellationToken)
    {
        var idPrefix = GetDocumentId(pathPrefix);
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        return await session.Query<AgentFile>(collectionName: AgentFile.CollectionName)
            .Where(file => file.Id.StartsWith(idPrefix))
            .Select(file => file.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string DirectoryPrefix(string directory) => NormalizePath(directory) is { Length: > 0 } normalized ? normalized + "/" : string.Empty;

    private string GetDocumentId(string normalizedPath) => $"{_options.DocumentIdPrefix}/{_root}/{normalizedPath}";

    private static string NormalizeFilePath(string path)
    {
        var normalized = NormalizePath(path);
        return normalized.Length == 0 ? throw new ArgumentException("A file path is required.", nameof(path)) : normalized;
    }

    private static string NormalizePath(string? path)
    {
        var segments = new List<string>();
        foreach (var segment in (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment == "..")
            {
                throw new ArgumentException($"Path '{path}' must not contain '..' segments.", nameof(path));
            }

            if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }
}

/// <summary>Settings for a <see cref="RavenAgentFileStore"/>.</summary>
/// <remarks>
/// Each file is the document <c>{DocumentIdPrefix}/{root}/{path}</c>, e.g. <c>AgentFiles/user-1/notes/todo.md</c>; the prefix
/// defaults to <c>AgentFiles</c>.
/// </remarks>
public sealed class RavenAgentFileStoreOptions() : RavenDocumentStoreOptions("AgentFiles");

/// <summary>A file saved by <see cref="RavenAgentFileStore"/>.</summary>
public sealed class AgentFile
{
    /// <summary>
    /// The collection of these documents, <c>@AgentFiles</c>. Pass it as <c>collectionName</c> when you query them.
    /// </summary>
    public const string CollectionName = "@AgentFiles";

    /// <summary>Gets or sets the document id, <c>{prefix}/{root}/{path}</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the set of files this file belongs to (the store's root).</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's path within its root, e.g. <c>notes/todo.md</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's text.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets when the file was last written (UTC).</summary>
    public DateTime LastModified { get; set; }
}
