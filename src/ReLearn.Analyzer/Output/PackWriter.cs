using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReLearn.Analysis;
using ReLearn.Infrastructure;
using ReLearn.Model;
using ReLearn.Output;

namespace ReLearn.Analysis;

/// Writes the knowledge pack: one JSON file per artifact type, per-capability bundles (sized for an LLM
/// context window), AST files, graphs (Mermaid + DOT) and a human/LLM readable summary.md.
public static class PackWriter
{
    public static void Write(AnalysisContext ctx, string outDir)
    {
        var pack = ctx.Pack;
        Directory.CreateDirectory(outDir);
        string P(string rel) => Path.Combine(outDir, rel);
        void J<T>(string rel, T value) { Json.Write(P(rel), value); pack.Manifest.Files[Path.GetFileNameWithoutExtension(rel)] = rel.Replace('\\', '/'); }

        J("dependencies.json", pack.Dependencies);
        J("callgraph.json", pack.CallGraph);
        J("entrypoints.json", pack.EntryPoints);
        J("data.json", pack.Data);
        J("integrations.json", pack.Integrations);
        J("rules.json", pack.Rules);
        J("flows.json", pack.Flows);
        J("journeys.json", pack.Journeys);
        J("capabilities.json", pack.Capabilities);

        // ---- capability bundles
        var rules = pack.Rules.ToDictionary(r => r.Id);
        var flows = pack.Flows.ToDictionary(f => f.Id);
        foreach (var c in pack.Capabilities)
        {
            var cflows = c.Flows.Select(id => flows[id]).ToList();
            var methodIds = cflows.SelectMany(f => f.Steps.Select(s => s.MethodId)).ToHashSet();
            var tables = cflows.SelectMany(f => f.EntityOperations.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bundle = new
            {
                capability = c,
                entryPoints = pack.EntryPoints.Where(e => c.EntryPoints.Contains(e.Id)),
                flows = cflows,
                rules = c.Rules.Where(rules.ContainsKey).Select(id => rules[id]),
                entities = pack.Data.Entities.Where(e => c.Entities.Contains(e.Name)),
                dbObjects = pack.Data.DbObjects.Where(o => tables.Contains(o.Name) || o.ReferencedBy.Any(methodIds.Contains)),
                dataOperations = pack.Data.Operations.Where(d => methodIds.Contains(d.MethodId)),
                integrations = pack.Integrations.Points.Where(i => methodIds.Contains(i.MethodId)),
                journeys = pack.Journeys.Journeys.Where(j => c.Journeys.Contains(j.Id)),
                screens = pack.Journeys.Screens.Where(s => pack.EntryPoints.Any(e => c.EntryPoints.Contains(e.Id) && e.Screen == s.Id)),
                methods = pack.CallGraph.Nodes.Where(n => methodIds.Contains(n.Id)),
            };
            Json.Write(P($"capabilities/{c.Slug}.json"), bundle);
        }

        // ---- graphs
        Directory.CreateDirectory(P("graphs"));
        File.WriteAllText(P("graphs/projects.mmd"), Mermaid.Projects(pack.Dependencies));
        File.WriteAllText(P("graphs/layers.mmd"), Mermaid.Layers(pack.Dependencies));
        File.WriteAllText(P("graphs/entities.mmd"), Mermaid.Er(pack.Data.Entities));
        File.WriteAllText(P("graphs/callgraph.dot"), CallGraphDot(pack));
        foreach (var f in pack.Flows.Take(500)) { Directory.CreateDirectory(P("graphs/flows")); File.WriteAllText(P($"graphs/flows/{f.Id}.mmd"), Mermaid.Sequence(f)); }

        // ---- AST
        if (ctx.Options.AstMode != "none") AstWriter.Write(ctx, P("ast"), ctx.Options.AstMode == "full");

        // ---- summary + manifest
        File.WriteAllText(P("summary.md"), Summary(ctx), new UTF8Encoding(false));
        pack.Manifest.Files["summary"] = "summary.md";
        pack.Manifest.Files["capabilityBundles"] = "capabilities/<slug>.json";
        pack.Manifest.Files["ast"] = ctx.Options.AstMode == "none" ? "(disabled)" : "ast/<project>/<file>.json";
        pack.Manifest.Files["graphs"] = "graphs/*.mmd, graphs/callgraph.dot, graphs/flows/<flow>.mmd";
        Json.Write(P("manifest.json"), pack.Manifest);
    }

    private static string CallGraphDot(KnowledgePack pack)
    {
        var sb = new StringBuilder("digraph callgraph {\n  rankdir=LR; node [shape=box, fontsize=10];\n");
        var nodes = pack.CallGraph.Nodes.Where(n => n.Kind is "method" or "ctor" or "top-level").ToDictionary(n => n.Id);
        string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";
        foreach (var n in nodes.Values) sb.AppendLine($"  {Q(n.Id)} [label={Q(n.Signature.Length > 80 ? n.Name : n.Signature)}, tooltip={Q(n.Stereotype)}];");
        foreach (var e in pack.CallGraph.Edges.Where(e => nodes.ContainsKey(e.Caller)))
            foreach (var t in (e.ResolvedTargets is { Count: > 0 } r ? r : new List<string> { e.Callee }).Where(nodes.ContainsKey))
                sb.AppendLine($"  {Q(e.Caller)} -> {Q(t)} [label={Q(e.Kind)}];");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Summary(AnalysisContext ctx)
    {
        var p = ctx.Pack;
        var sb = new StringBuilder();
        sb.AppendLine($"# ReLearn knowledge pack — {Path.GetFileName(p.Manifest.Input)}");
        sb.AppendLine();
        sb.AppendLine($"Generated {p.Manifest.GeneratedAtUtc} by relearn {p.Manifest.ToolVersion} · load mode **{p.Manifest.LoadMode}** · semantic fidelity **{p.Manifest.SemanticFidelity}** · languages {string.Join(", ", p.Manifest.Languages)}");
        sb.AppendLine();
        sb.AppendLine("> Start here. Read this file, then `capabilities.json`, then one `capabilities/<slug>.json` bundle at a time.");
        sb.AppendLine("> Every item has a stable id (EP-, FLOW-, BR-, INT-, UJ-, CAP-) and a `location` pointing into the legacy source.");
        sb.AppendLine();
        sb.AppendLine("## Counts");
        sb.AppendLine("| item | count |\n|---|---|");
        foreach (var (k, v) in p.Manifest.Counts) sb.AppendLine($"| {k} | {v} |");
        sb.AppendLine();
        sb.AppendLine("## Projects");
        sb.AppendLine("| project | kind | frameworks | LOC | references |\n|---|---|---|---|---|");
        foreach (var pr in p.Dependencies.Projects)
            sb.AppendLine($"| {pr.Name} | {pr.Kind} | {string.Join(", ", pr.TargetFrameworks)} | {pr.LinesOfCode} | {string.Join(", ", pr.ProjectReferences)} |");
        sb.AppendLine();
        sb.AppendLine("## Capabilities (modernization units)");
        sb.AppendLine("| id | capability | priority | flows | rules | entities | integrations |\n|---|---|---|---|---|---|---|");
        foreach (var c in p.Capabilities)
            sb.AppendLine($"| {c.Id} | [{Text.MdEscape(c.Name)}](capabilities/{c.Slug}.json) | {c.Priority} | {c.Flows.Count} | {c.Rules.Count} | {Text.MdEscape(string.Join(", ", c.Entities.Take(6)))} | {c.Integrations.Count} |");
        sb.AppendLine();
        sb.AppendLine("## Entry points by kind");
        foreach (var g in p.EntryPoints.GroupBy(e => e.Kind).OrderByDescending(g => g.Count())) sb.AppendLine($"- **{g.Key}**: {g.Count()}");
        sb.AppendLine();
        sb.AppendLine("## Data");
        sb.AppendLine($"- Data technologies: {string.Join(", ", p.Data.Operations.GroupBy(o => o.Technology).Select(g => $"{g.Key} ({g.Count()})"))}");
        sb.AppendLine($"- DbContexts: {string.Join(", ", p.Data.Contexts.Select(c => $"{c.Name} [{c.Technology}]"))}");
        sb.AppendLine($"- DB objects: {string.Join(", ", p.Data.DbObjects.GroupBy(o => o.Kind).Select(g => $"{g.Count()} {g.Key}(s)"))}");
        sb.AppendLine($"- Connections: {string.Join(", ", p.Data.Connections.Select(c => $"{c.Name} ({c.Provider})").Distinct())}");
        sb.AppendLine($"- Dynamic SQL sites: {p.Data.Operations.Count(o => o.DynamicSql)}");
        sb.AppendLine();
        sb.AppendLine("## Integrations");
        foreach (var g in p.Integrations.Points.GroupBy(i => (i.Category, i.Technology)).OrderBy(g => g.Key.Category))
            sb.AppendLine($"- **{g.Key.Category} / {g.Key.Technology}**: {g.Count()} site(s); targets: {Text.MdEscape(string.Join(", ", g.Select(i => i.Target).Where(t => t != null).Distinct().Take(8)))}");
        if (p.Integrations.ServiceEndpoints.Count > 0)
        {
            sb.AppendLine("- Service endpoints from config/contracts:");
            foreach (var e in p.Integrations.ServiceEndpoints.Distinct().Take(30)) sb.AppendLine($"  - {Text.MdEscape(e)}");
        }
        sb.AppendLine();
        sb.AppendLine("## Business rules by kind");
        foreach (var g in p.Rules.GroupBy(r => r.Kind).OrderByDescending(g => g.Count())) sb.AppendLine($"- **{g.Key}**: {g.Count()} ({g.Count(r => r.Confidence == "high")} high confidence)");
        sb.AppendLine();
        sb.AppendLine("## Modernization risk signals");
        var platform = p.Integrations.Points.Where(i => i.Category == "platform").GroupBy(i => i.Technology).Select(g => $"{g.Key} ({g.Count()})").ToList();
        if (platform.Count > 0) sb.AppendLine($"- Windows/platform-specific APIs: {string.Join(", ", platform)}");
        var procs = p.Data.DbObjects.Count(o => o.Kind is "procedure" or "function" or "trigger");
        if (procs > 0) sb.AppendLine($"- {procs} stored procedures/functions/triggers — business logic may live in the database.");
        var dyn = p.Data.Operations.Count(o => o.DynamicSql);
        if (dyn > 0) sb.AppendLine($"- {dyn} dynamic SQL construction sites — review for injection and for rule logic embedded in SQL.");
        var complex = p.CallGraph.Nodes.Where(n => n.CyclomaticComplexity >= 15).OrderByDescending(n => n.CyclomaticComplexity).Take(10).ToList();
        if (complex.Count > 0) sb.AppendLine($"- Most complex methods: {string.Join(", ", complex.Select(n => $"`{n.Name}` ({n.CyclomaticComplexity})"))}");
        var legacy = p.Dependencies.Projects.Where(x => x.TargetFrameworks.Any(t => t.StartsWith("v") || t.StartsWith("net4"))).Select(x => x.Name).ToList();
        if (legacy.Count > 0) sb.AppendLine($"- .NET Framework projects: {string.Join(", ", legacy)}");
        if (p.Manifest.SemanticFidelity != "full") sb.AppendLine("- Ad-hoc load: external types unresolved — call graph through third-party APIs is approximate.");
        sb.AppendLine();
        if (p.Manifest.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            foreach (var w in p.Manifest.Warnings.Take(40)) sb.AppendLine($"- {Text.MdEscape(w)}");
        }
        return sb.ToString();
    }
}

public static class AstWriter
{
    public static void Write(AnalysisContext ctx, string dir, bool full)
    {
        var byFile = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ctx.TypeSymbols.Values)
            foreach (var l in t.Locations.Where(l => l.IsInSource && l.SourceTree?.FilePath != null))
            {
                var path = l.SourceTree!.FilePath;
                if (!byFile.TryGetValue(path, out var list)) byFile[path] = list = new();
                if (!list.Contains(t, SymbolEqualityComparer.Default)) list.Add(t);
            }

        foreach (var (pid, comp) in ctx.Compilations)
        {
            var proj = ctx.Projects[pid];
            foreach (var tree in comp.SyntaxTrees)
            {
                var path = tree.FilePath;
                if (string.IsNullOrEmpty(path) || Paths.IsExcluded(path) || path.EndsWith(".g.cs") || path.Contains("AssemblyAttributes") || path.EndsWith("AssemblyInfo.cs")) continue;
                var rel = Paths.Relative(ctx.Root, path);
                var types = byFile.TryGetValue(path, out var l) ? l : new List<INamedTypeSymbol>();
                var root = tree.GetRoot();
                var usings = root is CompilationUnitSyntax cu ? cu.Usings.Select(u => u.Name?.ToString() ?? "").ToList() : new List<string>();
                object doc = new
                {
                    file = rel,
                    project = proj.Name,
                    language = comp.Language,
                    lines = tree.GetText().Lines.Count,
                    usings,
                    types = types.Select(t => TypeOutline(ctx, t, tree)).ToList(),
                    syntax = full ? Dump(root, comp.Language, 0) : null,
                };
                var outPath = Path.Combine(dir, Text.Slug(proj.Name), Regex.Replace(rel, @"^(\.\./)+", "") + ".json");
                Json.Write(outPath, doc);
            }
        }
    }

    private static object TypeOutline(AnalysisContext ctx, INamedTypeSymbol t, SyntaxTree tree)
    {
        var node = ctx.Types[Sym.Id(t)];
        var span = t.Locations.First(l => l.SourceTree == tree).GetLineSpan();
        var members = t.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.Locations.Any(l => l.SourceTree == tree)).Select(m =>
        {
            var ls = m.Locations.First(l => l.SourceTree == tree).GetLineSpan();
            var declSpan = m.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == tree)?.GetSyntax().GetLocation().GetLineSpan();
            ctx.Methods.TryGetValue(Sym.Id(m), out var mn);
            return new
            {
                kind = m switch { IMethodSymbol ms => ms.MethodKind.ToString(), IPropertySymbol => "Property", IFieldSymbol f => f.IsConst ? "Const" : "Field", IEventSymbol => "Event", INamedTypeSymbol => "NestedType", _ => m.Kind.ToString() },
                name = m.Name,
                signature = m.ToDisplayString(Sym.Signature),
                accessibility = m.DeclaredAccessibility.ToString().ToLowerInvariant(),
                modifiers = Modifiers(m),
                line = ls.StartLinePosition.Line + 1,
                endLine = (declSpan ?? ls).EndLinePosition.Line + 1,
                attributes = m.GetAttributes().Select(Sym.AttrText).ToList(),
                complexity = mn?.CyclomaticComplexity,
                constant = m is IFieldSymbol { HasConstantValue: true } cf ? Convert.ToString(cf.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) : null,
                doc = Summary(m.GetDocumentationCommentXml()),
                id = Sym.Id(m),
            };
        }).ToList();
        return new
        {
            id = node.Id,
            name = t.Name,
            fullName = node.FullName,
            kind = node.Kind,
            stereotype = node.Stereotype,
            line = span.StartLinePosition.Line + 1,
            baseType = node.BaseType,
            interfaces = node.Interfaces,
            attributes = node.Attributes,
            enumMembers = node.EnumMembers.Count > 0 ? node.EnumMembers : null,
            doc = Summary(t.GetDocumentationCommentXml()),
            members,
        };
    }

    private static List<string> Modifiers(ISymbol s)
    {
        var l = new List<string>();
        if (s.IsStatic) l.Add("static");
        if (s.IsAbstract) l.Add("abstract");
        if (s.IsVirtual) l.Add("virtual");
        if (s.IsOverride) l.Add("override");
        if (s.IsSealed) l.Add("sealed");
        if (s is IMethodSymbol { IsAsync: true }) l.Add("async");
        if (s is IFieldSymbol { IsReadOnly: true }) l.Add("readonly");
        return l;
    }

    private static string? Summary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var m = Regex.Match(xml, "<summary>(.*?)</summary>", RegexOptions.Singleline);
        return m.Success ? Text.OneLine(Regex.Replace(m.Groups[1].Value, "<[^>]+>", ""), 400) : null;
    }

    private static object Dump(SyntaxNode node, string language, int depth)
    {
        var ls = node.GetLocation().GetLineSpan();
        var kind = language == LanguageNames.CSharp
            ? ((Microsoft.CodeAnalysis.CSharp.SyntaxKind)node.RawKind).ToString()
            : ((Microsoft.CodeAnalysis.VisualBasic.SyntaxKind)node.RawKind).ToString();
        var children = new List<object>();
        if (depth < 200)
        {
            foreach (var c in node.ChildNodesAndTokens())
            {
                if (c.IsNode) children.Add(Dump(c.AsNode()!, language, depth + 1));
                else
                {
                    var tok = c.AsToken();
                    var tk = language == LanguageNames.CSharp
                        ? ((Microsoft.CodeAnalysis.CSharp.SyntaxKind)tok.RawKind).ToString()
                        : ((Microsoft.CodeAnalysis.VisualBasic.SyntaxKind)tok.RawKind).ToString();
                    if (tk.EndsWith("IdentifierToken") || tk.EndsWith("LiteralToken") || tk.Contains("Interpolated"))
                        children.Add(new { t = tk, v = tok.ValueText });
                }
            }
        }
        return new { k = kind, l = ls.StartLinePosition.Line + 1, e = ls.EndLinePosition.Line + 1, c = children.Count > 0 ? children : null };
    }
}
