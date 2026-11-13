namespace InternetData;

/// <summary>
/// What a lookup answers.
/// </summary>
/// <remarks>
/// <para>A <c>null</c> member is one your plan does not include. It never means "we could not
/// check", so null and <c>false</c> are genuinely different answers: null is "not in your plan",
/// <c>false</c> is "checked, and no". Write <c>result.IsHosting ?? false</c> when you only care
/// whether the address is flagged, and compare against <c>null</c> when the difference matters. The
/// same holds inside the shared objects: <c>Location.City</c> is null on a plan without it.</para>
/// <para>A detail object that is present but empty (Web IP's empty list) means the flag above it is
/// false. A populated one always carries every one of its keys.</para>
/// </remarks>
public sealed class Result
{
    private readonly LookupResponse raw;

    private Result(LookupResponse raw)
    {
        this.raw = raw;
    }

    internal static Result Of(LookupResponse body) => new(body);

    /// <summary>
    /// The answer a bogon gets, in the full shape the API serves one on a paid plan: every flag
    /// present and false, every detail object present and empty, and the location, AS, company,
    /// abuse contact and registry records with every field empty, as the API answers a bogon.
    /// </summary>
    /// <remarks>
    /// This is deliberately the WIDEST shape regardless of your plan, so do not infer which fields
    /// your plan includes from a bogon answer.
    /// </remarks>
    internal static Result Bogon(string ip) => new(
        new LookupResponse
        {
            Ip = ip,
            IsBogon = true,
            IsVpn = false,
            IsTor = false,
            IsRelay = false,
            IsResproxy = false,
            IsDcproxy = false,
            IsMobproxy = false,
            IsPubproxy = false,
            IsHosting = false,
            IsCdn = false,
            IsAnycast = false,
            IsCarrier = false,
            IsSatellite = false,
            IsSsh = false,
            IsSmtp = false,
            IsWeb = false,
            IsDnsResolver = false,
            IsBittorrent = false,
            Vpn = new ClassDetail(),
            Tor = new ClassDetail(),
            Relay = new ClassDetail(),
            Resproxy = new ProxyDetail(),
            Dcproxy = new ProxyDetail(),
            Mobproxy = new ProxyDetail(),
            Pubproxy = new PubproxyDetail(),
            Hosting = new ClassDetail(),
            Cdn = new ClassDetail(),
            Carrier = new CarrierDetail(),
            Satellite = new SatelliteDetail(),
            Ssh = new SshDetail(),
            Smtp = new SmtpDetail(),
            Web = Array.Empty<WebDetail>(),
            DnsResolver = new DnsResolverDetail(),
            Bittorrent = new BitTorrentDetail(),
            Location = new Location
            {
                Continent = "",
                ContinentName = "",
                Country = "",
                CountryName = "",
                Timezone = "",
                CurrencyCode = "",
                CurrencyName = "",
                CurrencySymbol = "",
                FlagEmoji = "",
                FlagUnicode = "",
                Region = "",
                RegionCode = "",
                City = "",
                PostalCode = "",
                Latitude = 0,
                Longitude = 0,
                AccuracyRadius = 0,
                GeonameId = "",
                DmaCode = "",
                LastChanged = "",
            },
            Asn = new Asn { Number = 0, Name = "", Domain = "", Type = "", Prefix = "", Country = "", RpkiStatus = "" },
            Company = new Company { Name = "", Domain = "", Type = "", Country = "" },
            Hostname = "",
            Abuse = new AbuseContact
            {
                Email = "",
                Name = "",
                Phone = "",
                Address = "",
                Country = "",
                Network = "",
                Validated = false,
                UnvalidatedSince = "",
            },
            Whois = new Whois
            {
                Id = "",
                Name = "",
                Org = "",
                Country = "",
                Status = "",
                Source = "",
                Domain = "",
                AbuseEmail = "",
                Asn = 0,
            },
            Rwhois = new Rwhois { Id = "", Name = "", Country = "", Source = "", Domain = "", AbuseEmail = "" },
            HostedDomains = new HostedDomains { Total = 0 },
        });

    /// <summary>The address that was looked up, as parsed: <c>::ffff:a.b.c.d</c> answers as <c>a.b.c.d</c>.</summary>
    public string Ip => raw.Ip;

    /// <summary>Whether the address is reserved space, answered from the address alone. On every answer.</summary>
    /// <remarks>
    /// This client answers a bogon itself, so a lookup of one never reaches the network.
    /// </remarks>
    public bool IsBogon => raw.IsBogon;

    /// <summary>Whether the address is in VPN IP: a VPN service's server. Paid plans and the trial.</summary>
    public bool? IsVpn => raw.IsVpn;

    /// <summary>Whether the address is in Tor IP: a Tor exit. On every answer.</summary>
    public bool IsTor => raw.IsTor;

    /// <summary>
    /// Whether the address is in Relay IP: an anonymizing relay such as iCloud Private Relay. On every
    /// answer.
    /// </summary>
    public bool IsRelay => raw.IsRelay;

    /// <summary>Whether the address is in Residential Proxy IP's 90-day window. Paid plans and the trial.</summary>
    public bool? IsResproxy => raw.IsResproxy;

    /// <summary>Whether the address is in Datacenter Proxy IP's 90-day window. Paid plans and the trial.</summary>
    public bool? IsDcproxy => raw.IsDcproxy;

    /// <summary>Whether the address is in Mobile Proxy IP's 90-day window. Paid plans and the trial.</summary>
    public bool? IsMobproxy => raw.IsMobproxy;

    /// <summary>
    /// Whether the address is in Public Proxy IP: a public proxy listens on it, or its traffic left
    /// from it. Paid plans and the trial.
    /// </summary>
    public bool? IsPubproxy => raw.IsPubproxy;

    /// <summary>Whether the address is in Hosting IP: a hosting or cloud network. Paid plans and the trial.</summary>
    public bool? IsHosting => raw.IsHosting;

    /// <summary>Whether the address is in CDN IP: a content delivery network. Paid plans and the trial.</summary>
    public bool? IsCdn => raw.IsCdn;

    /// <summary>Whether the address is in Anycast IP: announced from many places at once. On every answer.</summary>
    public bool IsAnycast => raw.IsAnycast;

    /// <summary>Whether the address is in Carrier IP: a mobile carrier's address space. On every answer.</summary>
    public bool IsCarrier => raw.IsCarrier;

    /// <summary>
    /// Whether the address is in Satellite IP: a satellite operator's address space. On every answer.
    /// </summary>
    public bool IsSatellite => raw.IsSatellite;

    /// <summary>Whether the address is in SSH IP: an SSH server answered on it. On every answer.</summary>
    public bool IsSsh => raw.IsSsh;

    /// <summary>Whether the address is in SMTP IP: a mail server answered on it. On every answer.</summary>
    public bool IsSmtp => raw.IsSmtp;

    /// <summary>
    /// Whether the address is in Web IP: a web server answered on it on port 80 or 443. On every answer.
    /// </summary>
    public bool IsWeb => raw.IsWeb;

    /// <summary>
    /// Whether the address is in DNS Resolver IP: an open DNS resolver answered on it. On every answer.
    /// </summary>
    public bool IsDnsResolver => raw.IsDnsResolver;

    /// <summary>
    /// Whether the address is in BitTorrent IP: a BitTorrent client was seen on it in the last 90
    /// days. On every answer.
    /// </summary>
    public bool IsBittorrent => raw.IsBittorrent;

    /// <summary>VPN IP's row. Empty when <see cref="IsVpn"/> is false. Paid plans and the trial.</summary>
    public ClassDetail? Vpn => raw.Vpn;

    /// <summary>Tor IP's row. Empty when <see cref="IsTor"/> is false. On every answer.</summary>
    public ClassDetail Tor => raw.Tor;

    /// <summary>Relay IP's row. Empty when <see cref="IsRelay"/> is false. On every answer.</summary>
    public ClassDetail Relay => raw.Relay;

    /// <summary>Detail behind <see cref="IsResproxy"/>. Empty when it is false. Paid plans and the trial.</summary>
    public ProxyDetail? Resproxy => raw.Resproxy;

    /// <summary>Detail behind <see cref="IsDcproxy"/>. Empty when it is false. Paid plans and the trial.</summary>
    public ProxyDetail? Dcproxy => raw.Dcproxy;

    /// <summary>Detail behind <see cref="IsMobproxy"/>. Empty when it is false. Paid plans and the trial.</summary>
    public ProxyDetail? Mobproxy => raw.Mobproxy;

    /// <summary>Detail behind <see cref="IsPubproxy"/>. Empty when it is false. Paid plans and the trial.</summary>
    public PubproxyDetail? Pubproxy => raw.Pubproxy;

    /// <summary>Detail behind <see cref="IsHosting"/>. Empty when it is false. Paid plans and the trial.</summary>
    public ClassDetail? Hosting => raw.Hosting;

    /// <summary>Detail behind <see cref="IsCdn"/>. Empty when it is false. Paid plans and the trial.</summary>
    public ClassDetail? Cdn => raw.Cdn;

    /// <summary>Detail behind <see cref="IsCarrier"/>. Empty when it is false. Paid plans and the trial.</summary>
    public CarrierDetail? Carrier => raw.Carrier;

    /// <summary>Detail behind <see cref="IsSatellite"/>. Empty when it is false. Paid plans and the trial.</summary>
    public SatelliteDetail? Satellite => raw.Satellite;

    /// <summary>Detail behind <see cref="IsSsh"/>. Empty when it is false. Paid plans and the trial.</summary>
    public SshDetail? Ssh => raw.Ssh;

    /// <summary>Detail behind <see cref="IsSmtp"/>. Empty when it is false. Paid plans and the trial.</summary>
    public SmtpDetail? Smtp => raw.Smtp;

    /// <summary>
    /// A row per port that answered, lowest first. Empty when <see cref="IsWeb"/> is false. Paid
    /// plans and the trial.
    /// </summary>
    public IReadOnlyList<WebDetail>? Web => raw.Web;

    /// <summary>Detail behind <see cref="IsDnsResolver"/>. Empty when it is false. Paid plans and the trial.</summary>
    public DnsResolverDetail? DnsResolver => raw.DnsResolver;

    /// <summary>Detail behind <see cref="IsBittorrent"/>. Empty when it is false. Paid plans and the trial.</summary>
    public BitTorrentDetail? Bittorrent => raw.Bittorrent;

    /// <summary>
    /// Where the address is, on every answer: the country and its currency on every plan, and the
    /// region, city and coordinates on a paid one, null on a plan without them.
    /// </summary>
    public Location Location => raw.Location;

    /// <summary>
    /// The AS announcing the address, on every answer: <see cref="InternetData.Asn.Number"/> is 0
    /// where none does.
    /// </summary>
    public Asn Asn => raw.Asn;

    /// <summary>The organization the address belongs to, on every answer.</summary>
    public Company Company => raw.Company;

    /// <summary>
    /// The name the address's reverse DNS (PTR) record points to, empty when it has none. On every
    /// answer.
    /// </summary>
    public string Hostname => raw.Hostname;

    /// <summary>Where to report abuse from the address, on every answer: its email on every plan.</summary>
    public AbuseContact Abuse => raw.Abuse;

    /// <summary>The registry's record for the range. Paid plans and the trial.</summary>
    public Whois? Whois => raw.Whois;

    /// <summary>The RWhois record for the range. Paid plans and the trial.</summary>
    public Rwhois? Rwhois => raw.Rwhois;

    /// <summary>How many domains are hosted on the address. Paid plans and the trial.</summary>
    public HostedDomains? HostedDomains => raw.HostedDomains;

    /// <summary>
    /// On answers to a key on the free plan only: <c>false</c> while the day's trial of every field
    /// lasts, <c>true</c> once the answer has Unlimited's.
    /// </summary>
    public bool? TrialExpired => raw.TrialExpired;

    /// <summary>The response exactly as it came off the wire.</summary>
    /// <remarks>
    /// The same instance is handed to every later caller that hits this address in the cache, so
    /// treat it as read only.
    /// </remarks>
    public LookupResponse Raw => raw;

    /// <inheritdoc/>
    public override string ToString()
        => $"{Ip} isBogon={IsBogon}";
}
