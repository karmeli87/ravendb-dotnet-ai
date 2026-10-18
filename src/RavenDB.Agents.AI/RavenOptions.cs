namespace RavenDB.Agents.AI;

/// <summary>Settings shared by every class in this library.</summary>
public abstract class RavenOptions
{
    /// <summary>Gets or sets the database to use. <see langword="null"/> uses the document store's default database.</summary>
    public string? Database { get; set; }
}

/// <summary>Settings shared by the classes that store their own documents: chat history, sessions, checkpoints, files and skills.</summary>
/// <remarks>Each class documents its own document ids. Like all RavenDB ids, they are case-insensitive.</remarks>
public abstract class RavenDocumentStoreOptions : RavenOptions
{
    /// <summary>Sets the default document id prefix.</summary>
    /// <param name="defaultDocumentIdPrefix">The prefix used when <see cref="DocumentIdPrefix"/> isn't set.</param>
    protected RavenDocumentStoreOptions(string defaultDocumentIdPrefix)
    {
        DocumentIdPrefix = defaultDocumentIdPrefix;
    }

    /// <summary>Gets or sets the prefix of the document ids, e.g. <c>Checkpoints</c>. A trailing <c>/</c> is removed.</summary>
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
    /// Gets or sets how long RavenDB keeps each document before deleting it, counted from when it was last saved (for chat
    /// history, from when the conversation started). <see langword="null"/> keeps documents forever.
    /// </summary>
    /// <remarks>Turn on document expiration in the database for this to take effect.</remarks>
    public TimeSpan? TimeToLive { get; set; }
}
