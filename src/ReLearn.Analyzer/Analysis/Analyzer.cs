using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReLearn.Infrastructure;
using ReLearn.Loading;
using ReLearn.Model;

namespace ReLearn.Analysis;

/// Orchestrates one extraction run: load → compile → catalog → walk → scan artifacts → synthesize → write.
public sealed class Analyzer
{
    private readonly AnalyzerOptions _o;
    public Analyzer(AnalyzerOptions o) => _o = o;

    public async Task<KnowledgePack> RunAsync()
    {
        var sw = Stopwatch.StartNew();
        void Step(string s) => Console.Error.WriteLine($"[{sw.Elapsed:mm\\:ss}] {s}");

        Step("Loading solution …");
        var loaded = await SolutionLoader.LoadAsync(_o.Input, _o.ForceAdhoc, s => Console.Error.WriteLine("  " + s));
        var ctx = new AnalysisContext(_o, loaded.RootDirectory);
        var pack = ctx.Pack;
        pack.Manifest.Input = Path.GetFullPath(_o.Input);
        pack.Manifest.RootDirectory = loaded.RootDirectory;
        pack.Manifest.LoadMode = loaded.Mode;
        pack.Manifest.SemanticFidelity = loaded.Mode == "msbuild" ? "full" : "partial";
        pack.Manifest.Warnings.AddRange(loaded.Warnings);

        Step("Compiling projects …");
        var seenProjectFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in loaded.Solution.Projects)
        {
            var key = project.FilePath ?? project.Name;
            if (!seenProjectFiles.Add(key)) continue; // multi-targeted projects load once per TFM
            var comp = await project.GetCompilationAsync();
            if (comp == null) continue;
            var name = project.Name.Contains('(') ? project.Name[..project.Name.IndexOf('(')] : project.Name;
            var pi = new Model.ProjectInfo
            {
                Name = name,
                Path = Paths.Relative(loaded.RootDirectory, project.FilePath),
                Language = project.Language,
                OutputKind = project.CompilationOptions?.OutputKind.ToString() ?? "",
                Documents = project.DocumentIds.Count,
                AssemblyReferences = project.MetadataReferences.Select(r => Path.GetFileNameWithoutExtension(r.Display ?? ""))
                    .Where(n => n.Length > 0 && !n.StartsWith("System") && !n.StartsWith("Microsoft.") && n != "mscorlib" && n != "netstandard").Distinct().OrderBy(n => n).ToList(),
            };
            ProjectFileReader.Enrich(pi, project.FilePath);
            if (pi.Kind == "library" && pi.OutputKind == "ConsoleApplication") pi.Kind = "console";
            foreach (var pr in project.ProjectReferences)
            {
                var target = loaded.Solution.GetProject(pr.ProjectId);
                if (target != null)
                {
                    var tn = target.Name.Contains('(') ? target.Name[..target.Name.IndexOf('(')] : target.Name;
                    if (!pi.ProjectReferences.Contains(tn)) pi.ProjectReferences.Add(tn);
                }
            }
            foreach (var tree in comp.SyntaxTrees) pi.LinesOfCode += tree.GetText().Lines.Count;
            ctx.Projects[project.Id] = pi;
            if (pi.IsTest)
            {
                ctx.TestProjects.Add(project.Id);
                if (!_o.IncludeTests) { pack.Dependencies.Projects.Add(pi); continue; }
            }
            ctx.Compilations[project.Id] = comp;
            pack.Dependencies.Projects.Add(pi);
            var errors = comp.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
            if (errors > 0) pack.Manifest.Warnings.Add($"{name}: {errors} compile errors (unresolved references reduce semantic precision).");
            if (!pack.Manifest.Languages.Contains(project.Language)) pack.Manifest.Languages.Add(project.Language);
        }

        Step($"Cataloguing types in {ctx.Compilations.Count} project(s) …");
        new TypeCatalog(ctx).Run();

        Step($"Walking {ctx.MethodSymbols.Count} method(s) …");
        WalkMethods(ctx);

        Step("Scanning SQL, config, views, XAML, markup …");
        new ArtifactScanner(ctx).Run();

        Step("Post-processing dependency map …");
        PostProcess(ctx);

        Step("Building business flows …");
        new FlowBuilder(ctx).Run();
        Step("Building user journeys …");
        new JourneyBuilder(ctx).Run();
        Step("Grouping capabilities …");
        new CapabilityBuilder(ctx).Run();

        pack.Manifest.Counts = new Dictionary<string, int>
        {
            ["projects"] = pack.Dependencies.Projects.Count,
            ["types"] = pack.Dependencies.Types.Count,
            ["methods"] = pack.CallGraph.Nodes.Count,
            ["callEdges"] = pack.CallGraph.Edges.Count,
            ["entryPoints"] = pack.EntryPoints.Count,
            ["entities"] = pack.Data.Entities.Count,
            ["dbObjects"] = pack.Data.DbObjects.Count,
            ["dataOperations"] = pack.Data.Operations.Count,
            ["integrationPoints"] = pack.Integrations.Points.Count,
            ["businessRules"] = pack.Rules.Count,
            ["flows"] = pack.Flows.Count,
            ["screens"] = pack.Journeys.Screens.Count,
            ["journeys"] = pack.Journeys.Journeys.Count,
            ["capabilities"] = pack.Capabilities.Count,
        };

        Step($"Writing knowledge pack to {_o.Output} …");
        PackWriter.Write(ctx, _o.Output);
        Step("Done.");
        return pack;
    }

    private static void WalkMethods(AnalysisContext ctx)
    {
        var models = new Dictionary<SyntaxTree, SemanticModel>();
        SemanticModel GetModel(Compilation c, SyntaxTree t)
        {
            if (!models.TryGetValue(t, out var m)) models[t] = m = c.GetSemanticModel(t);
            return m;
        }

        foreach (var (id, (symbol, pid)) in ctx.MethodSymbols.ToList())
        {
            if (!ctx.Compilations.TryGetValue(pid, out var comp)) continue;
            var node = ctx.Methods[id];
            var walker = new MethodWalker(ctx, node, symbol);
            foreach (var sr in symbol.DeclaringSyntaxReferences)
            {
                var syntax = sr.GetSyntax();
                if (!comp.ContainsSyntaxTree(syntax.SyntaxTree)) continue;
                var model = GetModel(comp, syntax.SyntaxTree);
                try
                {
                    var op = model.GetOperation(syntax) ?? (syntax.Parent != null ? model.GetOperation(syntax.Parent) : null);
                    if (op != null) { walker.Walk(op); continue; }
                    if (syntax is CompilationUnitSyntax cu)
                    {
                        // top-level statements
                        foreach (var gs in cu.Members.OfType<GlobalStatementSyntax>())
                            if (model.GetOperation(gs.Statement) is { } gop) walker.Walk(gop);
                    }
                }
                catch (Exception ex)
                {
                    ctx.Log($"walk failed for {id}: {ex.Message}");
                }
            }
            walker.Complete();
        }

        // console / desktop / worker entry points (Main)
        foreach (var (pid, comp) in ctx.Compilations)
        {
            var proj = ctx.Projects[pid];
            if (proj.Kind is not ("console" or "desktop" or "desktop-winforms" or "desktop-wpf" or "worker")) continue;
            IMethodSymbol? main = null;
            try { main = comp.GetEntryPoint(CancellationToken.None); } catch { }
            if (main == null) continue;
            var mid = Sym.Id(main);
            if (!ctx.Methods.ContainsKey(mid) || ctx.EntryMethodIds.Contains(mid)) continue;
            ctx.Pack.EntryPoints.Add(new EntryPoint
            {
                Id = $"EP-{ctx.Pack.EntryPoints.Count + 1:0000}", Kind = "console-main", Name = $"{proj.Name} Main", MethodId = mid,
                ContainingType = ctx.Methods[mid].ContainingType, Trigger = "process start", Location = ctx.Methods[mid].Location,
            });
            ctx.EntryMethodIds.Add(mid);
        }
    }

    private static void PostProcess(AnalysisContext ctx)
    {
        var pack = ctx.Pack;
        pack.Dependencies.Types = ctx.Types.Values.OrderBy(t => t.FullName).ToList();
        pack.Dependencies.TypeEdges = ctx.TypeEdges.ToList();
        pack.CallGraph.ExternalCalls = ctx.ExternalCalls.OrderBy(e => e.Caller).ToList();
        pack.CallGraph.Implementations = ctx.Implementations.ToDictionary(k => k.Key, v => v.Value.OrderBy(x => x).ToList());
        foreach (var n in pack.CallGraph.Nodes) n.Stereotype = ctx.Types.TryGetValue(n.ContainingType, out var t) ? t.Stereotype : n.Stereotype;

        // project and package edges
        foreach (var p in pack.Dependencies.Projects)
        {
            foreach (var r in p.ProjectReferences) pack.Dependencies.ProjectEdges.Add(new Edge { From = p.Name, To = r, Kind = "project-reference" });
            foreach (var pkg in p.Packages) pack.Dependencies.PackageEdges.Add(new Edge { From = p.Name, To = $"{pkg.Name}@{pkg.Version}", Kind = "package-reference" });
        }

        // namespace & layer aggregation
        var nsEdges = new Dictionary<(string, string), Edge>();
        var layerEdges = new Dictionary<(string, string), Edge>();
        foreach (var e in pack.Dependencies.TypeEdges)
        {
            if (!ctx.Types.TryGetValue(e.From, out var a) || !ctx.Types.TryGetValue(e.To, out var b)) continue;
            if (a.Namespace != b.Namespace)
            {
                if (nsEdges.TryGetValue((a.Namespace, b.Namespace), out var ne)) ne.Weight += e.Weight;
                else nsEdges[(a.Namespace, b.Namespace)] = new Edge { From = a.Namespace, To = b.Namespace, Kind = "namespace", Weight = e.Weight };
            }
            if (e.Kind is "calls" or "creates" or "injects" && a.Stereotype != b.Stereotype)
            {
                if (layerEdges.TryGetValue((a.Stereotype, b.Stereotype), out var le)) le.Weight += e.Weight;
                else layerEdges[(a.Stereotype, b.Stereotype)] = new Edge { From = a.Stereotype, To = b.Stereotype, Kind = "layer", Weight = e.Weight };
            }
        }
        pack.Dependencies.NamespaceEdges = nsEdges.Values.OrderByDescending(e => e.Weight).ToList();
        pack.Dependencies.Layers = layerEdges.Values.OrderByDescending(e => e.Weight).ToList();

        // external namespace usage per project
        var ext = new Dictionary<(string, string), Edge>();
        foreach (var c in pack.CallGraph.ExternalCalls)
        {
            var proj = ctx.Methods.TryGetValue(c.Caller, out var m) ? m.Project : "?";
            var ns = string.Join('.', c.Namespace.Split('.').Take(3));
            if (ns.Length == 0) continue;
            if (ext.TryGetValue((proj, ns), out var e)) e.Weight += c.Count;
            else ext[(proj, ns)] = new Edge { From = proj, To = ns, Kind = "uses-api", Weight = c.Count };
        }
        pack.Dependencies.ExternalNamespaceUsage = ext.Values.OrderBy(e => e.From).ThenByDescending(e => e.Weight).ToList();

        // DbObjects referenced only through EF entities: infer table names
        foreach (var e in pack.Data.Entities)
        {
            var table = e.Table ?? e.Name;
            if (!pack.Data.DbObjects.Any(o => string.Equals(o.Name, table, StringComparison.OrdinalIgnoreCase) || o.Name.EndsWith("." + table, StringComparison.OrdinalIgnoreCase)))
                pack.Data.DbObjects.Add(new DbObject { Name = table, Kind = "table", Source = "ef-mapping", Columns = e.Properties.Where(p => p.NavigationTo == null).Select(p => p.Column ?? p.Name).ToList(), Location = e.Location });
        }
        pack.Data.Entities = pack.Data.Entities.OrderBy(e => e.Name).ToList();
        pack.Data.DbObjects = pack.Data.DbObjects.OrderBy(o => o.Kind).ThenBy(o => o.Name).ToList();
        pack.Dependencies.DiRegistrations = pack.Dependencies.DiRegistrations
            .GroupBy(d => (d.Service, d.Implementation, d.Lifetime)).Select(g => g.First()).ToList();
    }
}
