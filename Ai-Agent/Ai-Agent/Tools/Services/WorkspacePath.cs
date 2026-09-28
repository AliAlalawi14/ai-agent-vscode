namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Resolves tool paths inside the workspace. Normalizes with GetFullPath before comparing,
    /// so "..\..\x", absolute paths and "C:\workspace-evil" (prefix trick) are all rejected.
    /// </summary>
    public static class WorkspacePath
    {
        /// <summary>The agent's own bookkeeping files in the workspace; hidden from list_directory/find_files.</summary>
        public static bool IsInternalFile(string fileName) =>
            fileName.Equals(".ai_changes.ndjson", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".ai_review_log.json", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the absolute path for <paramref name="relativePath"/>, or null if it points outside the workspace.
        /// </summary>
        public static string? Resolve(string workspaceRoot, string relativePath)
        {
            var root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            if (!IsUnder(root, fullPath)) return null;

            // A symlink or junction inside the workspace can point anywhere: every existing link on the way
            // from the root to the target must itself point inside the root.
            return LinksStayInside(root, fullPath) ? fullPath : null;
        }

        private static StringComparison PathComparison =>
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static bool IsUnder(string root, string fullPath) =>
            string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, PathComparison) ||
            fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

        private static bool LinksStayInside(string root, string fullPath)
        {
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative == ".") return true;

            var current = root;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (!info.Exists) return true;   // the rest doesn't exist yet (a file about to be created)
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                FileSystemInfo? target;
                try { target = info.ResolveLinkTarget(returnFinalTarget: true); }
                catch (IOException) { return false; }   // a link we can't follow is not trusted
                // null: a reparse point that is not a link (e.g. a OneDrive cloud placeholder), which is fine
                if (target != null && !IsUnder(root, Path.GetFullPath(target.FullName))) return false;
            }
            return true;
        }
    }
}
