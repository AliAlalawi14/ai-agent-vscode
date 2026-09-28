namespace Ai_Agent.Models
{
    /// <summary>
    /// Holds information about the project gathered at agent startup.
    /// This is injected into the system prompt so the AI knows the project
    /// without needing to discover it through tool calls.
    /// </summary>
    public class ProjectContext
    {
        /// <summary>
        /// The type of project (e.g., "ASP.NET Core Web API", "Console Application")
        /// </summary>
        public string ProjectType { get; set; } = "Unknown";

        /// <summary>
        /// The target framework (e.g., "net9.0", "net8.0")
        /// </summary>
        public string TargetFramework { get; set; } = "Unknown";

        /// <summary>
        /// List of NuGet packages with versions
        /// </summary>
        public List<string> Packages { get; set; } = new();

        /// <summary>
        /// Top-level folders in the project
        /// </summary>
        public List<string> Folders { get; set; } = new();

        /// <summary>
        /// Key configuration values from appsettings.json
        /// </summary>
        public string ConfigSummary { get; set; } = string.Empty;

        /// <summary>
        /// Summary of what Program.cs does
        /// </summary>
        public string ProgramSummary { get; set; } = string.Empty;

        /// <summary>
        /// List of .csproj files found (there might be multiple in a solution)
        /// </summary>
        public List<string> ProjectFiles { get; set; } = new();

        public ProjectIndex? ProjectIndex { get; set; }


    }
}
