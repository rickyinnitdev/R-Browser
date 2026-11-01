using System.Text;

namespace R1.Core;

public enum ResType { Other, Document, Stylesheet, Image, Media, Font, Script, Xhr, Subdocument, Ping, Websocket }

public enum Verdict { None, Block, Allow }

[Flags]
public enum RuleFlags : uint
{
    None = 0,
    Script = 1 << 0,
    Image = 1 << 1,
    Stylesheet = 1 << 2,
    Subdocument = 1 << 3,
    Xhr = 1 << 4,
    Media = 1 << 5,
    Font = 1 << 6,
    Document = 1 << 7,
    Other = 1 << 8,
    Ping = 1 << 9,
    Websocket = 1 << 10,
    ThirdParty = 1 << 11,
    FirstParty = 1 << 12,
    Important = 1 << 13,
    Exception = 1 << 14,
    AnchorDomain = 1 << 15,
    AnchorEnd = 1 << 16,
    HasWildcard = 1 << 17,
}

internal readonly struct Rule
{
    public readonly int Offset;
    public readonly int Length;
    public readonly RuleFlags Flags;
    public readonly int Next;

    public Rule(int offset, int length, RuleFlags flags, int next)
    {
        Offset = offset;
        Length = length;
        Flags = flags;
        Next = next;
    }
}

/// <summary>
/// ABP-syntax network + cosmetic filter engine. Rules live in one char blob with
/// int-linked buckets (host suffix index + 3/4-gram token index) so the whole
/// 129k rule set stays around 15 MB and matching is allocation free.
/// </summary>
public sealed class FilterEngine
{
    private const RuleFlags TypeAll = (RuleFlags.Script | RuleFlags.Image | RuleFlags.Stylesheet | RuleFlags.Subdocument |
                                        RuleFlags.Xhr | RuleFlags.Media | RuleFlags.Font | RuleFlags.Document |
                                        RuleFlags.Other | RuleFlags.Ping | RuleFlags.Websocket);

    private sealed class EngineData
    {
        public char[] Blob = new char[1 << 20];
        public int BlobLen;
        public Rule[] Rules = new Rule[16384];
        public int RuleCount;

        public readonly Dictionary<string, int> HostHead = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> TokenHead = new(StringComparer.Ordinal);

        public readonly List<string> GenericSelectors = new();
        public readonly Dictionary<string, List<string>> DomainSelectors = new(StringComparer.Ordinal);

        public int NetworkRules;
        public int CosmeticRules;
        public string CssNoCookies = "";
        public string CssWithCookies = "";
    }

    private volatile EngineData _data = new();
    private readonly Dictionary<string, string> _cssCache = new(StringComparer.Ordinal);
    private int _cssCacheChars;
    private const int CssCacheCharBudget = 1_000_000;
    private bool _loaded;

    public int NetworkRules => _data.NetworkRules;
    public int CosmeticRules => _data.CosmeticRules;
    public long LoadMs { get; private set; }

    private static readonly HashSet<string> CommonGrams = new(StringComparer.Ordinal)
    {
        "http", "https", "www.", ".www", "com/", ".com", "net/", ".net", "org/", ".org", "html", ".htm",
        "index", "?utm", "&utm", "=utm", "js?",
    };

    public bool IsLoaded => _loaded;

    public void Load(string text)
    {
        var next = new EngineData();
        var genericSeen = new HashSet<string>(StringComparer.Ordinal);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int lineStart = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            int len = i - lineStart;
            if (len > 1)
            {
                var span = text.AsSpan(lineStart, len);
                if (span[^1] == '\r') span = span[..^1];
                if (span.Length > 1)
                {
                    char first = span[0];
                    // '#' starts a comment, except cosmetic syntax ('##', '#@#', ...).
                    bool comment = first == '!' || first == '[' ||
                                   (first == '#' && !span.StartsWith("##"));
                    if (!comment)
                    {
                        if (span.IndexOf("##".AsSpan()) >= 0) ParseCosmetic(span, next, genericSeen);
                        else ParseNetwork(span, next);
                    }
                }
            }
            lineStart = i + 1;
        }

        Array.Resize(ref next.Blob, next.BlobLen);
        Array.Resize(ref next.Rules, next.RuleCount);
        next.HostHead.TrimExcess();
        next.TokenHead.TrimExcess();
        next.DomainSelectors.TrimExcess();
        foreach (var selectors in next.DomainSelectors.Values) selectors.TrimExcess();
        next.GenericSelectors.TrimExcess();
        next.GenericSelectors.Sort(StringComparer.Ordinal);
        sw.Stop();
        LoadMs = sw.ElapsedMilliseconds;

        _data = next;
        lock (_cssCache)
        {
            _cssCache.Clear();
            _cssCacheChars = 0;
        }
        _loaded = true;
    }

    private void ParseNetwork(ReadOnlySpan<char> span, EngineData next)
    {
        bool exception = span.StartsWith("@@");
        if (exception) span = span[2..];

        RuleFlags flags = exception ? RuleFlags.Exception : RuleFlags.None;
        int dollar = span.LastIndexOf('$');
        if (dollar > 0)
        {
            if (!ParseOptions(span[(dollar + 1)..], ref flags)) return;
            span = span[..dollar];
        }

        if (span.Length < 3) return;
        if (span[0] == '/') return; // regex / cosmetic handled elsewhere

        bool anchorDomain = false;
        if (span.StartsWith("||"))
        {
            anchorDomain = true;
            span = span[2..];
        }
        else if (span[0] == '|') return; // scheme anchored: target is scheme-less

        if (span[^1] == '|')
        {
            flags |= RuleFlags.AnchorEnd;
            span = span[..^1];
        }
        if (span.Length == 0) return;

        var pattern = span.ToString().ToLowerInvariant();
        if (pattern.Contains(' ')) return;
        if (pattern.Contains('*')) flags |= RuleFlags.HasWildcard;

        string? hostKey = null;
        if (anchorDomain)
        {
            flags |= RuleFlags.AnchorDomain;
            int cut = pattern.IndexOfAny(new[] { '/', '^', '*', '|', ':' });
            hostKey = cut < 0 ? pattern : pattern[..cut];
            if (hostKey.Length < 3 || !hostKey.Contains('.')) return;
            foreach (char c in hostKey)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-')) return;
            if (hostKey[0] == '.' || hostKey[^1] == '.') return;
        }

        string token = "";
        if (!anchorDomain)
        {
            token = TokenFor(pattern, next);
            if (token.Length == 0) token = "\u0001"; // fallback bucket
        }

        AddRule(pattern, flags, hostKey, token, next);
        next.NetworkRules++;
    }

    private static bool ParseOptions(ReadOnlySpan<char> opts, ref RuleFlags flags)
    {
        RuleFlags posTypes = RuleFlags.None;
        RuleFlags negTypes = RuleFlags.None;

        while (opts.Length > 0)
        {
            int comma = opts.IndexOf(',');
            var opt = comma < 0 ? opts : opts[..comma];
            opts = comma < 0 ? default : opts[(comma + 1)..];
            var o = opt.Trim();
            if (o.Length == 0) continue;
            bool neg = o[0] == '~';
            if (neg) o = o[1..];

            if (o.Equals("third-party", StringComparison.OrdinalIgnoreCase) || o.Equals("3p", StringComparison.OrdinalIgnoreCase))
            {
                if (neg) flags |= RuleFlags.FirstParty; else flags |= RuleFlags.ThirdParty;
            }
            else if (o.Equals("first-party", StringComparison.OrdinalIgnoreCase) || o.Equals("1p", StringComparison.OrdinalIgnoreCase))
            {
                if (neg) flags |= RuleFlags.ThirdParty; else flags |= RuleFlags.FirstParty;
            }
            else if (o.Equals("script", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Script; else posTypes |= RuleFlags.Script; }
            else if (o.Equals("image", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Image; else posTypes |= RuleFlags.Image; }
            else if (o.Equals("stylesheet", StringComparison.OrdinalIgnoreCase) || o.Equals("css", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Stylesheet; else posTypes |= RuleFlags.Stylesheet; }
            else if (o.Equals("subdocument", StringComparison.OrdinalIgnoreCase) || o.Equals("frame", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Subdocument; else posTypes |= RuleFlags.Subdocument; }
            else if (o.Equals("xmlhttprequest", StringComparison.OrdinalIgnoreCase) || o.Equals("xhr", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Xhr; else posTypes |= RuleFlags.Xhr; }
            else if (o.Equals("media", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Media; else posTypes |= RuleFlags.Media; }
            else if (o.Equals("font", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Font; else posTypes |= RuleFlags.Font; }
            else if (o.Equals("document", StringComparison.OrdinalIgnoreCase) || o.Equals("doc", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Document; else posTypes |= RuleFlags.Document; }
            else if (o.Equals("ping", StringComparison.OrdinalIgnoreCase) || o.Equals("beacon", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Ping; else posTypes |= RuleFlags.Ping; }
            else if (o.Equals("websocket", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Websocket; else posTypes |= RuleFlags.Websocket; }
            else if (o.Equals("other", StringComparison.OrdinalIgnoreCase)) { if (neg) negTypes |= RuleFlags.Other; else posTypes |= RuleFlags.Other; }
            else if (o.Equals("important", StringComparison.OrdinalIgnoreCase)) flags |= RuleFlags.Important;
            else if (o.StartsWith("domain=", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("redirect", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("csp", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("removeparam", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("replace", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("permissions", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("method", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("header", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("to=", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("from=", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("denyallow", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("generichide", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("elemhide", StringComparison.OrdinalIgnoreCase) ||
                     o.StartsWith("specifichide", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            // unknown option: ignore (keeps common lists usable)
        }

        if (posTypes != RuleFlags.None)
        {
            flags |= (posTypes & ~negTypes);
        }
        else if (negTypes != RuleFlags.None)
        {
            flags |= (TypeAll & ~negTypes);
        }

        return true;
    }

    private string TokenFor(string pattern, EngineData data)
    {
        string best = "";
        int bestCost = int.MaxValue;
        int runStart = -1;
        for (int i = 0; i <= pattern.Length; i++)
        {
            bool alnum = i < pattern.Length && char.IsLetterOrDigit(pattern[i]);
            if (alnum)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0)
            {
                int end = i;
                int runLen = end - runStart;
                for (int n = 4; n >= 3; n--)
                {
                    if (runLen < n) continue;
                    for (int j = runStart; j + n <= end; j++)
                    {
                        string gram = pattern.Substring(j, n);
                        if (CommonGrams.Contains(gram)) continue;
                        int cost = data.TokenHead.TryGetValue(gram, out int head) ? ChainLength(head, data.Rules) : 0;
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            best = gram;
                        }
                    }
                }
                runStart = -1;
            }
        }
        return best;
    }

    private static int ChainLength(int head, Rule[] rules)
    {
        int n = 0;
        while (head >= 0 && n < 256) { n++; head = rules[head].Next; }
        return n;
    }

    private static void AddRule(string pattern, RuleFlags flags, string? hostKey, string token, EngineData data)
    {
        if (data.BlobLen + pattern.Length >= data.Blob.Length)
            Array.Resize(ref data.Blob, Math.Max(data.Blob.Length * 2, data.BlobLen + pattern.Length + 1024));
        if (data.RuleCount >= data.Rules.Length)
            Array.Resize(ref data.Rules, data.Rules.Length * 2);

        pattern.CopyTo(0, data.Blob, data.BlobLen, pattern.Length);
        int head = -1;
        if (hostKey != null) data.HostHead.TryGetValue(hostKey, out head);
        else if (token.Length > 0) data.TokenHead.TryGetValue(token, out head);

        int idx = data.RuleCount++;
        data.Rules[idx] = new Rule(data.BlobLen, pattern.Length, flags, head);
        data.BlobLen += pattern.Length;

        if (hostKey != null) data.HostHead[hostKey] = idx;
        else if (token.Length > 0) data.TokenHead[token] = idx;
    }

    private static void ParseCosmetic(ReadOnlySpan<char> span, EngineData data, HashSet<string> genericSeen)
    {
        int idx = span.IndexOf("##".AsSpan());
        if (idx < 0) return;
        var selectorSpan = span[(idx + 2)..].Trim();
        if (selectorSpan.Length == 0 || selectorSpan.Length > 240) return;
        if (selectorSpan[0] == '+') return; // scriptlet
        string selector = selectorSpan.ToString();
        if (selector.Contains(":style(", StringComparison.Ordinal) ||
            selector.Contains(":remove(", StringComparison.Ordinal) ||
            selector.Contains(":has-text(", StringComparison.Ordinal) ||
            selector.Contains(":xpath(", StringComparison.Ordinal) ||
            selector.Contains(":matches-", StringComparison.Ordinal)) return;
        foreach (char c in selector)
            if (c == '\u0000' || c == '\n' || c == '{' || c == '}') return;

        var domains = span[..idx];
        if (domains.Length == 0)
        {
            if (!IsAdLike(selector)) return;
            if (data.GenericSelectors.Count >= 9000) return;
            if (selector.Length > 120) return;
            if (genericSeen.Add(selector)) data.GenericSelectors.Add(selector);
            data.CosmeticRules++;
            return;
        }

        foreach (var range in domains.Split(','))
        {
            var dom = domains[range].Trim();
            if (dom.Length < 3 || dom[0] == '~') continue;
            var d = dom.ToString();
            if (!d.Contains('.')) continue;
            bool hostLike = true;
            foreach (char c in d)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-')) { hostLike = false; break; }
            if (!hostLike) continue;
            if (!data.DomainSelectors.TryGetValue(d, out var list))
                data.DomainSelectors[d] = list = new List<string>();
            if (list.Count < 400 && !list.Contains(selector)) list.Add(selector);
            data.CosmeticRules++;
        }
    }

    private static bool IsAdLike(string selector)
    {
        // keep the generic cosmetic set focused; classic uBO has tens of thousands
        // of generic selectors and injecting all of them sabotages startup time.
        return selector.Contains("ad", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("sponsor", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("promo", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("banner", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("popup", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("overlay", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("sticky", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("consent", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("newsletter", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("paywall", StringComparison.OrdinalIgnoreCase) ||
               selector.Contains("interstitial", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- matching ----------------

    public Verdict Check(string host, string pathQuery, string documentHost, ResType type)
    {
        var data = _data;
        if (!_loaded || data.RuleCount == 0) return Verdict.None;

        string target = (host + pathQuery).ToLowerInvariant();
        RuleFlags typeFlag = TypeFlag(type);
        bool thirdParty = IsThirdParty(host, documentHost);

        bool blocked = false;
        bool allowed = false;

        // host suffix buckets: a.b.example.com -> a.b.example.com, b.example.com, example.com, com
        int start = 0;
        while (start < host.Length)
        {
            var suffix = host[start..];
            if (suffix.Length > 0 && data.HostHead.TryGetValue(suffix, out int head))
            {
                ScanChain(data, head, target, host.Length, typeFlag, thirdParty, ref blocked, ref allowed);
                if (allowed) return Verdict.Allow;
            }
            int dot = host.IndexOf('.', start);
            if (dot < 0 || dot + 1 >= host.Length) break;
            start = dot + 1;
        }

        if (blocked) { /* keep scanning for exceptions */ }

        // token buckets over the url (3 and 4 grams)
        int i = 0;
        while (i < target.Length)
        {
            if (!char.IsLetterOrDigit(target[i])) { i++; continue; }
            int runStart = i;
            while (i < target.Length && char.IsLetterOrDigit(target[i])) i++;
            int runLen = i - runStart;
            if (runLen >= 3)
            {
                for (int j = runStart; j + 3 <= i; j++)
                {
                    LookupToken(data, target.AsSpan(j, 3), target, host.Length, typeFlag, thirdParty, ref blocked, ref allowed);
                    if (allowed) return Verdict.Allow;
                }
            }
            if (runLen >= 4)
            {
                for (int j = runStart; j + 4 <= i; j++)
                {
                    LookupToken(data, target.AsSpan(j, 4), target, host.Length, typeFlag, thirdParty, ref blocked, ref allowed);
                    if (allowed) return Verdict.Allow;
                }
            }
        }

        if (!blocked && data.TokenHead.TryGetValue("\u0001", out int fallback))
            ScanChain(data, fallback, target, host.Length, typeFlag, thirdParty, ref blocked, ref allowed);

        if (allowed) return Verdict.Allow;
        return blocked ? Verdict.Block : Verdict.None;
    }

    private static void LookupToken(EngineData data, ReadOnlySpan<char> token, string target, int hostLen, RuleFlags typeFlag, bool third, ref bool blocked, ref bool allowed)
    {
        // In .NET 9+, Dictionary has GetAlternateLookup to lookup by ReadOnlySpan<char> without allocating string.
#if NET9_0_OR_GREATER
        var alt = data.TokenHead.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!alt.TryGetValue(token, out int head)) return;
#else
        if (!data.TokenHead.TryGetValue(token.ToString(), out int head)) return;
#endif
        ScanChain(data, head, target, hostLen, typeFlag, third, ref blocked, ref allowed);
    }

    private static void ScanChain(EngineData data, int head, string target, int hostLen, RuleFlags typeFlag, bool third, ref bool blocked, ref bool allowed)
    {
        int guard = 0;
        var rules = data.Rules;
        var blob = data.Blob;
        while (head >= 0 && guard++ < 4096)
        {
            ref readonly var rule = ref rules[head];
            var f = rule.Flags;
            head = rule.Next;

            if ((f & TypeAll) != RuleFlags.None && (f & typeFlag) == RuleFlags.None) continue;
            if ((f & RuleFlags.ThirdParty) != RuleFlags.None && !third) continue;
            if ((f & RuleFlags.FirstParty) != RuleFlags.None && third) continue;

            var pat = blob.AsSpan(rule.Offset, rule.Length);
            bool match;
            if ((f & RuleFlags.AnchorDomain) != RuleFlags.None)
            {
                match = MatchAt(target, 0, pat, (f & RuleFlags.AnchorEnd) != RuleFlags.None);
                if (!match)
                {
                    for (int i = 0; i < hostLen; i++)
                    {
                        if (target[i] == '.' && MatchAt(target, i + 1, pat, (f & RuleFlags.AnchorEnd) != RuleFlags.None))
                        {
                            match = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                match = MatchGeneric(target, pat, (f & RuleFlags.AnchorEnd) != RuleFlags.None);
            }

            if (!match) continue;
            if ((f & RuleFlags.Exception) != RuleFlags.None) allowed = true;
            else
            {
                blocked = true;
                if ((f & RuleFlags.Important) != RuleFlags.None) return;
            }
        }
    }

    private static RuleFlags TypeFlag(ResType type) => type switch
    {
        ResType.Script => RuleFlags.Script,
        ResType.Image => RuleFlags.Image,
        ResType.Stylesheet => RuleFlags.Stylesheet,
        ResType.Subdocument => RuleFlags.Subdocument,
        ResType.Xhr => RuleFlags.Xhr,
        ResType.Media => RuleFlags.Media,
        ResType.Font => RuleFlags.Font,
        ResType.Document => RuleFlags.Document,
        ResType.Ping => RuleFlags.Ping,
        ResType.Websocket => RuleFlags.Websocket,
        _ => RuleFlags.Other
    };

    private static bool IsSeparator(char c) =>
        !(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '%');

    private static bool MatchAt(string target, int start, ReadOnlySpan<char> pat, bool anchorEnd)
    {
        int ti = start, pi = 0;
        int starTi = -1, starPi = -1;

        while (true)
        {
            if (pi >= pat.Length) return !anchorEnd || ti >= target.Length;

            char pc = pat[pi];
            if (pc == '*')
            {
                starPi = pi++;
                starTi = ti;
                continue;
            }
            if (ti >= target.Length)
            {
                if (pc == '^' || pc == '*') { pi++; continue; }
                return false;
            }
            if (pc == '^')
            {
                if (IsSeparator(target[ti])) { pi++; ti++; continue; }
            }
            else if (pc == target[ti])
            {
                pi++; ti++;
                continue;
            }

            if (starPi >= 0)
            {
                pi = starPi + 1;
                ti = ++starTi;
                if (ti > target.Length) return false;
                continue;
            }
            return false;
        }
    }

    private static bool MatchGeneric(string target, ReadOnlySpan<char> pat, bool anchorEnd)
    {
        int lit = 0;
        while (lit < pat.Length && pat[lit] != '*' && pat[lit] != '^') lit++;
        if (lit == 0)
        {
            for (int s = 0; s <= target.Length; s++)
                if (MatchAt(target, s, pat, anchorEnd)) return true;
            return false;
        }

        var prefix = pat[..lit];
        int idx = 0;
        while (idx <= target.Length - lit)
        {
            int found = target.AsSpan(idx).IndexOf(prefix);
            if (found < 0) return false;
            int abs = idx + found;
            if (MatchAt(target, abs, pat, anchorEnd)) return true;
            idx = abs + 1;
        }
        return false;
    }

    public static bool IsThirdParty(string host, string docHost)
    {
        if (string.IsNullOrEmpty(docHost)) return true;
        var a = Registrable(host);
        var b = Registrable(docHost);
        return !a.Equals(b, StringComparison.Ordinal);
    }

    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "co.jp", "or.jp", "ne.jp", "co.jp", "com.au", "net.au", "org.au",
        "co.nz", "co.in", "co.za", "com.br", "com.mx", "com.tr", "com.cn", "com.tw", "com.sg", "co.kr", "co.id"
    };

    private static string Registrable(string host)
    {
        var parts = host.Split('.');
        if (parts.Length <= 2) return host;
        var lastTwo = parts[^2] + "." + parts[^1];
        if (TwoLevelSuffixes.Contains(lastTwo) && parts.Length >= 3)
            return parts[^3] + "." + lastTwo;
        return lastTwo;
    }

    // ---------------- cosmetics ----------------

    public string CosmeticCss(string host, bool includeCookieFilters)
    {
        var cacheKey = host + (includeCookieFilters ? "|1" : "|0");
        lock (_cssCache)
        {
            if (_cssCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        var data = _data;
        string generic = includeCookieFilters ? data.CssWithCookies : data.CssNoCookies;
        if (generic.Length == 0) generic = BuildGenericCss(data, includeCookieFilters);

        var sb = new StringBuilder(generic);
        if (host.Length > 0)
        {
            AppendDomainCss(data, sb, host, includeCookieFilters);
            int dot = host.IndexOf('.');
            while (dot > 0)
            {
                AppendDomainCss(data, sb, host[(dot + 1)..], includeCookieFilters);
                dot = host.IndexOf('.', dot + 1);
            }
        }

        var result = sb.Length == generic.Length ? generic : sb.ToString();
        lock (_cssCache)
        {
            if (_cssCache.TryGetValue(cacheKey, out var existing)) return existing;
            if (_cssCache.Count >= 24 || _cssCacheChars + result.Length > CssCacheCharBudget)
            {
                _cssCache.Clear();
                _cssCacheChars = 0;
            }
            if (result.Length <= CssCacheCharBudget)
            {
                _cssCache[cacheKey] = result;
                _cssCacheChars += result.Length;
            }
        }
        return result;
    }

    private static void AppendDomainCss(EngineData data, StringBuilder sb, string host, bool includeCookie)
    {
        if (!data.DomainSelectors.TryGetValue(host, out var list) || list.Count == 0) return;
        bool first = true;
        foreach (var sel in list)
        {
            if (!includeCookie && IsCookieSelector(sel)) continue;
            if (!first) sb.Append(',');
            sb.Append(sel);
            first = false;
        }
        if (first) return;
        sb.Append("{display:none!important}\n");
    }

    private static bool IsCookieSelector(string sel) =>
        sel.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        sel.Contains("consent", StringComparison.OrdinalIgnoreCase) ||
        sel.Contains("onetrust", StringComparison.OrdinalIgnoreCase) ||
        sel.Contains("cmp-", StringComparison.OrdinalIgnoreCase) ||
        sel.Contains("gdpr", StringComparison.OrdinalIgnoreCase);

    private static string BuildGenericCss(EngineData data, bool includeCookie)
    {
        var sb = new StringBuilder(data.GenericSelectors.Count * 24);
        int countInChunk = 0;
        foreach (var sel in data.GenericSelectors)
        {
            if (!includeCookie && IsCookieSelector(sel)) continue;
            if (countInChunk > 0) sb.Append(',');
            sb.Append(sel);
            countInChunk++;
            if (countInChunk >= 250)
            {
                sb.Append("{display:none!important}\n");
                countInChunk = 0;
            }
        }
        if (countInChunk > 0)
        {
            sb.Append("{display:none!important}\n");
        }
        var css = sb.ToString();
        if (includeCookie) data.CssWithCookies = css; else data.CssNoCookies = css;
        return css;
    }

    public int GenericCosmeticCount => _data.GenericSelectors.Count;

    // ---------------- runtime update helper ----------------

    public static string Prune(IEnumerable<string> rawLists)
    {
        var sb = new StringBuilder(4 << 20);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawLists)
        {
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length < 3 || t[0] == '!' || t[0] == '[' || t[0] == '#') continue;
                if (t.Contains("##+js") || t.Contains("$$") || t.Contains("#?#") || t.Contains("#$#") || t.Contains("#%#")) continue;
                if (t.Length > 400) continue;
                if (!seen.Add(t)) continue;
                sb.Append(t).Append('\n');
            }
        }
        return sb.ToString();
    }
}
