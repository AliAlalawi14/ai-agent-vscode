using Ai_Agent.Mcp;
using Microsoft.AspNetCore.Mvc;

namespace Ai_Agent.Controllers
{
    /// <summary>State of the configured MCP servers and their tools (the panel's MCP settings show it).</summary>
    [ApiController]
    [Route("api/[controller]")]
    public class McpController : ControllerBase
    {
        private readonly McpConnectionManager _mcp;

        public McpController(McpConnectionManager mcp) => _mcp = mcp;

        [HttpGet("status")]
        public IActionResult Status() => Ok(new { servers = _mcp.GetStatus(), configErrors = _mcp.ConfigErrors });
    }
}
