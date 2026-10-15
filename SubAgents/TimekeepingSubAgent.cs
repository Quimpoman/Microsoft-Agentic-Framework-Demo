using Agent.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.SubAgents
{

    /// <summary>
    /// Timekeeping specialist subagent.
    /// Handles: attendance records, leave requests, overtime logs.
    /// </summary>
    public class TimekeepingSubAgent : IHrisSubAgent
    {
        public ChatClientAgent Agent { get; }
        private readonly IDbContextFactory<EVOLVE_VRContext> _dbFactory;
        private readonly IChatClient _chatClient;

        public TimekeepingSubAgent(IChatClient chatClient, IDbContextFactory<EVOLVE_VRContext> dbFactory)
        {
            _dbFactory = dbFactory;
            _chatClient = chatClient;

            var (leaveTypes, status) = FetchLookupsAsync().GetAwaiter().GetResult();


            Agent = new ChatClientAgent(
                chatClient,
                name: "timekeeping_agent",
                instructions: """
                You are a Timekeeping specialist assistant. You ONLY answer questions about:
                - Attendance records (time-in, time-out, tardiness, absences, overtime)
                - Leave applications and requests, and their approval status
                - Official Business Trip (OB) applications and requests, and their approval status
                - Overtime applications and requests, and their approval status
                - Certificate of Attendance (COA) applications and requests, and their approval status

                When querying attendance:
                - Always use a date range — never query without one.
                - Default to current month if the user doesn't specify.
                - Use QueryAttendance with only the filters you know.
                - Present time values in readable format (e.g. 08:02 AM).
                - Summarize OT breakdown only if it contains non-zero values.

                When querying leave applications:
                - Employee ID is optional. If a name is provided instead, try to find the employee ID via hr_agent's EmployeeProfile tool.
                - Date filtering covers any application whose StartDate–EndDate
                  range overlaps with the requested period.
                - Pass the user's exact date expression — the tool resolves it.
                - Present results in a clear readable format.

                When querying Official Business trips (OB) applications:
                - Employee ID is optional. If a name is provided instead, try to find the employee ID via hr_agent's EmployeeProfile tool.
                - Date filtering covers any application whose StartDate–EndDate
                  range overlaps with the requested period.
                - Pass the user's exact date expression — the tool resolves it.
                - Present results in a clear readable format.

                When querying Overtime applications:
                - Employee ID is optional. If a name is provided instead, try to find the employee ID via hr_agent's EmployeeProfile tool.
                - Date filtering covers any application whose StartDate–EndDate
                  range overlaps with the requested period.
                - Pass the user's exact date expression — the tool resolves it.
                - Present results in a clear readable format.

                When querying COA applications:
                - Employee ID is optional. If a name is provided instead, try to find the employee ID via hr_agent's EmployeeProfile tool.
                - Date filtering covers any application whose StartDate–EndDate
                  range overlaps with the requested period.
                - Pass the user's exact date expression — the tool resolves it.
                - Present results in a clear readable format.

                Always use your tools to fetch real data — never guess or fabricate.
                If a question is outside your scope, say: "This is outside Timekeeping scope."
                """,
                tools:
                [
                AIFunctionFactory.Create(QueryAttendance),
                BuildLeaveRequestsTool(leaveTypes, status),
                BuildOfficialBusinessRequestsTool(status),
                BuildOvertimeRequestsTool(status),
                BuildCertificateOfAttendanceRequestsTool(status)
                ]);
        }

        // ── AI-powered date range resolver ────────────────────────────────────────
        private async Task<(DateTime start, DateTime end)> ResolveDateRangeWithAI(string? expression)
        {
            var today = DateTime.Today;
            var fallback = (
                new DateTime(today.Year, today.Month, 1),
                new DateTime(today.Year, today.Month,
                    DateTime.DaysInMonth(today.Year, today.Month)));

            // No expression — return current month immediately, no AI call needed
            if (string.IsNullOrWhiteSpace(expression))
                return fallback;

            // Looks like an explicit date range already — parse directly, skip AI
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
                // Single-turn AI call — no tools, no session, pure date parsing
                var prompt = $$"""
                    Today is {{today:yyyy-MM-dd}} ({{today:dddd}}).

                    Convert the following date expression into a JSON object with
                    "start" and "end" fields in yyyy-MM-dd format.

                    Rules:
                    - Interpret relative expressions like "this week", "last month",
                      "next week", "last 2 weeks", "yesterday", "ngayong buwan",
                      "isang linggo", "nakaraang buwan", etc.
                    - Week starts on Monday.
                    - For a single day like "yesterday" or "today", start and end are the same date.
                    - Return ONLY the JSON object. No explanation, no markdown, no extra text.

                    Expression: "{{expression}}"

                    Expected output format:
                    {"start":"2025-05-01","end":"2025-05-31"}
                    """;

                var response = await _chatClient.GetResponseAsync(
                     [new AIChatMessage(AIChatRole.User, prompt)],
                     new ChatOptions { MaxOutputTokens = 60 }
                 );

                var raw = response.Text?.Trim() ?? "";

                // Strip markdown fences if model wraps it anyway
                raw = raw.Replace("```json", "").Replace("```", "").Trim();

                var parsed = JsonSerializer.Deserialize<DateRangeResult>(raw);

                if (parsed is not null
                    && DateTime.TryParse(parsed.Start, out var start)
                    && DateTime.TryParse(parsed.End, out var end))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DateParser] '{expression}' → {start:yyyy-MM-dd} to {end:yyyy-MM-dd}");
                    return (start, end);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DateParser] Failed to parse '{expression}': {ex.Message}");
            }

            // Fallback — current month
            return fallback;
        }

        // ── Simple DTO for deserializing AI date response ─────────────────────────
        private record DateRangeResult(
            [property: JsonPropertyName("start")] string? Start,
            [property: JsonPropertyName("end")] string? End);

        // ── Lookups — fetched once at startup ────────────────────────────────────
        private async Task<(string leaveType, string status)> FetchLookupsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var leaveTypes = await db.ref_LeaveTypeCode
                .AsNoTracking()
                .Where(l => l.LeaveTypeDesc != null && l.LeaveTypeCode != 1)
                .Select(l => l.LeaveTypeDesc!.Trim())
                .Distinct()
                .OrderBy(l => l)
                .ToListAsync();

            var status = await db.ref_LeaveStatus
               .AsNoTracking()
               .Where(l => l.LeaveStatusDesc != null)
               .Select(l => l.LeaveStatusDesc!.Trim())
               .Distinct()
               .OrderBy(l => l)
               .ToListAsync();

            return (
               string.Join(", ", leaveTypes),
               string.Join(", ", status)
           );
        }


        // ── Tools ────────────────────────────────────────────────────────────────

        [Description(
       "Queries attendance records for one or more employees within a date range. " +
       "Returns time-in/out, tardiness, absences, leave type, and OT breakdown. " +
       "Always provide at least employeeID or lastname. Date range defaults to current month.")]
        private async Task<string> QueryAttendance(
       [Description("Filter by employee ID (exact match), e.g. 93.")]
        int? employeeID = null,

       [Description("Filter by last name (partial, case-insensitive).")]
        string? lastname = null,

       [Description(
            "Date range as a natural language expression. Examples: " +
            "'this week', 'last month', 'last 2 weeks', 'yesterday', " +
            "'2025-05-01 to 2025-05-31', 'ngayong buwan', 'isang linggo'. " +
            "Pass the user's exact words — the tool resolves it automatically. " +
            "Defaults to current month if not provided.")]
        string? dateRange = null,

       [Description("Filter only records where employee was tardy.")]
        bool? tardyOnly = null,

       [Description("Filter only records where employee was absent.")]
        bool? absentOnly = null,

       [Description("Filter only records with a specific leave type, e.g. 'Vacation Leave'.")]
        string? leaveType = null,

       [Description("Filter only records with overtime (any OT column > 0).")]
        bool? overtimeOnly = null,

       [Description("Maximum records to return. Defaults to 60.")]
        int top = 60)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // ← AI resolves whatever the user typed
            var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);

            IQueryable<AI_vwGet_TrnAttendance> query = db.AI_vwGet_TrnAttendance.AsNoTracking();

            if (employeeID.HasValue)
                query = query.Where(a => a.EmployeeID.ToString() == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(lastname))
                query = query.Where(a =>
                    EF.Functions.Like(a.Lastname!, $"%{lastname.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(dateRange))
            query = query.Where(a =>
                a.TranDate.HasValue &&
                a.TranDate.Value.Date >= startDate.Date &&
                a.TranDate.Value.Date <= endDate.Date);

            if (tardyOnly == true)
                query = query.Where(a => a.Tardy > 0);

            if (absentOnly == true)
                query = query.Where(a => a.Absence > 0);

            if (!string.IsNullOrWhiteSpace(leaveType))
                query = query.Where(a =>
                    EF.Functions.Like(a.LeaveTypeDesc!, $"%{leaveType.Trim()}%"));

            if (overtimeOnly == true)
                query = query.Where(a =>
                a.RegularOT > 0 ||
                a.RegularDayNight > 0 ||
                a.RegularOTNight > 0 ||

                a.RestDay > 0 ||
                a.RestDayOT > 0 ||
                a.RestDayNight > 0 ||
                a.RestDayOTNight > 0 ||

                a.LegalHoliday > 0 ||
                a.LegalHolidayOT > 0 ||
                a.LegalHolidayNight > 0 ||
                a.LegalHolidayOTNight > 0 ||

                a.RestDayLegalHoliday > 0 ||
                a.RestDayLegalOT > 0 ||
                a.RestDayLegalNight > 0 ||
                a.RestDayLegalOTNight > 0 ||

                a.SpecialHoliday > 0 ||
                a.SpecialHolidayOT > 0 ||
                a.SpecialHolidayNight > 0 ||
                a.SpecialHolidayOTNight > 0 ||

                a.RestDaySpecialHoliday > 0 ||
                a.RestDaySpecialOT > 0 ||
                a.RestDaySpecialNight > 0 ||
                a.RestDaySpecialOTNight > 0
                );

            var results = await query
                .OrderBy(a => a.TranDate)
                .ThenBy(a => a.Lastname)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = "No attendance records found.",
                    dateRange = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}"
                });

            var projected = results.Select(a => new
            {
                a.EmployeeID,
                Name = $"{a.Lastname}, {a.Firstname}",
                Date = a.TranDate?.ToString("yyyy-MM-dd"),
                Day = a.DayOfTheWeek,
                TimeIn = a.TimeIN?.ToString("hh:mm tt") ?? "—",
                TimeOut = a.TimeOUT?.ToString("hh:mm tt") ?? "—",
                Shift = a.ShiftTimeDesc,
                TotalHours = a.TotalHr,
                Tardy = a.Tardy > 0 ? a.Tardy : (double?)null,
                Absent = a.Absence > 0 ? a.Absence : (double?)null,
                Undertime = a.Undertime > 0 ? a.Undertime : (double?)null,
                Leave = a.LeaveTypeDesc,
                Remarks = a.Remarks,

                // Regular
                RegularOT = a.RegularOT > 0 ? a.RegularOT : (double?)null,
                RegularDayNight = a.RegularDayNight > 0 ? a.RegularDayNight : (double?)null,
                RegularOTNight = a.RegularOTNight > 0 ? a.RegularOTNight : (double?)null,

                // Rest Day
                RestDay = a.RestDay > 0 ? a.RestDay : (double?)null,
                RestDayOT = a.RestDayOT > 0 ? a.RestDayOT : (double?)null,
                RestDayNight = a.RestDayNight > 0 ? a.RestDayNight : (double?)null,
                RestDayOTNight = a.RestDayOTNight > 0 ? a.RestDayOTNight : (double?)null,

                // Legal Holiday
                LegalHoliday = a.LegalHoliday > 0 ? a.LegalHoliday : (double?)null,
                LegalHolidayOT = a.LegalHolidayOT > 0 ? a.LegalHolidayOT : (double?)null,
                LegalHolidayNight = a.LegalHolidayNight > 0 ? a.LegalHolidayNight : (double?)null,
                LegalHolidayOTNight = a.LegalHolidayOTNight > 0 ? a.LegalHolidayOTNight : (double?)null,

                // Rest Day Legal Holiday
                RestDayLegalHoliday = a.RestDayLegalHoliday > 0 ? a.RestDayLegalHoliday : (double?)null,
                RestDayLegalOT = a.RestDayLegalOT > 0 ? a.RestDayLegalOT : (double?)null,
                RestDayLegalNight = a.RestDayLegalNight > 0 ? a.RestDayLegalNight : (double?)null,
                RestDayLegalOTNight = a.RestDayLegalOTNight > 0 ? a.RestDayLegalOTNight : (double?)null,

                // Special Holiday
                SpecialHoliday = a.SpecialHoliday > 0 ? a.SpecialHoliday : (double?)null,
                SpecialHolidayOT = a.SpecialHolidayOT > 0 ? a.SpecialHolidayOT : (double?)null,
                SpecialHolidayNight = a.SpecialHolidayNight > 0 ? a.SpecialHolidayNight : (double?)null,
                SpecialHolidayOTNight = a.SpecialHolidayOTNight > 0 ? a.SpecialHolidayOTNight : (double?)null,

                // Rest Day Special Holiday
                RestDaySpecialHoliday = a.RestDaySpecialHoliday > 0 ? a.RestDaySpecialHoliday : (double?)null,
                RestDaySpecialOT = a.RestDaySpecialOT > 0 ? a.RestDaySpecialOT : (double?)null,
                RestDaySpecialNight = a.RestDaySpecialNight > 0 ? a.RestDaySpecialNight : (double?)null,
                RestDaySpecialOTNight = a.RestDaySpecialOTNight > 0 ? a.RestDaySpecialOTNight : (double?)null

            });

            return JsonSerializer.Serialize(projected, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        // ── GetLeaveRequests tool builder — dynamic leaveType description ─────────
        private AIFunction BuildLeaveRequestsTool(string leaveTypes, string status)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["leaveType"] =
                    $"Filter by leave type (exact match). Valid values: {leaveTypes}. " +
                    $"Leave null to return all leave types.",
                ["status"] =
                $"Filter by leave status (exact match). Valid values: {status}. " +
                $"Leave null to return all status.",
            };

            return AIFunctionFactory.Create(
                method: typeof(TimekeepingSubAgent).GetMethod(
                    nameof(GetLeaveRequests),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "GetLeaveRequests",
                    Description =
                        "Gets leave applications filed by an employee. " +
                        "Filters by date range (any application overlapping the period), " +
                        "leave type, and approval status. " +
                        "Returns leave type, dates, duration, reason, status, and remarks.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }

        // ── Tool: GetLeaveRequests ────────────────────────────────────────────────
        private async Task<string> GetLeaveRequests(
            [Description("Optional: The employee ID (exact match), e.g. 92274.")]
        int? employeeID = null,

        string? leaveType = null,   // ← description injected dynamically

            [Description(
            "Date range to filter applications. Any leave whose StartDate–EndDate " +
            "overlaps this period is included. Accepts natural language like " +
            "'this month', 'last week', '2026-04-01 to 2026-04-30'. " +
            "Defaults to current month if not provided.")]
        string? dateRange = null,

        string? status = null, // ← description injected dynamically

            [Description("Maximum records to return. Defaults to 50.")]
        int top = 50)
        {
            //if (employeeID is null)
            //    return JsonSerializer.Serialize(new
            //    {
            //        message = "Please provide an employee ID to query leave requests."
            //    });

            await using var db = await _dbFactory.CreateDbContextAsync();

            // Resolve date range — reuses AI-powered resolver
            var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);

            IQueryable<vwGet_TrnLeaveApplication> query = db.vwGet_TrnLeaveApplication
                .AsNoTracking();

            // Overlap logic: application overlaps the range if
            // its StartDate <= rangeEnd AND its EndDate >= rangeStart
            if (employeeID.HasValue)
                query = query.Where(e => e.OLDNumber == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(startDate.ToString()) && !string.IsNullOrWhiteSpace(endDate.ToString()))
                query = query.Where(l =>
                    l.StartDate.HasValue && l.EndDate.HasValue &&
                    l.StartDate.Value.Date <= endDate.Date &&
                    l.EndDate.Value.Date >= startDate.Date);

            if (!string.IsNullOrWhiteSpace(leaveType))
                query = query.Where(l =>
                    l.LeaveTypeDesc!.Trim() == leaveType.Trim());

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(l =>
                    l.LeaveStatusDesc!.Trim() == status.Trim());

            var results = await query
                .Where(x => x.LeaveTypeCode != 1)
                .OrderByDescending(l => l.StartDate)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = $"No leave applications found for Employee ID {employeeID}.",
                    employeeID,
                    dateRange = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                    status,
                    leaveType
                });

            var projected = results.Select(l => new
            {
                LeaveType = l.LeaveTypeDesc,
                StartDate = l.StartDate?.ToString("yyyy-MM-dd"),
                EndDate = l.EndDate?.ToString("yyyy-MM-dd"),
                Duration = l.Duration,
                Reason = l.Reason,
                Destination = l.Destination,
                Status = l.LeaveStatusDesc,
                Remarks = l.Remarks,
                FiledBy = l.CreatedByAndDate,
                LastEditedDate = l.EditedDate?.ToString("yyyy-MM-dd"),
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

        private AIFunction BuildOfficialBusinessRequestsTool(string status)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {            
                ["status"] =
                $"Filter by official business trip status (exact match). Valid values: {status}. " +
                $"status null to return all status types.",
            };

            return AIFunctionFactory.Create(
                method: typeof(TimekeepingSubAgent).GetMethod(
                    nameof(GetOfficialBusinessRequests),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "GetOfficialBusinessRequests",
                    Description =
                        "Gets official business trip (OB) applications filed by an employee. " +
                        "Filters by date range (any application overlapping the period), " +
                        "official busines, and approval status. " +
                        "Returns dates, duration, reason, status, and remarks.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }

       

        // ── Tool: GetOfficialBusinessRequests ────────────────────────────────────────────────
        private async Task<string> GetOfficialBusinessRequests(
            [Description("Optional: The employee ID (exact match), e.g. 92274.")]
        int? employeeID = null,


        string? dateRange = null,

        string? status = null, // ← description injected dynamically

            [Description("Maximum records to return. Defaults to 50.")]
        int top = 50)
        {
            //if (employeeID is null)
            //    return JsonSerializer.Serialize(new
            //    {
            //        message = "Please provide an employee ID to query official business trip requests."
            //    });

            await using var db = await _dbFactory.CreateDbContextAsync();

            // Resolve date range — reuses AI-powered resolver
            var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);

            IQueryable<vwGet_TrnLeaveApplication> query = db.vwGet_TrnLeaveApplication
                .AsNoTracking();

            // Overlap logic: application overlaps the range if
            // its StartDate <= rangeEnd AND its EndDate >= rangeStart

            if (employeeID.HasValue)
                query = query.Where(e => e.OLDNumber == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(startDate.ToString()) && !string.IsNullOrWhiteSpace(endDate.ToString()))
            query = query.Where(l =>
                l.StartDate.HasValue && l.EndDate.HasValue &&
                l.StartDate.Value.Date <= endDate.Date &&
                l.EndDate.Value.Date >= startDate.Date);

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(l =>
                    l.LeaveStatusDesc!.Trim() == status.Trim());

            var results = await query
                .Where(x => x.LeaveTypeCode == 1)
                .OrderByDescending(l => l.StartDate)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = $"No official business trip applications found for Employee ID {employeeID}.",
                    employeeID,
                    dateRange = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                    status                  
                });

            var projected = results.Select(l => new
            {
                StartDate = l.StartDate?.ToString("yyyy-MM-dd"),
                EndDate = l.EndDate?.ToString("yyyy-MM-dd"),
                TimeStart = l.TimeStart?.ToString("hh:mm tt") ?? "—",
                TimeEnd = l.TimeEnd?.ToString("hh:mm tt") ?? "—",
                Duration = l.Duration,
                Reason = l.Reason,
                Destination = l.Destination,
                Status = l.LeaveStatusDesc,
                Remarks = l.Remarks,
                FiledBy = l.CreatedByAndDate,
                LastEditedDate = l.EditedDate?.ToString("yyyy-MM-dd"),
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

        private AIFunction BuildOvertimeRequestsTool(string status)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["status"] =
                $"Filter by overtime request status (exact match). Valid values: {status}. " +
                $"status null to return all status types.",
            };

            return AIFunctionFactory.Create(
                method: typeof(TimekeepingSubAgent).GetMethod(
                    nameof(GetOvertimeRequest),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "GetOvertimeRequest",
                    Description =
                        "Gets Overtime request applications filed by an employee. " +
                        "Filters by date range (any application overlapping the period), " +
                        "and approval status. " +
                        "Returns dates, duration, reason, status, and remarks.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }

        // ── Tool: GetOvertimeRequests ────────────────────────────────────────────────
        private async Task<string> GetOvertimeRequest(
            [Description("Optional: The employee ID (exact match), e.g. 92274.")]
        int? employeeID = null,


        string? dateRange = null,

        string? status = null, // ← description injected dynamically

            [Description("Maximum records to return. Defaults to 50.")]
        int top = 50)
        {
            //if (employeeID is null)
            //    return JsonSerializer.Serialize(new
            //    {
            //        message = "Please provide an employee ID to query official business trip requests."
            //    });

            await using var db = await _dbFactory.CreateDbContextAsync();

            // Resolve date range — reuses AI-powered resolver
            var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);

            IQueryable<vwGet_TrnOvertimeFiling> query = db.vwGet_TrnOvertimeFiling
                .AsNoTracking();

            // Overlap logic: application overlaps the range if
            // its StartDate <= rangeEnd AND its EndDate >= rangeStart

            if (employeeID.HasValue)
                query = query.Where(e => e.OLDNumber == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(startDate.ToString()) && !string.IsNullOrWhiteSpace(endDate.ToString()))
                query = query.Where(l =>
                    l.TransactionDate.HasValue &&
                    l.TransactionDate.Value.Date <= endDate.Date &&
                    l.TransactionDate.Value.Date >= startDate.Date);

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(l =>
                    l.LeaveStatusDesc!.Trim() == status.Trim());

            var results = await query         
                .OrderByDescending(l => l.TransactionDate)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = $"No overtime applications found for Employee ID {employeeID}.",
                    employeeID,
                    dateRange = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                    status
                });

            var projected = results.Select(l => new
            {
                Name = l.EmployeeName,
                TransactionDate = l.TransactionDate?.ToString("yyyy-MM-dd"),
                TotalHours = l.TotalHrs?.ToString(),
                TimeStart = l.TimeStart?.ToString("hh:mm tt") ?? "—",
                TimeEnd = l.TimeEnd?.ToString("hh:mm tt") ?? "—",
                WorkDescription = l.WorkDescription,
                Status = l.LeaveStatusDesc,
                Remarks = l.Remarks,
                FiledBy = l.CreatedByAndDate,
                LastEditedDate = l.EditedDate?.ToString("yyyy-MM-dd"),
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

        private AIFunction BuildCertificateOfAttendanceRequestsTool(string status)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["status"] =
                $"Filter by certificate of attendance (COA) request status (exact match). Valid values: {status}. " +
                $"status null to return all status types.",
            };

            return AIFunctionFactory.Create(
                method: typeof(TimekeepingSubAgent).GetMethod(
                    nameof(GetCertificateOfAttendanceRequest),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "GetCertificateOfAttendanceRequest",
                    Description =
                        "Gets 'Certificate of attendance' (COA) request applications filed by an employee. " +
                        "Filters by date range (any application overlapping the period), " +
                        "and approval status. " +
                        "Returns dates, duration, reason, status, and remarks.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }

        // ── Tool: GetOvertimeRequests ────────────────────────────────────────────────
        private async Task<string> GetCertificateOfAttendanceRequest(
            [Description("Optional: The employee ID (exact match), e.g. 92274.")]
        int? employeeID = null,


        string? dateRange = null,

        string? status = null, // ← description injected dynamically

            [Description("Maximum records to return. Defaults to 50.")]
        int top = 50)
        {
            //if (employeeID is null)
            //    return JsonSerializer.Serialize(new
            //    {
            //        message = "Please provide an employee ID to query official business trip requests."
            //    });

            await using var db = await _dbFactory.CreateDbContextAsync();

            // Resolve date range — reuses AI-powered resolver
            var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);

            IQueryable<vwGet_TrnCOA> query = db.vwGet_TrnCOA
                .AsNoTracking();

            // Overlap logic: application overlaps the range if
            // its StartDate <= rangeEnd AND its EndDate >= rangeStart

            if (employeeID.HasValue)
                query = query.Where(e => e.OLDNumber == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(startDate.ToString()) && !string.IsNullOrWhiteSpace(endDate.ToString()))
                query = query.Where(l =>
                    l.StartDate.HasValue &&
                    l.StartDate.Value.Date <= endDate.Date &&
                    l.StartDate.Value.Date >= startDate.Date);

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(l =>
                    l.LeaveStatusDesc!.Trim() == status.Trim());

            var results = await query
                .OrderByDescending(l => l.StartDate)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = $"No certificate of attendance applications found for Employee ID {employeeID}.",
                    employeeID,
                    dateRange = $"{startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}",
                    status
                });

            var projected = results.Select(l => new
            {
                Name = l.EmployeeName,
                TransactionDate = l.StartDate?.ToString("yyyy-MM-dd"),
                TimeStart = l.TimeStart?.ToString("hh:mm tt") ?? "—",
                TimeEnd = l.TimeEnd?.ToString("hh:mm tt") ?? "—",
                Description = l.Description,
                Reason = l.Reason,
                Status = l.LeaveStatusDesc,
                Remarks = l.Remarks,
                FiledBy = l.CreatedByAndDate,
                LastEditedDate = l.EditedDate?.ToString("yyyy-MM-dd"),
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
    }
}
