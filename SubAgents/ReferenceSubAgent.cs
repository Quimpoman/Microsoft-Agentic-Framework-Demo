using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
//using Agent.Data;
using Agent.Models;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;



namespace Agent.SubAgents
{

    public class ReferenceSubAgent : IHrisSubAgent
    {
        public ChatClientAgent Agent { get; }
        private readonly IDbContextFactory<EVOLVE_VRContext> _dbFactory;
        private readonly IChatClient _chatClient;

        public ReferenceSubAgent(
            IChatClient chatClient,
            IDbContextFactory<EVOLVE_VRContext> dbFactory)
        {
            _dbFactory = dbFactory;
            _chatClient = chatClient;

            var (modules, actions) = FetchLookupsAsync().GetAwaiter().GetResult();

            Agent = new ChatClientAgent(
                chatClient,
                name: "audit_agent",
                instructions: """
                You are an Audit Trail specialist. You answer questions about
                system activity logs — who did what, when, and on which module.

                You serve two types of users:
                - Employees checking their own activity (provide employeeID filter)
                - HR/Admins checking any employee's activity (may omit employeeID
                  or query by module/action across all employees)

                When querying:
                - Always use a date range — default to current month if not specified.
                - Pass the user's exact date expression — the tool resolves it.
                - For broad queries without an employeeID, warn that results may be
                  large and apply a reasonable Top limit.
                - Never fabricate log entries. Only relay what the tool returns.
                - Present results chronologically, clearly showing who, what, and when.
                """,
                tools:
                [
                    BuildAuditTool(modules, actions),
                ]);
            //            .WithDebugLogging("audit_agent")
        }

        // ── Lookups — fetched once at startup ─────────────────────────────────────
        private async Task<(string modules, string actions)> FetchLookupsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var modules = await db.AI_vwGet_AuditTrail
                .AsNoTracking()
                .Where(a => a.Modules != null)
                .Select(a => a.Modules!.Trim())
                .Distinct().OrderBy(m => m)
                .ToListAsync();

            var actions = await db.AI_vwGet_AuditTrail
                .AsNoTracking()
                .Where(a => a.Did != null)
                .Select(a => a.Did!.Trim())
                .Distinct().OrderBy(a => a)
                .Take(50)   // cap — actions can be verbose
                .ToListAsync();

            return (
                string.Join(", ", modules),
                string.Join(", ", actions)
            );
        }

        // ── Tool builder ──────────────────────────────────────────────────────────
        private AIFunction BuildAuditTool(string modules, string actions)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["module"] =
                    $"Filter by system module (exact match). Valid values: {modules}. " +
                    $"Leave null to return all modules.",

                ["action"] =
                    $"Filter by action performed (partial match). Known actions include: {actions}. " +
                    $"Leave null to return all actions.",
            };

            return AIFunctionFactory.Create(
                method: typeof(ReferenceSubAgent).GetMethod(
                    nameof(QueryAuditTrail),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "QueryAuditTrail",
                    Description =
                        "Queries the system audit trail — activity logs showing who performed " +
                        "what action, on which module, and when. " +
                        "Supports filtering by employee, module, action, and date range. " +
                        "EmployeeID is optional — omit for admin-level queries across all employees.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }

        // ── Tool: QueryAuditTrail ─────────────────────────────────────────────────
        private async Task<string> QueryAuditTrail(
            [Description(
            "Filter by employee ID. Provide for self-service queries. " +
            "Leave null for admin queries spanning all employees.")]
        int? employeeID = null,

            string? module = null,   // ← description injected dynamically
            string? action = null,   // ← description injected dynamically

            [Description(
            "Date range for audit log entries. Accepts natural language: " +
            "'today', 'this week', 'last month', 'ngayong araw', etc. " +
            "Defaults to current month if not provided.")]
        string? dateRange = null,

            [Description(
            "Keyword to search in Remarks (partial, case-insensitive). " +
            "e.g. 'LeaveType 2', 'Successful', 'Failed'.")]
        string? remarksKeyword = null,

            [Description(
            "Maximum records to return. Defaults to 50. " +
            "Use a lower value for broad admin queries to avoid overwhelming results.")]
        int top = 50)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);

            IQueryable<AI_vwGet_AuditTrail> query = db.AI_vwGet_AuditTrail.AsNoTracking();

            // ── Filters ───────────────────────────────────────────────────────────
            if (employeeID.HasValue)
                query = query.Where(a => a.EmployeeID == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(module))
                query = query.Where(a =>
                    a.Modules!.Trim() == module.Trim());

            if (!string.IsNullOrWhiteSpace(action))
                query = query.Where(a =>
                    EF.Functions.Like(a.Did!, $"%{action.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(remarksKeyword))
                query = query.Where(a =>
                    EF.Functions.Like(a.Remarks!, $"%{remarksKeyword.Trim()}%"));

            // Date range on Trandate
            query = query.Where(a =>
                a.Trandate.HasValue &&
                a.Trandate.Value >= startDate &&
                a.Trandate.Value < endDate.AddDays(1));   // inclusive end

            var results = await query
                .OrderByDescending(a => a.Trandate)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = "No audit trail records found for the given filters.",
                    employeeID,
                    module,
                    dateRange = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}"
                });

            var projected = results.Select(a => new
            {
                a.EmployeeID,
                Module = a.Modules,
                Action = a.Did,
                Timestamp = a.Trandate?.ToString("yyyy-MM-dd HH:mm:ss"),
                Reference = a.referenceNo,
                Remarks = a.Remarks
            });

            return JsonSerializer.Serialize(new
            {
                EmployeeID = employeeID,
                DateRange = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                Count = results.Count,
                Records = projected
            },
            new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        // ── AI date resolver — same implementation as TimekeepingSubAgent ─────────
        private async Task<(DateTime start, DateTime end)> ResolveDateRangeWithAI(
            string? expression)
        {
            var today = DateTime.Today;
            var fallback = (
                new DateTime(today.Year, today.Month, 1),
                new DateTime(today.Year, today.Month,
                    DateTime.DaysInMonth(today.Year, today.Month)));

            if (string.IsNullOrWhiteSpace(expression))
                return fallback;

            if (expression.Contains(" to ") || expression.Contains(" - "))
            {
                var sep = expression.Contains(" to ") ? " to " : " - ";
                var parts = expression.Split(sep, StringSplitOptions.TrimEntries);
                if (parts.Length == 2
                    && DateTime.TryParse(parts[0], out var s)
                    && DateTime.TryParse(parts[1], out var e))
                    return (s, e);
            }

            try
            {
                var prompt = $$"""
                Today is {{today:yyyy-MM-dd}} ({{today:dddd}}).

                Convert the following date expression into a JSON object with
                "start" and "end" fields in yyyy-MM-dd format.

                Rules:
                - Interpret relative expressions like "this week", "last month",
                  "next week", "last 2 weeks", "yesterday", "ngayong buwan",
                  "isang linggo", "nakaraang buwan", etc.
                - Week starts on Monday.
                - For a single day, start and end are the same date.
                - Return ONLY the JSON object. No explanation, no markdown.

                Expression: "{{expression}}"

                Expected output format:
                {"start":"2025-05-01","end":"2025-05-31"}
                """;

                var response = await _chatClient.GetResponseAsync(
                    [new AIChatMessage(AIChatRole.User, prompt)],
                    new ChatOptions { MaxOutputTokens = 60 });

                var raw = response.Text?.Trim() ?? "";
                raw = raw.Replace("```json", "").Replace("```", "").Trim();

                var parsed = JsonSerializer.Deserialize<DateRangeResult>(raw);

                if (parsed is not null
                    && DateTime.TryParse(parsed.Start, out var start)
                    && DateTime.TryParse(parsed.End, out var end))
                    return (start, end);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DateParser] Failed: {ex.Message}");
            }

            return fallback;
        }

        private record DateRangeResult(
            [property: JsonPropertyName("start")] string? Start,
            [property: JsonPropertyName("end")] string? End);
    }
}
