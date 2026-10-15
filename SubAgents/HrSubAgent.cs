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

namespace Agent.SubAgents
{

    /// <summary>
    /// HR specialist subagent.
    /// Handles: employee profiles, leave balances, company policies.
    /// </summary>
    public class HrSubAgent : IHrisSubAgent
    {
        public ChatClientAgent Agent { get; }


        private readonly IDbContextFactory<EVOLVE_VRContext> _dbContext;



        public HrSubAgent(IChatClient chatClient, IDbContextFactory<EVOLVE_VRContext> dbContext)
        {
            _dbContext = dbContext;
            var (statuses, genders, positions, civilstatuses, religions, department, leavetype) = FetchLookupsAsync().GetAwaiter().GetResult();


            Agent = new ChatClientAgent(
                chatClient,
                name: "hr_agent",
                instructions: """
                You are an HR specialist assistant. You ONLY answer questions about:
                - Employee profiles and personal data (employment status, gender, position, civilstatus, religion, department).
                - Leave balances (VL, SL, EL, etc.)
                - Company policies (benefits, handbook, onboarding, offboarding)

               
                 When querying employees with EmployeeProfile:
                - Use QueryEmployees with only the filters you know.
                - Never guess or fabricate data.
                
                When querying leave balances with GetLeaveBalance:
                - Always use GetLeaveBalance with at least an employeeID.
                - FiscalYear defaults to the current year if not specified.
                - If the user asks for a specific leave type, pass it exactly
                  as listed in the tool description.
                - Present balances in a clear, readable table format.



                Always use your tools to fetch real data — never guess or fabricate.
                If you cannot find a record for the given employee ID, say so clearly.
                If a question is outside your scope, say: "This is outside HR scope."
                """,
                tools:
                [
                EmployeeProfile(statuses, genders, positions, civilstatuses, religions, department),
                GetLeaveBalance(leavetype),
                AIFunctionFactory.Create(GetCompanyPolicy),
                ]);
        }

        // ── Fetch lookups from DB once at startup ────────────────────────────────────

        private async Task<(string statuses, string genders, string positions, string civilStatuses, string religions, string department, string leavetypes)> FetchLookupsAsync()
        {


            await using var db = await _dbContext.CreateDbContextAsync();

            var statuses = await db.ref_StatusCode.Select(e => e.Status!.Trim()).ToListAsync();

            var genders = await db.ref_Gender.Select(e => e.Gender!.Trim()).ToListAsync();

            var positions = await db.ref_Position.Select(e => e.Position!.Trim()).ToListAsync();

            var civilStatuses = await db.ref_CivilStatus.Select(e => e.CivilStatus!.Trim()).ToListAsync();

            var religions = await db.ref_Religion.Select(e => e.Religion!.Trim()).ToListAsync();

            var department = await db.ref_Department.Select(e => e.DepartmentDesc!.Trim()).ToListAsync();

            var leavetype = await db.ref_LeaveTypeCode.Select(e => e.LeaveTypeDesc!.Trim()).ToListAsync();


            return (
                string.Join(", ", statuses),
                string.Join(", ", genders),
                string.Join(", ", positions),
                string.Join(", ", civilStatuses),
                string.Join(", ", religions),
                string.Join(", ", department),
                string.Join(", ", leavetype)
            );



        }

      

        // ── Simple DTO for deserializing AI date response ─────────────────────────
        private record DateRangeResult(
            [property: JsonPropertyName("start")] string? Start,
            [property: JsonPropertyName("end")] string? End);



        // ── Tools ────────────────────────────────────────────────────────────────

        // ── Build the tool with dynamic descriptions baked in ────────────────────────
        [Description($@"Employee Information. Can filter (statuses, genders, positions, civilstatuses, religions, department)
        Retrievable data (name, statuses, genders, positions, civilstatuses, religions, department, BirthDate, EmailAddress, MobileNo, PrimaryAddress, CivilStatus, Religion, Gender, Nationality, SSS, TIN, PH (PhilHealth), HDMF (Pag-Ibig), Branch, Division, Region, Union Member Status, Area, PayDivisor, BankAccountNo)")]
        private AIFunction EmployeeProfile(string statuses, string genders, string positions, string civilstatuses, string religions, string department)
        {
            // Map parameter names to their dynamic descriptions
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["status"] = $"Filter by employment status. Valid values: {statuses}.",
                ["gender"] = $"Filter by gender. Valid values: {genders}.",
                ["position"] = $"Filter by position title (partial match). Known positions: {positions}.",
                ["civilstatus"] = $"Filter by civil status. Valid values: {civilstatuses}.",
                ["religion"] = $"Filter by religion. Valid values: {religions}.",
                ["department"] = $"Filter by department. Valid values: {department}.",
            };

            return AIFunctionFactory.Create(
                method: typeof(HrSubAgent)
                    .GetMethod(nameof(QueryEmployees),
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "QueryEmployees",
                    Description = "Query employee records using any combination of optional filters. " +
                                  "Only set the filters relevant to the question — leave the rest null. " +
                                  "Returns a JSON array of matching employee records.",

                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        // ← The correct property: receives the ParameterInfo directly
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc          // override with dynamic value from DB
                                : null          // fall back to [Description] attribute on the method
                    }
                });
        }


        // ── The actual tool method — now takes flat params instead of a DTO ──────────
        // (AIFunctionFactoryOptions.Parameters maps by parameter name)
        private async Task<string> QueryEmployees(
            [Description("Filter by exact employee ID number, e.g. 93.")]
            int?    employeeID  = null,

            [Description("Filter by last name (partial, case-insensitive).")]
            string? lastname    = null,

            [Description("Filter by first name (partial, case-insensitive).")]
            string? firstname   = null,

            string? status = null,     // ← description comes from ParameterDescriptionProvider
            string? gender = null,     // ← description comes from ParameterDescriptionProvider
            string? position = null,     // ← description comes from ParameterDescriptionProvider       
            string? civilStatus = null,     // ← description comes from ParameterDescriptionProvider
            string? religion = null,     // ← description comes from ParameterDescriptionProvider
            string? department = null,     // ← description comes from ParameterDescriptionProvider
            [Description("Filter by birthdate (datetime format).")]
            DateTime? birthdate   = null,
            [Description("Maximum records to return. Defaults to 20.")]
            int top= 20)
        {
            await using var db = await _dbContext.CreateDbContextAsync();

            IQueryable<AI_vwGet_MstEmployee> query = db.AI_vwGet_MstEmployee.AsNoTracking();

            if (employeeID.HasValue)
                query = query.Where(e => e.EmployeeID == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(lastname))
                query = query.Where(e => EF.Functions.Like(e.Lastname!, $"%{lastname.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(firstname))
                query = query.Where(e => EF.Functions.Like(e.Firstname!, $"%{firstname.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(e => e.Status!.ToUpper() == status.ToUpper());

            if (!string.IsNullOrWhiteSpace(gender))
                query = query.Where(e => e.Gender!.Trim().ToUpper() == gender.ToUpper());

            if (!string.IsNullOrWhiteSpace(position))
                query = query.Where(e => EF.Functions.Like(e.Position!, $"%{position.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(civilStatus))
                query = query.Where(e => EF.Functions.Like(e.CivilStatus!, $"%{civilStatus.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(religion))
                query = query.Where(e => EF.Functions.Like(e.Religion!, $"%{religion.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(department))
                query = query.Where(e => EF.Functions.Like(e.DepartmentDesc!, $"%{department.Trim()}%"));

            if (!string.IsNullOrWhiteSpace(birthdate.ToString()))
            {
                query = query.Where(e => e.BirthDate.HasValue && e.BirthDate.Value.Month == birthdate.Value.Month && e.BirthDate.Value.Day == birthdate.Value.Day);
            } 



            var results = await query
                .OrderBy(e => e.Lastname)
                .ThenBy(e => e.Firstname)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = "No employees found matching the given filters."
                });

            return JsonSerializer.Serialize(results, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }

        // ── GetLeaveBalance tool builder — dynamic leaveType description ─────────
        private AIFunction GetLeaveBalance(string leaveTypes)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["leaveType"] =
                    $"Filter by leave type (exact match). Valid values: {leaveTypes}. " +
                    $"Leave null to return all leave types.",
            };

            return AIFunctionFactory.Create(
                method: typeof(HrSubAgent).GetMethod(
                    nameof(BuildLeaveBalanceTool),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "BuildLeaveBalanceTool",
                    Description =
                        "Gets an employee's leave balance — remaining days per leave type " +
                        "for a given fiscal year. Returns CFW balance, YTD balance, " +
                        "used days, and total balance.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }


        // ── Tool: GetLeaveBalance ─────────────────────────────────────────────────
        private async Task<string> BuildLeaveBalanceTool(
            [Description("The employee ID (exact match), e.g. 93.")]
        int? employeeID = null,

            string? leaveType = null,   // ← description injected by BuildLeaveBalanceTool

            [Description(
            "Fiscal year to query, e.g. 2026. " +
            "Defaults to the current year if not provided.")]
        int? fiscalYear = null)
        {
            if (employeeID is null)
                return JsonSerializer.Serialize(new
                {
                    message = "Please provide an employee ID to query leave balances."
                });

            await using var db = await _dbContext.CreateDbContextAsync();

            var year = fiscalYear ?? DateTime.Today.Year;

            IQueryable<AI_vwGet_LeaveBalance> query = db.AI_vwGet_LeaveBalance
                .AsNoTracking()
                .Where(l => l.EmployeeID.ToString() == employeeID.Value.ToString())
                .Where(l => l.FiscalYear.ToString() == year.ToString());

            if (!string.IsNullOrWhiteSpace(leaveType))
                query = query.Where(l =>
                    l.LeaveTypeDesc!.Trim() == leaveType.Trim());

            var results = await query
                .OrderBy(l => l.LeaveTypeDesc)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = $"No leave balance records found for Employee ID {employeeID} " +
                                 $"in fiscal year {year}.",
                    employeeID,
                    fiscalYear = year
                });

            // Project to a clean DTO — skip internal IDs, keep what matters
            var projected = results.Select(l => new
            {
                LeaveType = l.LeaveTypeDesc,
                CFWBalance = l.CFWBalance,
                YTDBalance = l.YTDBalance,
                UsedYTD = l.UsedYTD,
                TotalBalance = l.TotalBalance,
                FiscalYear = l.FiscalYear
            });

            return JsonSerializer.Serialize(new
            {
                EmployeeID = employeeID,
                FiscalYear = year,
                Balances = projected
            },
            new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        [Description("Gets company policy information for a given HR topic.")]
        private static async Task<string> GetCompanyPolicy(
            [Description("The HR topic, e.g. 'maternity leave', 'remote work', 'dress code'")] string topic)
        {
            await Task.Delay(10);
            // TODO: replace with real policy document lookup / vector search
            return $"[Policy: '{topic}'] Please refer to the Employee Handbook, Section 4. " +
                   $"For further assistance, contact HR at hr@company.com.";
        }
    }
}
