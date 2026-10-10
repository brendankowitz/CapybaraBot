namespace Capybara.Runtime.Copilot.Configuration;

/// <summary>How stdio MCP servers are isolated from the CLI's environment (FR-SAF-08).</summary>
public enum StdioMcpIsolation
{
    /// <summary>Launch through <c>env -i</c> with only <c>PATH</c>, <c>HOME</c> and <c>LANG</c> set.</summary>
    ClearEnvironment,

    /// <summary>Refuse every stdio MCP server. There is deliberately no mode that inherits the environment.</summary>
    Refuse,
}
