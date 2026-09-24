namespace Paradise.Cli.Test;

public class ShellFoldersTests
{
    [Test]
    public async Task open_does_not_create_a_missing_directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "paradise-tray-open-" + Guid.NewGuid().ToString("N"));
        try
        {
            ShellFolders.Open(path);
            await Assert.That(Directory.Exists(path)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Test]
    public async Task open_rejects_an_empty_path()
    {
        await Assert.That(() => ShellFolders.Open("")).Throws<ArgumentException>();
    }
}
