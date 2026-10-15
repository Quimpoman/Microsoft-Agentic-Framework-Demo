using Agent.Models;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
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
    /// Payroll specialist subagent.
    /// Handles: payslips, deductions, government contributions.
    /// </summary>
    public class PayrollSubAgent : IHrisSubAgent
    {
        private readonly IDbContextFactory<EVOLVE_VRContext> _dbFactory;
        private readonly IChatClient _chatClient;

        public ChatClientAgent Agent { get; }

        public PayrollSubAgent(IChatClient chatClient, IDbContextFactory<EVOLVE_VRContext> dbFactory)
        {
            _dbFactory = dbFactory;
            _chatClient = chatClient;

            var (companies, months, groups, deductionDescs) = FetchLookupsAsync().GetAwaiter().GetResult();

            Agent = new ChatClientAgent(
                chatClient,
                name: "payroll_agent",
                instructions: """
                
                You are a Payroll specialist assistant. Tools Available Include:
                
                BuildPayrollTool (For Querying Payroll Data)
                - Payroll computation details (earnings, deductions, net pay)
                - Government contributions (SSS, PhilHealth, Pag-IBIG, withholding tax)
                - Attendance-based deductions (tardy, undertime, absences)
                - Payroll periods and covered dates

                BuildLoanTool (For querying Employee Loans)

                When querying payroll:
                - Primary lookup is by MonthText + CoveredYear.
                - If the user says "latest" or doesn't specify a period,
                  omit MonthText and CoveredYear — the tool returns the
                  most recent record automatically.
                - Never fabricate amounts. Only relay what the tool returns.
                - Present amounts in PHP format with 2 decimal places.
                - Always show the full earnings and deductions breakdown.
                
                 When querying loans:
                - If the user asks for active loans, use Status: ACTIVE.
                - Always show Balance, TotalPaidAmount, and LoanAmount together
                  so the employee understands how much has been paid vs remaining.
                - Present amounts in PHP format with 2 decimal places.
                
                """,
                tools:
                [
                BuildPayrollTool(companies, months, groups),
                BuildLoanTool(deductionDescs),
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

        // ── Lookups — fetched once at startup ─────────────────────────────────────
        private async Task<(string companies, string months, string groups, string deuctiondesc)> FetchLookupsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var companies = await db.ref_Client
                .AsNoTracking()
                .Where(p => p.CompanyName != null)
                .Select(p => p.CompanyName!.Trim())
                .Distinct().OrderBy(c => c)
                .ToListAsync();

            var months = await db.ref_Periods
                .AsNoTracking()
                .Where(p => p.MonthText != null)
                .Select(p => p.MonthText!.Trim())
                .Distinct().OrderBy(m => m)
                .ToListAsync();

            var groups = await db.ref_PayrollGrouping
                .AsNoTracking()
                .Where(p => p.PayrollGroupDescription != null)
                .Select(p => p.PayrollGroupDescription!.Trim())
                .Distinct().OrderBy(g => g)
                .ToListAsync();
            var deuctiondesc = await db.ref_Deductions
             .AsNoTracking()
             .Where(p => p.Active == true)
             .Select(p => p.DeductionDesc!.Trim())
             .Distinct().OrderBy(g => g)
             .ToListAsync();

            return (
                string.Join(", ", companies),
                string.Join(", ", months),
                string.Join(", ", groups),
                string.Join(", ", deuctiondesc)
            );
        }


        // ── Tools ────────────────────────────────────────────────────────────────

        // ── Tool builder — injects dynamic lookup descriptions ────────────────────
        private AIFunction BuildPayrollTool(
            string companies, string months, string groups)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["companyName"] =
                    $"Filter by company name (exact match). Valid values: {companies}. " +
                    $"Leave null if not needed.",

                ["monthText"] =
                    $"Filter by payroll month label (exact match). Valid values: {months}. " +
                    $"Use the exact label as listed, e.g. 'MARCH 30', 'MARCH 15'. " +
                    $"Leave null to return all months.",

                ["payrollGroup"] =
                    $"Filter by payroll group (exact match). Valid values: {groups}. " +
                    $"Leave null if not needed.",
            };

            return AIFunctionFactory.Create(
                method: typeof(PayrollSubAgent).GetMethod(
                    nameof(GetPayrollDetails),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "GetPayrollDetails",
                    Description =
                        "Gets payroll computation details for an employee — earnings, " +
                        "deductions, government contributions, and net pay. " +
                        "Primary lookup is by MonthText + CoveredYear. " +
                        "Returns the most recent record if no period is specified.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }

        // ── Tool: GetPayrollDetails ───────────────────────────────────────────────
        private async Task<string> GetPayrollDetails(
            [Description("The employee ID (exact match), e.g. 21041.")]
        int? employeeID = null,

            string? monthText = null,   // ← description injected dynamically
            string? companyName = null,   // ← description injected dynamically
            string? payrollGroup = null,   // ← description injected dynamically

            [Description(
            "The covered year, e.g. 2026. " +
            "Defaults to current year if not provided.")]
            int? coveredYear = null,

             [Description(
            "Timekeeping Date Range (start to end)")]
            string? dateRange = null,

               [Description(
            "Timekeeping Date Range (start to end)")]
            DateTime? payrolldate   = null,

            [Description(
            "Maximum number of payroll records to return. " +
            "Defaults to 5 — most users only need the latest few periods.")]
        int top = 50)
        {
            //if (employeeID is null)
            //    return JsonSerializer.Serialize(new
            //    {
            //        message = "Please provide an employee ID to query payroll details."
            //    });

            await using var db = await _dbFactory.CreateDbContextAsync();

            var year = coveredYear ?? DateTime.Today.Year;

            // Resolve date range — reuses AI-powered resolver
            var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);


            IQueryable<AI_vwGet_TrnComputationDetails> query = db.AI_vwGet_TrnComputationDetails
                .AsNoTracking()
                .Where(p => p.OLDNumber == employeeID.Value.ToString());

            // ── Filters ───────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(monthText))
                query = query.Where(p =>
                    p.MonthText!.Trim() == monthText.Trim());

            if (!string.IsNullOrWhiteSpace(companyName))
                query = query.Where(p =>
                    p.CompanyName!.Trim() == companyName.Trim());

            if (!string.IsNullOrWhiteSpace(payrollGroup))
                query = query.Where(p =>
                    p.PayrollGroupDescription!.Trim() == payrollGroup.Trim());

            // Only apply year filter when no specific monthText is given —
            // if monthText is provided, year scope is already implied
            if (string.IsNullOrWhiteSpace(monthText))
                query = query.Where(p => p.CoveredYear == year.ToString());

            // Only apply year filter when no specific monthText is given —
            if (!string.IsNullOrWhiteSpace(payrolldate.ToString()))
            {
                query = query.Where(e => e.PayrollDate.Value == payrolldate.Value);
            }

            if (!string.IsNullOrWhiteSpace(dateRange))
                query = query.Where(a =>
                    (a.StartDate.Value.Date >= startDate.Date &&
                    a.StartDate.Value.Date <= endDate.Date) || 
                    (a.EndDate.Value.Date >= startDate.Date &&
                    a.EndDate.Value.Date <= endDate.Date));

            var results = await query
                .OrderByDescending(p => p.PayrollDate)   // latest first
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = $"No payroll records found for Employee ID {employeeID}.",
                    employeeID,
                    monthText,
                    coveredYear = year
                });

            // ── Collect all ComputationHeaderIDs from results in one batch ────────────
            // Avoids N+1 queries — fetch all income/deduction details in two queries

            // NEW (correct, since ComputationHeaderID is non-nullable decimal):
            //var incomes = incomesByHeader.TryGetValue(
            //    p.ComputationHeaderID, out var inc)
            //    ? inc : new List<TrnAllIncome>();





            // ── Project ───────────────────────────────────────────────────────────────
            var projected = results.Select(p =>
            {


                // Match income/deduction details to this payroll record
                var allIncomes = db.vwRpt_OtherIncome
                    .Select(i => new
                    {
                        i.EmployeeID,
                        i.Particulars,
                        i.Amount,
                        i.ComputationHeaderID
                    })
                   .AsNoTracking()
                   .Where(i =>
                       i.EmployeeID == p.EmployeeID &&
                       i.ComputationHeaderID == p.ComputationHeaderID)
                   .OrderBy(i => i.ComputationHeaderID)
                   .ToList();

                var allDeductions = db.vwGet_AllDeductions
                  .AsNoTracking()
                  .Where(i =>
                       i.EmployeeID == p.EmployeeID &&
                       i.ComputationHeaderID == p.ComputationHeaderID)
                  .OrderBy(d => d.SortOrder).ToList();



                return new
                {
                    Employee = new
                    {
                        p.OLDNumber,
                        Name = $"{p.Lastname}, {p.Firstname} {p.Middlename}".Trim()
                    },

                    Period = new
                    {
                        p.MonthText,
                        p.CoveredYear,
                        p.PayrollGroupDescription,
                        StartDate = p.StartDate?.ToString("yyyy-MM-dd"),
                        EndDate = p.EndDate?.ToString("yyyy-MM-dd"),
                        PayrollDate = p.PayrollDate?.ToString("yyyy-MM-dd")
                    },

                    Earnings = new
                    {
                        BasicPay = p.BasicPay,
                        PeriodRate = p.PeriodRate,
                        OtherIncome = p.OtherIncome > 0 ? p.OtherIncome : (double?)null,
                        GrossIncome = p.GrossIncome,

                        // ← expanded line items from AllIncome
                        OtherIncomeDetails = allIncomes
                            .Where(i => i.Amount > 0)
                            .Select(i => new
                            {
                                Particular = i.Particulars,
                                Amount = i.Amount
                            })
                            .ToList()
                    },

                    GovernmentContributions = new
                    {
                        SSS = p.SSSEE,
                        PhilHealth = p.PHEE,
                        PagIBIG = p.HDMFEE,
                        MP2 = p.MPFEE > 0 ? p.MPFEE : (double?)null,
                        WTax = p.WTax
                    },

                    AttendanceDeductions = new
                    {
                        Tardy = p.Tardy > 0 ? p.Tardy : (double?)null,
                        Undertime = p.Undertime > 0 ? p.Undertime : (double?)null,
                        Absent = p.Absent > 0 ? p.Absent : (double?)null,
                    },

                    OtherDeductions = p.OtherDeduction > 0
                        ? p.OtherDeduction
                        : (double?)null,

                    // ← expanded line items from AllDeduction
                    OtherDeductionDetails = allDeductions
                        .Where(d => d.Amount > 0)
                        .Select(d => new
                        {
                            Particular = d.Particulars,
                            Amount = d.Amount
                        })
                        .ToList(),

                    TotalDeductions = p.Deductions,
                    NetEarnings = p.NetEarnings
                };
            });

            return JsonSerializer.Serialize(projected, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        // ── Loan tool builder ─────────────────────────────────────────────────────────
        private AIFunction BuildLoanTool(string deductionDescs)
        {
            var dynamicDescriptions = new Dictionary<string, string>
            {
                ["deductionDesc"] =
                    $"Filter by loan type (exact match). Valid values: {deductionDescs}. " +
                    $"Leave null to return all loan types.",
            };

            return AIFunctionFactory.Create(
                method: typeof(PayrollSubAgent).GetMethod(
                    nameof(GetLoanInquiry),
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!,
                target: this,
                options: new AIFunctionFactoryOptions
                {
                    Name = "GetLoanInquiry",
                    Description =
                        "Gets loan deduction records for an employee — loan type, amount, " +
                        "monthly deduction, total paid, and remaining balance. " +
                        "Can filter by loan type and status.",
                    JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
                    {
                        ParameterDescriptionProvider = param =>
                            dynamicDescriptions.TryGetValue(param.Name!, out var desc)
                                ? desc
                                : null
                    }
                });
        }


        // ── Tool: GetLoanInquiry ──────────────────────────────────────────────────────
        private async Task<string> GetLoanInquiry(
            [Description("The employee ID (exact match), e.g. 92230.")]
            int? employeeID = null,
            
            string? deductionDesc = null,   // ← description injected dynamically
             
            [Description("Status of the Loan. Accepted Values: (ACTIVE, INACTIVE)")]
            string? status = null,   // ← description injected dynamically

            [Description(
                "Filter loans that are still active within a date range. " +
                "Accepts natural language: 'this year', '2025', 'last year'. " +
                "Matches loans where StartPay <= rangeEnd AND EndPay >= rangeStart. " +
                "Leave null to return all loans regardless of date.")]
            string? dateRange = null,
            
            [Description("Maximum records to return. Defaults to 20.")]
            int top = 20)
        {
            if (employeeID is null)
                return JsonSerializer.Serialize(new
                {
                    message = "Please provide an employee ID to query loan records."
                });

            await using var db = await _dbFactory.CreateDbContextAsync();

            IQueryable<vwRpt_LoanDeductionRegister> query = db.vwRpt_LoanDeductionRegister
                .AsNoTracking()
                .Where(l => l.OLDNumber == employeeID.Value.ToString());

            if (!string.IsNullOrWhiteSpace(deductionDesc))
                query = query.Where(l =>
                    l.DeductionDesc!.Trim() == deductionDesc.Trim());

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(l =>
                    l.Status!.Trim().ToUpper() == status.Trim().ToUpper());

            // Optional date overlap filter — same pattern as leave applications
            if (!string.IsNullOrWhiteSpace(dateRange))
            {
                var (startDate, endDate) = await ResolveDateRangeWithAI(dateRange);
                query = query.Where(l =>
                    l.StartPay.HasValue && l.EndPay.HasValue &&
                    l.StartPay.Value.Date <= endDate.Date &&
                    l.EndPay.Value.Date >= startDate.Date);
            }

            var results = await query
                .OrderByDescending(l => l.LoanDate)
                .Take(top)
                .ToListAsync();

            if (results.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    message = $"No loan records found for Employee ID {employeeID}.",
                    employeeID,
                    deductionDesc,
                    status
                });

            var projected = results.Select(l => new
            {
                EmployeeID = l.OLDNumber,
                Name = l.EmployeeName,
                Position = l.Position,
                LoanType = l.DeductionDesc,
                Status = l.Status,
                LoanDate = l.LoanDate?.ToString("yyyy-MM-dd"),
                StartPay = l.StartPay?.ToString("yyyy-MM-dd"),
                EndPay = l.EndPay?.ToString("yyyy-MM-dd"),
                Terms = l.Terms.HasValue ? $"{l.Terms} months" : null,
                LoanAmount = l.LoanAmount,
                MonthlyDeduction = l.MonthlyDeduction,
                TotalPaid = l.TotalPaidAmount,
                Balance = l.Balance,
                // Computed progress — useful context for the LLM to explain to the user
                PercentPaid = l.LoanAmount > 0
                    ? Math.Round((l.TotalPaidAmount ?? 0) / l.LoanAmount.Value * 100, 1)
                    : (double?)null
            });

            return JsonSerializer.Serialize(new
            {
                EmployeeID = employeeID,
                Count = results.Count,
                Loans = projected
            },
            new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

      
    }
}
