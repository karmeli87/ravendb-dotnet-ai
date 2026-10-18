namespace RavenDB.Agents.AI;

/// <summary>Options shared by every RavenDB integration in this library.</summary>
public abstract class RavenOptions
{
    /// <summary>Gets or sets the database name; <see langword="null"/> uses the store's default database.</summary>
    public string? Database { get; set; }
}

/// <summary>Options for integrations that own their documents (chat history, sessions, checkpoints, files, skills).</summary>
/// <remarks>
/// Document ids are <c>{DocumentIdPrefix}/...</c>; each options class documents its own layout. Like all RavenDB ids, they
/// are case-insensitive.
/// </remarks>
public abstract class RavenDocumentStoreOptions : RavenOptions
{
    /// <summary>Initializes a new instance of the <see cref="RavenDocumentStoreOptions"/> class.</summary>
    /// <param name="defaultDocumentIdPrefix">The default document id prefix.</param>
    protected RavenDocumentStoreOptions(string defaultDocumentIdPrefix)
    {
        DocumentIdPrefix = defaultDocumentIdPrefix;
    }

    /// <summary>Gets or sets the document id prefix.</summary>
    public string DocumentIdPrefix
    {
        get;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value.TrimEnd('/');
        }
    }

    /// <summary>
    /// Gets or sets a time-to-live after which RavenDB deletes a document, counted from when it was last stored
    /// (for chat history, from when the conversation was created). Requires the Expiration feature to be enabled on the database.
    /// </summary>
    public TimeSpan? TimeToLive { get; set; }
}
