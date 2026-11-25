using System.Text.RegularExpressions;
using ReLearn.Infrastructure;
using ReLearn.Model;

namespace ReLearn.Analysis;

/// Builds business flows by walking the call graph from every entry point.
public sealed class FlowBuilder
{
    private static readonly HashSet<string> NoiseStereotypes = new() { "dto", "exception", "enum", "utility", "mapper", "config", "external" };

    private readonly AnalysisContext _ctx;
    private readonly Dictionary<string, List<CallEdge>> _out;
    private readonly Dictionary<string, List<DataOperation>> _dataByMethod;
    private readonly Dictionary<string, List<IntegrationPoint>> _intByMethod;
    private readonly Dictionary<string, List<BusinessRule>> _rulesByMethod;
    private readonly Dictionary<string, HashSet<string>> _diImpl; // service full name -> impl full names

    public FlowBuilder(AnalysisContext ctx)
    {
        _ctx = ctx;
        _out = ctx.Pack.CallGraph.Edges.GroupBy(e => e.Caller).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Order).ThenBy(e => e.Line).ToList());
        _dataByMethod = ctx.Pack.Data.Operations.GroupBy(d => d.MethodId).ToDictionary(g => g.Key, g => g.ToList());
        _intByMethod = ctx.Pack.Integrations.Points.GroupBy(d => d.MethodId).ToDictionary(g => g.Key, g => g.ToList());
        _rulesByMethod = ctx.Pack.Rules.Where(r => r.MethodId != null).GroupBy(r => r.MethodId!).ToDictionary(g => g.Key, g => g.ToList());
        _diImpl = ctx.Pack.Dependencies.DiRegistrations.Where(d => d.Implementation != null)
            .GroupBy(d => StripGenerics(d.Service)).ToDictionary(g => g.Key, g => g.Select(d => StripGenerics(d.Implementation!)).ToHashSet());
    }

    private static string StripGenerics(string s) { var i = s.IndexOf('<'); return i < 0 ? s : s[..i]; }

    public void Run()
    {
        var seq = 0;
        foreach (var ep in _ctx.Pack.EntryPoints)
        {
            if (!_ctx.Methods.ContainsKey(ep.MethodId)) continue;
            var flow = new BusinessFlow
            {
                Id = $"FLOW-{++seq:0000}",
                EntryPointId = ep.Id,
                Name = FlowName(ep),
                Capability = CapabilityOf(ep),
                Trigger = ep.Kind + (ep.Trigger != null ? $" ({ep.Trigger})" : "") + (ep.Route != null ? $" {ep.HttpMethod} {ep.Route}".TrimEnd() : ""),
                Roles = ep.Roles.Concat(ep.Policies.Select(p => "policy:" + p)).ToList(),
            };
            var visited = new HashSet<string>();
            Visit(flow, ep.MethodId, 0, null, null, visited);
            Summarize(flow);
            _ctx.Pack.Flows.Add(flow);
        }
    }

    private void Visit(BusinessFlow flow, string methodId, int depth, string? from, FlowStep? mergeInto, HashSet<string> visited)
    {
        if (flow.Steps.Count >= _ctx.Options.MaxFlowSteps) { flow.Truncated = true; return; }
        if (depth > _ctx.Options.MaxFlowDepth) { flow.Truncated = true; return; }
        if (!_ctx.Methods.TryGetValue(methodId, out var node)) return;
        var inline = node.Kind is "lambda" or "local-function";
        if (!inline && !visited.Add(methodId)) return; // recursion / repeated call: summarized once per flow

        FlowStep step;
        if (inline && mergeInto != null) step = mergeInto;
        else
        {
            var type = _ctx.Types.TryGetValue(node.ContainingType, out var tn) ? tn : null;
            step = new FlowStep
            {
                Order = flow.Steps.Count + 1,
                Depth = depth,
                MethodId = methodId,
                Method = node.Name,
                Type = type?.Name ?? node.ContainingType,
                Stereotype = node.Stereotype,
                CalledFrom = from,
            };
            flow.Steps.Add(step);
        }
        if (_dataByMethod.TryGetValue(methodId, out var data))
            foreach (var d in data) step.Data.Add($"{d.Operation} {string.Join(",", d.Targets.DefaultIfEmpty("?"))} [{d.Technology}]");
        if (_intByMethod.TryGetValue(methodId, out var ints))
            foreach (var i in ints.Where(i => i.Category != "config")) step.Integrations.Add($"{i.Id}: {i.Operation} {i.Technology}{(i.Target != null ? " → " + i.Target : "")}");
        if (_rulesByMethod.TryGetValue(methodId, out var rules))
            foreach (var r in rules) if (!step.Rules.Contains(r.Id)) step.Rules.Add(r.Id);

        if (!_out.TryGetValue(methodId, out var edges)) return;
        foreach (var e in edges)
        {
            if (e.Kind == "constructor" && _ctx.Methods.TryGetValue(e.Callee, out var cn) && NoiseStereotypes.Contains(cn.Stereotype)) continue;
            if (e.Kind == "constructor" && _ctx.Methods.TryGetValue(e.Callee, out var cn2) && cn2.Stereotype == "entity") continue;
            foreach (var target in Targets(e))
            {
                if (_ctx.Methods.TryGetValue(target, out var tn) && NoiseStereotypes.Contains(tn.Stereotype) && tn.Kind != "lambda") continue;
                var childInline = tn?.Kind is "lambda" or "local-function";
                Visit(flow, target, childInline ? depth : depth + 1, step.MethodId, childInline ? step : null, visited);
            }
        }
    }

    private IEnumerable<string> Targets(CallEdge e)
    {
        if (e.ResolvedTargets is not { Count: > 0 } impls) { yield return e.Callee; yield break; }
        // prefer DI-registered implementations of the interface
        var iface = _ctx.Methods.TryGetValue(e.Callee, out var m) && _ctx.Types.TryGetValue(m.ContainingType, out var it) ? StripGenerics(it.FullName) : null;
        if (iface != null && _diImpl.TryGetValue(iface, out var registered))
        {
            var chosen = impls.Where(i => _ctx.Methods.TryGetValue(i, out var im) && _ctx.Types.TryGetValue(im.ContainingType, out var t) && registered.Contains(StripGenerics(t.FullName))).ToList();
            if (chosen.Count > 0) { foreach (var c in chosen) yield return c; yield break; }
        }
        // non-test implementations only, cap fan-out
        foreach (var i in impls.Where(i => _ctx.Methods.TryGetValue(i, out var im) && im.Stereotype != "test").Take(4)) yield return i;
    }

    private void Summarize(BusinessFlow f)
    {
        f.Layers = f.Steps.Select(s => s.Stereotype).Where(s => s != "other").Distinct().ToList();
        foreach (var s in f.Steps)
        {
            foreach (var d in s.Data)
            {
                var m = Regex.Match(d, @"^(\S+) (.+?) \[");
                if (!m.Success) continue;
                foreach (var t in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!f.EntityOperations.TryGetValue(t, out var ops)) f.EntityOperations[t] = ops = new List<string>();
                    if (!ops.Contains(m.Groups[1].Value)) ops.Add(m.Groups[1].Value);
                }
            }
            foreach (var i in s.Integrations) if (!f.Integrations.Contains(i)) f.Integrations.Add(i);
            foreach (var r in s.Rules) if (!f.Rules.Contains(r)) f.Rules.Add(r);
        }
        f.Mutates = f.EntityOperations.Values.SelectMany(v => v).Any(o => o is "create" or "update" or "delete" or "write" or "exec" or "upsert")
                    || f.Integrations.Any(i => Regex.IsMatch(i, "publish|send|produce|enqueue|push", RegexOptions.IgnoreCase));
    }

    private string FlowName(EntryPoint ep) => ep.Kind switch
    {
        "http" or "minimal-api" or "razor-page" or "function" when ep.Route != null => $"{ep.HttpMethod} {ep.Route}",
        "winforms-event" or "wpf-event" or "webforms-event" or "wpf-command" => $"{ScreenName(ep.Screen)}: {Text.Humanize(ep.Trigger ?? ep.Name)}",
        "message-consumer" or "mediator-handler" => $"Handle {ep.Trigger}",
        _ => Text.Humanize(ep.Name),
    };

    private static string ScreenName(string? screen) => screen == null ? "" : Text.Humanize(screen[(screen.IndexOf(':') + 1)..]);

    public string CapabilityOf(EntryPoint ep)
    {
        var typeName = _ctx.Types.TryGetValue(ep.ContainingType, out var t) ? t.Name : ep.ContainingType;
        string Strip(string n) => Regex.Replace(n, "(Controller|Form|Window|Page|PageModel|Model|View|ViewModel|Hub|Service|Consumer|Handler|Job|Worker|Function|Functions|Endpoints?)$", "");
        switch (ep.Kind)
        {
            case "minimal-api" when ep.Route != null:
            case "function" when ep.Route != null:
            {
                var segs = ep.Route.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => !s.StartsWith("{") && !Regex.IsMatch(s, "^(api|v\\d+)$", RegexOptions.IgnoreCase)).ToList();
                return segs.Count > 0 ? Text.Humanize(char.ToUpperInvariant(segs[0][0]) + segs[0][1..]) : Text.Humanize(Strip(typeName));
            }
            case "mediator-handler" or "message-consumer" when ep.Trigger != null:
                return Text.Humanize(Regex.Replace(Strip(typeName), "(Create|Update|Delete|Get|Add|Remove|Submit|Cancel|Approve|Reject)", ""));
            default:
                var s = Strip(typeName);
                return Text.Humanize(s.Length == 0 ? typeName : s);
        }
    }
}

/// Derives user journeys from screens, navigation edges and the flows each screen triggers.
public sealed class JourneyBuilder
{
    private readonly AnalysisContext _ctx;
    public JourneyBuilder(AnalysisContext ctx) => _ctx = ctx;

    public void Run()
    {
        var jm = _ctx.Pack.Journeys;
        jm.Navigation = _ctx.NavigationEdges.ToList();
        foreach (var e in jm.Navigation)
            foreach (var id in new[] { e.From, e.To })
                if (id != "app:start" && jm.Screens.All(s => s.Id != id))
                    jm.Screens.Add(new Screen { Id = id, Name = Text.Humanize(id[(id.IndexOf(':') + 1)..]), Kind = "inferred" });

        var flowsByScreen = new Dictionary<string, List<BusinessFlow>>();
        foreach (var f in _ctx.Pack.Flows)
        {
            var ep = _ctx.Pack.EntryPoints.First(e => e.Id == f.EntryPointId);
            if (ep.Screen == null) continue;
            if (!flowsByScreen.TryGetValue(ep.Screen, out var l)) flowsByScreen[ep.Screen] = l = new();
            l.Add(f);
        }
        foreach (var s in jm.Screens)
            if (flowsByScreen.TryGetValue(s.Id, out var fl))
                foreach (var f in fl) if (!s.Actions.Contains(f.Name)) s.Actions.Add(f.Name);

        var adj = jm.Navigation.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToList());
        var indeg = jm.Navigation.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.Count());
        var starts = new List<string>();
        if (adj.TryGetValue("app:start", out var appStarts)) starts.AddRange(appStarts);
        starts.AddRange(jm.Screens.Where(s => Regex.IsMatch(s.Id, @"(?i)(home/index|/index$|:login|login/|:main|mainform|dashboard|default|page:/$)")).Select(s => s.Id));
        starts.AddRange(adj.Keys.Where(k => k != "app:start" && !indeg.ContainsKey(k)));
        starts = starts.Distinct().ToList();

        var seq = 0;
        var journeys = new List<UserJourney>();
        foreach (var start in starts)
        {
            var path = new List<string> { start };
            Dfs(start, path, adj, journeys, flowsByScreen, ref seq);
            if (journeys.Count >= _ctx.Options.MaxJourneys) break;
        }

        // screens with actions but no navigation in or out (typical WinForms dialogs, single-page tools)
        var covered = journeys.SelectMany(j => j.Steps.Select(s => s.Screen)).ToHashSet();
        foreach (var (screen, flows) in flowsByScreen)
        {
            if (covered.Contains(screen) || journeys.Count >= _ctx.Options.MaxJourneys) continue;
            var sc = jm.Screens.FirstOrDefault(s => s.Id == screen);
            journeys.Add(new UserJourney
            {
                Id = $"UJ-{++seq:0000}",
                Name = $"Use {sc?.Name ?? screen}",
                Channel = ChannelOf(screen),
                Actor = Actor(flows),
                Derivation = "screen-actions",
                Confidence = "medium",
                Steps = flows.Select((f, i) => new JourneyStep { Order = i + 1, Screen = screen, Action = f.Name, Flows = new() { f.Id } }).ToList(),
            });
        }

        // API-only capabilities: resource lifecycle journeys (create -> read -> update -> delete)
        var apiFlows = _ctx.Pack.Flows.Where(f => _ctx.Pack.EntryPoints.First(e => e.Id == f.EntryPointId) is { Screen: null, Kind: "http" or "minimal-api" or "function" or "grpc" or "wcf" or "asmx" or "signalr" }
                                               || _ctx.Pack.EntryPoints.First(e => e.Id == f.EntryPointId) is { Kind: "http" } ep2 && !jm.Screens.Any(s => s.Id == ep2.Screen && s.Kind != "inferred"))
            .GroupBy(f => f.Capability);
        foreach (var g in apiFlows)
        {
            if (journeys.Count >= _ctx.Options.MaxJourneys) break;
            int Rank(BusinessFlow f) => f.Name.StartsWith("POST") ? 0 : f.Name.StartsWith("GET") && !f.Name.Contains('{') ? 1 : f.Name.StartsWith("GET") ? 2 : f.Name.StartsWith("PUT") || f.Name.StartsWith("PATCH") ? 3 : f.Name.StartsWith("DELETE") ? 5 : 4;
            var ordered = g.OrderBy(Rank).ThenBy(f => f.Name).ToList();
            journeys.Add(new UserJourney
            {
                Id = $"UJ-{++seq:0000}",
                Name = $"{g.Key} lifecycle (API consumer)",
                Channel = "api",
                Actor = Actor(ordered),
                Derivation = "resource-lifecycle",
                Confidence = "low",
                Steps = ordered.Select((f, i) => new JourneyStep { Order = i + 1, Screen = "api:" + g.Key, Action = f.Name, Flows = new() { f.Id } }).ToList(),
            });
        }
        jm.Journeys = journeys;
    }

    private void Dfs(string node, List<string> path, Dictionary<string, List<string>> adj, List<UserJourney> outList, Dictionary<string, List<BusinessFlow>> flowsByScreen, ref int seq)
    {
        if (outList.Count >= _ctx.Options.MaxJourneys) return;
        var next = adj.TryGetValue(node, out var n) ? n.Where(x => !path.Contains(x)).ToList() : new List<string>();
        if (next.Count == 0 || path.Count >= 8)
        {
            var screens = path.Where(p => p != "app:start").ToList();
            if (screens.Count < 2 && !(screens.Count == 1 && flowsByScreen.ContainsKey(screens[0]))) return;
            var flows = screens.SelectMany(s => flowsByScreen.TryGetValue(s, out var l) ? l : new List<BusinessFlow>()).ToList();
            var names = screens.Select(s => _ctx.Pack.Journeys.Screens.FirstOrDefault(x => x.Id == s)?.Name ?? s).ToList();
            outList.Add(new UserJourney
            {
                Id = $"UJ-{++seq:0000}",
                Name = string.Join(" → ", names),
                Channel = ChannelOf(screens[0]),
                Actor = Actor(flows),
                Derivation = "navigation-graph",
                Confidence = flows.Count > 0 ? "medium" : "low",
                Steps = screens.Select((s, i) => new JourneyStep
                {
                    Order = i + 1,
                    Screen = s,
                    Action = i + 1 < screens.Count ? $"navigate to {names[i + 1]}" : null,
                    Flows = flowsByScreen.TryGetValue(s, out var l) ? l.Select(f => f.Id).ToList() : new(),
                }).ToList(),
            });
            return;
        }
        foreach (var x in next)
        {
            path.Add(x);
            Dfs(x, path, adj, outList, flowsByScreen, ref seq);
            path.RemoveAt(path.Count - 1);
            if (outList.Count >= _ctx.Options.MaxJourneys) return;
        }
    }

    private static string ChannelOf(string screenId) =>
        screenId.StartsWith("winform:") || screenId.StartsWith("wpf:") || screenId.StartsWith("vm:") ? "desktop" : screenId.StartsWith("api:") ? "api" : "web";

    private static string Actor(IEnumerable<BusinessFlow> flows)
    {
        var roles = flows.SelectMany(f => f.Roles).Where(r => !r.StartsWith("policy:")).Distinct().ToList();
        return roles.Count > 0 ? string.Join(" / ", roles) : "user";
    }
}

/// Groups flows into business capabilities (the unit that becomes a Spec Kit feature / BMAD epic).
public sealed class CapabilityBuilder
{
    private readonly AnalysisContext _ctx;
    public CapabilityBuilder(AnalysisContext ctx) => _ctx = ctx;

    public void Run()
    {
        var caps = new List<Capability>();
        var byName = _ctx.Pack.Flows.GroupBy(f => string.IsNullOrWhiteSpace(f.Capability) ? "General" : f.Capability);
        var rulesById = _ctx.Pack.Rules.ToDictionary(r => r.Id);
        foreach (var g in byName)
        {
            var flows = g.ToList();
            var cap = new Capability { Name = g.Key, Slug = Text.Slug(g.Key) };
            cap.Flows = flows.Select(f => f.Id).ToList();
            cap.EntryPoints = flows.Select(f => f.EntryPointId).ToList();
            cap.Rules = flows.SelectMany(f => f.Rules).Distinct().ToList();
            cap.Entities = flows.SelectMany(f => f.EntityOperations.Keys).Distinct().ToList();
            cap.Integrations = flows.SelectMany(f => f.Integrations).Select(i => i.Split(':')[0]).Distinct().ToList();
            cap.Types = flows.SelectMany(f => f.Steps.Select(s => s.Type)).Distinct().ToList();
            // type-level rules (declarative validation, parameters) for types this capability touches
            var typeIds = flows.SelectMany(f => f.Steps).Select(s => _ctx.Methods.TryGetValue(s.MethodId, out var m) ? m.ContainingType : null).Where(x => x != null).ToHashSet();
            foreach (var r in _ctx.Pack.Rules.Where(r => r.MethodId == null && r.ContainingType != null && typeIds.Contains(r.ContainingType)))
                if (!cap.Rules.Contains(r.Id)) cap.Rules.Add(r.Id);
            // declarative validation on entities the capability touches
            foreach (var r in _ctx.Pack.Rules.Where(r => r.Kind is "declarative-validation" or "fluent-validation" or "data-constraint" && r.Entities.Any(e => cap.Entities.Contains(e))))
                if (!cap.Rules.Contains(r.Id)) cap.Rules.Add(r.Id);
            cap.Journeys = _ctx.Pack.Journeys.Journeys.Where(j => j.Steps.Any(s => s.Flows.Any(f => cap.Flows.Contains(f)))).Select(j => j.Id).ToList();
            caps.Add(cap);
        }

        // priority: writes + rules + breadth
        var scored = caps.Select(c =>
        {
            var flows = _ctx.Pack.Flows.Where(f => c.Flows.Contains(f.Id)).ToList();
            var score = flows.Count + 2 * c.Rules.Count + 3 * flows.Count(f => f.Mutates) + c.Integrations.Count;
            return (c, score);
        }).OrderByDescending(x => x.score).ToList();
        for (var i = 0; i < scored.Count; i++)
        {
            var c = scored[i].c;
            c.Id = $"CAP-{i + 1:000}";
            c.Priority = i < Math.Ceiling(scored.Count * 0.3) ? "P1" : i < Math.Ceiling(scored.Count * 0.7) ? "P2" : "P3";
        }
        _ctx.Pack.Capabilities = scored.Select(x => x.c).ToList();
        foreach (var c in _ctx.Pack.Capabilities)
        {
            // ensure unique slugs
            var dup = _ctx.Pack.Capabilities.Where(o => o != c && o.Slug == c.Slug).ToList();
            foreach (var d in dup) d.Slug += "-" + d.Id.ToLowerInvariant();
        }
    }
}
