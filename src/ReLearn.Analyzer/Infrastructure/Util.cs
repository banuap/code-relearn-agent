using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using ReLearn.Model;
using Location = ReLearn.Model.Location;

namespace ReLearn.Infrastructure;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
    }

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException($"Could not read {path}");
}

public static class Text
{
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);

    public static string OneLine(string? s, int max = 300)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = Ws.Replace(s, " ").Trim();
        return t.Length <= max ? t : t[..max] + " …";
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        var prevDash = false;
        // split PascalCase into words
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))) && !prevDash)
            {
                sb.Append('-');
            }
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); prevDash = false; }
            else if (!prevDash && sb.Length > 0) { sb.Append('-'); prevDash = true; }
        }
        var r = sb.ToString().Trim('-');
        while (r.Contains("--")) r = r.Replace("--", "-");
        return r.Length == 0 ? "item" : (r.Length > 50 ? r[..50].Trim('-') : r);
    }

    public static string Humanize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(c == '_' ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    public static string RedactConnectionString(string cs) =>
        Regex.Replace(cs, @"(?i)(password|pwd|user id|uid|accountkey|sharedaccesskey|secret|token)\s*=\s*[^;]*", m => m.Groups[1].Value + "=***");

    public static string MdEscape(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

public static class Paths
{
    public static string Relative(string root, string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try
        {
            var rel = System.IO.Path.GetRelativePath(root, path);
            return rel.Replace('\\', '/');
        }
        catch { return path.Replace('\\', '/'); }
    }

    public static bool IsExcluded(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/") || p.Contains("/obj/") || p.Contains("/node_modules/") || p.Contains("/.git/")
               || p.Contains("/packages/") || p.Contains("/.vs/") || p.Contains("/TestResults/");
    }
}

public static class Sym
{
    public static readonly SymbolDisplayFormat Signature = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static readonly SymbolDisplayFormat Short = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static readonly SymbolDisplayFormat Full = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static ISymbol Normalize(ISymbol s)
    {
        if (s is IMethodSymbol m)
        {
            if (m.ReducedFrom != null) m = m.ReducedFrom;
            return m.OriginalDefinition;
        }
        return s.OriginalDefinition;
    }

    public static string Id(ISymbol s)
    {
        s = Normalize(s);
        var id = s.GetDocumentationCommentId();
        return string.IsNullOrEmpty(id) ? s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : id;
    }

    /// Fully-qualified type name without generic args, e.g. "Confluent.Kafka.IProducer`2" -> "Confluent.Kafka.IProducer".
    public static string TypeName(ITypeSymbol? t)
    {
        if (t == null) return "";
        var orig = t.OriginalDefinition;
        var ns = orig.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        var outer = "";
        for (var c = orig.ContainingType; c != null; c = c.ContainingType) outer = c.Name + "." + outer;
        return ns + outer + orig.Name;
    }

    public static string Namespace(ISymbol s) =>
        s.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : "";

    public static Location? Loc(ISymbol s, string root)
    {
        var l = s.Locations.FirstOrDefault(x => x.IsInSource);
        return l == null ? null : Loc(l, root);
    }

    public static Location? Loc(Microsoft.CodeAnalysis.Location l, string root)
    {
        if (!l.IsInSource) return null;
        var span = l.GetLineSpan();
        return new Location { File = Paths.Relative(root, span.Path), Line = span.StartLinePosition.Line + 1, EndLine = span.EndLinePosition.Line + 1 };
    }

    public static Location? Loc(SyntaxNode? n, string root) => n == null ? null : Loc(n.GetLocation(), root);

    public static Location? DeclLoc(ISymbol s, string root)
    {
        var r = s.DeclaringSyntaxReferences.FirstOrDefault();
        if (r == null) return Loc(s, root);
        var node = r.GetSyntax();
        return Loc(node, root);
    }

    public static IEnumerable<INamedTypeSymbol> BaseTypes(ITypeSymbol t)
    {
        for (var b = t.BaseType; b != null; b = b.BaseType) yield return b;
    }

    public static bool InheritsFrom(ITypeSymbol t, params string[] fullNames)
    {
        foreach (var b in BaseTypes(t))
        {
            var n = TypeName(b);
            if (fullNames.Any(f => f == n)) return true;
        }
        return false;
    }

    public static bool Implements(ITypeSymbol t, params string[] fullNames) =>
        t.AllInterfaces.Any(i => fullNames.Contains(TypeName(i)));

    public static string AttrName(AttributeData a)
    {
        var n = a.AttributeClass?.Name ?? "";
        return n.EndsWith("Attribute") ? n[..^9] : n;
    }

    public static string AttrText(AttributeData a)
    {
        var args = new List<string>();
        foreach (var c in a.ConstructorArguments) args.Add(ConstText(c));
        foreach (var kv in a.NamedArguments) args.Add($"{kv.Key}={ConstText(kv.Value)}");
        return args.Count == 0 ? AttrName(a) : $"{AttrName(a)}({string.Join(", ", args)})";
    }

    public static string ConstText(TypedConstant c)
    {
        if (c.IsNull) return "null";
        if (c.Kind == TypedConstantKind.Array) return "[" + string.Join(", ", c.Values.Select(ConstText)) + "]";
        if (c.Kind == TypedConstantKind.Type) return (c.Value as ITypeSymbol)?.Name ?? "";
        if (c.Value is string s) return "\"" + s + "\"";
        if (c.Kind == TypedConstantKind.Enum && c.Type is INamedTypeSymbol et)
        {
            var member = et.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, c.Value));
            return member != null ? $"{et.Name}.{member.Name}" : c.Value?.ToString() ?? "";
        }
        return Convert.ToString(c.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    public static string? AttrString(AttributeData a, int ctorIndex = 0, string? named = null)
    {
        if (named != null)
        {
            foreach (var kv in a.NamedArguments)
                if (kv.Key == named && kv.Value.Value is string ns) return ns;
        }
        if (a.ConstructorArguments.Length > ctorIndex && a.ConstructorArguments[ctorIndex].Value is string s) return s;
        return null;
    }
}
