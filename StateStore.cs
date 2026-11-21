using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexChime;

internal sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexChime", "settings.json");

    public SavedState Load()
    {
        try
        {
            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<SavedState>(stream, JsonOptions);
            if (state?.Timers is null || state.Reminders is null || state.Timers.Exists(timer => timer is null) ||
                state.Reminders.Exists(reminder => reminder is null || reminder.Text is null ||
                    reminder.Hour is < 0 or > 23 || reminder.Minute is < 0 or > 59))
                throw new JsonException("Invalid ApexChime settings.");

            return state;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new();
        }
    }

    public void Save(SavedState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }
}
