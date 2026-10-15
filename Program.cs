
using Agent;
using Agent.Models;
using Agent.SubAgents;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using System.ComponentModel;



class Program
{

    static async Task Main(string[] args)
    {



        // ── Configuration ────────────────────────────────────────────────────────────
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        string apiKey = config["OpenAI:ApiKey"] ?? throw new Exception("API key not found. Run: dotnet user-secrets set \"OpenAI:ApiKey\" \"your-key\"");
        apiKey = "";
        string connectionString = "";

        // ── Services ─────────────────────────────────────────────────────────────────
        var services = new ServiceCollection();

        // Register the shared IChatClient (all agents share one underlying client)
        services
            .AddChatClient(
                new OpenAIClient(apiKey)
                    .GetChatClient("gpt-4o-mini")
                    .AsIChatClient())
            .UseFunctionInvocation();

        // ── EF Core — must be AddDbContextFactory, not AddDbContext ──────────────────
        services.AddDbContextFactory<EVOLVE_VRContext>(options =>
            options.UseSqlServer(connectionString));
        // Register subagents — each gets the shared IChatClient injected
        services.AddSingleton<HrSubAgent>();
        services.AddSingleton<TimekeepingSubAgent>();
        services.AddSingleton<PayrollSubAgent>();
        services.AddSingleton<ReferenceSubAgent>();

        // Register the orchestrator — depends on the three subagents above
        services.AddSingleton<HrisOrchestrator>();

        var sp = services.BuildServiceProvider();

        // ── Resolve orchestrator and create a session ─────────────────────────────────
        var orchestrator = sp.GetRequiredService<HrisOrchestrator>();
        var session = await orchestrator.Agent.CreateSessionAsync();

        // ── Chat Loop ─────────────────────────────────────────────────────────────────
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("          HRIS ASSISTANT — CONSOLE           ");
        Console.WriteLine("══════════════════════════════════════════════");
        Console.ResetColor();
        Console.WriteLine("Sample IDs: EMP-001, EMP-002, EMP-003");
        Console.WriteLine("Type 'exit' to quit.\n");

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("You > ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Thinking...");
            Console.ResetColor();

            try
            {
                var response = await orchestrator.Agent.RunAsync(input, session, options: null);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\nAssistant >> {response}\n");
                Console.ResetColor();
                Console.WriteLine("──────────────────────────────────────────────");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine("\nGoodbye!");


    }
}
