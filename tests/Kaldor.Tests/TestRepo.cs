using System.Text.Json;

namespace Kaldor.Tests;

/// <summary>A throwaway git repository with Kaldor enabled, as `kaldor init` leaves it minus the hook.</summary>
static class TestRepo
{
    public static void Create(string root)
    {
        Directory.CreateDirectory(root);
        Proc.Git(root, "init", "-q", "-b", "main");
        Proc.Git(root, "config", "user.email", "test@example.com");
        Proc.Git(root, "config", "user.name", "Test");
        Proc.Git(root, "config", "commit.gpgsign", "false");
        Directory.CreateDirectory(Path.Combine(root, ".kaldor"));
        File.WriteAllText(Path.Combine(root, ".kaldor", "rules.json"), JsonSerializer.Serialize(Rules.Default, Repo.Json));
        File.WriteAllText(Path.Combine(root, ".kaldor", ".gitignore"), "*\n!.gitignore\n!rules.json\n");
        Proc.Git(root, "add", "-A");
        Proc.Git(root, "commit", "-q", "-m", "init");
    }

    /// <summary>Deletes the directories that exist, read-only git objects included.</summary>
    public static void Delete(params string[] dirs)
    {
        foreach (var dir in dirs.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }
}
