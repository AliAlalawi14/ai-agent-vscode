using Ai_Agent.Agent.Services;
using Ai_Agent.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Ai_Agent.Tools.Services
{
    /// <summary>
    /// Change tracking with session grouping, unified diff patches, and multi-level revert support.
    ///
    /// Each workspace has its own append-only log (&lt;workspace&gt;/.ai_changes.ndjson), so a change can only be
    /// looked up, and reverted, in the workspace it was made in.
    /// </summary>
    public class ChangeTracker
    {
        public const string LogFileName = ".ai_changes.ndjson";

        private readonly UnifiedDiffService _diffService;
        private readonly ILogger<ChangeTracker> _logger;
        private readonly int _maxSessions;

        // Per workspace (normalized root): the parsed log, loaded on first use
        private readonly Dictionary<string, List<PendingChange>> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _lock = new(1, 1);

        public ChangeTracker(UnifiedDiffService diffService, ILogger<ChangeTracker> logger, IOptions<Config.AgentOptions>? options = null)
        {
            _diffService = diffService;
            _logger = logger;
            _maxSessions = Math.Max(1, options?.Value.ChangeLogMaxSessions ?? 50);
        }

        /// <summary>
        /// Records a file change with a unified diff patch, in the workspace's own log.
        /// </summary>
        public async Task<PendingChange> RecordChangeAsync(
            string workspaceRoot,
            string sessionId,
            int sequenceNumber,
            string filePath,
            string toolUsed,
            string beforeContent,
            string afterContent,
            bool isNewFile,
            bool isDeletion = false)
        {
            var patch = _diffService.ComputeDiff(filePath, beforeContent, afterContent);

            var change = new PendingChange
            {
                SessionId = sessionId,
                SequenceNumber = sequenceNumber,
                FilePath = filePath,
                ToolUsed = toolUsed,
                Patch = patch,
                BeforeContent = beforeContent,
                AfterHash = Hash(afterContent),
                IsNewFile = isNewFile,
                IsDeletion = isDeletion,
                Status = "applied",
                Timestamp = DateTime.UtcNow,
                Summary = GenerateSummary(toolUsed, filePath, patch, isNewFile),
                Workspace = Normalize(workspaceRoot)
            };

            await AppendToLogAsync(workspaceRoot, change);

            _logger.LogInformation(
                "Change {ChangeId} recorded: {File} ({Tool}) — patch {PatchSize} bytes",
                change.ChangeId, filePath, toolUsed, patch.Length);

            return change;
        }

        /// <summary>
        /// Gets all changes for a session, ordered by sequence number.
        /// </summary>
        public async Task<List<PendingChange>> GetSessionChangesAsync(string workspaceRoot, string sessionId)
        {
            var changes = await LoadChangesAsync(workspaceRoot);
            return changes
                .Where(c => c.SessionId == sessionId)
                .OrderBy(c => c.SequenceNumber)
                .ToList();
        }

        /// <summary>
        /// Gets a single change by its ID (only changes made in this workspace are found).
        /// </summary>
        public async Task<PendingChange?> GetChangeAsync(string workspaceRoot, string changeId)
        {
            var changes = await LoadChangesAsync(workspaceRoot);
            return changes.FirstOrDefault(c => c.ChangeId == changeId);
        }

        /// <summary>
        /// Gets recent changes across all sessions of this workspace.
        /// </summary>
        public async Task<List<PendingChange>> GetRecentChangesAsync(string workspaceRoot, int count = 20)
        {
            var changes = await LoadChangesAsync(workspaceRoot);
            return changes
                .OrderByDescending(c => c.Timestamp)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Gets changes for a specific file, most recent first.
        /// </summary>
        public async Task<List<PendingChange>> GetFileChangesAsync(string workspaceRoot, string filePath)
        {
            var changes = await LoadChangesAsync(workspaceRoot);
            return changes
                .Where(c => c.FilePath == filePath)
                .OrderByDescending(c => c.Timestamp)
                .ToList();
        }

        /// <summary>
        /// Marks a change as reverted.
        /// </summary>
        public async Task<bool> MarkRevertedAsync(string workspaceRoot, string changeId)
        {
            var changes = await LoadChangesAsync(workspaceRoot);
            var change = changes.FirstOrDefault(c => c.ChangeId == changeId);
            if (change == null) return false;

            change.Status = "reverted";
            await SaveAllChangesAsync(workspaceRoot, changes);
            _logger.LogInformation("Change {ChangeId} marked as reverted", changeId);
            return true;
        }

        /// <summary>
        /// Undoes one change on disk without ever writing a guess:
        /// 1. File untouched since the change (hash matches) → restore the exact BeforeContent.
        /// 2. File edited since → reverse-apply the patch, with every line verified.
        /// 3. Patch no longer matches → refuse (returns the reason) and leave the file alone.
        /// The file's UTF-8 BOM, if any, is kept. Does not update the change's status.
        /// </summary>
        public async Task<(bool Ok, string? Error)> RevertFileAsync(PendingChange change, string workspaceRoot)
        {
            // A change recorded in another workspace must never be applied here
            if (!string.IsNullOrEmpty(change.Workspace) &&
                !string.Equals(change.Workspace, Normalize(workspaceRoot), StringComparison.OrdinalIgnoreCase))
                return (false, "This change belongs to a different workspace.");

            var fullPath = WorkspacePath.Resolve(workspaceRoot, change.FilePath);
            if (fullPath == null)
                return (false, "The file is outside the workspace.");

            try
            {
                if (change.IsDeletion)
                {
                    // Undo a delete: recreate the file, unless something new is there now
                    if (File.Exists(fullPath))
                        return (false, "A file with this name exists again; revert refused so nothing is overwritten.");
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await File.WriteAllTextAsync(fullPath, change.BeforeContent);
                    _logger.LogInformation("Reverted change {ChangeId}: recreated {File}", change.ChangeId, change.FilePath);
                    return (true, null);
                }

                if (!File.Exists(fullPath))
                    return change.IsNewFile ? (true, null) : (false, "The file no longer exists.");

                var bytes = await File.ReadAllBytesAsync(fullPath);
                var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var current = hasBom
                    ? System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                    : System.Text.Encoding.UTF8.GetString(bytes);
                var untouched = !string.IsNullOrEmpty(change.AfterHash) && Hash(current) == change.AfterHash;

                if (change.IsNewFile)
                {
                    if (!string.IsNullOrEmpty(change.AfterHash) && !untouched)
                        return (false, "The file was edited after the agent created it. Delete it yourself if you still want it gone.");
                    File.Delete(fullPath);
                }
                else
                {
                    var restored = untouched
                        ? change.BeforeContent
                        : _diffService.ApplyPatch(current, change.Patch, reverse: true);
                    await File.WriteAllTextAsync(fullPath, restored, new System.Text.UTF8Encoding(hasBom));
                }

                _logger.LogInformation("Reverted change {ChangeId}: {File} ({Method})",
                    change.ChangeId, change.FilePath, untouched ? "exact restore" : "verified reverse patch");
                return (true, null);
            }
            catch (PatchConflictException ex)
            {
                return (false, $"{ex.Message} Revert refused so nothing is overwritten.");
            }
            catch (IOException ex)
            {
                return (false, ex.Message);
            }
        }

        private static string Hash(string content) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

        private static string Normalize(string workspaceRoot) =>
            Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static string LogPath(string workspaceRoot) => Path.Combine(Normalize(workspaceRoot), LogFileName);

        /// <summary>
        /// Reverts all changes in a session (marks them and restores files).
        /// Returns the list of reverted changes.
        /// </summary>
        public async Task<List<PendingChange>> RevertSessionAsync(string sessionId, string workspaceRoot)
        {
            var sessionChanges = await GetSessionChangesAsync(workspaceRoot, sessionId);
            var reverted = new List<PendingChange>();

            // Revert in reverse order (last change first)
            foreach (var change in sessionChanges
                .Where(c => c.Status == "applied")
                .OrderByDescending(c => c.SequenceNumber))
            {
                var (ok, error) = await RevertFileAsync(change, workspaceRoot);
                if (!ok)
                {
                    // Stop at the first conflict: earlier changes to the same file build on this one
                    _logger.LogWarning("Session revert stopped at {ChangeId} ({File}): {Error}",
                        change.ChangeId, change.FilePath, error);
                    break;
                }

                change.Status = "reverted";
                reverted.Add(change);
            }

            if (reverted.Count > 0)
            {
                await SaveAllChangesAsync(workspaceRoot, await LoadChangesAsync(workspaceRoot));
            }

            return reverted;
        }

        // ── Persistence (NDJSON per workspace — append-only, crash-safe) ──────

        private async Task AppendToLogAsync(string workspaceRoot, PendingChange change)
        {
            await _lock.WaitAsync();
            try
            {
                var line = JsonSerializer.Serialize(change) + "\n";
                await File.AppendAllTextAsync(LogPath(workspaceRoot), line);

                // Invalidate this workspace's cache
                _cache.Remove(Normalize(workspaceRoot));
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<List<PendingChange>> LoadChangesAsync(string workspaceRoot)
        {
            var key = Normalize(workspaceRoot);
            await _lock.WaitAsync();
            try
            {
                if (_cache.TryGetValue(key, out var cached))
                    return cached;

                var path = LogPath(workspaceRoot);
                var changes = new List<PendingChange>();
                if (File.Exists(path))
                {
                    foreach (var line in await File.ReadAllLinesAsync(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var change = JsonSerializer.Deserialize<PendingChange>(line);
                            if (change != null)
                                changes.Add(change);
                        }
                        catch (JsonException)
                        {
                            _logger.LogWarning("Skipping corrupted change log line in {Path}", path);
                        }
                    }
                }

                changes = await ApplyRetentionAsync(path, changes);
                _cache[key] = changes;
                return changes;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Keeps the log to the most recent sessions (Agent:ChangeLogMaxSessions): every change stores the file's
        /// full before-content, so an unbounded log grows with every edit. Called under the lock, once per load.
        /// </summary>
        private async Task<List<PendingChange>> ApplyRetentionAsync(string path, List<PendingChange> changes)
        {
            var keep = changes
                .GroupBy(c => c.SessionId)
                .OrderByDescending(g => g.Max(c => c.Timestamp))
                .Take(_maxSessions)
                .SelectMany(g => g.Select(c => c.SessionId))
                .ToHashSet();
            if (keep.Count == changes.Select(c => c.SessionId).Distinct().Count())
                return changes;

            var kept = changes.Where(c => keep.Contains(c.SessionId)).ToList();
            await File.WriteAllLinesAsync(path, kept.Select(c => JsonSerializer.Serialize(c)));
            _logger.LogInformation("Change log {Path} compacted: {Before} -> {After} changes (last {Sessions} sessions kept)",
                path, changes.Count, kept.Count, _maxSessions);
            return kept;
        }

        private async Task SaveAllChangesAsync(string workspaceRoot, List<PendingChange> changes)
        {
            await _lock.WaitAsync();
            try
            {
                await File.WriteAllLinesAsync(LogPath(workspaceRoot), changes.Select(c => JsonSerializer.Serialize(c)));
                _cache[Normalize(workspaceRoot)] = changes;
            }
            finally
            {
                _lock.Release();
            }
        }

        private static string GenerateSummary(string toolUsed, string filePath, string patch, bool isNewFile)
        {
            if (string.IsNullOrEmpty(patch))
                return $"{toolUsed}: {filePath} (no changes detected)";

            var lineCount = patch.Split('\n').Length;
            var fileName = Path.GetFileName(filePath);

            return toolUsed switch
            {
                // write_file on an existing file replaces it, so don't call that "Created"
                "write_file" => $"{(isNewFile ? "Created" : "Rewrote")} {fileName}" +
                    (lineCount > 5 ? $" ({lineCount} lines)" : ""),
                "edit_file" => $"Modified {fileName}",
                "delete_file" => $"Deleted {fileName}",
                "move_file" => isNewFile ? $"Moved here: {fileName}" : $"Moved away: {fileName}",
                "replace_lines" => $"Modified {fileName}" +
                    (lineCount > 5 ? $" ({lineCount} line diff)" : ""),
                _ => $"{toolUsed}: {fileName} ({lineCount} line diff)"
            };
        }
    }
}
