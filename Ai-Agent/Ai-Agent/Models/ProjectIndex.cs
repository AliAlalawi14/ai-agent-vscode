namespace Ai_Agent.Models
{
    /// <summary>
    /// A lightweight index of the project structure.
    /// Built at startup so the AI knows the codebase without searching.
    /// </summary>
    public class ProjectIndex
    {
        /// <summary>
        /// All classes found in the project with their details.
        /// </summary>
        public List<ClassInfo> Classes { get; set; } = new();

        /// <summary>
        /// All files with their summaries.
        /// </summary>
        public List<FileInfo> Files { get; set; } = new();

        /// <summary>
        /// Total files indexed.
        /// </summary>
        public int TotalFiles { get; set; }

        /// <summary>
        /// Total classes found.
        /// </summary>
        public int TotalClasses { get; set; }

        /// <summary>
        /// All dependencies between classes.
        /// </summary>
        public List<DependencyInfo> Dependencies { get; set; } = new();
    }

    /// <summary>
    /// Information about a single class.
    /// </summary>
    public class ClassInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public List<string> Methods { get; set; } = new();
        public List<string> Properties { get; set; } = new();
        public List<string> Dependencies { get; set; } = new();

    }

    /// <summary>
    /// Information about a single file.
    /// </summary>
    public class FileInfo
    {
        public string Path { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public int Lines { get; set; }
    }


    /// <summary>
    /// Represents a relationship between two classes.
    /// </summary>
    public class DependencyInfo
    {
        public string FromClass { get; set; } = string.Empty;
        public string ToClass { get; set; } = string.Empty;
        public string Relationship { get; set; } = string.Empty; // "uses", "inherits", "implements"
    }
}
