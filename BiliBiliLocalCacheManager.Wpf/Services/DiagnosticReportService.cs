using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class DiagnosticReportService(
    IDiagnosticEventRecorder eventRecorder,
    ISensitiveDataRedactor redactor) : IDiagnosticReportService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DiagnosticReportResult> ExportAsync(
        DiagnosticReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.RedactionContext);
        cancellationToken.ThrowIfCancellationRequested();

        var outputPath = Path.GetFullPath(request.DestinationPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The diagnostic report directory does not exist: {outputDirectory}");
        }

        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.writing";
        var events = eventRecorder.GetRecentEvents();
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                using (var archive = new ZipArchive(
                           output,
                           ZipArchiveMode.Create,
                           leaveOpen: true))
                {
                    await WriteRedactedJsonEntryAsync(
                        archive,
                        "diagnostics.json",
                        new DiagnosticDocument(DateTimeOffset.UtcNow, request.Context),
                        request.RedactionContext,
                        cancellationToken);
                    await WriteRedactedJsonEntryAsync(
                        archive,
                        "recent-events.json",
                        events,
                        request.RedactionContext,
                        cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
            return new DiagnosticReportResult(
                outputPath,
                new FileInfo(outputPath).Length,
                events.Count);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task WriteRedactedJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        SensitiveDataRedactionContext redactionContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var element = JsonSerializer.SerializeToElement(value, SerializerOptions);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        using var writer = new Utf8JsonWriter(
            entryStream,
            new JsonWriterOptions { Indented = true });
        WriteRedactedElement(writer, element, redactionContext, cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private void WriteRedactedElement(
        Utf8JsonWriter writer,
        JsonElement element,
        SensitiveDataRedactionContext redactionContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WritePropertyName(property.Name);
                    WriteRedactedElement(
                        writer,
                        property.Value,
                        redactionContext,
                        cancellationToken);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedElement(writer, item, redactionContext, cancellationToken);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(
                    redactor.Redact(element.GetString() ?? string.Empty, redactionContext));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort: never hide the original export failure.
        }
    }

    private sealed record DiagnosticDocument(
        DateTimeOffset GeneratedAtUtc,
        DiagnosticReportContext Context);
}
