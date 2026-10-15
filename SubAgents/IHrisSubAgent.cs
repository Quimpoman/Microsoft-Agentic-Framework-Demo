using Microsoft.Agents.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Agents.AI;

namespace Agent.SubAgents
{

    /// <summary>
    /// Marker interface for all HRIS subagents.
    /// Ensures every subagent exposes the underlying ChatClientAgent
    /// so the orchestrator can register it as a tool via .AsTool().
    /// </summary>
    public interface IHrisSubAgent
    {
        ChatClientAgent Agent { get; }
    }
}
