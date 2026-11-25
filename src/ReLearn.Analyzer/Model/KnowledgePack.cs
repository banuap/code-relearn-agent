namespace ReLearn.Model;

// ---------------------------------------------------------------------------
// The "knowledge pack" is the language-neutral contract between the ReLearn
// (extraction) agent and any Modernization agent. Every item carries a stable
// id and a source Location so downstream specs can cite legacy evidence.
// ---------------------------------------------------------------------------

public sealed class Location
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int EndLine { get; set; }
    public override string ToString() => $"{File}:{Line}";
}

public sealed class Manifest
{
    public string Tool { get; set; } = "relearn";
    public string ToolVersion { get; set; } = "0.1.0";
    public string SchemaVersion { get; set; } = "1.0";
    public string GeneratedAtUtc { get; set; } = DateTime.UtcNow.ToString("o");
    public string Input { get; set; } = "";
    public string RootDirectory { get; set; } = "";
    public string LoadMode { get; set; } = "msbuild"; // msbuild | adhoc
    public string SemanticFidelity { get; set; } = "full"; // full | partial
    public List<string> Languages { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, string> Files { get; set; } = new();
}

// ----------------------------- Inventory / dependencies ---------------------

public sealed class ProjectInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Language { get; set; } = "";
    public string Kind { get; set; } = "library"; // web, api, desktop-winforms, desktop-wpf, webforms, console, worker, function, library, test
    public string? Sdk { get; set; }
    public List<string> TargetFrameworks { get; set; } = new();
    public string OutputKind { get; set; } = "";
    public List<string> ProjectReferences { get; set; } = new();
    public List<PackageRef> Packages { get; set; } = new();
    public List<string> AssemblyReferences { get; set; } = new();
    public int Documents { get; set; }
    public int LinesOfCode { get; set; }
    public bool IsTest { get; set; }
}

public sealed class PackageRef
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
}

public sealed class Edge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Weight { get; set; } = 1;
}

public sealed class TypeNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Kind { get; set; } = "";          // class, interface, struct, record, enum, delegate
    public string Stereotype { get; set; } = "other";
    public string Project { get; set; } = "";
    public string Accessibility { get; set; } = "";
    public bool IsAbstract { get; set; }
    public bool IsStatic { get; set; }
    public bool IsPartial { get; set; }
    public string? BaseType { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public List<string> Attributes { get; set; } = new();
    public List<string> EnumMembers { get; set; } = new();
    public Location? Location { get; set; }
    public List<string> Files { get; set; } = new();
}

public sealed class DiRegistration
{
    public string Service { get; set; } = "";
    public string? Implementation { get; set; }
    public string Lifetime { get; set; } = "";
    public string Container { get; set; } = "Microsoft.Extensions.DependencyInjection";
    public Location? Location { get; set; }
}

public sealed class DependencyMap
{
    public List<ProjectInfo> Projects { get; set; } = new();
    public List<Edge> ProjectEdges { get; set; } = new();
    public List<Edge> PackageEdges { get; set; } = new();
    public List<TypeNode> Types { get; set; } = new();
    public List<Edge> TypeEdges { get; set; } = new();
    public List<Edge> NamespaceEdges { get; set; } = new();
    public List<Edge> ExternalNamespaceUsage { get; set; } = new();
    public List<DiRegistration> DiRegistrations { get; set; } = new();
    public List<Edge> Layers { get; set; } = new(); // stereotype -> stereotype aggregated
}

// ----------------------------- Call graph -----------------------------------

public sealed class MethodNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ContainingType { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Kind { get; set; } = "method"; // method, ctor, accessor, operator, lambda, local-function, top-level
    public string Accessibility { get; set; } = "";
    public bool IsAsync { get; set; }
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public string ReturnType { get; set; } = "";
    public List<string> Parameters { get; set; } = new();
    public List<string> Attributes { get; set; } = new();
    public string Project { get; set; } = "";
    public string Stereotype { get; set; } = "other";
    public int CyclomaticComplexity { get; set; } = 1;
    public int LinesOfCode { get; set; }
    public Location? Location { get; set; }
}

public sealed class CallEdge
{
    public string Caller { get; set; } = "";
    public string Callee { get; set; } = "";
    public string Kind { get; set; } = "direct"; // direct, virtual, interface, constructor, delegate, event, lambda
    public int Line { get; set; }
    public int Order { get; set; }
    public List<string>? ResolvedTargets { get; set; }
}

public sealed class ExternalCall
{
    public string Caller { get; set; } = "";
    public string Api { get; set; } = "";
    public string Namespace { get; set; } = "";
    public int Count { get; set; }
}

public sealed class CallGraph
{
    public List<MethodNode> Nodes { get; set; } = new();
    public List<CallEdge> Edges { get; set; } = new();
    public List<ExternalCall> ExternalCalls { get; set; } = new();
    public Dictionary<string, List<string>> Implementations { get; set; } = new();
}

// ----------------------------- Entry points ---------------------------------

public sealed class EntryPoint
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = ""; // http, minimal-api, razor-page, mvc-view, webforms-page, winforms-event, wpf-event, wpf-command, signalr, grpc, wcf, asmx, hosted-service, scheduled-job, message-consumer, function, console-main, mediator-handler
    public string Name { get; set; } = "";
    public string? HttpMethod { get; set; }
    public string? Route { get; set; }
    public string? Trigger { get; set; }
    public string MethodId { get; set; } = "";
    public string ContainingType { get; set; } = "";
    public string? Screen { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<string> Policies { get; set; } = new();
    public bool AllowAnonymous { get; set; }
    public bool RequiresAuth { get; set; }
    public List<string> Parameters { get; set; } = new();
    public string? ReturnType { get; set; }
    public Location? Location { get; set; }
}

// ----------------------------- Data -----------------------------------------

public sealed class EntityProperty
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Nullable { get; set; }
    public bool IsKey { get; set; }
    public string? Column { get; set; }
    public List<string> Constraints { get; set; } = new();
    public string? NavigationTo { get; set; }
    public bool IsCollection { get; set; }
}

public sealed class EntityModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Table { get; set; }
    public string? Schema { get; set; }
    public List<string> DbContexts { get; set; } = new();
    public List<EntityProperty> Properties { get; set; } = new();
    public Location? Location { get; set; }
}

public sealed class DbContextModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Technology { get; set; } = "";
    public List<string> EntitySets { get; set; } = new();
    public Location? Location { get; set; }
}

public sealed class DataOperation
{
    public string MethodId { get; set; } = "";
    public string Technology { get; set; } = "";   // ef-core, ef6, ado.net, dapper, nhibernate, mongodb, cosmos, linq2sql, sql-literal
    public string Operation { get; set; } = "";    // read, create, update, delete, write, exec, ddl, unknown
    public List<string> Targets { get; set; } = new(); // entities / tables / procs
    public string? Sql { get; set; }
    public bool DynamicSql { get; set; }
    public string Api { get; set; } = "";
    public Location? Location { get; set; }
}

public sealed class DbObject
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";  // table, view, procedure, function, trigger, index, sequence, type
    public string Source { get; set; } = ""; // sql-file, sql-literal, ef-mapping, migration, edmx, inferred
    public List<string> Columns { get; set; } = new();
    public List<string> ReferencedBy { get; set; } = new();
    public Location? Location { get; set; }
}

public sealed class ConnectionInfo
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Redacted { get; set; } = "";
    public string File { get; set; } = "";
}

public sealed class ModelConfig
{
    public string? Entity { get; set; }
    public string Text { get; set; } = "";
    public Location? Location { get; set; }
}

public sealed class DataModel
{
    public List<DbContextModel> Contexts { get; set; } = new();
    public List<EntityModel> Entities { get; set; } = new();
    public List<DbObject> DbObjects { get; set; } = new();
    public List<DataOperation> Operations { get; set; } = new();
    public List<ConnectionInfo> Connections { get; set; } = new();
    public List<ModelConfig> ModelConfiguration { get; set; } = new();
}

// ----------------------------- Integrations ---------------------------------

public sealed class IntegrationPoint
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";   // cache, queue, stream, pubsub, http, soap, grpc, email, storage, file, scheduler, realtime, config, identity, search, platform, mediator
    public string Technology { get; set; } = "";  // redis, kafka, rabbitmq, ...
    public string Direction { get; set; } = "";   // outbound, inbound, read, write
    public string Operation { get; set; } = "";
    public string? Target { get; set; }           // topic / queue / key / url / config key
    public string Api { get; set; } = "";
    public string MethodId { get; set; } = "";
    public Location? Location { get; set; }
}

public sealed class ConfigEntry
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public string File { get; set; } = "";
}

public sealed class IntegrationModel
{
    public List<IntegrationPoint> Points { get; set; } = new();
    public List<ConfigEntry> Configuration { get; set; } = new();
    public List<string> ServiceEndpoints { get; set; } = new(); // WCF endpoints, base URLs found in config
}

// ----------------------------- Business rules -------------------------------

public sealed class BusinessRule
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";       // validation, guard, authorization, decision, decision-table, state-transition, calculation, declarative-validation, fluent-validation, parameter, data-constraint
    public string Title { get; set; } = "";
    public string? Condition { get; set; }
    public string? Outcome { get; set; }
    public string? Message { get; set; }
    public string? Expression { get; set; }
    public List<string[]>? Table { get; set; }   // decision-table rows [when, then]
    public List<string> Literals { get; set; } = new();
    public List<string> Entities { get; set; } = new();
    public string? MethodId { get; set; }
    public string? ContainingType { get; set; }
    public string Confidence { get; set; } = "medium"; // high, medium, low
    public bool Trivial { get; set; }
    public string? BusinessStatement { get; set; }  // filled by the agent's enrichment pass
    public List<string>? Questions { get; set; }
    public Location? Location { get; set; }
}

// ----------------------------- Flows / journeys -----------------------------

public sealed class FlowStep
{
    public int Order { get; set; }
    public int Depth { get; set; }
    public string MethodId { get; set; } = "";
    public string Method { get; set; } = "";
    public string Type { get; set; } = "";
    public string Stereotype { get; set; } = "";
    public string? CalledFrom { get; set; }
    public List<string> Data { get; set; } = new();
    public List<string> Integrations { get; set; } = new();
    public List<string> Rules { get; set; } = new();
}

public sealed class BusinessFlow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Capability { get; set; } = "";
    public string EntryPointId { get; set; } = "";
    public string Trigger { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public List<FlowStep> Steps { get; set; } = new();
    public List<string> Layers { get; set; } = new();
    public Dictionary<string, List<string>> EntityOperations { get; set; } = new();
    public List<string> Integrations { get; set; } = new();
    public List<string> Rules { get; set; } = new();
    public bool Truncated { get; set; }
    public bool Mutates { get; set; }
    public string? Goal { get; set; }               // enrichment
    public string? BusinessName { get; set; }       // enrichment
}

public sealed class JourneyStep
{
    public int Order { get; set; }
    public string Screen { get; set; } = "";
    public string? Action { get; set; }
    public List<string> Flows { get; set; } = new();
}

public sealed class UserJourney
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Channel { get; set; } = ""; // web, desktop, api
    public string Actor { get; set; } = "user";
    public string Derivation { get; set; } = ""; // navigation-graph, resource-lifecycle, screen-actions
    public string Confidence { get; set; } = "medium";
    public List<JourneyStep> Steps { get; set; } = new();
}

public sealed class Screen
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ""; // mvc-view, razor-page, webforms, winform, wpf-window, wpf-page, blazor
    public string? Type { get; set; }
    public List<string> Actions { get; set; } = new();
    public List<string> Fields { get; set; } = new();
    public Location? Location { get; set; }
}

public sealed class JourneyModel
{
    public List<Screen> Screens { get; set; } = new();
    public List<Edge> Navigation { get; set; } = new();
    public List<UserJourney> Journeys { get; set; } = new();
}

public sealed class Capability
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Priority { get; set; } = "P2";
    public string? Description { get; set; }        // enrichment
    public List<string> EntryPoints { get; set; } = new();
    public List<string> Flows { get; set; } = new();
    public List<string> Rules { get; set; } = new();
    public List<string> Entities { get; set; } = new();
    public List<string> Integrations { get; set; } = new();
    public List<string> Journeys { get; set; } = new();
    public List<string> Types { get; set; } = new();
}

/// Written by the ReLearn agent after reading the pack (business-language layer). Optional.
public sealed class Enrichment
{
    public Dictionary<string, CapabilityEnrichment> Capabilities { get; set; } = new();
    public Dictionary<string, RuleEnrichment> Rules { get; set; } = new();
    public Dictionary<string, FlowEnrichment> Flows { get; set; } = new();
    public Dictionary<string, JourneyEnrichment> Journeys { get; set; } = new();
}
public sealed class CapabilityEnrichment { public string? Name { get; set; } public string? Description { get; set; } public string? Priority { get; set; } }
public sealed class RuleEnrichment { public string? Statement { get; set; } public string? Title { get; set; } public string? Confidence { get; set; } public bool Drop { get; set; } public string? DropReason { get; set; } public List<string>? Questions { get; set; } }
public sealed class FlowEnrichment { public string? Name { get; set; } public string? Goal { get; set; } public string? Actor { get; set; } }
public sealed class JourneyEnrichment { public string? Name { get; set; } public string? Actor { get; set; } }

// ----------------------------- Whole pack -----------------------------------

public sealed class KnowledgePack
{
    public Manifest Manifest { get; set; } = new();
    public DependencyMap Dependencies { get; set; } = new();
    public CallGraph CallGraph { get; set; } = new();
    public List<EntryPoint> EntryPoints { get; set; } = new();
    public DataModel Data { get; set; } = new();
    public IntegrationModel Integrations { get; set; } = new();
    public List<BusinessRule> Rules { get; set; } = new();
    public List<BusinessFlow> Flows { get; set; } = new();
    public JourneyModel Journeys { get; set; } = new();
    public List<Capability> Capabilities { get; set; } = new();
}
