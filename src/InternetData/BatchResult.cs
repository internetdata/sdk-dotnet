using System.Collections;

namespace InternetData;

/// <summary>
/// One address's answer inside a batch: either a <see cref="InternetData.Result"/> or the error
/// that address hit.
/// </summary>
/// <remarks>
/// C# has no union type, so "the failure is the value" needs a wrapper. One address erroring must
/// leave the others answered, which is why a batch carries these rather than throwing.
/// </remarks>
public sealed class BatchResult
{
    private BatchResult(Result? result, InternetDataException? error)
    {
        this.Result = result;
        this.Error = error;
    }

    internal static BatchResult Found(Result result) => new(result, null);

    internal static BatchResult Failed(InternetDataException error) => new(null, error);

    /// <summary>Whether this address was answered.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>The answer, or null when this address failed.</summary>
    public Result? Result { get; }

    /// <summary>Why this address failed, or null when it did not.</summary>
    public InternetDataException? Error { get; }

    /// <summary>The answer, rethrowing this address's error if it has one.</summary>
    public Result GetResultOrThrow() => Result ?? throw Error!;

    /// <inheritdoc/>
    public override string ToString() => IsSuccess ? Result!.ToString() : $"error: {Error!.Message}";
}

// Dictionary<K,V> makes no promise about enumeration order, and the batch contract is that
// iteration follows the input. Backed by the dictionary for lookup and by the input list for
// order, so a caller gets both without paying for a sort.
internal sealed class OrderedResults : IReadOnlyDictionary<string, BatchResult>
{
    private readonly IReadOnlyList<string> order;
    private readonly IReadOnlyDictionary<string, BatchResult> byIp;

    internal OrderedResults(IReadOnlyList<string> order, IReadOnlyDictionary<string, BatchResult> byIp)
    {
        this.order = order;
        this.byIp = byIp;
    }

    public BatchResult this[string key] => byIp[key];

    public IEnumerable<string> Keys => order;

    public IEnumerable<BatchResult> Values => order.Select(ip => byIp[ip]);

    public int Count => order.Count;

    public bool ContainsKey(string key) => byIp.ContainsKey(key);

    public bool TryGetValue(string key, out BatchResult value) => byIp.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, BatchResult>> GetEnumerator()
        => order.Select(ip => new KeyValuePair<string, BatchResult>(ip, byIp[ip])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
