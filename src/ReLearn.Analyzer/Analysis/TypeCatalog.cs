using Microsoft.CodeAnalysis;
using ReLearn.Infrastructure;
using ReLearn.Model;
using Location = ReLearn.Model.Location;

namespace ReLearn.Analysis;

/// Pass 1 (symbols only): types, stereotypes, entities, entry points, screens, declarative rules,
/// interface/override implementation maps, and structural type dependencies.
public sealed class TypeCatalog
{
    private readonly AnalysisContext _ctx;
    public TypeCatalog(AnalysisContext ctx) => _ctx = ctx;

    public static IEnumerable<INamedTypeSymbol> SourceTypes(Compilation c)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(c.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur is INamespaceSymbol ns)
            {
                foreach (var m in ns.GetMembers()) stack.Push(m);
            }
            else if (cur is INamedTypeSymbol t)
            {
                if (t.Locations.Any(l => l.IsInSource)) yield return t;
                foreach (var nested in t.GetTypeMembers()) stack.Push(nested);
            }
        }
    }

    public void Run()
    {
        // 1) register every source type with a provisional stereotype
        foreach (var (pid, comp) in _ctx.Compilations)
        {
            var proj = _ctx.Projects[pid];
            var isTest = _ctx.TestProjects.Contains(pid);
            foreach (var t in SourceTypes(comp))
            {
                var id = Sym.Id(t);
                if (_ctx.Types.ContainsKey(id)) continue;
                var node = new TypeNode
                {
                    Id = id,
                    Name = t.Name,
                    FullName = t.ToDisplayString(Sym.Full),
                    Namespace = Sym.Namespace(t),
                    Kind = t.IsRecord ? "record" : t.TypeKind.ToString().ToLowerInvariant(),
                    Project = proj.Name,
                    Accessibility = t.DeclaredAccessibility.ToString().ToLowerInvariant(),
                    IsAbstract = t.IsAbstract && t.TypeKind == TypeKind.Class,
                    IsStatic = t.IsStatic,
                    IsPartial = t.DeclaringSyntaxReferences.Length > 1,
                    BaseType = t.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType and not SpecialType.System_Enum } b ? b.ToDisplayString(Sym.Full) : null,
                    Interfaces = t.Interfaces.Select(i => i.ToDisplayString(Sym.Full)).ToList(),
                    Attributes = t.GetAttributes().Select(Sym.AttrText).ToList(),
                    Location = Sym.Loc(t, _ctx.Root),
                    Files = t.Locations.Where(l => l.IsInSource).Select(l => Paths.Relative(_ctx.Root, l.SourceTree?.FilePath)).Distinct().ToList(),
                    Stereotype = Stereotype(t, isTest, proj),
                };
                if (t.TypeKind == TypeKind.Enum)
                    node.EnumMembers = t.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => $"{f.Name}={f.ConstantValue}").ToList();
                _ctx.Types[id] = node;
                _ctx.TypeSymbols[id] = t;
            }
        }

        // 2) entities (needs all types registered) then re-stereotype entities
        DiscoverEntities();

        // 3) per type: members, edges, implementations, entry points, screens, declarative rules
        foreach (var (id, t) in _ctx.TypeSymbols)
        {
            var node = _ctx.Types[id];
            var pid = ProjectOf(t);
            RegisterMethods(t, node, pid);
            StructuralEdges(t, node);
            ImplementationMaps(t);
            if (node.Stereotype == "test" || node.Stereotype == "migration") continue;
            EntryPoints(t, node);
            Screens(t, node);
            DeclarativeRules(t, node);
        }
    }

    private ProjectId ProjectOf(INamedTypeSymbol t)
    {
        foreach (var (pid, c) in _ctx.Compilations)
            if (SymbolEqualityComparer.Default.Equals(c.Assembly, t.ContainingAssembly)) return pid;
        return _ctx.Compilations.Keys.First();
    }

    // ------------------------------------------------------------------ stereotypes
    private string Stereotype(INamedTypeSymbol t, bool isTestProject, Model.ProjectInfo proj)
    {
        var n = t.Name;
        bool Has(string attr) => t.GetAttributes().Any(a => Sym.AttrName(a) == attr);

        if (isTestProject || t.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a => Sym.AttrName(a) is "Fact" or "Theory" or "Test" or "TestMethod" or "TestCase")))
            return "test";
        if (t.TypeKind == TypeKind.Enum) return "enum";
        if (t.TypeKind == TypeKind.Interface)
            return Has("ServiceContract") ? "service-contract" : "contract";
        if (Sym.InheritsFrom(t, Classifiers.ControllerBases) || Has("ApiController") || (n.EndsWith("Controller") && !t.IsAbstract && proj.Kind.StartsWith("web")))
            return "controller";
        if (Sym.InheritsFrom(t, Classifiers.RazorPageBases)) return "razor-page";
        if (Sym.InheritsFrom(t, Classifiers.BlazorBases)) return "blazor-component";
        if (Sym.InheritsFrom(t, Classifiers.HubBases)) return "hub";
        if (Sym.InheritsFrom(t, Classifiers.DbContextBases)) return "dbcontext";
        if (Sym.InheritsFrom(t, Classifiers.MigrationBases)) return "migration";
        if (Sym.InheritsFrom(t, Classifiers.FluentValidatorBases) || n.EndsWith("Validator")) return "validator";
        if (Sym.Implements(t, Classifiers.EntityConfigInterfaces)) return "entity-config";
        if (Sym.InheritsFrom(t, Classifiers.FormBases)) return "form";
        if (Sym.InheritsFrom(t, Classifiers.UserControlBases)) return "user-control";
        if (Sym.InheritsFrom(t, Classifiers.WpfWindowBases)) return "window";
        if (Sym.InheritsFrom(t, Classifiers.WpfPageBases)) return "wpf-view";
        if (Sym.InheritsFrom(t, Classifiers.WebFormsPageBases)) return "webforms-page";
        if (Sym.InheritsFrom(t, Classifiers.WebFormsControlBases)) return "webforms-control";
        if (Sym.InheritsFrom(t, Classifiers.HostedBases) || Sym.Implements(t, Classifiers.HostedInterfaces)) return "hosted-service";
        if (Sym.InheritsFrom(t, Classifiers.ServiceBaseWin)) return "windows-service";
        if (Sym.Implements(t, Classifiers.JobInterfaces)) return "job";
        if (Sym.Implements(t, Classifiers.ConsumerInterfaces)) return "consumer";
        if (Sym.Implements(t, Classifiers.MediatorHandlers)) return "handler";
        if (Sym.InheritsFrom(t, Classifiers.AutoMapperProfile)) return "mapper";
        if (Sym.InheritsFrom(t, Classifiers.ViewModelBases) || n.EndsWith("ViewModel")) return "viewmodel";
        if (t.GetMembers().OfType<IMethodSymbol>().Any(m => m.GetAttributes().Any(a => Sym.AttrName(a) is "WebMethod"))) return "asmx-service";
        if (Sym.InheritsFrom(t, "System.Exception")) return "exception";
        if (n is "Program" or "Startup" or "Global" or "MvcApplication" or "App" || n.EndsWith("Module") && Sym.Namespace(t).Contains("Ioc")) return "bootstrap";
        if (n.EndsWith("Repository") || n.EndsWith("Repo") || n.EndsWith("Dao") || n.EndsWith("DAO") || n.EndsWith("Dal") || n.EndsWith("DataAccess") || n.EndsWith("Store") || n.EndsWith("Gateway")) return "repository";
        if (n.EndsWith("Service") || n.EndsWith("Manager") || n.EndsWith("Engine") || n.EndsWith("Processor") || n.EndsWith("Calculator") || n.EndsWith("Handler") || n.EndsWith("Facade") || n.EndsWith("UseCase") || n.EndsWith("Workflow") || n.EndsWith("Policy") || n.EndsWith("Rule") || n.EndsWith("Rules") || n.EndsWith("BL") || n.EndsWith("Logic") || n.EndsWith("Bll"))
            return "service";
        if (n.EndsWith("Client") || n.EndsWith("Proxy") || n.EndsWith("Adapter") || n.EndsWith("Connector")) return "adapter";
        if (n.EndsWith("Dto") || n.EndsWith("DTO") || n.EndsWith("Request") || n.EndsWith("Response") || n.EndsWith("Command") || n.EndsWith("Query") || n.EndsWith("Message") || n.EndsWith("Event") || n.EndsWith("Contract") || n.EndsWith("Vm") || n.EndsWith("Model") || n.EndsWith("Args"))
            return "dto";
        if (n.EndsWith("Options") || n.EndsWith("Settings") || n.EndsWith("Configuration") || n.EndsWith("Config")) return "config";
        if (n.EndsWith("Extensions") || n.EndsWith("Helper") || n.EndsWith("Helpers") || n.EndsWith("Util") || n.EndsWith("Utils") || n.EndsWith("Utility")) return "utility";
        if (Has("Table") || Has("Entity")) return "entity";
        var ns = Sym.Namespace(t);
        if (ns.Contains(".Domain") || ns.Contains(".Entities") || ns.Contains(".Core.Model")) return "domain";
        return "other";
    }

    // ------------------------------------------------------------------ entities
    private void DiscoverEntities()
    {
        var ctxs = _ctx.Pack.Data.Contexts;
        foreach (var (id, t) in _ctx.TypeSymbols)
        {
            var node = _ctx.Types[id];
            if (node.Stereotype == "dbcontext")
            {
                var tech = Sym.InheritsFrom(t, "Microsoft.EntityFrameworkCore.DbContext") ? "ef-core"
                         : Sym.InheritsFrom(t, "System.Data.Linq.DataContext") ? "linq2sql" : "ef6";
                var dc = new DbContextModel { Id = id, Name = t.Name, Technology = tech, Location = node.Location };
                foreach (var p in AllProperties(t))
                {
                    if (p.Type is INamedTypeSymbol pt && pt.TypeArguments.Length == 1 && pt.Name is "DbSet" or "IDbSet" or "ObjectSet" or "Table" or "IQueryable")
                    {
                        var et = pt.TypeArguments[0];
                        dc.EntitySets.Add($"{p.Name}:{et.Name}");
                        MarkEntity(et, dc.Name, p.Name);
                    }
                }
                ctxs.Add(dc);
            }
            else if (node.Stereotype == "entity-config")
            {
                var iface = t.AllInterfaces.FirstOrDefault(i => Sym.TypeName(i) == "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration");
                if (iface != null) MarkEntity(iface.TypeArguments[0], null, null);
            }
            if (t.GetAttributes().Any(a => Sym.AttrName(a) == "Table")) MarkEntity(t, null, null);
        }

        foreach (var (id, name) in _ctx.EntityByType.ToList())
        {
            if (!_ctx.TypeSymbols.TryGetValue(id, out var t)) continue;
            var node = _ctx.Types[id];
            if (node.Stereotype is "other" or "domain" or "dto" or "entity") node.Stereotype = "entity";
            var em = _ctx.Pack.Data.Entities.First(e => e.Id == id);
            em.Location = node.Location;
            var tableAttr = t.GetAttributes().FirstOrDefault(a => Sym.AttrName(a) == "Table");
            if (tableAttr != null)
            {
                em.Table = Sym.AttrString(tableAttr) ?? em.Table;
                em.Schema = Sym.AttrString(tableAttr, 99, "Schema");
            }
            foreach (var p in AllProperties(t).Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public))
            {
                var ep = new EntityProperty
                {
                    Name = p.Name,
                    Type = p.Type.ToDisplayString(Sym.Short),
                    Nullable = p.NullableAnnotation == NullableAnnotation.Annotated || p.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
                };
                foreach (var a in p.GetAttributes())
                {
                    var an = Sym.AttrName(a);
                    if (an == "Key") ep.IsKey = true;
                    else if (an == "Column") ep.Column = Sym.AttrString(a);
                    else if (an is "NotMapped") ep.Constraints.Add("not-mapped");
                    else ep.Constraints.Add(Sym.AttrText(a));
                }
                if (!ep.IsKey && (p.Name == "Id" || p.Name == t.Name + "Id" || p.Name == t.Name + "ID")) ep.IsKey = true;
                var elem = ElementType(p.Type);
                var target = elem ?? p.Type;
                if (_ctx.EntityByType.TryGetValue(Sym.Id(target), out var navName))
                {
                    ep.NavigationTo = navName;
                    ep.IsCollection = elem != null;
                }
                em.Properties.Add(ep);
            }
        }
    }

    private void MarkEntity(ITypeSymbol et, string? context, string? setName)
    {
        var id = Sym.Id(et);
        if (!_ctx.Types.ContainsKey(id)) return; // entity defined outside the solution
        if (!_ctx.EntityByType.ContainsKey(id))
        {
            _ctx.EntityByType[id] = et.Name;
            _ctx.Pack.Data.Entities.Add(new EntityModel { Id = id, Name = et.Name, Table = setName });
        }
        var em = _ctx.Pack.Data.Entities.First(e => e.Id == id);
        if (context != null && !em.DbContexts.Contains(context)) em.DbContexts.Add(context);
        if (em.Table == null && setName != null) em.Table = setName;
    }

    private static IEnumerable<IPropertySymbol> AllProperties(INamedTypeSymbol t)
    {
        var seen = new HashSet<string>();
        for (INamedTypeSymbol? cur = t; cur != null && cur.SpecialType != SpecialType.System_Object; cur = cur.BaseType)
            foreach (var p in cur.GetMembers().OfType<IPropertySymbol>())
                if (!p.IsIndexer && seen.Add(p.Name)) yield return p;
    }

    public static ITypeSymbol? ElementType(ITypeSymbol t)
    {
        if (t.SpecialType == SpecialType.System_String) return null;
        if (t is IArrayTypeSymbol at) return at.ElementType;
        if (t is INamedTypeSymbol nt)
        {
            var ienum = nt.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                ? nt
                : nt.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
            if (ienum != null) return ienum.TypeArguments[0];
        }
        return null;
    }

    // ------------------------------------------------------------------ methods
    private void RegisterMethods(INamedTypeSymbol t, TypeNode node, ProjectId pid)
    {
        foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.IsImplicitlyDeclared && m.MethodKind != MethodKind.Constructor && m.Name != "<Main>$") continue;
            if (m.IsImplicitlyDeclared && m.MethodKind == MethodKind.Constructor && !m.DeclaringSyntaxReferences.Any()) continue;
            if (m.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove)
            {
                // auto-properties / field-like events carry no logic
                var txt = m.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().ToString() ?? "";
                if (!txt.Contains('{') && !txt.Contains("=>") && !txt.Contains("Return")) continue;
            }
            var id = Sym.Id(m);
            if (_ctx.Methods.ContainsKey(id)) continue;
            var loc = Sym.DeclLoc(m, _ctx.Root);
            var mn = new MethodNode
            {
                Id = id,
                Name = m.MethodKind == MethodKind.Constructor ? t.Name + ".ctor" : m.Name,
                ContainingType = node.Id,
                Signature = m.ToDisplayString(Sym.Signature),
                Kind = m.MethodKind switch
                {
                    MethodKind.Constructor or MethodKind.StaticConstructor => "ctor",
                    MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove => "accessor",
                    MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
                    _ => m.Name == "<Main>$" ? "top-level" : "method",
                },
                Accessibility = m.DeclaredAccessibility.ToString().ToLowerInvariant(),
                IsAsync = m.IsAsync,
                IsStatic = m.IsStatic,
                IsAbstract = m.IsAbstract,
                ReturnType = m.ReturnType.ToDisplayString(Sym.Short),
                Parameters = m.Parameters.Select(p => $"{p.Type.ToDisplayString(Sym.Short)} {p.Name}").ToList(),
                Attributes = m.GetAttributes().Select(Sym.AttrText).ToList(),
                Project = node.Project,
                Stereotype = node.Stereotype,
                Location = loc,
                LinesOfCode = loc == null ? 0 : loc.EndLine - loc.Line + 1,
            };
            _ctx.Methods[id] = mn;
            _ctx.MethodSymbols[id] = (m, pid);
            _ctx.Pack.CallGraph.Nodes.Add(mn);
        }
    }

    // ------------------------------------------------------------------ structural dependencies
    private void StructuralEdges(INamedTypeSymbol t, TypeNode node)
    {
        void Ref(ITypeSymbol? target, string kind)
        {
            if (target == null) return;
            foreach (var x in Flatten(target))
                if (_ctx.IsSourceType(x)) _ctx.AddTypeEdge(node.Id, Sym.Id(x), kind);
        }

        if (t.BaseType != null) Ref(t.BaseType, "inherits");
        foreach (var i in t.Interfaces) Ref(i, "implements");
        foreach (var member in t.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol f when !f.IsImplicitlyDeclared: Ref(f.Type, "field"); break;
                case IPropertySymbol p: Ref(p.Type, "property"); break;
                case IMethodSymbol m when !m.IsImplicitlyDeclared || m.MethodKind == MethodKind.Constructor:
                    Ref(m.ReturnType, "returns");
                    foreach (var p in m.Parameters) Ref(p.Type, m.MethodKind == MethodKind.Constructor ? "injects" : "parameter");
                    break;
            }
        }
    }

    private static IEnumerable<ITypeSymbol> Flatten(ITypeSymbol t)
    {
        yield return t.OriginalDefinition;
        if (t is INamedTypeSymbol nt)
            foreach (var a in nt.TypeArguments)
                foreach (var x in Flatten(a)) yield return x;
        if (t is IArrayTypeSymbol at)
            foreach (var x in Flatten(at.ElementType)) yield return x;
    }

    private void ImplementationMaps(INamedTypeSymbol t)
    {
        if (t.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return;
        foreach (var iface in t.AllInterfaces)
        {
            foreach (var im in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (t.FindImplementationForInterfaceMember(im) is IMethodSymbol impl && impl.Locations.Any(l => l.IsInSource))
                    _ctx.AddImplementation(Sym.Id(im), Sym.Id(impl));
            }
        }
        foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
        {
            for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
                _ctx.AddImplementation(Sym.Id(o), Sym.Id(m));
        }

        // MediatR: message type -> Handle
        foreach (var iface in t.AllInterfaces.Where(i => Classifiers.MediatorHandlers.Contains(Sym.TypeName(i)) || Classifiers.ConsumerInterfaces.Contains(Sym.TypeName(i))))
        {
            var msg = iface.TypeArguments.FirstOrDefault();
            var handle = iface.GetMembers().OfType<IMethodSymbol>().FirstOrDefault();
            if (msg == null || handle == null) continue;
            if (t.FindImplementationForInterfaceMember(handle) is IMethodSymbol impl)
                _ctx.MediatorHandlers[Sym.TypeName(msg)] = Sym.Id(impl);
        }
    }

    // ------------------------------------------------------------------ entry points
    private void EntryPoints(INamedTypeSymbol t, TypeNode node)
    {
        var classAttrs = t.GetAttributes();
        var classRoles = Roles(classAttrs);
        var classPolicies = Policies(classAttrs);
        var classAnon = classAttrs.Any(a => Sym.AttrName(a) == "AllowAnonymous");
        var classAuth = classAttrs.Any(a => Sym.AttrName(a) == "Authorize");
        var methods = t.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared).ToList();

        EntryPoint Make(IMethodSymbol m, string kind)
        {
            var attrs = m.GetAttributes();
            var ep = new EntryPoint
            {
                Kind = kind,
                MethodId = Sym.Id(m),
                ContainingType = node.Id,
                Name = $"{t.Name}.{m.Name}",
                Roles = classRoles.Concat(Roles(attrs)).Distinct().ToList(),
                Policies = classPolicies.Concat(Policies(attrs)).Distinct().ToList(),
                AllowAnonymous = classAnon || attrs.Any(a => Sym.AttrName(a) == "AllowAnonymous"),
                Parameters = m.Parameters.Select(p => $"{p.Type.ToDisplayString(Sym.Short)} {p.Name}" + (p.GetAttributes().Any() ? " [" + string.Join(",", p.GetAttributes().Select(Sym.AttrName)) + "]" : "")).ToList(),
                ReturnType = m.ReturnType.ToDisplayString(Sym.Short),
                Location = Sym.DeclLoc(m, _ctx.Root),
            };
            ep.RequiresAuth = !ep.AllowAnonymous && (classAuth || attrs.Any(a => Sym.AttrName(a) == "Authorize"));
            return ep;
        }

        void Add(EntryPoint ep)
        {
            ep.Id = $"EP-{_ctx.Pack.EntryPoints.Count + 1:0000}";
            _ctx.Pack.EntryPoints.Add(ep);
            _ctx.EntryMethodIds.Add(ep.MethodId);
        }

        switch (node.Stereotype)
        {
            case "controller":
            {
                var ctrl = t.Name.EndsWith("Controller") ? t.Name[..^10] : t.Name;
                var classRoute = classAttrs.Where(a => Sym.AttrName(a) is "Route" or "RoutePrefix").Select(a => Sym.AttrString(a)).FirstOrDefault(r => r != null);
                foreach (var m in methods.Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic))
                {
                    var attrs = m.GetAttributes();
                    if (attrs.Any(a => Sym.AttrName(a) == "NonAction")) continue;
                    var ep = Make(m, "http");
                    var verbAttr = attrs.FirstOrDefault(a => Classifiers.HttpVerbAttributes.Contains(Sym.AttrName(a)));
                    ep.HttpMethod = verbAttr != null ? Sym.AttrName(verbAttr)[4..].ToUpperInvariant()
                        : attrs.Any(a => Sym.AttrName(a) == "AcceptVerbs") ? string.Join("|", attrs.First(a => Sym.AttrName(a) == "AcceptVerbs").ConstructorArguments.SelectMany(c => c.Kind == TypedConstantKind.Array ? c.Values.ToArray() : new[] { c }).Select(Sym.ConstText)).Replace("\"", "")
                        : m.Name.StartsWith("Get") ? "GET" : m.Name.StartsWith("Post") || m.Name.StartsWith("Create") ? "POST" : m.Name.StartsWith("Put") || m.Name.StartsWith("Update") ? "PUT" : m.Name.StartsWith("Delete") ? "DELETE" : "ANY";
                    var methodRoute = (verbAttr != null ? Sym.AttrString(verbAttr) : null) ?? attrs.Where(a => Sym.AttrName(a) == "Route").Select(a => Sym.AttrString(a)).FirstOrDefault();
                    string route;
                    if (methodRoute != null && (methodRoute.StartsWith("/") || methodRoute.StartsWith("~/"))) route = methodRoute.TrimStart('~');
                    else if (classRoute != null) route = "/" + classRoute.Trim('/') + (string.IsNullOrEmpty(methodRoute) ? "" : "/" + methodRoute);
                    else if (methodRoute != null) route = "/" + methodRoute;
                    else route = $"/{ctrl}/{m.Name}";
                    ep.Route = route.Replace("[controller]", ctrl, StringComparison.OrdinalIgnoreCase).Replace("[action]", m.Name, StringComparison.OrdinalIgnoreCase).Replace("//", "/");
                    ep.Name = $"{ep.HttpMethod} {ep.Route}";
                    ep.Screen = $"mvc:{ctrl}/{m.Name}";
                    Add(ep);
                }
                break;
            }
            case "razor-page":
            {
                var file = node.Files.FirstOrDefault() ?? "";
                var idx = file.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
                var pageRoute = idx >= 0 ? file[(idx + 6)..].Replace(".cshtml.cs", "").Replace(".cs", "") : "/" + t.Name.Replace("Model", "");
                if (pageRoute.EndsWith("/Index")) pageRoute = pageRoute[..^6] + "/";
                foreach (var m in methods.Where(m => m.Name.StartsWith("On") && m.DeclaredAccessibility == Accessibility.Public))
                {
                    var ep = Make(m, "razor-page");
                    var verb = m.Name.StartsWith("OnGet") ? "GET" : m.Name.StartsWith("OnPost") ? "POST" : m.Name.StartsWith("OnPut") ? "PUT" : m.Name.StartsWith("OnDelete") ? "DELETE" : "ANY";
                    ep.HttpMethod = verb;
                    var handler = m.Name.Replace("Async", "");
                    handler = handler.Length > 2 + verb.Length ? handler[(2 + verb.Length)..] : "";
                    ep.Route = pageRoute + (handler.Length > 0 ? $"?handler={handler}" : "");
                    ep.Screen = $"page:{pageRoute}";
                    ep.Name = $"{verb} {ep.Route}";
                    Add(ep);
                }
                break;
            }
            case "hub":
                foreach (var m in methods.Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic && !m.IsOverride))
                {
                    var ep = Make(m, "signalr"); ep.Route = $"hub:{t.Name}/{m.Name}"; Add(ep);
                }
                break;
            case "hosted-service":
            case "windows-service":
                foreach (var m in methods.Where(m => m.Name is "ExecuteAsync" or "StartAsync" or "OnStart" or "OnTimer" or "DoWork"))
                {
                    var ep = Make(m, "hosted-service"); ep.Trigger = "background"; Add(ep);
                }
                break;
            case "job":
                foreach (var m in methods.Where(m => m.Name == "Execute"))
                {
                    var ep = Make(m, "scheduled-job"); ep.Trigger = "schedule"; Add(ep);
                }
                break;
            case "consumer":
                foreach (var m in methods.Where(m => m.Name is "Consume" or "Handle"))
                {
                    var ep = Make(m, "message-consumer");
                    ep.Trigger = m.Parameters.FirstOrDefault()?.Type.ToDisplayString(Sym.Short);
                    Add(ep);
                }
                break;
            case "handler":
                foreach (var m in methods.Where(m => m.Name == "Handle"))
                {
                    var ep = Make(m, "mediator-handler");
                    ep.Trigger = m.Parameters.FirstOrDefault()?.Type.ToDisplayString(Sym.Short);
                    Add(ep);
                }
                break;
            case "form":
            case "user-control":
            case "window":
            case "wpf-view":
            case "webforms-page":
            case "webforms-control":
            {
                var kind = node.Stereotype switch
                {
                    "window" or "wpf-view" => "wpf-event",
                    "webforms-page" or "webforms-control" => "webforms-event",
                    _ => "winforms-event",
                };
                foreach (var m in methods.Where(IsUiEventHandler))
                {
                    var ep = Make(m, kind);
                    ep.Screen = ScreenId(node);
                    ep.Trigger = m.Name;
                    Add(ep);
                }
                break;
            }
        }

        // attribute-driven, stereotype independent
        foreach (var m in methods)
        {
            var attrs = m.GetAttributes();
            if (_ctx.EntryMethodIds.Contains(Sym.Id(m))) continue;
            var fn = attrs.FirstOrDefault(a => Sym.AttrName(a) is "Function" or "FunctionName");
            if (fn != null)
            {
                var ep = Make(m, "function");
                ep.Name = Sym.AttrString(fn) ?? m.Name;
                var trig = m.Parameters.SelectMany(p => p.GetAttributes()).FirstOrDefault(a => Sym.AttrName(a).EndsWith("Trigger"));
                if (trig != null)
                {
                    ep.Trigger = Sym.AttrText(trig);
                    if (Sym.AttrName(trig) == "HttpTrigger")
                    {
                        ep.HttpMethod = string.Join("|", trig.ConstructorArguments.SelectMany(c => c.Kind == TypedConstantKind.Array ? c.Values.ToArray() : new[] { c }).Where(c => c.Value is string).Select(c => ((string)c.Value!).ToUpperInvariant()));
                        ep.Route = "/" + (Sym.AttrString(trig, 99, "Route") ?? ep.Name);
                    }
                }
                Add(ep);
            }
            else if (attrs.Any(a => Sym.AttrName(a) == "WebMethod"))
            {
                var ep = Make(m, "asmx"); ep.Route = $"/{t.Name}.asmx/{m.Name}"; Add(ep);
            }
            else if (attrs.Any(a => Sym.AttrName(a) == "RelayCommand"))
            {
                var ep = Make(m, "wpf-command"); ep.Screen = $"vm:{t.Name}"; ep.Trigger = m.Name + "Command"; Add(ep);
            }
            else if (attrs.Any(a => Sym.AttrName(a) is "RecurringJob" or "AutomaticRetry"))
            {
                var ep = Make(m, "scheduled-job"); Add(ep);
            }
        }

        // WCF service implementations
        foreach (var iface in t.AllInterfaces.Where(i => i.GetAttributes().Any(a => Sym.AttrName(a) == "ServiceContract")))
        {
            foreach (var op in iface.GetMembers().OfType<IMethodSymbol>().Where(x => x.GetAttributes().Any(a => Sym.AttrName(a) == "OperationContract")))
            {
                if (t.TypeKind != TypeKind.Class || t.FindImplementationForInterfaceMember(op) is not IMethodSymbol impl) continue;
                if (_ctx.EntryMethodIds.Contains(Sym.Id(impl))) continue;
                var ep = Make(impl, "wcf"); ep.Route = $"wcf:{iface.Name}/{op.Name}"; Add(ep);
            }
        }

        // gRPC service implementations (generated base class nested in a static class)
        if (t.BaseType is { } bt && bt.Name.EndsWith("Base") && bt.ContainingType is { IsStatic: true } holder
            && holder.GetMembers("BindService").Any())
        {
            foreach (var m in methods.Where(m => m.IsOverride))
            {
                var ep = Make(m, "grpc"); ep.Route = $"grpc:{holder.Name}/{m.Name}"; Add(ep);
            }
        }
    }

    public static bool IsUiEventHandler(IMethodSymbol m) =>
        m.Parameters.Length == 2
        && m.Parameters[0].Type.SpecialType == SpecialType.System_Object
        && (m.Parameters[1].Type.Name.EndsWith("EventArgs") || m.Parameters[1].Type.Name.EndsWith("Args"))
        && m.ReturnsVoid || (m.Parameters.Length == 2 && m.Name == "Page_Load");

    private static List<string> Roles(IEnumerable<AttributeData> attrs) =>
        attrs.Where(a => Sym.AttrName(a) is "Authorize" or "PrincipalPermission")
             .Select(a => Sym.AttrString(a, 99, "Roles") ?? Sym.AttrString(a, 99, "Role"))
             .Where(s => !string.IsNullOrEmpty(s))
             .SelectMany(s => s!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
             .ToList();

    private static List<string> Policies(IEnumerable<AttributeData> attrs) =>
        attrs.Where(a => Sym.AttrName(a) == "Authorize")
             .Select(a => Sym.AttrString(a, 99, "Policy") ?? Sym.AttrString(a, 0))
             .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

    // ------------------------------------------------------------------ screens
    public static string ScreenId(TypeNode node) => node.Stereotype switch
    {
        "form" or "user-control" => $"winform:{node.Name}",
        "window" or "wpf-view" => $"wpf:{node.Name}",
        "webforms-page" or "webforms-control" => $"webforms:{node.Name}",
        "razor-page" => $"page:{node.Name}",
        "blazor-component" => $"blazor:{node.Name}",
        "viewmodel" => $"vm:{node.Name}",
        _ => $"type:{node.Name}",
    };

    private void Screens(INamedTypeSymbol t, TypeNode node)
    {
        if (node.Stereotype is not ("form" or "user-control" or "window" or "wpf-view" or "webforms-page" or "webforms-control" or "blazor-component" or "viewmodel")) return;
        var screen = new Screen
        {
            Id = ScreenId(node),
            Name = Text.Humanize(node.Name.Replace("Form", "").Replace("Window", "").Replace("ViewModel", "").Replace("Page", "")),
            Kind = node.Stereotype,
            Type = node.Id,
            Location = node.Location,
        };
        if (screen.Name.Length == 0) screen.Name = node.Name;
        // UI controls declared as fields (WinForms designer) give us the screen's fields & actions
        foreach (var f in t.GetMembers().OfType<IFieldSymbol>())
        {
            var tn = f.Type.Name;
            if (tn is "TextBox" or "ComboBox" or "CheckBox" or "DateTimePicker" or "NumericUpDown" or "RadioButton" or "ListBox" or "MaskedTextBox" or "RichTextBox" or "DataGridView" or "DataGrid" or "GridView" or "DropDownList" or "Calendar")
                screen.Fields.Add($"{f.Name}:{tn}");
            else if (tn is "Button" or "LinkButton" or "ToolStripMenuItem" or "ToolStripButton" or "MenuItem" or "ImageButton" or "LinkLabel")
                screen.Actions.Add($"{f.Name}:{tn}");
        }
        // view-model bindable properties / commands
        if (node.Stereotype == "viewmodel")
        {
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>().Where(p => p.DeclaredAccessibility == Accessibility.Public))
            {
                if (p.Type.Name.Contains("Command")) screen.Actions.Add(p.Name);
                else screen.Fields.Add($"{p.Name}:{p.Type.ToDisplayString(Sym.Short)}");
            }
        }
        _ctx.Pack.Journeys.Screens.Add(screen);
    }

    // ------------------------------------------------------------------ declarative rules
    private void DeclarativeRules(INamedTypeSymbol t, TypeNode node)
    {
        // DataAnnotations on properties (entities, DTOs, view models)
        foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
        {
            foreach (var a in p.GetAttributes())
            {
                if (a.AttributeClass == null || !Sym.InheritsFrom(a.AttributeClass, "System.ComponentModel.DataAnnotations.ValidationAttribute")) continue;
                var msg = Sym.AttrString(a, 99, "ErrorMessage");
                _ctx.AddRule(new BusinessRule
                {
                    Kind = "declarative-validation",
                    Title = $"{t.Name}.{p.Name} must satisfy {Sym.AttrName(a)}",
                    Condition = $"{t.Name}.{p.Name} {Sym.AttrText(a)}",
                    Outcome = "reject invalid input",
                    Message = msg,
                    Literals = a.ConstructorArguments.Select(Sym.ConstText).ToList(),
                    Entities = _ctx.EntityByType.ContainsKey(node.Id) ? new() { t.Name } : new(),
                    ContainingType = node.Id,
                    Confidence = "high",
                    Location = Sym.Loc(p, _ctx.Root),
                });
            }
        }

        // business parameters: numeric/string constants in domain-ish classes
        if (node.Stereotype is "service" or "domain" or "entity" or "other" or "config" or "controller" or "handler" or "form" or "viewmodel")
        {
            foreach (var f in t.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst && f.ConstantValue != null))
            {
                var v = f.ConstantValue;
                var isNumber = v is int or long or decimal or double or float or short;
                var looksBusiness = System.Text.RegularExpressions.Regex.IsMatch(f.Name, "(?i)rate|limit|max|min|threshold|fee|tax|discount|days|percent|amount|price|cost|age|term|grace|penalty|interest|quota|level|tier|bonus|commission|cap|ceiling|floor|period|retry|timeout");
                if (!(isNumber && looksBusiness)) continue;
                _ctx.AddRule(new BusinessRule
                {
                    Kind = "parameter",
                    Title = $"Business parameter {Text.Humanize(f.Name)} = {v}",
                    Expression = $"{t.Name}.{f.Name} = {Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)}",
                    Literals = new() { Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" },
                    ContainingType = node.Id,
                    Confidence = "medium",
                    Location = Sym.Loc(f, _ctx.Root),
                });
            }
        }

        // authorization attributes
        foreach (var (sym, label) in new[] { ((ISymbol)t, t.Name) }.Concat(t.GetMembers().OfType<IMethodSymbol>().Select(m => ((ISymbol)m, $"{t.Name}.{m.Name}"))))
        {
            foreach (var a in sym.GetAttributes().Where(a => Sym.AttrName(a) is "Authorize" or "PrincipalPermission" or "RequiresPermission" or "HasPermission"))
            {
                var roles = Sym.AttrString(a, 99, "Roles");
                var policy = Sym.AttrString(a, 99, "Policy") ?? Sym.AttrString(a, 0);
                if (roles == null && policy == null) continue;
                _ctx.AddRule(new BusinessRule
                {
                    Kind = "authorization",
                    Title = $"{label} requires " + (roles != null ? $"role(s) {roles}" : $"policy {policy}"),
                    Condition = Sym.AttrText(a),
                    Outcome = "deny access when not satisfied",
                    MethodId = sym is IMethodSymbol ? Sym.Id(sym) : null,
                    ContainingType = node.Id,
                    Confidence = "high",
                    Location = Sym.Loc(sym, _ctx.Root),
                });
            }
        }
    }
}
