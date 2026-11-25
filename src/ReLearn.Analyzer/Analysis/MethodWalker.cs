using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using ReLearn.Infrastructure;
using ReLearn.Model;
using Location = ReLearn.Model.Location;

namespace ReLearn.Analysis;

/// Pass 2: walks the IOperation tree of every source method (language agnostic: C# and VB).
/// Produces call edges, external API usage, data operations, integration points, business-rule
/// candidates, DI registrations, minimal-API endpoints, UI navigation and cyclomatic complexity.
public sealed class MethodWalker : OperationWalker
{
    private static readonly Regex StateName = new("(?i)(status|state|stage|phase|step)$", RegexOptions.Compiled);
    private static readonly HashSet<string> RuleExcluded = new() { "test", "migration", "mapper", "entity-config", "bootstrap", "dto", "exception", "enum", "contract" };
    private static readonly HashSet<string> MapVerbs = new() { "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "Map" };
    private static readonly HashSet<string> EfConfigMethods = new()
    {
        "ToTable", "ToView", "HasKey", "HasMaxLength", "IsRequired", "HasPrecision", "HasIndex", "IsUnique", "HasOne", "HasMany",
        "WithOne", "WithMany", "HasForeignKey", "HasDefaultValue", "HasDefaultValueSql", "HasColumnName", "HasConversion",
        "HasData", "HasCheckConstraint", "HasQueryFilter", "OwnsOne", "OwnsMany", "HasDiscriminator", "HasComputedColumnSql", "IsConcurrencyToken", "IsRowVersion",
    };

    private readonly AnalysisContext _ctx;
    private readonly MethodNode _root;
    private readonly IMethodSymbol _rootSymbol;
    private readonly TypeNode _type;
    private readonly Stack<string> _callers = new();
    private readonly List<string> _pendingSql = new();
    private readonly HashSet<string> _sqlSeen = new();
    private readonly HashSet<string> _dataSeen = new();
    private readonly HashSet<string> _intSeen = new();
    private readonly HashSet<int> _ruleSeen = new();
    private bool _commandIsProc;
    private int _branches;
    private int _order;

    public MethodWalker(AnalysisContext ctx, MethodNode root, IMethodSymbol rootSymbol)
    {
        _ctx = ctx;
        _root = root;
        _rootSymbol = rootSymbol;
        _type = ctx.Types.TryGetValue(root.ContainingType, out var t) ? t : new TypeNode { Id = root.ContainingType, Name = root.ContainingType };
        _callers.Push(root.Id);
    }

    private string Caller => _callers.Peek();
    private bool RulesEnabled => !RuleExcluded.Contains(_type.Stereotype);
    private string Ctrl => _type.Name.EndsWith("Controller") ? _type.Name[..^10] : _type.Name;

    public void Walk(IOperation op)
    {
        try { Visit(op); }
        catch (InsufficientExecutionStackException) { _ctx.Log($"deep tree skipped in {_root.Id}"); }
    }

    public void Complete() => _root.CyclomaticComplexity = 1 + _branches;

    // =====================================================================================
    // calls
    // =====================================================================================
    public override void VisitInvocation(IInvocationOperation op)
    {
        var target = op.TargetMethod;
        if (_type.Stereotype == "migration") { Migration(op); base.VisitInvocation(op); return; }

        HandleCall(target, op, op.Arguments, isCtor: false, isVirtual: op.IsVirtual);
        var name = target.Name;

        DiRegistration(op, target);
        Navigation(op, target);
        if (MapVerbs.Contains(name) && op.Arguments.Length >= 3) MinimalApi(op, name);
        if (name is "AddPolicy" && op.Arguments.Length >= 2) PolicyRule(op);

        base.VisitInvocation(op);
    }

    public override void VisitObjectCreation(IObjectCreationOperation op)
    {
        if (_type.Stereotype != "migration")
        {
            if (op.Constructor != null) HandleCall(op.Constructor, op, op.Arguments, isCtor: true, isVirtual: false);
            var t = op.Type as INamedTypeSymbol;
            if (t != null)
            {
                // ADO.NET: new SqlCommand("select ...", conn)
                if (t.Name.EndsWith("Command") && !_ctx.IsSourceType(t) && op.Arguments.Length > 0 && Str(op.Arguments[0].Value) is { } sql
                    && (Classifiers.LooksLikeSql(sql.text) || !sql.text.Trim().Contains(' ')))
                { _pendingSql.Add(sql.text); _sqlSeen.Add(sql.text); }
                // UI navigation: a screen creates/opens another screen
                var targetStereo = _ctx.StereotypeOfType(t);
                if (targetStereo is "form" or "window" or "wpf-view" or "user-control" && _ctx.Types.TryGetValue(Sym.Id(t), out var tn))
                {
                    var from = _type.Stereotype is "form" or "window" or "wpf-view" or "user-control" or "viewmodel" ? TypeCatalog.ScreenId(_type)
                             : _type.Stereotype == "bootstrap" ? "app:start" : TypeCatalog.ScreenId(_type);
                    _ctx.AddNavigation(from, TypeCatalog.ScreenId(tn), "opens");
                }
                // MVVM commands: new RelayCommand(Save) / new DelegateCommand(OnSave)
                if (t.AllInterfaces.Any(i => Sym.TypeName(i) == "System.Windows.Input.ICommand"))
                {
                    foreach (var a in op.Arguments)
                        if (Unwrap(a.Value) is IDelegateCreationOperation { Target: IMethodReferenceOperation mr } && _ctx.Methods.ContainsKey(Sym.Id(mr.Method)))
                            AddUiEntry(Sym.Id(mr.Method), "wpf-command", $"vm:{_type.Name}", mr.Method.Name);
                }
            }
        }
        base.VisitObjectCreation(op);
    }

    private void HandleCall(IMethodSymbol target, IOperation op, IReadOnlyList<IArgumentOperation> args, bool isCtor, bool isVirtual)
    {
        var norm = (IMethodSymbol)Sym.Normalize(target);
        var calleeId = Sym.Id(norm);
        var line = LineOf(op);

        if (_ctx.Methods.ContainsKey(calleeId))
        {
            var kind = isCtor ? "constructor"
                : norm.ContainingType?.TypeKind == TypeKind.Interface ? "interface"
                : isVirtual && (norm.IsVirtual || norm.IsAbstract || norm.IsOverride) ? "virtual"
                : "direct";
            var edge = new CallEdge { Caller = Caller, Callee = calleeId, Kind = kind, Line = line, Order = ++_order };
            if (kind is "interface" or "virtual" && _ctx.Implementations.TryGetValue(calleeId, out var impls))
                edge.ResolvedTargets = impls.ToList();
            _ctx.AddCall(edge);
            if (norm.ContainingType != null) _ctx.AddTypeEdge(_type.Id, Sym.Id(norm.ContainingType), isCtor ? "creates" : "calls");
            return;
        }

        var typeName = Sym.TypeName(norm.ContainingType);
        var methodName = isCtor ? ".ctor" : norm.Name;
        _ctx.AddExternal(Caller, typeName + "." + methodName, norm.ContainingType == null ? "" : Sym.Namespace(norm.ContainingType));

        if (!ClassifyData(target, typeName, methodName, op, args))
            ClassifyIntegration(typeName, methodName, op, args, target);
    }

    // =====================================================================================
    // data access
    // =====================================================================================
    private bool ClassifyData(IMethodSymbol target, string typeName, string method, IOperation op, IReadOnlyList<IArgumentOperation> args)
    {
        // LINQ over a DbSet (EF / EF6 / LINQ-to-SQL) -> read of entity
        if (typeName is "System.Linq.Queryable" or "System.Linq.Enumerable" or "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions" or "System.Data.Entity.QueryableExtensions"
            && !method.StartsWith("Execute"))
        {
            if (args.Count > 0 && RootDbSet(args[0].Value) is { } entity)
            {
                RecordData("linq", Classifiers.Match(Classifiers.Data, typeName, method)?.Technology ?? "ef", "read", new() { entity }, null, false, typeName + "." + method, op, StatementKey(op));
                return true;
            }
            if (typeName is "System.Linq.Queryable" or "System.Linq.Enumerable") return false;
        }

        var p = Classifiers.Match(Classifiers.Data, typeName, method);
        if (p == null) return false;

        var targets = new List<string>();
        string? sql = null; var dynamic = false;
        // DbSet<T>.Add(...) -> T
        if (target.ContainingType is { TypeArguments.Length: 1 } ct && _ctx.EntityName(ct.TypeArguments[0]) is { } en) targets.Add(en);
        else if (target.ContainingType is { TypeArguments.Length: 1 } ct2) targets.Add(ct2.TypeArguments[0].Name);
        if (p.TargetArg >= 0 && args.Count > p.TargetArg && Str(args[p.TargetArg].Value) is { } s) { sql = s.text; dynamic = s.dynamic; }
        else if (p.TargetArg >= 0 && args.Count > 0)
        {
            // named argument "sql" / "commandText"
            var named = args.FirstOrDefault(a => a.Parameter?.Name is "sql" or "commandText" or "query" or "storedProcName");
            if (named != null && Str(named.Value) is { } s2) { sql = s2.text; dynamic = s2.dynamic; }
        }
        if (sql == null && p.Technology.StartsWith("ado") && _pendingSql.Count > 0) sql = _pendingSql[^1];
        if (sql != null) _sqlSeen.Add(sql);
        // Dapper commandType: CommandType.StoredProcedure
        var isProc = _commandIsProc || args.Any(a => a.Parameter?.Name == "commandType" && a.Value.Syntax.ToString().Contains("StoredProcedure"));

        var operation = p.Operation;
        if (sql != null)
        {
            var procs = Classifiers.SqlProcedures(sql, isProc);
            if (isProc && procs.Count > 0) operation = "exec";
            else if (Classifiers.LooksLikeSql(sql)) operation = Classifiers.SqlOperation(sql);
            foreach (var t in Classifiers.SqlTables(sql)) { targets.Add(t); DbObj(t, "table", "sql-literal", op); }
            foreach (var pr in procs) { targets.Add(pr); DbObj(pr, "procedure", "sql-literal", op); }
        }
        RecordData(p.Technology, p.Technology, operation, targets.Distinct().ToList(), sql, dynamic, typeName + "." + method, op, LineOf(op).ToString());
        return true;
    }

    private void RecordData(string _, string tech, string operation, List<string> targets, string? sql, bool dynamic, string api, IOperation op, string key)
    {
        if (!_dataSeen.Add($"{key}|{string.Join(",", targets)}|{operation}")) return;
        _ctx.Pack.Data.Operations.Add(new DataOperation
        {
            MethodId = Caller,
            Technology = tech,
            Operation = operation,
            Targets = targets,
            Sql = sql == null ? null : Text.OneLine(sql, 2000),
            DynamicSql = dynamic,
            Api = api,
            Location = Sym.Loc(op.Syntax, _ctx.Root),
        });
    }

    private void DbObj(string name, string kind, string source, IOperation op)
    {
        var list = _ctx.Pack.Data.DbObjects;
        var o = list.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.Kind == kind);
        if (o == null) list.Add(o = new DbObject { Name = name, Kind = kind, Source = source, Location = Sym.Loc(op.Syntax, _ctx.Root) });
        if (!o.ReferencedBy.Contains(Caller)) o.ReferencedBy.Add(Caller);
    }

    private string? RootDbSet(IOperation v)
    {
        for (var i = 0; i < 25 && v != null; i++)
        {
            v = Unwrap(v);
            if (v.Type is INamedTypeSymbol nt && nt.Name is "DbSet" or "IDbSet" or "Table" or "ObjectSet" && nt.TypeArguments.Length == 1)
                return _ctx.EntityName(nt.TypeArguments[0]) ?? nt.TypeArguments[0].Name;
            v = v switch
            {
                IInvocationOperation inv => inv.Instance ?? (inv.Arguments.Length > 0 ? inv.Arguments[0].Value : null)!,
                IPropertyReferenceOperation pr => null!,
                IConditionalAccessOperation ca => ca.Operation,
                _ => null!,
            };
        }
        return null;
    }

    private void Migration(IInvocationOperation op)
    {
        var n = op.TargetMethod.Name;
        if (n is "CreateTable" or "RenameTable" or "AddColumn" or "CreateIndex")
        {
            var nameArg = op.Arguments.FirstOrDefault(a => a.Parameter?.Name is "name" or "table");
            if (nameArg != null && Str(nameArg.Value) is { } s)
            {
                var tableName = n == "CreateTable" || n == "RenameTable" ? s.text : (op.Arguments.FirstOrDefault(a => a.Parameter?.Name == "table") is { } ta && Str(ta.Value) is { } ts ? ts.text : s.text);
                var obj = _ctx.Pack.Data.DbObjects.FirstOrDefault(x => string.Equals(x.Name, tableName, StringComparison.OrdinalIgnoreCase) && x.Kind == "table");
                if (obj == null) _ctx.Pack.Data.DbObjects.Add(obj = new DbObject { Name = tableName, Kind = "table", Source = "migration", Location = Sym.Loc(op.Syntax, _ctx.Root) });
                if (n == "AddColumn" && Str(nameArg.Value) is { } col) obj.Columns.Add(col.text);
                if (n == "CreateTable")
                {
                    // columns: table => new { Id = table.Column<int>(...), ... }
                    foreach (var init in op.Descendants().OfType<IAnonymousObjectCreationOperation>().Take(1))
                        foreach (var a in init.Initializers.OfType<ISimpleAssignmentOperation>())
                            if (a.Target is IPropertyReferenceOperation pr) obj.Columns.Add(pr.Property.Name);
                }
            }
        }
        else if (n == "Sql" && op.Arguments.Length > 0 && Str(op.Arguments[0].Value) is { } sql)
        {
            foreach (var t in Classifiers.SqlTables(sql.text)) DbObj(t, "table", "migration", op);
        }
    }

    // =====================================================================================
    // integrations
    // =====================================================================================
    private void ClassifyIntegration(string typeName, string method, IOperation op, IReadOnlyList<IArgumentOperation> args, IMethodSymbol? target)
    {
        var p = Classifiers.Match(Classifiers.Integrations, typeName, method);
        if (p == null) return;
        string? targetValue = null;
        if (p.TargetArg >= 0 && args.Count > p.TargetArg && Str(args[p.TargetArg].Value) is { } s) targetValue = s.text;
        // Kafka/RabbitMQ/ServiceBus: topic / queue often a named argument
        targetValue ??= args.Where(a => a.Parameter?.Name is "topic" or "queue" or "queueName" or "exchange" or "routingKey" or "key" or "requestUri" or "address" or "path" or "entityPath")
                            .Select(a => Str(a.Value)?.text).FirstOrDefault(x => x != null);

        if (!_intSeen.Add($"{typeName}.{method}|{LineOf(op)}")) return;
        _ctx.Pack.Integrations.Points.Add(new IntegrationPoint
        {
            Id = _ctx.NextIntegrationId(),
            Category = p.Category,
            Technology = p.Technology,
            Direction = p.Direction,
            Operation = string.IsNullOrEmpty(p.Operation) ? method : p.Operation,
            Target = targetValue == null ? null : Text.OneLine(targetValue, 300),
            Api = $"{typeName}.{method}",
            MethodId = Caller,
            Location = Sym.Loc(op.Syntax, _ctx.Root),
        });

        // In-process dispatch (MediatR, MassTransit, NServiceBus): connect sender -> handler in the call graph
        if (p.Category is "mediator" or "queue" && args.Count > 0)
        {
            var msgType = Unwrap(args[^1].Value).Type ?? (target?.TypeArguments.Length > 0 ? target.TypeArguments[0] : null);
            if (msgType != null && _ctx.MediatorHandlers.TryGetValue(Sym.TypeName(msgType), out var handler))
                _ctx.AddCall(new CallEdge { Caller = Caller, Callee = handler, Kind = p.Category == "mediator" ? "mediator" : "message", Line = LineOf(op), Order = ++_order });
        }
    }

    public override void VisitPropertyReference(IPropertyReferenceOperation op)
    {
        var prop = op.Property;
        if (prop.IsIndexer)
        {
            var typeName = Sym.TypeName(prop.ContainingType);
            var isWrite = op.Parent is ISimpleAssignmentOperation sa && sa.Target == op;
            var fromConfigManager = op.Instance is IPropertyReferenceOperation ip && Sym.TypeName(ip.Property.ContainingType) is "System.Configuration.ConfigurationManager" or "System.Web.Configuration.WebConfigurationManager";
            if (typeName.StartsWith("Microsoft.Extensions.Configuration.") || fromConfigManager
                || typeName is "System.Web.Caching.Cache" or "System.Web.SessionState.HttpSessionState" or "System.Web.HttpSessionStateBase" or "System.Web.HttpApplicationState")
            {
                ClassifyIntegration(fromConfigManager ? "System.Configuration.ConfigurationManager" : typeName, isWrite ? "set_Item" : "get_Item", op, op.Arguments, null);
            }
        }
        else if (_ctx.Methods.TryGetValue(Sym.Id(prop.GetMethod ?? (ISymbol)prop), out _) && prop.GetMethod != null)
        {
            // computed source property getters participate in flows (e.g. Order.Total)
            var getterId = Sym.Id(prop.GetMethod);
            var decl = prop.GetMethod.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (decl != null && decl.ToString().Contains("=>") || decl?.ToString().Contains("return") == true)
                _ctx.AddCall(new CallEdge { Caller = Caller, Callee = getterId, Kind = "property", Line = LineOf(op), Order = ++_order });
        }
        base.VisitPropertyReference(op);
    }

    // =====================================================================================
    // DI, navigation, minimal APIs, policies
    // =====================================================================================
    private void DiRegistration(IInvocationOperation op, IMethodSymbol m)
    {
        var n = m.Name;
        string? service = null, impl = null, lifetime = null, container = "Microsoft.Extensions.DependencyInjection";
        if (Regex.IsMatch(n, "^(TryAdd|Add)(Scoped|Transient|Singleton|HostedService|HttpClient|DbContext|DbContextPool|DbContextFactory)$"))
        {
            lifetime = Regex.Replace(n, "^(TryAdd|Add)", "");
            if (m.TypeArguments.Length >= 2) { service = m.TypeArguments[0].ToDisplayString(Sym.Full); impl = m.TypeArguments[1].ToDisplayString(Sym.Full); }
            else if (m.TypeArguments.Length == 1) { service = impl = m.TypeArguments[0].ToDisplayString(Sym.Full); }
            else
            {
                var typeofs = op.Arguments.Select(a => Unwrap(a.Value)).OfType<ITypeOfOperation>().ToList();
                if (typeofs.Count >= 1) service = typeofs[0].TypeOperand.ToDisplayString(Sym.Full);
                if (typeofs.Count >= 2) impl = typeofs[1].TypeOperand.ToDisplayString(Sym.Full);
            }
        }
        else if (n is "As" or "To" or "ImplementedBy" && m.TypeArguments.Length == 1)
        {
            for (var inst = op.Instance; inst != null; inst = (inst as IInvocationOperation)?.Instance)
            {
                if (inst is IInvocationOperation { TargetMethod: { Name: "RegisterType" or "Bind" or "For" or "Register", TypeArguments.Length: 1 } rm })
                {
                    var root = rm.TypeArguments[0].ToDisplayString(Sym.Full);
                    var here = m.TypeArguments[0].ToDisplayString(Sym.Full);
                    (service, impl) = n == "As" ? (here, root) : (root, here);
                    container = rm.Name switch { "RegisterType" => "Autofac", "Bind" => "Ninject", "For" => "Castle.Windsor", _ => "SimpleInjector" };
                    lifetime = "unspecified";
                    break;
                }
            }
        }
        else if (n is "RegisterType" or "Register" && m.TypeArguments.Length == 2)
        {
            service = m.TypeArguments[0].ToDisplayString(Sym.Full); impl = m.TypeArguments[1].ToDisplayString(Sym.Full);
            container = n == "RegisterType" ? "Unity" : "SimpleInjector"; lifetime = "unspecified";
        }
        if (service == null) return;
        _ctx.Pack.Dependencies.DiRegistrations.Add(new DiRegistration { Service = service, Implementation = impl, Lifetime = lifetime ?? "", Container = container, Location = Sym.Loc(op.Syntax, _ctx.Root) });
    }

    private void Navigation(IInvocationOperation op, IMethodSymbol m)
    {
        var n = m.Name;
        string Arg(int i) => op.Arguments.Length > i && Str(op.Arguments[i].Value) is { } s ? s.text : "";
        string Named(string param) => op.Arguments.FirstOrDefault(x => x.Parameter?.Name == param) is { } na && Str(na.Value) is { } ns ? ns.text : "";
        var fromMvc = $"mvc:{Ctrl}/{_rootSymbol.Name}";

        switch (n)
        {
            case "RedirectToAction" or "RedirectToActionPermanent":
            {
                var action = Named("actionName"); if (action == "") action = Arg(0);
                var ctrl = Named("controllerName"); if (ctrl == "") ctrl = Ctrl;
                if (action != "") _ctx.AddNavigation(fromMvc, $"mvc:{ctrl}/{action}", "redirect");
                break;
            }
            case "RedirectToPage" or "RedirectToPagePermanent":
            {
                var page = Arg(0);
                var from = _type.Stereotype == "razor-page" ? $"page:{RazorRouteOf(_type)}" : fromMvc;
                if (page != "") _ctx.AddNavigation(from, $"page:{page}", "redirect");
                break;
            }
            case "View" or "PartialView" when _type.Stereotype == "controller":
            {
                var view = op.Arguments.Length > 0 && Str(op.Arguments[0].Value) is { } v && !v.text.Contains(' ') ? v.text : _rootSymbol.Name;
                var id = $"mvc:{Ctrl}/{_rootSymbol.Name}";
                if (_ctx.Pack.Journeys.Screens.All(s => s.Id != id))
                    _ctx.Pack.Journeys.Screens.Add(new Screen { Id = id, Name = $"{Text.Humanize(Ctrl)} {Text.Humanize(view)}", Kind = n == "View" ? "mvc-view" : "mvc-partial", Type = _type.Id, Location = Sym.Loc(op.Syntax, _ctx.Root) });
                break;
            }
            case "Redirect" or "Transfer" or "RedirectPermanent":
            {
                var url = Arg(0);
                if (url.Contains(".aspx", StringComparison.OrdinalIgnoreCase))
                    _ctx.AddNavigation(TypeCatalog.ScreenId(_type), "webforms:" + Path.GetFileNameWithoutExtension(url.Split('?')[0]), "redirect");
                else if (url.StartsWith("/") || url.StartsWith("~/"))
                    _ctx.AddNavigation(_type.Stereotype == "controller" ? fromMvc : TypeCatalog.ScreenId(_type), "url:" + url.TrimStart('~'), "redirect");
                break;
            }
            case "Navigate":
            {
                foreach (var a in op.Arguments)
                {
                    var v = Unwrap(a.Value);
                    ITypeSymbol? t = v is ITypeOfOperation to ? to.TypeOperand : v is IObjectCreationOperation oc ? oc.Type : null;
                    if (t != null && _ctx.Types.TryGetValue(Sym.Id(t), out var tn))
                        _ctx.AddNavigation(TypeCatalog.ScreenId(_type), TypeCatalog.ScreenId(tn), "navigate");
                    else if (Str(v) is { } s && (s.text.EndsWith(".xaml") || s.text.StartsWith("/")))
                        _ctx.AddNavigation(TypeCatalog.ScreenId(_type), "url:" + s.text, "navigate");
                }
                break;
            }
            case "Run" when Sym.TypeName(m.ContainingType) == "System.Windows.Forms.Application":
            {
                if (op.Arguments.Length > 0 && Unwrap(op.Arguments[0].Value) is IObjectCreationOperation oc && oc.Type != null && _ctx.Types.TryGetValue(Sym.Id(oc.Type), out var tn))
                    _ctx.AddNavigation("app:start", TypeCatalog.ScreenId(tn), "start");
                break;
            }
        }
    }

    private static string RazorRouteOf(TypeNode t)
    {
        var file = t.Files.FirstOrDefault() ?? "";
        var idx = file.IndexOf("/Pages/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? file[(idx + 6)..].Replace(".cshtml.cs", "") : "/" + t.Name;
    }

    private void MinimalApi(IInvocationOperation op, string name)
    {
        var routeArg = op.Arguments.FirstOrDefault(a => a.Parameter?.Name == "pattern") ?? op.Arguments[1];
        if (Str(routeArg.Value) is not { } route) return;
        var handlerArg = op.Arguments.FirstOrDefault(a => a.Parameter?.Name is "handler" or "requestDelegate") ?? op.Arguments[^1];
        string? handlerId = null;
        var hv = Unwrap(handlerArg.Value);
        if (hv is IDelegateCreationOperation dc)
        {
            if (dc.Target is IAnonymousFunctionOperation af) handlerId = LambdaId(af);
            else if (dc.Target is IMethodReferenceOperation mr) handlerId = Sym.Id(mr.Method);
        }
        else if (hv is IAnonymousFunctionOperation af2) handlerId = LambdaId(af2);
        var verb = name == "Map" ? "ANY" : name == "MapMethods" ? "MULTI" : name[3..].ToUpperInvariant();
        var ep = new EntryPoint
        {
            Id = $"EP-{_ctx.Pack.EntryPoints.Count + 1:0000}",
            Kind = "minimal-api",
            HttpMethod = verb,
            Route = route.text.StartsWith("/") ? route.text : "/" + route.text,
            Name = $"{verb} {(route.text.StartsWith("/") ? route.text : "/" + route.text)}",
            MethodId = handlerId ?? Caller,
            ContainingType = _type.Id,
            Location = Sym.Loc(op.Syntax, _ctx.Root),
        };
        // chained .RequireAuthorization("Policy")
        for (var p = op.Parent; p != null; p = p.Parent)
        {
            if (p is IInvocationOperation chained && chained.TargetMethod.Name == "RequireAuthorization")
            {
                ep.RequiresAuth = true;
                ep.Policies.AddRange(chained.Arguments.Select(a => Str(a.Value)?.text).Where(x => x != null).Select(x => x!));
            }
            if (p is IExpressionStatementOperation) break;
        }
        _ctx.Pack.EntryPoints.Add(ep);
        _ctx.EntryMethodIds.Add(ep.MethodId);
    }

    private void PolicyRule(IInvocationOperation op)
    {
        if (Str(op.Arguments.Length > 1 && op.Arguments[0].Parameter?.Name == "name" ? op.Arguments[0].Value : op.Arguments[^2].Value) is not { } name) return;
        _ctx.AddRule(new BusinessRule
        {
            Kind = "authorization",
            Title = $"Authorization policy '{name.text}'",
            Condition = Text.OneLine(op.Syntax.ToString(), 400),
            Outcome = "deny access when policy requirements are not met",
            MethodId = Caller,
            ContainingType = _type.Id,
            Confidence = "high",
            Location = Sym.Loc(op.Syntax, _ctx.Root),
        });
    }

    private void AddUiEntry(string methodId, string kind, string screen, string trigger)
    {
        var existing = _ctx.Pack.EntryPoints.FirstOrDefault(e => e.MethodId == methodId);
        if (existing != null) { existing.Trigger ??= trigger; return; }
        var m = _ctx.Methods[methodId];
        _ctx.Pack.EntryPoints.Add(new EntryPoint
        {
            Id = $"EP-{_ctx.Pack.EntryPoints.Count + 1:0000}", Kind = kind, Name = $"{_type.Name}.{m.Name}", MethodId = methodId,
            ContainingType = m.ContainingType, Screen = screen, Trigger = trigger, Location = m.Location,
        });
        _ctx.EntryMethodIds.Add(methodId);
    }

    // =====================================================================================
    // delegates, lambdas, events
    // =====================================================================================
    private string LambdaId(IAnonymousFunctionOperation af)
    {
        var span = af.Syntax.GetLocation().GetLineSpan().StartLinePosition;
        return $"{_root.Id}#lambda@{span.Line + 1}:{span.Character + 1}";
    }

    public override void VisitAnonymousFunction(IAnonymousFunctionOperation op)
    {
        var id = LambdaId(op);
        if (!_ctx.Methods.ContainsKey(id))
        {
            var node = new MethodNode
            {
                Id = id, Name = $"{_root.Name}<lambda@{LineOf(op)}>", ContainingType = _root.ContainingType, Kind = "lambda",
                Signature = Text.OneLine(op.Syntax.ToString(), 120), Project = _root.Project, Stereotype = _root.Stereotype,
                IsAsync = op.Symbol.IsAsync, Location = Sym.Loc(op.Syntax, _ctx.Root),
            };
            _ctx.Methods[id] = node;
            _ctx.Pack.CallGraph.Nodes.Add(node);
        }
        _ctx.AddCall(new CallEdge { Caller = Caller, Callee = id, Kind = "lambda", Line = LineOf(op), Order = ++_order });
        _callers.Push(id);
        base.VisitAnonymousFunction(op);
        _callers.Pop();
    }

    public override void VisitLocalFunction(ILocalFunctionOperation op)
    {
        var id = $"{_root.Id}#local:{op.Symbol.Name}";
        if (!_ctx.Methods.ContainsKey(id))
        {
            var node = new MethodNode
            {
                Id = id, Name = $"{_root.Name}.{op.Symbol.Name}", ContainingType = _root.ContainingType, Kind = "local-function",
                Signature = op.Symbol.ToDisplayString(Sym.Signature), Project = _root.Project, Stereotype = _root.Stereotype, Location = Sym.Loc(op.Syntax, _ctx.Root),
            };
            _ctx.Methods[id] = node;
            _ctx.Pack.CallGraph.Nodes.Add(node);
        }
        _callers.Push(id);
        base.VisitLocalFunction(op);
        _callers.Pop();
    }

    public override void VisitMethodReference(IMethodReferenceOperation op)
    {
        var id = Sym.Id(op.Method);
        if (_ctx.Methods.ContainsKey(id) && op.Parent is IDelegateCreationOperation)
            _ctx.AddCall(new CallEdge { Caller = Caller, Callee = id, Kind = "delegate", Line = LineOf(op), Order = ++_order });
        base.VisitMethodReference(op);
    }

    public override void VisitEventAssignment(IEventAssignmentOperation op)
    {
        if (op.Adds && Unwrap(op.HandlerValue) is IDelegateCreationOperation { Target: IMethodReferenceOperation mr })
        {
            var hid = Sym.Id(mr.Method);
            if (_ctx.Methods.ContainsKey(hid))
            {
                _ctx.AddCall(new CallEdge { Caller = Caller, Callee = hid, Kind = "event", Line = LineOf(op), Order = ++_order });
                if (op.EventReference is IEventReferenceOperation er)
                {
                    var source = er.Instance is IFieldReferenceOperation f ? f.Field.Name : er.Instance is IPropertyReferenceOperation p ? p.Property.Name : "this";
                    var ep = _ctx.Pack.EntryPoints.FirstOrDefault(e => e.MethodId == hid);
                    if (ep != null) ep.Trigger = $"{source}.{er.Event.Name}";
                }
            }
        }
        base.VisitEventAssignment(op);
    }

    // =====================================================================================
    // complexity
    // =====================================================================================
    public override void VisitWhileLoop(IWhileLoopOperation op) { _branches++; base.VisitWhileLoop(op); }
    public override void VisitForLoop(IForLoopOperation op) { _branches++; base.VisitForLoop(op); }
    public override void VisitForEachLoop(IForEachLoopOperation op) { _branches++; base.VisitForEachLoop(op); }
    public override void VisitForToLoop(IForToLoopOperation op) { _branches++; base.VisitForToLoop(op); }
    public override void VisitCatchClause(ICatchClauseOperation op) { _branches++; base.VisitCatchClause(op); }
    public override void VisitCoalesce(ICoalesceOperation op) { _branches++; base.VisitCoalesce(op); }
    public override void VisitConditionalAccess(IConditionalAccessOperation op) { _branches++; base.VisitConditionalAccess(op); }

    public override void VisitBinaryOperator(IBinaryOperation op)
    {
        if (op.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr) _branches++;
        base.VisitBinaryOperator(op);
    }

    // =====================================================================================
    // business rules
    // =====================================================================================
    public override void VisitConditional(IConditionalOperation op)
    {
        _branches++;
        if (RulesEnabled && op.Condition != null) ConditionalRule(op);
        base.VisitConditional(op);
    }

    private void ConditionalRule(IConditionalOperation op)
    {
        var cond = op.Condition;
        var condText = Text.OneLine(cond.Syntax.ToString(), 300);
        var outcome = Outcome(op.WhenTrue);
        var negated = false;
        if (outcome == null && op.WhenFalse != null)
        {
            outcome = Outcome(op.WhenFalse);
            negated = outcome != null;
        }
        var literals = Literals(cond);
        var entities = Entities(cond);

        if (outcome is { } o && o.kind is "validation" or "authorization" or "guard" or "existence")
        {
            var trivial = o.kind == "guard" && (o.exception?.StartsWith("ArgumentNull") == true || (o.exception?.StartsWith("Argument") == true && condText.Contains("null")));
            AddRule(op, new BusinessRule
            {
                Kind = o.kind,
                Title = o.message != null ? Text.OneLine(o.message, 140) : $"{Text.Humanize(_rootSymbol.Name)}: {(negated ? "unless" : "when")} {Text.OneLine(condText, 100)} → {o.outcome}",
                Condition = negated ? $"!({condText})" : condText,
                Outcome = o.outcome,
                Message = o.message,
                Literals = literals,
                Entities = entities,
                Confidence = o.message != null ? "high" : o.kind == "existence" ? "medium" : "medium",
                Trivial = trivial,
            });
            return;
        }

        // decision: condition compares against business literals / enums / constants
        if (literals.Count > 0 && IsSignificant(literals) && op.WhenTrue != null && op.IsRef == false)
        {
            var effect = outcome?.outcome ?? Summarize(op.WhenTrue);
            if (string.IsNullOrWhiteSpace(effect)) return;
            AddRule(op, new BusinessRule
            {
                Kind = "decision",
                Title = $"{Text.Humanize(_rootSymbol.Name)}: when {Text.OneLine(condText, 100)}",
                Condition = condText,
                Outcome = Text.OneLine(effect, 300) + (op.WhenFalse != null ? " | otherwise: " + Text.OneLine(Summarize(op.WhenFalse), 200) : ""),
                Literals = literals,
                Entities = entities,
                Confidence = "medium",
            });
        }
    }

    private (string kind, string outcome, string? message, string? exception)? Outcome(IOperation? branch)
    {
        if (branch == null) return null;
        foreach (var x in ShallowDescendants(branch))
        {
            switch (x)
            {
                case IThrowOperation t:
                {
                    var ex = Unwrap(t.Exception);
                    var exName = ex?.Type?.Name ?? "Exception";
                    var msg = ex is IObjectCreationOperation oc ? oc.Arguments.Select(a => Str(a.Value)?.text).FirstOrDefault(s => s != null) : null;
                    var kind = Regex.IsMatch(exName, "Unauthori[sz]ed|Security|Forbidden|AccessDenied|Permission") ? "authorization"
                             : Regex.IsMatch(exName, "NotFound|KeyNotFound|DoesNotExist|Missing") ? "existence"
                             : exName.StartsWith("Argument") && msg == null ? "guard"
                             : "validation";
                    return (kind, $"throw {exName}", msg, exName);
                }
                case IReturnOperation r when Unwrap(r.ReturnedValue) is IInvocationOperation inv && Classifiers.ErrorResultMethods.Contains(inv.TargetMethod.Name):
                {
                    var name = inv.TargetMethod.Name;
                    var msg = inv.Arguments.Select(a => Str(a.Value)?.text).FirstOrDefault(s => s != null);
                    var kind = name is "Forbid" or "Unauthorized" or "Challenge" ? "authorization" : name is "NotFound" or "HttpNotFound" ? "existence" : "validation";
                    return (kind, $"respond {name}", msg, null);
                }
                case IReturnOperation r2 when Unwrap(r2.ReturnedValue) is IObjectCreationOperation oc2 && oc2.Type != null && Regex.IsMatch(oc2.Type.Name, "Error|Failure|Problem|BadRequest|NotFound|Invalid"):
                {
                    var msg = oc2.Arguments.Select(a => Str(a.Value)?.text).FirstOrDefault(s => s != null);
                    return (oc2.Type.Name.Contains("NotFound") ? "existence" : "validation", $"return {oc2.Type.Name}", msg, null);
                }
                case IInvocationOperation i when i.TargetMethod.Name is "AddModelError" or "AddError" or "Fail" or "Failure" or "Invalid" or "Error"
                                                 || (i.TargetMethod.Name == "Add" && i.Instance?.Syntax.ToString().Contains("rror") == true)
                                                 || (i.TargetMethod.Name == "Show" && i.TargetMethod.ContainingType?.Name == "MessageBox"):
                {
                    var msg = i.Arguments.Select(a => Str(a.Value)?.text).Where(s => s != null && s.Length > 3).LastOrDefault();
                    if (i.TargetMethod.Name == "AddModelError") msg = i.Arguments.Length > 1 ? Str(i.Arguments[1].Value)?.text ?? msg : msg;
                    if (i.TargetMethod.Name == "Show") msg = i.Arguments.Length > 0 ? Str(i.Arguments[0].Value)?.text : msg;
                    return ("validation", $"{i.TargetMethod.Name}(…)", msg, null);
                }
                case ISimpleAssignmentOperation a when MemberName(a.Target) is { } mn && StateName.IsMatch(mn):
                    return ("state", $"set {mn} = {Text.OneLine(a.Value.Syntax.ToString(), 80)}", null, null);
                case IReturnOperation r3 when r3.ReturnedValue != null && Unwrap(r3.ReturnedValue).ConstantValue.HasValue:
                    return ("decision", $"return {Text.OneLine(r3.ReturnedValue.Syntax.ToString(), 80)}", null, null);
            }
        }
        return null;
    }

    /// `x ?? throw new NotFoundException("…")` — existence/validation rule expressed as a throw expression.
    public override void VisitThrow(IThrowOperation op)
    {
        if (RulesEnabled)
        {
            IOperation? p = op.Parent;
            while (p is IConversionOperation) p = p.Parent;
            if (p is ICoalesceOperation co && Outcome(op) is { } th)
            {
                AddRule(op, new BusinessRule
                {
                    Kind = th.kind == "guard" ? "existence" : th.kind,
                    Title = th.message != null ? Text.OneLine(th.message, 140) : $"{Text.Humanize(_rootSymbol.Name)}: {Text.OneLine(co.Value.Syntax.ToString(), 80)} must exist",
                    Condition = $"({Text.OneLine(co.Value.Syntax.ToString(), 200)}) is null",
                    Outcome = th.outcome,
                    Message = th.message,
                    Entities = Entities(co.Value),
                    Confidence = th.message != null ? "high" : "medium",
                });
            }
        }
        base.VisitThrow(op);
    }

    public override void VisitSwitch(ISwitchOperation op)
    {
        _branches += Math.Max(0, op.Cases.Length - 1);
        if (RulesEnabled && op.Cases.Length >= 2)
        {
            var rows = new List<string[]>();
            var state = false;
            foreach (var c in op.Cases)
            {
                var when = string.Join(" | ", c.Clauses.Select(cl => cl switch
                {
                    ISingleValueCaseClauseOperation s => s.Value.Syntax.ToString(),
                    IDefaultCaseClauseOperation => "default",
                    IPatternCaseClauseOperation p => p.Pattern.Syntax.ToString() + (p.Guard != null ? " when " + p.Guard.Syntax : ""),
                    _ => cl.Syntax.ToString(),
                }));
                var then = Text.OneLine(string.Join("; ", c.Body.SelectMany(b => b is IBlockOperation blk ? blk.Operations : new[] { b }.AsEnumerable())
                    .Where(b => b is not IBranchOperation).Take(3).Select(b => b.Syntax.ToString())), 200);
                if (c.Body.SelectMany(b => b.DescendantsAndSelf()).OfType<ISimpleAssignmentOperation>().Any(a => MemberName(a.Target) is { } n && StateName.IsMatch(n))) state = true;
                rows.Add(new[] { Text.OneLine(when, 120), then });
            }
            AddRule(op, new BusinessRule
            {
                Kind = state ? "state-transition" : "decision-table",
                Title = $"{Text.Humanize(_rootSymbol.Name)}: decision on {Text.OneLine(op.Value.Syntax.ToString(), 80)}",
                Condition = Text.OneLine(op.Value.Syntax.ToString(), 200),
                Table = rows,
                Literals = rows.Select(r => r[0]).ToList(),
                Entities = Entities(op.Value),
                Confidence = "medium",
            });
        }
        base.VisitSwitch(op);
    }

    public override void VisitSwitchExpression(ISwitchExpressionOperation op)
    {
        _branches += Math.Max(0, op.Arms.Length - 1);
        if (RulesEnabled && op.Arms.Length >= 2)
        {
            var rows = op.Arms.Select(a => new[]
            {
                Text.OneLine(a.Pattern.Syntax + (a.Guard != null ? " when " + a.Guard.Syntax : ""), 120),
                Text.OneLine(a.Value.Syntax.ToString(), 200),
            }).ToList();
            var target = op.Parent is ISimpleAssignmentOperation sa ? MemberName(sa.Target) : null;
            AddRule(op, new BusinessRule
            {
                Kind = target != null && StateName.IsMatch(target) ? "state-transition" : "decision-table",
                Title = $"{Text.Humanize(_rootSymbol.Name)}: {(target != null ? target + " from " : "mapping of ")}{Text.OneLine(op.Value.Syntax.ToString(), 80)}",
                Condition = Text.OneLine(op.Value.Syntax.ToString(), 200),
                Table = rows,
                Literals = rows.Select(r => r[0]).ToList(),
                Entities = Entities(op.Value),
                Confidence = "medium",
            });
        }
        base.VisitSwitchExpression(op);
    }

    public override void VisitSimpleAssignment(ISimpleAssignmentOperation op)
    {
        if (RulesEnabled)
        {
            var name = MemberName(op.Target);
            // ADO.NET command text / type
            if (name == "CommandText" && Str(op.Value) is { } sql) { _pendingSql.Add(sql.text); _sqlSeen.Add(sql.text); }
            else if (name == "CommandType" && op.Value.Syntax.ToString().Contains("StoredProcedure")) _commandIsProc = true;
            else if (name != null && StateName.IsMatch(name) && Unwrap(op.Value) is IFieldReferenceOperation { Field.ContainingType.TypeKind: TypeKind.Enum } or ILiteralOperation)
            {
                var guard = Enclosing<IConditionalOperation>(op)?.Condition?.Syntax.ToString();
                AddRule(op, new BusinessRule
                {
                    Kind = "state-transition",
                    Title = $"{Text.Humanize(_rootSymbol.Name)} sets {name} to {Text.OneLine(op.Value.Syntax.ToString(), 60)}",
                    Condition = guard == null ? null : Text.OneLine(guard, 300),
                    Outcome = $"{Text.OneLine(op.Target.Syntax.ToString(), 80)} = {Text.OneLine(op.Value.Syntax.ToString(), 80)}",
                    Literals = new() { Text.OneLine(op.Value.Syntax.ToString(), 80) },
                    Entities = Entities(op.Target),
                    Confidence = "high",
                });
            }
            else if (name != null) Calculation(op, name, op.Value);
        }
        else if (MemberName(op.Target) == "CommandText" && Str(op.Value) is { } sql2) { _pendingSql.Add(sql2.text); _sqlSeen.Add(sql2.text); }
        base.VisitSimpleAssignment(op);
    }

    public override void VisitCompoundAssignment(ICompoundAssignmentOperation op)
    {
        if (RulesEnabled && MemberName(op.Target) is { } name) Calculation(op, name, op);
        base.VisitCompoundAssignment(op);
    }

    public override void VisitVariableDeclarator(IVariableDeclaratorOperation op)
    {
        if (RulesEnabled && op.Initializer?.Value is { } v) Calculation(op, op.Symbol.Name, v);
        base.VisitVariableDeclarator(op);
    }

    public override void VisitReturn(IReturnOperation op)
    {
        if (RulesEnabled && op.ReturnedValue is { } v && Regex.IsMatch(_rootSymbol.Name, "(?i)calc|compute|total|price|cost|fee|tax|discount|amount|rate|score|premium|interest|balance|get.*(total|amount|price)"))
            Calculation(op, _rootSymbol.Name, v);
        base.VisitReturn(op);
    }

    private void Calculation(IOperation anchor, string target, IOperation value)
    {
        if (value.Type == null || !IsNumeric(value.Type)) return;
        var all = value.DescendantsAndSelf().ToList();
        var arithmetic = all.OfType<IBinaryOperation>().Any(b => b.OperatorKind is BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder)
                         || (anchor is ICompoundAssignmentOperation ca && ca.OperatorKind is BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide)
                         || all.OfType<IBinaryOperation>().Any(b => b.OperatorKind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract) && all.OfType<IPropertyReferenceOperation>().Count() >= 2;
        var lits = all.OfType<ILiteralOperation>().Where(l => l.ConstantValue.HasValue && l.ConstantValue.Value is not null and not string).Select(l => Convert.ToString(l.ConstantValue.Value, System.Globalization.CultureInfo.InvariantCulture)!).ToList();
        var consts = all.OfType<IFieldReferenceOperation>().Where(f => f.Field.IsConst || (f.Field.IsStatic && f.Field.IsReadOnly)).Select(f => $"{f.Field.ContainingType.Name}.{f.Field.Name}").ToList();
        var mathCalls = all.OfType<IInvocationOperation>().Any(i => i.TargetMethod.ContainingType?.Name is "Math" or "Decimal" && i.TargetMethod.Name is "Round" or "Ceiling" or "Floor" or "Pow" or "Max" or "Min" or "Truncate");
        var significant = lits.Any(l => l is not ("0" or "1" or "-1")) || consts.Count > 0 || mathCalls;
        if (!arithmetic || !significant) return;
        if (anchor is IVariableDeclaratorOperation && Regex.IsMatch(target, "^(i|j|k|idx|index|count|n)$")) return;
        AddRule(anchor, new BusinessRule
        {
            Kind = "calculation",
            Title = $"{Text.Humanize(target)} calculation in {Text.Humanize(_rootSymbol.Name)}",
            Expression = $"{target} = {Text.OneLine(value.Syntax.ToString(), 300)}",
            Condition = Enclosing<IConditionalOperation>(anchor)?.Condition?.Syntax is { } g ? Text.OneLine(g.ToString(), 200) : null,
            Literals = lits.Concat(consts).Distinct().ToList(),
            Entities = Entities(value),
            Confidence = consts.Count > 0 || lits.Any(l => l.Contains('.')) ? "high" : "medium",
        });
    }

    public override void VisitExpressionStatement(IExpressionStatementOperation op)
    {
        var st = _type.Stereotype;
        if (st == "validator")
        {
            var txt = op.Syntax.ToString();
            if (txt.Contains("RuleFor"))
            {
                var msg = op.Descendants().OfType<IInvocationOperation>().Where(i => i.TargetMethod.Name == "WithMessage")
                    .Select(i => i.Arguments.Length > 0 ? Str(i.Arguments[^1].Value)?.text : null).FirstOrDefault(s => s != null);
                var validated = _ctx.TypeSymbols.TryGetValue(_type.Id, out var vt) ? Sym.BaseTypes(vt).FirstOrDefault(b => b.Name == "AbstractValidator")?.TypeArguments.FirstOrDefault()?.Name : null;
                AddRule(op, new BusinessRule
                {
                    Kind = "fluent-validation",
                    Title = msg != null ? Text.OneLine(msg, 140) : $"{validated ?? _type.Name}: {Text.OneLine(txt, 120)}",
                    Condition = Text.OneLine(txt, 600),
                    Outcome = "reject invalid input",
                    Message = msg,
                    Entities = validated != null ? new() { validated } : new(),
                    Confidence = "high",
                });
            }
        }
        else if (st is "dbcontext" or "entity-config")
        {
            var invs = op.Descendants().OfType<IInvocationOperation>().ToList();
            if (op.Operation is IInvocationOperation top && (EfConfigMethods.Contains(top.TargetMethod.Name) || invs.Any(i => EfConfigMethods.Contains(i.TargetMethod.Name))))
            {
                var entity = invs.Select(i => i.TargetMethod).Where(m => m.Name == "Entity" && m.TypeArguments.Length == 1).Select(m => m.TypeArguments[0].Name).FirstOrDefault();
                if (entity == null && _ctx.TypeSymbols.TryGetValue(_type.Id, out var ct))
                    entity = ct.AllInterfaces.FirstOrDefault(i => i.Name == "IEntityTypeConfiguration")?.TypeArguments.FirstOrDefault()?.Name;
                var text = Text.OneLine(op.Syntax.ToString(), 600);
                _ctx.Pack.Data.ModelConfiguration.Add(new ModelConfig { Entity = entity, Text = text, Location = Sym.Loc(op.Syntax, _ctx.Root) });

                var all = invs.Append(top).ToList();
                foreach (var i in all.Where(i => i.TargetMethod.Name is "ToTable" or "ToView"))
                {
                    if (i.Arguments.Length == 0 || Str(i.Arguments.FirstOrDefault(a => a.Parameter?.Name == "name")?.Value ?? i.Arguments[^1].Value) is not { } tn) continue;
                    if (i.TargetMethod.Name == "ToTable" && entity != null)
                    {
                        var em = _ctx.Pack.Data.Entities.FirstOrDefault(e => e.Name == entity);
                        if (em != null) em.Table = tn.text;
                    }
                    DbObj(tn.text, i.TargetMethod.Name == "ToView" ? "view" : "table", "ef-mapping", op);
                }
                if (all.Any(i => i.TargetMethod.Name is "HasQueryFilter" or "HasCheckConstraint" or "IsUnique"))
                {
                    AddRule(op, new BusinessRule
                    {
                        Kind = "data-constraint",
                        Title = $"{entity ?? "Entity"}: " + (text.Contains("HasQueryFilter") ? "global query filter" : text.Contains("IsUnique") ? "uniqueness constraint" : "check constraint"),
                        Condition = text,
                        Entities = entity != null ? new() { entity } : new(),
                        Confidence = "high",
                    });
                }
            }
        }
        base.VisitExpressionStatement(op);
    }

    public override void VisitLiteral(ILiteralOperation op)
    {
        if (op.ConstantValue.HasValue && op.ConstantValue.Value is string s && Classifiers.LooksLikeSql(s) && !_sqlSeen.Contains(s))
        {
            var key = LineOf(op).ToString();
            if (!_dataSeen.Any(k => k.StartsWith(key + "|")))
            {
                var tables = Classifiers.SqlTables(s);
                var procs = Classifiers.SqlProcedures(s, false);
                foreach (var t in tables) DbObj(t, "table", "sql-literal", op);
                foreach (var p in procs) DbObj(p, "procedure", "sql-literal", op);
                RecordData("sql", "sql-literal", Classifiers.SqlOperation(s), tables.Concat(procs).ToList(), s, false, "string-literal", op, key);
            }
        }
        base.VisitLiteral(op);
    }

    // =====================================================================================
    // helpers
    // =====================================================================================
    private void AddRule(IOperation anchor, BusinessRule r)
    {
        if (!_ruleSeen.Add(anchor.Syntax.SpanStart)) return;
        r.MethodId = _root.Id;
        r.ContainingType = _type.Id;
        r.Location = Sym.Loc(anchor.Syntax, _ctx.Root);
        _ctx.AddRule(r);
    }

    private static IEnumerable<IOperation> ShallowDescendants(IOperation root)
    {
        var q = new Queue<IOperation>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var x = q.Dequeue();
            yield return x;
            foreach (var c in x.ChildOperations)
            {
                if (c is IConditionalOperation or ILoopOperation or ISwitchOperation or IAnonymousFunctionOperation or ILocalFunctionOperation) continue;
                q.Enqueue(c);
            }
        }
    }

    private static string Summarize(IOperation? branch)
    {
        if (branch == null) return "";
        var ops = branch is IBlockOperation b ? b.Operations : ImmutableArrayOf(branch);
        return string.Join("; ", ops.Take(3).Select(o => Text.OneLine(o.Syntax.ToString(), 120)));
    }

    private static IEnumerable<IOperation> ImmutableArrayOf(IOperation o) { yield return o; }

    private List<string> Literals(IOperation cond)
    {
        var list = new List<string>();
        foreach (var d in cond.DescendantsAndSelf())
        {
            switch (d)
            {
                case ILiteralOperation l when l.ConstantValue.HasValue && l.ConstantValue.Value != null:
                    list.Add(l.ConstantValue.Value is string s ? $"\"{s}\"" : Convert.ToString(l.ConstantValue.Value, System.Globalization.CultureInfo.InvariantCulture)!);
                    break;
                case IFieldReferenceOperation f when f.Field.ContainingType?.TypeKind == TypeKind.Enum || f.Field.IsConst:
                    list.Add($"{f.Field.ContainingType?.Name}.{f.Field.Name}");
                    break;
            }
        }
        return list.Distinct().ToList();
    }

    private static bool IsSignificant(List<string> literals) =>
        literals.Any(l => l is not ("0" or "1" or "-1" or "\"\"" or "True" or "False" or "true" or "false") && !l.StartsWith("String.") && !l.StartsWith("string."));

    private List<string> Entities(IOperation op)
    {
        var set = new List<string>();
        foreach (var d in op.DescendantsAndSelf())
        {
            ITypeSymbol? t = d switch
            {
                IPropertyReferenceOperation p => p.Instance?.Type ?? p.Property.ContainingType,
                IParameterReferenceOperation pr => pr.Type,
                ILocalReferenceOperation lr => lr.Type,
                _ => null,
            };
            var e = _ctx.EntityName(t);
            if (e != null && !set.Contains(e)) set.Add(e);
            var e2 = _ctx.EntityName(d.Type);
            if (e2 != null && !set.Contains(e2)) set.Add(e2);
        }
        return set;
    }

    private static string? MemberName(IOperation target) => Unwrap(target) switch
    {
        IPropertyReferenceOperation p => p.Property.Name,
        IFieldReferenceOperation f => f.Field.Name,
        ILocalReferenceOperation l => l.Local.Name,
        _ => null,
    };

    private static bool IsNumeric(ITypeSymbol t)
    {
        var st = t.SpecialType;
        if (t.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && t is INamedTypeSymbol nt) st = nt.TypeArguments[0].SpecialType;
        return st is SpecialType.System_Decimal or SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16;
    }

    private static T? Enclosing<T>(IOperation op) where T : class, IOperation
    {
        for (var p = op.Parent; p != null; p = p.Parent)
        {
            if (p is T t) return t;
            if (p is IMethodBodyOperation or IAnonymousFunctionOperation) break;
        }
        return null;
    }

    private static string StatementKey(IOperation op)
    {
        var cur = op;
        while (cur.Parent != null && cur.Parent is not (IExpressionStatementOperation or IVariableDeclarationGroupOperation or IReturnOperation or IBlockOperation))
            cur = cur.Parent;
        var stmt = cur.Parent ?? cur;
        return "stmt" + stmt.Syntax.SpanStart;
    }

    private static int LineOf(IOperation op) => op.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static IOperation Unwrap(IOperation? op)
    {
        while (op is IConversionOperation c) op = c.Operand;
        while (op is IParenthesizedOperation p) op = p.Operand;
        return op!;
    }

    /// Best-effort string value of an expression: constants, interpolated strings, concatenations, const fields.
    public static (string text, bool dynamic)? Str(IOperation? op)
    {
        op = Unwrap(op);
        if (op == null) return null;
        if (op.ConstantValue.HasValue && op.ConstantValue.Value is string s) return (s, false);
        switch (op)
        {
            case IInterpolatedStringOperation i:
            {
                var sb = new System.Text.StringBuilder();
                foreach (var part in i.Parts)
                {
                    if (part is IInterpolatedStringTextOperation t && t.Text.ConstantValue.Value is string ts) sb.Append(ts);
                    else if (part is IInterpolationOperation ip) sb.Append('{').Append(Text.OneLine(ip.Expression.Syntax.ToString(), 40)).Append('}');
                }
                return (sb.ToString(), true);
            }
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } b when b.Type?.SpecialType == SpecialType.System_String:
            {
                var l = Str(b.LeftOperand); var r = Str(b.RightOperand);
                if (l == null && r == null) return null;
                return ((l?.text ?? "{?}") + (r?.text ?? "{?}"), true);
            }
            case ILocalReferenceOperation lr:
            {
                // follow a simple `var sql = "..."` declaration in the same method
                var decl = lr.Local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax().ToString();
                if (decl != null)
                {
                    var m = Regex.Match(decl, "=\\s*@?\\$?\"(?<s>(?:[^\"]|\"\")*)\"", RegexOptions.Singleline);
                    if (m.Success) return (m.Groups["s"].Value.Replace("\"\"", "\""), true);
                }
                return null;
            }
        }
        return null;
    }
}
