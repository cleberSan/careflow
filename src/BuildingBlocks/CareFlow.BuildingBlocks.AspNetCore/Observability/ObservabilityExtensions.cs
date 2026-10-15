using System.Diagnostics;
using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CareFlow.BuildingBlocks.AspNetCore.Observability;

public static class ObservabilityExtensions
{
    /// <summary>Prefixo de todas as fontes de trace e medidores do CareFlow (mensageria e serviços).</summary>
    public const string CareFlowSources = "CareFlow.*";

    /// <summary>
    /// Traces, métricas e logs com OpenTelemetry, correlacionados por trace. O exportador OTLP só é ligado
    /// quando <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> está configurado, então rodar localmente não exige coletor.
    /// Só o contexto W3C (<c>traceparent</c>) atravessa os serviços: baggage recebido de fora é descartado,
    /// e caminhos de URL saem com o template da rota no lugar de identificadores (ADR-0010).
    /// </summary>
    public static WebApplicationBuilder AddCareFlowObservability(
        this WebApplicationBuilder builder,
        string serviceName,
        params string[] meterNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        TraceContextOnlyPropagator.Install(builder.Services);

        // Logs de console (JSON em produção) também levam trace e span, para cruzar com o trace no Grafana.
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Logging.AddOpenTelemetry(logging =>
        {
            // Escopos ficam de fora: o do ASP.NET Core traz o caminho da requisição com identificadores.
            // A correlação vem do trace_id/span_id que todo registro exportado já carrega.
            logging.IncludeScopes = false;
            logging.IncludeFormattedMessage = true;
            logging.AddProcessor(new LogRedactionProcessor());
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: ServiceVersion(), autoGenerateServiceInstanceId: true)
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName.ToLowerInvariant())]))
            .WithTracing(tracing => tracing
                .AddSource(CareFlowSources)
                .AddSource("System.Net.Http")
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddNpgsql()
                .AddProcessor(new SpanRedactionProcessor()))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter(CareFlowSources, "System.Net.Http", "System.Runtime")
                .AddMeter(meterNames));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }

    private static string? ServiceVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}

/// <summary>
/// Propagador que só lê e escreve <c>traceparent</c>/<c>tracestate</c>. O padrão do .NET também propaga
/// <c>baggage</c>, que é texto livre vindo do cliente: um chamador poderia fazer um valor qualquer (inclusive
/// dado pessoal) viajar por todos os serviços. Aqui baggage não entra nem sai.
/// </summary>
public sealed class TraceContextOnlyPropagator : DistributedContextPropagator
{
    private static readonly DistributedContextPropagator W3C = CreateW3CPropagator();

    public static TraceContextOnlyPropagator Instance { get; } = new();

    public override IReadOnlyCollection<string> Fields { get; } = ["traceparent", "tracestate"];

    /// <summary>Usado pelo HttpClient (global) e pela hospedagem do ASP.NET Core (via DI).</summary>
    public static void Install(IServiceCollection services)
    {
        Current = Instance;
        services.RemoveAll<DistributedContextPropagator>();
        services.AddSingleton<DistributedContextPropagator>(Instance);
        Sdk.SetDefaultTextMapPropagator(new OpenTelemetry.Context.Propagation.TraceContextPropagator());
    }

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (activity is null || setter is null || activity.IdFormat != ActivityIdFormat.W3C || activity.Id is null)
        {
            return;
        }

        setter(carrier, "traceparent", activity.Id);
        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            setter(carrier, "tracestate", activity.TraceStateString);
        }
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        W3C.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
