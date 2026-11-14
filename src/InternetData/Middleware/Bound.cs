namespace InternetData.Middleware;

/// <summary>
/// A bound on a numeric member. Every side set must hold, so two of them are a range:
/// <c>Bound.Gte(5).AndLt(100)</c>.
/// </summary>
public sealed class Bound
{
    private readonly double? gte;
    private readonly double? gt;
    private readonly double? lte;
    private readonly double? lt;

    private Bound(double? gte, double? gt, double? lte, double? lt)
    {
        this.gte = gte;
        this.gt = gt;
        this.lte = lte;
        this.lt = lt;
    }

    /// <summary>At or above <paramref name="value"/>.</summary>
    public static Bound Gte(double value) => new(value, null, null, null);

    /// <summary>Strictly above <paramref name="value"/>.</summary>
    public static Bound Gt(double value) => new(null, value, null, null);

    /// <summary>At or below <paramref name="value"/>.</summary>
    public static Bound Lte(double value) => new(null, null, value, null);

    /// <summary>Strictly below <paramref name="value"/>.</summary>
    public static Bound Lt(double value) => new(null, null, null, value);

    /// <summary>Narrows this bound, so <c>Gte(5).AndLt(100)</c> is a range.</summary>
    public Bound AndGte(double value) => new(value, gt, lte, lt);

    /// <summary>Narrows this bound.</summary>
    public Bound AndGt(double value) => new(gte, value, lte, lt);

    /// <summary>Narrows this bound.</summary>
    public Bound AndLte(double value) => new(gte, gt, value, lt);

    /// <summary>Narrows this bound.</summary>
    public Bound AndLt(double value) => new(gte, gt, lte, value);

    internal bool Matches(object? got)
    {
        if (got is null || !TryAsDouble(got, out double value))
        {
            return false;
        }
        if (gte is not null && value < gte)
        {
            return false;
        }
        if (gt is not null && value <= gt)
        {
            return false;
        }
        if (lte is not null && value > lte)
        {
            return false;
        }
        return lt is null || value < lt;
    }

    internal static bool TryAsDouble(object value, out double result)
    {
        switch (value)
        {
            case bool:
                result = 0;
                return false;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float
                or double or decimal:
                result = System.Convert.ToDouble(value);
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
