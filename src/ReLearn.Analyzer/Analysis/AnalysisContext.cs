using Microsoft.CodeAnalysis;
using ReLearn.Infrastructure;
using ReLearn.Model;

namespace ReLearn.Analysis;

public sealed class AnalyzerOptions
{
    public string Input { get; set; } = ".";
    public string Output { get; set; } = "relearn-out";
    public string AstMode { get; set; } = "outline"; // none | outline | full
    public bool IncludeTests { get; set; }
    public bool IncludeTrivialRules { get; set; }
    public bool ForceAdhoc { get; set; }
    public int MaxFlowDepth { get; set; } = 12;
    public int MaxFlowSteps { get; set; } = 250;
    public int MaxJourneys { get; set; } = 200;
    public bool Verbose { get; set; }
}

/// Shared mutable state for one analysis run. All collections are keyed by stable symbol ids.
public sealed class AnalysisContext
{
    public AnalysisContext(AnalyzerOptions options, string root)
    {
        Options = options;
        Root = root;
    }

    public AnalyzerOptions Options { get; }
    public string Root { get; }
    public KnowledgePack Pack { get; } = new();

    // project bookkeeping
    public Dictionary<ProjectId, Model.ProjectInfo> Projects { get; } = new();
    public Dictionary<ProjectId, Compilation> Compilations { get; } = new();
    public HashSet<ProjectId> TestProjects { get; } = new();

    // symbol indices
    public Dictionary<string, TypeNode> Types { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, INamedTypeSymbol> TypeSymbols { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, MethodNode> Methods { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (IMethodSymbol symbol, ProjectId project)> MethodSymbols { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Implementations { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EntityByType { get; } = new(StringComparer.Ordinal); // type id -> entity name
    public Dictionary<string, string> MediatorHandlers { get; } = new(StringComparer.Ordinal); // message type full name -> Handle method id
    public HashSet<string> EntryMethodIds { get; } = new(StringComparer.Ordinal);

    // edge accumulation (deduped)
    private readonly Dictionary<(string, string, string), Edge> _typeEdges = new();
    private readonly Dictionary<(string, string), ExternalCall> _external = new();
    private readonly HashSet<(string, string, int)> _callSeen = new();
    private readonly Dictionary<(string, string), Edge> _nav = new();

    private int _ruleSeq, _intSeq;

    public void Log(string msg)
    {
        if (Options.Verbose) Console.Error.WriteLine("  " + msg);
    }

    public string NextRuleId() => $"BR-{++_ruleSeq:0000}";
    public string NextIntegrationId() => $"INT-{++_intSeq:0000}";

    public void AddTypeEdge(string from, string to, string kind)
    {
        if (from == to || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
        if (_typeEdges.TryGetValue((from, to, kind), out var e)) e.Weight++;
        else _typeEdges[(from, to, kind)] = new Edge { From = from, To = to, Kind = kind };
    }

    public void AddCall(CallEdge edge)
    {
        if (_callSeen.Add((edge.Caller, edge.Callee, edge.Line)))
            Pack.CallGraph.Edges.Add(edge);
    }

    public void AddExternal(string caller, string api, string ns)
    {
        if (_external.TryGetValue((caller, api), out var e)) e.Count++;
        else _external[(caller, api)] = new ExternalCall { Caller = caller, Api = api, Namespace = ns, Count = 1 };
    }

    public void AddNavigation(string from, string to, string kind)
    {
        if (from == to) return;
        if (_nav.TryGetValue((from, to), out var e)) e.Weight++;
        else _nav[(from, to)] = new Edge { From = from, To = to, Kind = kind };
    }

    public void AddRule(BusinessRule r)
    {
        if (r.Trivial && !Options.IncludeTrivialRules) return;
        r.Id = NextRuleId();
        Pack.Rules.Add(r);
    }

    public void AddImplementation(string abstractId, string implId)
    {
        if (abstractId == implId) return;
        if (!Implementations.TryGetValue(abstractId, out var set)) Implementations[abstractId] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(implId);
    }

    public IEnumerable<Edge> TypeEdges => _typeEdges.Values;
    public IEnumerable<ExternalCall> ExternalCalls => _external.Values;
    public IEnumerable<Edge> NavigationEdges => _nav.Values;

    public string StereotypeOfType(ITypeSymbol? t)
    {
        if (t == null) return "other";
        return Types.TryGetValue(Sym.Id(t), out var n) ? n.Stereotype : "external";
    }

    public bool IsSourceType(ITypeSymbol? t) => t != null && Types.ContainsKey(Sym.Id(t));

    public string? EntityName(ITypeSymbol? t)
    {
        if (t == null) return null;
        if (EntityByType.TryGetValue(Sym.Id(t), out var n)) return n;
        // DbSet<T>, IQueryable<T>, IEnumerable<T>, List<T> ... of an entity
        if (t is INamedTypeSymbol nt && nt.TypeArguments.Length == 1)
            return EntityByType.TryGetValue(Sym.Id(nt.TypeArguments[0]), out var inner) ? inner : null;
        return null;
    }
}
