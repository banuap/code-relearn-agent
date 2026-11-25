using System.Text.RegularExpressions;

namespace ReLearn.Analysis;

/// A pattern matched against the fully-qualified containing type of a called API (generic arity stripped)
/// and optionally the method name. First match wins, so list specific patterns before generic ones.
public sealed record ApiPattern(string TypePrefix, string? MethodRegex, string Category, string Technology, string Direction, string Operation = "", int TargetArg = -1)
{
    private readonly Regex? _rx = MethodRegex == null ? null : new Regex("^(" + MethodRegex + ")$", RegexOptions.Compiled);
    public bool Matches(string typeName, string method) =>
        (typeName == TypePrefix || typeName.StartsWith(TypePrefix + ".", StringComparison.Ordinal) || (TypePrefix.EndsWith("*") && typeName.StartsWith(TypePrefix[..^1], StringComparison.Ordinal)))
        && (_rx == null || _rx.IsMatch(method));
}

public static class Classifiers
{
    // ------------------------------------------------------------------ integrations
    public static readonly ApiPattern[] Integrations =
    {
        // ---- cache
        new("Microsoft.Extensions.Caching.Memory.IMemoryCache", null, "cache", "in-memory", "read-write", "", 0),
        new("Microsoft.Extensions.Caching.Memory.CacheExtensions", null, "cache", "in-memory", "read-write", "", 1),
        new("Microsoft.Extensions.Caching.Memory.MemoryCache", null, "cache", "in-memory", "read-write", "", 0),
        new("Microsoft.Extensions.Caching.Distributed.IDistributedCache", null, "cache", "distributed", "read-write", "", 0),
        new("Microsoft.Extensions.Caching.Distributed.DistributedCacheExtensions", null, "cache", "distributed", "read-write", "", 1),
        new("Microsoft.Extensions.Caching.Hybrid.HybridCache", null, "cache", "hybrid", "read-write", "", 0),
        new("StackExchange.Redis.ISubscriber", "Publish.*|Subscribe.*", "pubsub", "redis", "outbound", "", 0),
        new("StackExchange.Redis.IDatabase", null, "cache", "redis", "read-write", "", 0),
        new("StackExchange.Redis.IDatabaseAsync", null, "cache", "redis", "read-write", "", 0),
        new("StackExchange.Redis.ConnectionMultiplexer", "Connect.*", "cache", "redis", "connect", "", 0),
        new("System.Runtime.Caching.ObjectCache", null, "cache", "system-runtime-caching", "read-write", "", 0),
        new("System.Runtime.Caching.MemoryCache", null, "cache", "system-runtime-caching", "read-write", "", 0),
        new("System.Web.Caching.Cache", null, "cache", "aspnet-cache", "read-write", "", 0),
        new("Enyim.Caching", null, "cache", "memcached", "read-write"),
        new("NCache*", null, "cache", "ncache", "read-write"),

        // ---- streaming / Kafka
        new("Confluent.Kafka.IProducer", "Produce.*|Flush|InitTransactions|CommitTransaction", "stream", "kafka", "outbound", "publish", 0),
        new("Confluent.Kafka.IConsumer", "Consume|Subscribe|Assign|Commit.*", "stream", "kafka", "inbound", "consume", 0),
        new("Confluent.Kafka.ProducerBuilder", "Build", "stream", "kafka", "config", "producer"),
        new("Confluent.Kafka.ConsumerBuilder", "Build", "stream", "kafka", "config", "consumer"),
        new("Confluent.Kafka", null, "stream", "kafka", "other"),
        new("Azure.Messaging.EventHubs", null, "stream", "azure-event-hubs", "read-write"),
        new("Amazon.Kinesis", null, "stream", "aws-kinesis", "read-write"),

        // ---- queues / service buses
        new("RabbitMQ.Client.IModel", "BasicPublish.*", "queue", "rabbitmq", "outbound", "publish", 0),
        new("RabbitMQ.Client.IChannel", "BasicPublish.*", "queue", "rabbitmq", "outbound", "publish", 0),
        new("RabbitMQ.Client.IModelExensions", "BasicPublish.*", "queue", "rabbitmq", "outbound", "publish", 1),
        new("RabbitMQ.Client.IModel", "BasicConsume.*|BasicGet.*", "queue", "rabbitmq", "inbound", "consume", 0),
        new("RabbitMQ.Client.IChannel", "BasicConsume.*|BasicGet.*", "queue", "rabbitmq", "inbound", "consume", 0),
        new("RabbitMQ.Client.IModel", "QueueDeclare.*|ExchangeDeclare.*|QueueBind.*", "queue", "rabbitmq", "config", "declare", 0),
        new("RabbitMQ.Client", null, "queue", "rabbitmq", "other"),
        new("Azure.Messaging.ServiceBus.ServiceBusClient", "CreateSender|CreateReceiver|CreateProcessor", "queue", "azure-service-bus", "config", "connect", 0),
        new("Azure.Messaging.ServiceBus.ServiceBusSender", null, "queue", "azure-service-bus", "outbound", "send"),
        new("Azure.Messaging.ServiceBus.ServiceBusReceiver", null, "queue", "azure-service-bus", "inbound", "receive"),
        new("Azure.Messaging.ServiceBus.ServiceBusProcessor", null, "queue", "azure-service-bus", "inbound", "process"),
        new("Azure.Messaging.ServiceBus", null, "queue", "azure-service-bus", "other"),
        new("Microsoft.Azure.ServiceBus", null, "queue", "azure-service-bus-legacy", "other"),
        new("Azure.Storage.Queues.QueueClient", null, "queue", "azure-storage-queue", "read-write"),
        new("System.Messaging.MessageQueue", "Send|BeginSend", "queue", "msmq", "outbound", "send"),
        new("System.Messaging.MessageQueue", "Receive.*|Peek.*|BeginReceive|BeginPeek", "queue", "msmq", "inbound", "receive"),
        new("System.Messaging", null, "queue", "msmq", "other"),
        new("Amazon.SQS.IAmazonSQS", "SendMessage.*", "queue", "aws-sqs", "outbound", "send"),
        new("Amazon.SQS.IAmazonSQS", "ReceiveMessage.*|DeleteMessage.*", "queue", "aws-sqs", "inbound", "receive"),
        new("Amazon.SimpleNotificationService", null, "pubsub", "aws-sns", "outbound", "publish"),
        new("MassTransit.IPublishEndpoint", "Publish", "queue", "masstransit", "outbound", "publish"),
        new("MassTransit.ISendEndpoint", "Send", "queue", "masstransit", "outbound", "send"),
        new("MassTransit.IBus", "Publish|Send", "queue", "masstransit", "outbound", "publish"),
        new("MassTransit.ConsumeContext", "Publish|Send|Respond.*", "queue", "masstransit", "outbound", "publish"),
        new("NServiceBus.IMessageSession", "Send|Publish", "queue", "nservicebus", "outbound", "send"),
        new("NServiceBus.IMessageHandlerContext", "Send|Publish|Reply", "queue", "nservicebus", "outbound", "send"),
        new("IBM.WMQ", null, "queue", "ibm-mq", "read-write"),
        new("Apache.NMS", null, "queue", "activemq", "read-write"),
        new("MediatR.IMediator", "Send|Publish", "mediator", "mediatr", "in-process", "dispatch"),
        new("MediatR.ISender", "Send", "mediator", "mediatr", "in-process", "dispatch"),
        new("MediatR.IPublisher", "Publish", "mediator", "mediatr", "in-process", "dispatch"),

        // ---- HTTP / RPC
        new("System.Net.Http.HttpClient", "Get.*|Post.*|Put.*|Patch.*|Delete.*|Send.*", "http", "httpclient", "outbound", "", 0),
        new("System.Net.Http.Json.HttpClientJsonExtensions", null, "http", "httpclient", "outbound", "", 1),
        new("System.Net.WebClient", "Download.*|Upload.*|Open.*", "http", "webclient", "outbound", "", 0),
        new("System.Net.HttpWebRequest", "GetResponse.*|GetRequestStream.*", "http", "httpwebrequest", "outbound"),
        new("System.Net.WebRequest", "Create.*", "http", "httpwebrequest", "outbound", "", 0),
        new("RestSharp", "Execute.*|Get.*|Post.*|Put.*|Delete.*", "http", "restsharp", "outbound"),
        new("Flurl", null, "http", "flurl", "outbound"),
        new("System.ServiceModel.ClientBase", null, "soap", "wcf-client", "outbound"),
        new("System.ServiceModel.ChannelFactory", null, "soap", "wcf-client", "outbound"),
        new("System.Web.Services.Protocols.SoapHttpClientProtocol", null, "soap", "asmx-client", "outbound"),
        new("Grpc.Net.Client.GrpcChannel", null, "grpc", "grpc-client", "outbound"),
        new("Grpc.Core.ClientBase", null, "grpc", "grpc-client", "outbound"),
        new("Microsoft.AspNetCore.SignalR.IClientProxy", "SendAsync|SendCoreAsync", "realtime", "signalr", "outbound", "push"),
        new("Microsoft.AspNetCore.SignalR.ClientProxyExtensions", "SendAsync", "realtime", "signalr", "outbound", "push", 1),
        new("Microsoft.AspNetCore.SignalR.Client.HubConnection", null, "realtime", "signalr-client", "outbound"),

        // ---- email / notifications
        new("System.Net.Mail.SmtpClient", "Send.*", "email", "smtp", "outbound", "send"),
        new("MailKit", null, "email", "mailkit", "outbound", "send"),
        new("SendGrid", null, "email", "sendgrid", "outbound", "send"),
        new("Twilio", null, "notification", "twilio", "outbound", "send"),

        // ---- storage / files
        new("Azure.Storage.Blobs", null, "storage", "azure-blob", "read-write"),
        new("Amazon.S3", null, "storage", "aws-s3", "read-write"),
        new("System.IO.File", null, "file", "filesystem", "read-write", "", 0),
        new("System.IO.Directory", null, "file", "filesystem", "read-write", "", 0),
        new("System.IO.FileStream", ".ctor", "file", "filesystem", "read-write", "open", 0),
        new("System.IO.StreamWriter", ".ctor", "file", "filesystem", "write", "open", 0),
        new("System.IO.StreamReader", ".ctor", "file", "filesystem", "read", "open", 0),
        new("System.IO.FileSystemWatcher", null, "file", "file-watcher", "inbound"),
        new("System.IO.Ports.SerialPort", null, "platform", "serial-port", "read-write"),

        // ---- scheduling / background
        new("Hangfire.BackgroundJob", null, "scheduler", "hangfire", "outbound", "enqueue"),
        new("Hangfire.RecurringJob", null, "scheduler", "hangfire", "config", "recurring"),
        new("Hangfire.IBackgroundJobClient", null, "scheduler", "hangfire", "outbound", "enqueue"),
        new("Quartz.IScheduler", null, "scheduler", "quartz", "config", "schedule"),
        new("System.Threading.Timer", ".ctor", "scheduler", "timer", "config", "timer"),
        new("System.Timers.Timer", null, "scheduler", "timer", "config", "timer"),

        // ---- configuration / identity / search / platform
        new("Microsoft.Extensions.Configuration.IConfiguration", "get_Item|GetSection", "config", "ms-configuration", "read", "read", 0),
        new("Microsoft.Extensions.Configuration.ConfigurationExtensions", "GetConnectionString|GetSection", "config", "ms-configuration", "read", "read", 1),
        new("Microsoft.Extensions.Configuration.ConfigurationBinder", "GetValue|Get|Bind", "config", "ms-configuration", "read", "read", 1),
        new("Microsoft.Extensions.Options.IOptions", null, "config", "options-pattern", "read", "read"),
        new("System.Configuration.ConfigurationManager", null, "config", "app-config", "read", "read"),
        new("System.Collections.Specialized.NameValueCollection", "get_Item", "config", "app-config", "read", "read", 0),
        new("Microsoft.AspNetCore.Identity.UserManager", null, "identity", "aspnet-identity", "read-write"),
        new("Microsoft.AspNetCore.Identity.SignInManager", null, "identity", "aspnet-identity", "read-write"),
        new("System.Web.Security.Membership", null, "identity", "aspnet-membership", "read-write"),
        new("System.Web.Security.FormsAuthentication", null, "identity", "forms-auth", "read-write"),
        new("System.DirectoryServices", null, "identity", "active-directory", "read"),
        new("Microsoft.Identity.Client", null, "identity", "msal", "outbound"),
        new("Nest", null, "search", "elasticsearch", "read-write"),
        new("Elastic.Clients.Elasticsearch", null, "search", "elasticsearch", "read-write"),
        new("Microsoft.Win32.Registry", null, "platform", "windows-registry", "read-write"),
        new("Microsoft.Win32.RegistryKey", null, "platform", "windows-registry", "read-write"),
        new("System.Diagnostics.EventLog", null, "platform", "windows-eventlog", "write"),
        new("System.Management", null, "platform", "wmi", "read"),
        new("System.Diagnostics.Process", "Start", "platform", "process", "outbound", "start", 0),
        new("System.Runtime.InteropServices.Marshal", null, "platform", "com-interop", "other"),
        new("System.Printing", null, "platform", "printing", "outbound"),
        new("System.Drawing.Printing", null, "platform", "printing", "outbound"),
        new("Microsoft.Office.Interop", null, "platform", "office-interop", "outbound"),
        new("CrystalDecisions", null, "platform", "crystal-reports", "outbound"),
        new("Microsoft.Reporting", null, "platform", "ssrs-reportviewer", "outbound"),
    };

    // ------------------------------------------------------------------ data access
    public static readonly ApiPattern[] Data =
    {
        // EF Core / EF6 / LINQ providers
        new("Microsoft.EntityFrameworkCore.DbContext", "SaveChanges.*", "data", "ef-core", "write", "write"),
        new("Microsoft.EntityFrameworkCore.DbSet", "Add.*|Attach.*", "data", "ef-core", "write", "create"),
        new("Microsoft.EntityFrameworkCore.DbSet", "Update.*", "data", "ef-core", "write", "update"),
        new("Microsoft.EntityFrameworkCore.DbSet", "Remove.*", "data", "ef-core", "write", "delete"),
        new("Microsoft.EntityFrameworkCore.DbSet", "Find.*|AsQueryable|AsNoTracking", "data", "ef-core", "read", "read"),
        new("Microsoft.EntityFrameworkCore.RelationalQueryableExtensions", "FromSql.*", "data", "ef-core", "read", "read", 1),
        new("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", "ExecuteSql.*|SqlQuery.*", "data", "ef-core", "write", "exec", 1),
        new("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ExecuteDelete.*", "data", "ef-core", "write", "delete"),
        new("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", "ExecuteUpdate.*", "data", "ef-core", "write", "update"),
        new("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", null, "data", "ef-core", "read", "read"),
        new("System.Data.Entity.DbContext", "SaveChanges.*", "data", "ef6", "write", "write"),
        new("System.Data.Entity.DbSet", "Add.*|Attach.*", "data", "ef6", "write", "create"),
        new("System.Data.Entity.DbSet", "Remove.*", "data", "ef6", "write", "delete"),
        new("System.Data.Entity.DbSet", "Find.*|SqlQuery", "data", "ef6", "read", "read", 0),
        new("System.Data.Entity.Database", "ExecuteSqlCommand.*|SqlQuery.*", "data", "ef6", "write", "exec", 0),
        new("System.Data.Linq.DataContext", null, "data", "linq2sql", "read-write"),
        new("System.Data.Linq.Table", "InsertOnSubmit|InsertAllOnSubmit", "data", "linq2sql", "write", "create"),
        new("System.Data.Linq.Table", "DeleteOnSubmit|DeleteAllOnSubmit", "data", "linq2sql", "write", "delete"),
        // Dapper
        new("Dapper.SqlMapper", "Query.*|Read.*", "data", "dapper", "read", "read", 1),
        new("Dapper.SqlMapper", "Execute.*", "data", "dapper", "write", "exec", 1),
        // ADO.NET (any provider)
        new("System.Data.SqlClient.SqlCommand", "ExecuteReader.*|ExecuteScalar.*|ExecuteXmlReader.*", "data", "ado.net", "read", "read"),
        new("Microsoft.Data.SqlClient.SqlCommand", "ExecuteReader.*|ExecuteScalar.*|ExecuteXmlReader.*", "data", "ado.net", "read", "read"),
        new("System.Data.SqlClient.SqlCommand", "ExecuteNonQuery.*", "data", "ado.net", "write", "exec"),
        new("Microsoft.Data.SqlClient.SqlCommand", "ExecuteNonQuery.*", "data", "ado.net", "write", "exec"),
        new("System.Data.Common.DbCommand", "ExecuteReader.*|ExecuteScalar.*", "data", "ado.net", "read", "read"),
        new("System.Data.Common.DbCommand", "ExecuteNonQuery.*", "data", "ado.net", "write", "exec"),
        new("System.Data.IDbCommand", "ExecuteReader.*|ExecuteScalar.*", "data", "ado.net", "read", "read"),
        new("System.Data.IDbCommand", "ExecuteNonQuery.*", "data", "ado.net", "write", "exec"),
        new("System.Data.Common.DbDataAdapter", "Fill.*", "data", "ado.net", "read", "read"),
        new("System.Data.Common.DbDataAdapter", "Update.*", "data", "ado.net", "write", "update"),
        new("System.Data.SqlClient.SqlBulkCopy", "WriteToServer.*", "data", "ado.net", "write", "create"),
        new("Microsoft.Data.SqlClient.SqlBulkCopy", "WriteToServer.*", "data", "ado.net", "write", "create"),
        new("Oracle.ManagedDataAccess.Client.OracleCommand", "Execute.*", "data", "ado.net-oracle", "read-write", "exec"),
        new("Oracle.DataAccess.Client.OracleCommand", "Execute.*", "data", "ado.net-oracle", "read-write", "exec"),
        new("Npgsql.NpgsqlCommand", "Execute.*", "data", "ado.net-postgres", "read-write", "exec"),
        new("MySql.Data.MySqlClient.MySqlCommand", "Execute.*", "data", "ado.net-mysql", "read-write", "exec"),
        new("System.Data.OleDb.OleDbCommand", "Execute.*", "data", "ado.net-oledb", "read-write", "exec"),
        new("System.Data.Odbc.OdbcCommand", "Execute.*", "data", "ado.net-odbc", "read-write", "exec"),
        new("Microsoft.Data.Sqlite.SqliteCommand", "Execute.*", "data", "ado.net-sqlite", "read-write", "exec"),
        new("System.Data.SQLite.SQLiteCommand", "Execute.*", "data", "ado.net-sqlite", "read-write", "exec"),
        new("Microsoft.Practices.EnterpriseLibrary.Data.Database", "Execute.*|LoadDataSet", "data", "entlib-data", "read-write", "exec", 0),
        // NHibernate
        new("NHibernate.ISession", "Save.*|Persist.*", "data", "nhibernate", "write", "create"),
        new("NHibernate.ISession", "Update.*|Merge.*|SaveOrUpdate.*", "data", "nhibernate", "write", "update"),
        new("NHibernate.ISession", "Delete.*", "data", "nhibernate", "write", "delete"),
        new("NHibernate.ISession", "Get.*|Load.*|Query.*|QueryOver.*|CreateCriteria.*", "data", "nhibernate", "read", "read"),
        new("NHibernate.ISession", "CreateSQLQuery|CreateQuery", "data", "nhibernate", "read", "read", 0),
        // NoSQL
        new("MongoDB.Driver.IMongoCollection", "Find.*|Aggregate.*|Count.*", "data", "mongodb", "read", "read"),
        new("MongoDB.Driver.IMongoCollection", "Insert.*", "data", "mongodb", "write", "create"),
        new("MongoDB.Driver.IMongoCollection", "Update.*|Replace.*|FindOneAndUpdate.*", "data", "mongodb", "write", "update"),
        new("MongoDB.Driver.IMongoCollection", "Delete.*", "data", "mongodb", "write", "delete"),
        new("MongoDB.Driver.IMongoCollectionExtensions", null, "data", "mongodb", "read-write", "", 0),
        new("Microsoft.Azure.Cosmos.Container", null, "data", "cosmosdb", "read-write"),
        new("Amazon.DynamoDBv2", null, "data", "dynamodb", "read-write"),
    };

    public static ApiPattern? Match(ApiPattern[] table, string typeName, string method)
    {
        foreach (var p in table)
            if (p.Matches(typeName, method)) return p;
        return null;
    }

    // ------------------------------------------------------------------ type stereotypes
    public static readonly string[] ControllerBases =
    {
        "Microsoft.AspNetCore.Mvc.ControllerBase", "Microsoft.AspNetCore.Mvc.Controller",
        "System.Web.Mvc.Controller", "System.Web.Http.ApiController", "System.Web.Mvc.ControllerBase",
    };
    public static readonly string[] DbContextBases =
    {
        "Microsoft.EntityFrameworkCore.DbContext", "System.Data.Entity.DbContext", "System.Data.Objects.ObjectContext",
        "System.Data.Entity.Core.Objects.ObjectContext", "System.Data.Linq.DataContext",
        "Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext",
    };
    public static readonly string[] FormBases = { "System.Windows.Forms.Form" };
    public static readonly string[] UserControlBases = { "System.Windows.Forms.UserControl" };
    public static readonly string[] WpfWindowBases = { "System.Windows.Window" };
    public static readonly string[] WpfPageBases = { "System.Windows.Controls.Page", "System.Windows.Controls.UserControl" };
    public static readonly string[] WebFormsPageBases = { "System.Web.UI.Page" };
    public static readonly string[] WebFormsControlBases = { "System.Web.UI.UserControl", "System.Web.UI.MasterPage" };
    public static readonly string[] RazorPageBases = { "Microsoft.AspNetCore.Mvc.RazorPages.PageModel" };
    public static readonly string[] BlazorBases = { "Microsoft.AspNetCore.Components.ComponentBase" };
    public static readonly string[] HubBases = { "Microsoft.AspNetCore.SignalR.Hub", "Microsoft.AspNet.SignalR.Hub" };
    public static readonly string[] HostedBases = { "Microsoft.Extensions.Hosting.BackgroundService" };
    public static readonly string[] HostedInterfaces = { "Microsoft.Extensions.Hosting.IHostedService" };
    public static readonly string[] ServiceBaseWin = { "System.ServiceProcess.ServiceBase" };
    public static readonly string[] MigrationBases = { "Microsoft.EntityFrameworkCore.Migrations.Migration", "System.Data.Entity.Migrations.DbMigration", "FluentMigrator.Migration" };
    public static readonly string[] FluentValidatorBases = { "FluentValidation.AbstractValidator" };
    public static readonly string[] AutoMapperProfile = { "AutoMapper.Profile" };
    public static readonly string[] JobInterfaces = { "Quartz.IJob" };
    public static readonly string[] ConsumerInterfaces = { "MassTransit.IConsumer", "NServiceBus.IHandleMessages", "Rebus.Handlers.IHandleMessages" };
    public static readonly string[] MediatorHandlers = { "MediatR.IRequestHandler", "MediatR.INotificationHandler" };
    public static readonly string[] EntityConfigInterfaces = { "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration" };
    public static readonly string[] ViewModelBases = { "CommunityToolkit.Mvvm.ComponentModel.ObservableObject", "Prism.Mvvm.BindableBase", "GalaSoft.MvvmLight.ViewModelBase", "Caliburn.Micro.Screen", "ReactiveUI.ReactiveObject" };
    public static readonly string[] NotifyInterfaces = { "System.ComponentModel.INotifyPropertyChanged" };

    public static readonly HashSet<string> HttpVerbAttributes = new() { "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions" };
    public static readonly HashSet<string> ErrorResultMethods = new()
    {
        "BadRequest", "NotFound", "Conflict", "UnprocessableEntity", "Forbid", "Unauthorized", "ValidationProblem", "Problem",
        "StatusCode", "HttpNotFound", "BadRequestObjectResult", "NotFoundObjectResult", "Challenge",
    };

    // ------------------------------------------------------------------ SQL
    private static readonly Regex SqlStart = new(@"^\s*(\(\s*)?(SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|WITH|CREATE|ALTER|DROP|TRUNCATE|CALL|BEGIN|DECLARE|SET\s+NOCOUNT)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlShape = new(@"\b(FROM|INTO|SET|WHERE|VALUES|JOIN|TABLE|PROCEDURE|PROC)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TableRx = new(@"\b(?:FROM|JOIN|INTO|UPDATE|MERGE\s+INTO|MERGE|DELETE\s+FROM|TABLE|USING)\s+((?:\[?[\w$#@]+\]?\.){0,2}\[?[#\w$@]+\]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExecRx = new(@"\b(?:EXEC|EXECUTE|CALL)\s+(?:@\w+\s*=\s*)?((?:\[?\w+\]?\.){0,2}\[?\w+\]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProcNameOnly = new(@"^\s*((?:\[?\w+\]?\.){0,2}\[?\w+\]?)\s*$", RegexOptions.Compiled);
    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "SELECT", "WHERE", "SET", "VALUES", "AS", "ON", "(", "dbo", "OUTPUT", "TOP", "DISTINCT", "INNER", "LEFT", "RIGHT", "OUTER", "CROSS", "NOLOCK", "WITH" };

    public static bool LooksLikeSql(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length >= 8 && SqlStart.IsMatch(s) && (SqlShape.IsMatch(s) || ExecRx.IsMatch(s));

    public static string SqlOperation(string sql)
    {
        var m = SqlStart.Match(sql);
        var kw = m.Success ? m.Groups[2].Value.ToUpperInvariant() : "";
        if (kw == "WITH")
        {
            if (Regex.IsMatch(sql, @"\)\s*(INSERT)\b", RegexOptions.IgnoreCase)) return "create";
            if (Regex.IsMatch(sql, @"\)\s*(UPDATE)\b", RegexOptions.IgnoreCase)) return "update";
            if (Regex.IsMatch(sql, @"\)\s*(DELETE)\b", RegexOptions.IgnoreCase)) return "delete";
            return "read";
        }
        return kw switch
        {
            "SELECT" => "read",
            "INSERT" => "create",
            "UPDATE" => "update",
            "DELETE" or "TRUNCATE" => "delete",
            "MERGE" => "upsert",
            "EXEC" or "EXECUTE" or "CALL" => "exec",
            "CREATE" or "ALTER" or "DROP" => "ddl",
            _ => "unknown",
        };
    }

    public static List<string> SqlTables(string sql)
    {
        var set = new List<string>();
        foreach (Match m in TableRx.Matches(sql))
        {
            var t = Clean(m.Groups[1].Value);
            if (t.Length == 0 || SqlKeywords.Contains(t) || t.StartsWith("@") || t.StartsWith("{")) continue;
            if (!set.Contains(t, StringComparer.OrdinalIgnoreCase)) set.Add(t);
        }
        return set;
    }

    public static List<string> SqlProcedures(string sql, bool commandIsProc)
    {
        var list = new List<string>();
        foreach (Match m in ExecRx.Matches(sql)) list.Add(Clean(m.Groups[1].Value));
        if (commandIsProc || (list.Count == 0 && !sql.Contains(' ')))
        {
            var m = ProcNameOnly.Match(sql);
            if (m.Success) list.Add(Clean(m.Groups[1].Value));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Clean(string s) => s.Replace("[", "").Replace("]", "").Trim().TrimEnd(';', ',', ')');
}
