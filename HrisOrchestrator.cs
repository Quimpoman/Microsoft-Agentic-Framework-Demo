using Agent.SubAgents;
using Microsoft.Agents.AI;
using Microsoft.CodeAnalysis.Differencing;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent
{

    /// <summary>
    /// Main HRIS orchestrator agent.
    /// Has no domain tools of its own — it delegates every question
    /// to the appropriate specialist subagent via .AsTool().
    /// </summary>


    public class HrisOrchestrator
    {
        public ChatClientAgent Agent { get; }

        public HrisOrchestrator(
            IChatClient chatClient,
            HrSubAgent hr,
            TimekeepingSubAgent timekeeping,
            PayrollSubAgent payroll,
            ReferenceSubAgent reference)
        {
            Agent = new ChatClientAgent(
                chatClient,
                name: "hris_orchestrator",
                instructions: $@"
                Date and Time Today: {DateTime.Now:MMMM dd, yyyy hh:mm tt}

                You are an HRIS (Human Resource Information System) assistant for employees.
                You have three specialist agents available as tools:

                - hr_agent            → employee profiles, employee data, leave balances, company policies.
                - timekeeping_agent   → attendance logs, leave requests, official business (OB) trip requests, overtime requests, certificate of attendance (COA) requests
                - payroll_agent       → payslips, deductions, government contributions, loans inquiry
                - reference_agent       → system activity logs, who did what and when

                Rules:
                1. Delegate EVERY question to the most relevant specialist agent.
                2. For cross-domain questions (e.g. ""Why is my pay lower this month?""),
                   call MULTIPLE agents and combine their answers into one clear response.
                3. If the employee's ID is required and not yet provided, please look it up in the hr_agent's EmployeeProfile tool.
                4. Never fabricate data. Only relay what specialist agents return.
                5. If you're unsure if you are allowed to retrieve the data, check if the agent and tools allow it. If not, respond with: ""I'm sorry, but I don't have access to that information.""
                6. If the user asks how to use the tool, can you check the parameters by accessing the agents?
                7. Respond in a friendly, concise, and professional tone.
                ",
                tools:
                [
                hr.Agent.AsAIFunction(),
                timekeeping.Agent.AsAIFunction(),
                payroll.Agent.AsAIFunction(),
                reference.Agent.AsAIFunction(),
                ]);
        }
    }
}
