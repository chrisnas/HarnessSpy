using System.IO;
using HarnessSpy.Core.Models;
using HarnessSpy.Core.Sessions;

namespace HarnessSpy.Wpf.ViewModels;

internal sealed class CursorReconstructionFolderProjector
{
    public IReadOnlyList<CursorReconstructionItemViewModel> Project(
        IReadOnlyList<CursorReconstructionItemViewModel> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        FolderNode root = new(string.Empty, string.Empty);
        List<ProjectedNode> special = [];
        foreach (CursorReconstructionItemViewModel workspace in workspaces)
        {
            WorkspaceContext context =
                workspace.Workspace?.Workspace ??
                WorkspaceContext.Unknown;
            string? rootPath = RootedWorkspacePath(context);
            ProjectedNode projected = new(
                workspace,
                workspace.Workspace?.Sessions
                    .Max(session => session.Session.LastActivityAtUtc),
                workspace.Header,
                workspace.Workspace?.Sessions.Count ?? 0);
            if (rootPath is null)
            {
                special.Add(projected);
                continue;
            }

            Insert(root, rootPath, projected);
        }

        return root.Children.Values
            .Select(Convert)
            .Concat(special)
            .OrderByDescending(node =>
                node.LastActivityAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(node => node.SortKey, StringComparer.OrdinalIgnoreCase)
            .Select(node => node.Node)
            .ToArray();
    }

    private ProjectedNode Convert(FolderNode folder)
    {
        ProjectedNode[] nested = folder.Children.Values
            .Select(Convert)
            .OrderByDescending(node =>
                node.LastActivityAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(node => node.SortKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (folder.Workspace is ProjectedNode workspace &&
            nested.Length == 0)
        {
            return workspace;
        }

        ProjectedNode[] members = folder.Workspace is ProjectedNode current
            ? [current, .. nested]
            : nested;
        int sessionCount = members.Sum(node => node.SessionCount);
        CursorReconstructionItemViewModel node = new(
            CursorReconstructionItemKind.Folder,
            folder.Segment,
            $"{sessionCount:N0} " +
                $"session{(sessionCount == 1 ? string.Empty : "s")}",
            $"Filesystem ancestor '{folder.FullPath}' groups workspaces " +
                "without changing their reconstruction identity.",
            folderPath: folder.FullPath)
        {
            IsExpanded = true
        };
        foreach (ProjectedNode member in members)
        {
            node.Children.Add(member.Node);
        }

        return new ProjectedNode(
            node,
            members
                .Select(member => member.LastActivityAtUtc)
                .Where(value => value is not null)
                .Max(),
            folder.Segment,
            sessionCount);
    }

    private void Insert(
        FolderNode root,
        string path,
        ProjectedNode workspace)
    {
        FolderNode current = root;
        foreach ((string segment, string fullPath) in SplitPath(path))
        {
            if (!current.Children.TryGetValue(
                    segment,
                    out FolderNode? child))
            {
                child = new FolderNode(segment, fullPath);
                current.Children[segment] = child;
            }

            current = child;
        }

        current.Workspace ??= workspace;
    }

    private string? RootedWorkspacePath(WorkspaceContext workspace)
    {
        if (workspace.Kind != WorkspaceContextKind.Normal ||
            workspace.DisplayRoots.Count != 1)
        {
            return null;
        }

        string candidate = workspace.DisplayRoots[0];
        return Path.IsPathRooted(candidate) ? candidate : null;
    }

    private IReadOnlyList<(string Segment, string FullPath)> SplitPath(
        string path)
    {
        List<(string Segment, string FullPath)> steps = [];
        string root;
        try
        {
            root = Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            root = string.Empty;
        }

        string remainder;
        if (root.Length > 0)
        {
            steps.Add((root, root));
            remainder =
                path.Length > root.Length ? path[root.Length..] : string.Empty;
        }
        else
        {
            remainder = path;
        }

        string accumulated = root;
        foreach (string segment in remainder.Split(
                     [
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar
                     ],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = Join(accumulated, segment);
            steps.Add((segment, accumulated));
        }

        return steps;
    }

    private string Join(string parent, string child)
    {
        if (parent.Length == 0)
        {
            return child;
        }

        return parent[^1] is '\\' or '/'
            ? parent + child
            : parent + Path.DirectorySeparatorChar + child;
    }

    private sealed class FolderNode(string segment, string fullPath)
    {
        public string Segment { get; } = segment;

        public string FullPath { get; } = fullPath;

        public Dictionary<string, FolderNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ProjectedNode? Workspace { get; set; }
    }

    private sealed record ProjectedNode(
        CursorReconstructionItemViewModel Node,
        DateTimeOffset? LastActivityAtUtc,
        string SortKey,
        int SessionCount);
}
