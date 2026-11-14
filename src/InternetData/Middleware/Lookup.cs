namespace InternetData.Middleware;

/// <summary>What a middleware attached to the request, whether or not it succeeded.</summary>
public sealed class Lookup
{
    internal Lookup(bool blocked, string? ip, Result? result, InternetDataException? error)
    {
        Blocked = blocked;
        Ip = ip;
        Result = result;
        Error = error;
    }

    /// <summary>Whether the condition matched. Always false when none was configured.</summary>
    public bool Blocked { get; }

    /// <summary>The address that was classified, as the selector resolved it.</summary>
    public string? Ip { get; }

    /// <summary>The answer. Null when the lookup failed.</summary>
    public Result? Result { get; }

    /// <summary>Why the lookup failed. Null when it succeeded.</summary>
    public InternetDataException? Error { get; }
}
