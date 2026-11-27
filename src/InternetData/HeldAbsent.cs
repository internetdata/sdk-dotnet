namespace InternetData;

// A member the API serves on some plans alone keeps the type it had while every plan carried it, so
// a decoded answer without it reads that type's empty value. This says which ones it did not carry,
// for the middleware, which treats a member the wire left out as a plan gap and never as that value.
// An answer built in code lists none: it carries what it holds.
internal interface IHeldAbsent
{
    HashSet<string>? NotServed { get; set; }
}

public partial class LookupResponse : IHeldAbsent
{
    private HashSet<string>? notServed;

    HashSet<string>? IHeldAbsent.NotServed
    {
        get => notServed;
        set => notServed = value;
    }
}

public partial class Asn : IHeldAbsent
{
    private HashSet<string>? notServed;

    HashSet<string>? IHeldAbsent.NotServed
    {
        get => notServed;
        set => notServed = value;
    }
}
