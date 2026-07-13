using System.Text.RegularExpressions;

namespace VoiceAssistant.API.Services;

public class VisualAssetService
{
    private static readonly Regex SafeStorageFileName =
        new(@"\A[0-9a-f]{32}\.(jpg|png|webp)\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _rootPath;
    private readonly ILogger<VisualAssetService> _logger;

    public VisualAssetService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<VisualAssetService> logger)
    {
        _rootPath = ResolveVisualPath(configuration["Visual:Path"], environment.ContentRootPath);
        _logger = logger;
    }

    public static string ResolveVisualPath(string? configuredPath, string contentRootPath)
    {
        var visualPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(contentRootPath, "visual")
            : configuredPath;
        return Path.GetFullPath(Path.IsPathRooted(visualPath)
            ? visualPath
            : Path.Combine(contentRootPath, visualPath));
    }

    public static string CreateStorageFileName(Guid id, string mimeType) =>
        $"{id:N}.{ImageContentInspector.ExtensionForMimeType(mimeType)}";

    public bool TryResolvePath(string storageFileName, out string filePath)
    {
        filePath = "";
        if (!SafeStorageFileName.IsMatch(storageFileName)) return false;

        var candidate = Path.GetFullPath(Path.Combine(_rootPath, storageFileName));
        var relative = Path.GetRelativePath(_rootPath, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;

        filePath = candidate;
        return true;
    }

    public async Task WriteAsync(string storageFileName, byte[] content, CancellationToken cancellationToken)
    {
        if (!TryResolvePath(storageFileName, out var path))
            throw new InvalidOperationException("Invalid visual asset storage filename");

        Directory.CreateDirectory(_rootPath);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove temporary visual asset file");
            }
        }
    }

    public async Task<byte[]?> ReadAsync(string storageFileName, CancellationToken cancellationToken)
    {
        if (!TryResolvePath(storageFileName, out var path) || !File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public bool TryOpenRead(string storageFileName, out FileStream? stream)
    {
        stream = null;
        if (!TryResolvePath(storageFileName, out var path) || !File.Exists(path)) return false;
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return true;
    }

    public Task DeleteIfExistsAsync(string storageFileName)
    {
        if (!TryResolvePath(storageFileName, out var path) || !File.Exists(path)) return Task.CompletedTask;
        File.Delete(path);
        return Task.CompletedTask;
    }
}
