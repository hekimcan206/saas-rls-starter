using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SaasRls.Tenancy.Npgsql;
using Serilog;
using Serilog.Formatting.Compact;

namespace SaasRls.Api.Infrastructure;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Structured JSON logs on stdout plus OpenTelemetry traces and metrics. The OTLP exporter is enabled only when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set, so the service runs without a collector.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        // Configured through DI rather than a static bootstrap logger, so several hosts can live in one process
        // (WebApplicationFactory instances in integration tests).
        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? builder.Environment.ApplicationName;
        var serviceVersion = typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString(3);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql")
                // tenancy.session_variable.leaks: a pooled connection arrived with the tenant set at session level.
                .AddMeter(TenantDataSource.MeterName));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
