using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core;

public sealed class RegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates =
        new(StringComparer.OrdinalIgnoreCase);

    public RegistryStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalProjectBridge")) { }

    public RegistryStore(string appDataDirectory) => AppDataDirectory = Path.GetFullPath(appDataDirectory);

    public string AppDataDirectory { get; }
    private string SettingsPath => Path.Combine(AppDataDirectory, "projects.json");
    private string ConnectionsPath => Path.Combine(AppDataDirectory, "connections.json");

    public AppSettings Load()
    {
        TryRecoverLegacyTemporary<AppSettings>(SettingsPath);
        if (!File.Exists(SettingsPath)) return new AppSettings();
        using var stream = OpenForSharedRead(SettingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        Normalize(settings);
        return settings;
    }

    public async Task<AppSettings> LoadAsync()
    {
        TryRecoverLegacyTemporary<AppSettings>(SettingsPath);
        if (!File.Exists(SettingsPath)) return new AppSettings();
        await using var stream = OpenForSharedRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new AppSettings();
        Normalize(settings);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Normalize(settings);
        await SaveJsonAsync(SettingsPath, settings);
    }

    public ConnectionProfile LoadConnection()
    {
        TryRecoverLegacyTemporary<ConnectionProfile>(ConnectionsPath);
        if (!File.Exists(ConnectionsPath)) return new ConnectionProfile();
        using var stream = OpenForSharedRead(ConnectionsPath);
        var profile = JsonSerializer.Deserialize<ConnectionProfile>(stream, JsonOptions) ?? new ConnectionProfile();
        if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "ProjectBridge";
        return profile;
    }

    public async Task SaveConnectionAsync(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "ProjectBridge";
        await SaveJsonAsync(ConnectionsPath, profile);
    }

    /// <summary>
    /// Updates readiness timestamps only when the persisted runtime configuration still describes
    /// the active connection. The comparison and write share the same gate as configuration saves.
    /// </summary>
    public async Task<ConnectionProfile?> TryUpdateConnectionReadinessAsync(ConnectionProfile activeProfile)
    {
        ArgumentNullException.ThrowIfNull(activeProfile);
        var gate = SaveGates.GetOrAdd(ConnectionsPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var persisted = LoadConnection();
            if (!ConnectionProfileConfiguration.RuntimeConfigurationEquals(activeProfile, persisted)) return null;
            persisted.LastConnectedAt = activeProfile.LastConnectedAt;
            persisted.LastAuthorizedAt = activeProfile.LastAuthorizedAt;
            persisted.LastVerifiedCall = activeProfile.LastVerifiedCall;
            persisted.LastVerifiedClientId = activeProfile.LastVerifiedClientId;
            await SaveJsonWithoutGateAsync(ConnectionsPath, persisted);
            return persisted;
        }
        finally { gate.Release(); }
    }

    private static FileStream OpenForSharedRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static void TryRecoverLegacyTemporary<T>(string path)
    {
        var legacyTemporary = path + ".tmp";
        try
        {
            if (!File.Exists(legacyTemporary)) return;
            if (File.Exists(path) && File.GetLastWriteTimeUtc(legacyTemporary) <= File.GetLastWriteTimeUtc(path)) return;
            using (var stream = OpenForSharedRead(legacyTemporary))
                if (JsonSerializer.Deserialize<T>(stream, JsonOptions) is null) return;
            File.Move(legacyTemporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A legacy writer may still own the file, or the interrupted write may be incomplete.
            // In both cases the last valid primary file remains authoritative.
        }
    }

    private async Task SaveJsonAsync<T>(string path, T value)
    {
        var gate = SaveGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await SaveJsonWithoutGateAsync(path, value);
        }
        finally { gate.Release(); }
    }

    private async Task SaveJsonWithoutGateAsync<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            await ReplaceWithRetryAsync(temporary, path);
            TryDelete(path + ".tmp");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task ReplaceWithRetryAsync(string temporary, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, target, true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(25 * (attempt + 1));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>按规范化路径登记项目；同一路径忽略大小写只登记一次。</summary>
    public static ProjectRecord UpsertProject(AppSettings settings, string path, string? name = null)
    {
        var fullPath = Path.GetFullPath(path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var project = settings.Projects.FirstOrDefault(p =>
            string.Equals(p.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            project = new ProjectRecord
            {
                Name = name ?? System.IO.Path.GetFileName(fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
                Path = fullPath
            };
            settings.Projects.Add(project);
        }
        else if (!string.IsNullOrWhiteSpace(name)) project.Name = name.Trim();
        project.Path = fullPath;
        return project;
    }

    private static void Normalize(AppSettings settings)
    {
        foreach (var project in settings.Projects.Where(p => p.Id == Guid.Empty))
            project.Id = Guid.NewGuid();
        if (settings.SelectedProjectId is null && !string.IsNullOrWhiteSpace(settings.SelectedProjectPath))
            settings.SelectedProjectId = settings.Projects.FirstOrDefault(project =>
                string.Equals(project.Path, settings.SelectedProjectPath, StringComparison.OrdinalIgnoreCase))?.Id;
        if (settings.SelectedProjectId is { } selectedId && settings.Projects.All(project => project.Id != selectedId))
            settings.SelectedProjectId = settings.Projects.FirstOrDefault()?.Id;
        settings.SelectedProjectPath = settings.Projects.FirstOrDefault(project => project.Id == settings.SelectedProjectId)?.Path;
        settings.Version = Math.Max(settings.Version, 2);
    }
}
