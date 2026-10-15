using System.Collections.Concurrent;
using System.Diagnostics;

using CareFlow.BuildingBlocks.AspNetCore.Observability;

using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace CareFlow.BuildingBlocks.Tests.Observability;

public sealed partial class RedactionProcessorTests
{
    private const string Ref = "3f2a9c1e-1b2c-4d5e-8f90-123456789abc";
    private const string PatientPath = "/v1/patients/" + Ref;

    [Fact]
    public void Span_ServerPath_BecomesTheRouteTemplate()
    {
        // Arrange
        using var activity = new Activity("GET");
        activity.SetTag("http.route", "/v1/patients/{patientRef:guid}");
        activity.SetTag("url.path", $"/v1/patients/{Ref}");
        activity.SetTag("url.query", "?cpf=12345678909");

        // Act
        new SpanRedactionProcessor().OnEnd(activity);

        // Assert
        Assert.Equal("/v1/patients/{patientRef:guid}", activity.GetTagItem("url.path"));
        Assert.Null(activity.GetTagItem("url.query"));
    }

    [Fact]
    public void Span_ClientUrl_AndFreeText_AreMasked()
    {
        // Arrange
        using var activity = new Activity($"GET /v1/patients/{Ref}");
        activity.SetTag("url.full", $"http://vault/v1/patients/{Ref}/contact?x=1");
        activity.SetTag("url.path", $"/v1/patients/{Ref}/contact");
        activity.SetTag("error.detail", "falhou para sintetico@example.com");
        activity.SetTag("http.response.status_code", 200);
        activity.SetStatus(ActivityStatusCode.Error, "CPF 123.456.789-09 recusado");

        // Act
        new SpanRedactionProcessor().OnEnd(activity);

        // Assert
        Assert.Equal("http://vault/v1/patients/{id}/contact", activity.GetTagItem("url.full"));
        Assert.Equal("/v1/patients/{id}/contact", activity.GetTagItem("url.path"));
        Assert.Equal("falhou para [email]", activity.GetTagItem("error.detail"));
        Assert.Equal(200, activity.GetTagItem("http.response.status_code"));
        Assert.Equal("CPF [cpf] recusado", activity.StatusDescription);
        Assert.Equal("GET /v1/patients/{id}", activity.DisplayName);
    }

    [Fact]
    public void Span_WithoutPersonalData_IsUntouched()
    {
        // Arrange
        using var activity = new Activity("publish consent.revoked");
        activity.SetTag("messaging.system", "rabbitmq");

        // Act
        new SpanRedactionProcessor().OnEnd(activity);

        // Assert
        Assert.Equal("rabbitmq", activity.GetTagItem("messaging.system"));
        Assert.Equal("publish consent.revoked", activity.DisplayName);
    }

    [Fact]
    public void Log_FrameworkCategories_LoseIdentifiers_OwnCategoriesKeepThem_BothLosePersonalData()
    {
        // Arrange
        var exported = new ConcurrentQueue<(string Category, string Message, string Attributes)>();
        using (var factory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
               {
                   options.IncludeFormattedMessage = true;
                   options.AddProcessor(new LogRedactionProcessor());
                   options.AddProcessor(new SimpleLogRecordExportProcessor(new CapturingExporter(exported)));
               })))
        {
            // Act
            var framework = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
            LogRequest(framework, PatientPath);
            var own = factory.CreateLogger("CareFlow.Notifications.Delivery");
            LogNotice(own, Ref, "sintetico@example.com");
            LogCount(own, 3);
        }

        // Assert
        var entries = exported.ToArray();
        Assert.Equal(3, entries.Length);
        Assert.Equal("Request starting GET /v1/patients/{id}", entries[0].Message);
        Assert.Contains("Path=/v1/patients/{id}", entries[0].Attributes, StringComparison.Ordinal);
        Assert.Equal($"Aviso {Ref} para [email]", entries[1].Message);
        Assert.DoesNotContain("example.com", entries[1].Attributes, StringComparison.Ordinal);
        Assert.Equal("Sem atributos de texto: 3", entries[2].Message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request starting GET {Path}")]
    private static partial void LogRequest(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aviso {NotificationId} para {Email}")]
    private static partial void LogNotice(ILogger logger, string notificationId, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sem atributos de texto: {Count}")]
    private static partial void LogCount(ILogger logger, int count);

    private sealed class CapturingExporter(ConcurrentQueue<(string, string, string)> sink) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                sink.Enqueue((
                    record.CategoryName ?? string.Empty,
                    record.FormattedMessage ?? string.Empty,
                    string.Join(';', (record.Attributes ?? []).Select(a => $"{a.Key}={a.Value}"))));
            }

            return ExportResult.Success;
        }
    }
}
