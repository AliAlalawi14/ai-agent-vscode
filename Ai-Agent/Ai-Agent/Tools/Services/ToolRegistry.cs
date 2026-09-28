using Ai_Agent.Models;
using Ai_Agent.Tools.Interfaces;

namespace Ai_Agent.Tools.Services
{
    public class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools = new();

        public void RegisterTool(ITool tool)
        {
            _tools[tool.Name] = tool;
        }

        public void RemoveWhere(Func<ITool, bool> predicate)
        {
            foreach (var name in _tools.Values.Where(predicate).Select(t => t.Name).ToList())
                _tools.Remove(name);
        }

        public IEnumerable<ITool> GetAllTools()
        {
            return _tools.Values;
        }

        public ITool? GetTool(string name)
        {
            _tools.TryGetValue(name, out var tool);
            return tool;
        }

        public async Task<string> ExecuteToolAsync(
            string toolName, Dictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            var tool = GetTool(toolName);
            if (tool == null)
            {
                return $"ERROR: Unknown tool '{toolName}'. Available tools: {string.Join(", ", _tools.Keys)}";
            }

            return await tool.ExecuteAsync(parameters, cancellationToken);
        }

        /// <summary>
        /// Builds OpenAI-compatible tool definitions for function calling. Parameters are strings unless the
        /// tool declares a typed schema (integer, boolean, enum, array); "[Optional]" descriptions are not required.
        /// </summary>
        public List<ToolDefinition> GetToolDefinitions()
        {
            var definitions = new List<ToolDefinition>();

            foreach (var tool in _tools.Values)
            {
                var properties = new Dictionary<string, object>();
                var required = new List<string>();
                var schemas = tool.ParameterSchemas;

                foreach (var param in tool.Parameters)
                {
                    var optional = param.Value.StartsWith("[Optional]", StringComparison.OrdinalIgnoreCase);
                    var description = optional ? param.Value["[Optional]".Length..].Trim() : param.Value;

                    var schema = schemas.TryGetValue(param.Key, out var typed) && typed is Dictionary<string, object> typedDict
                        ? new Dictionary<string, object>(typedDict)
                        : new Dictionary<string, object> { ["type"] = "string" };
                    schema["description"] = description;
                    properties[param.Key] = schema;

                    if (!optional)
                        required.Add(param.Key);
                }

                definitions.Add(new ToolDefinition
                {
                    Type = "function",
                    Function = new FunctionDefinition
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        Parameters = new
                        {
                            type = "object",
                            properties = properties,
                            required = required
                        }
                    }
                });
            }

            return definitions;
        }
    }
}
