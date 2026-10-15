using CareFlow.PatientVault.Api.Common.Security;

namespace CareFlow.PatientVault.Api.Features.Patients;

internal static class PatientEndpoints
{
    public static IServiceCollection AddPatientFeatures(this IServiceCollection services) =>
        services
            .AddScoped<RegisterPatientHandler>()
            .AddScoped<UpdatePatientHandler>();

    public static IEndpointRouteBuilder MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var patients = app.MapGroup("/v1/patients")
            .WithTags("Patients")
            .AddEndpointFilter(static async (context, next) =>
            {
                // Respostas do cofre nunca vão para cache de navegador ou proxy.
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context).ConfigureAwait(false);
            });

        patients.MapPost("/", RegisterPatientEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("RegisterPatient")
            .WithSummary("Cadastra um paciente e devolve só o pseudônimo (patientRef).");

        patients.MapGet("/{patientRef:guid}", GetPatientEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithName("GetPatient")
            .WithSummary("Devolve os dados pessoais decifrados e o ETag da versão atual.");

        patients.MapGet("/{patientRef:guid}/contact", GetPatientContactEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.ContactRead)
            .WithName("GetPatientContact")
            .WithSummary("Devolve só telefone e e-mail, para avisos ao paciente. Aceita token de serviço interno.");

        patients.MapGet("/{patientRef:guid}/triage-profile", GetTriageProfileEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.TriageProfileRead)
            .WithName("GetTriageProfile")
            .WithSummary("Devolve só idade em anos e nome, para a triagem mascarar a queixa. Aceita token de serviço interno.");

        patients.MapPut("/{patientRef:guid}", UpdatePatientEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Write)
            .WithName("UpdatePatient")
            .WithSummary("Altera nome, nascimento e contatos. Exige If-Match com o ETag lido.");

        patients.MapPost("/lookup", LookupPatientEndpoint.HandleAsync)
            .RequireAuthorization(AuthorizationPolicies.Read)
            .WithName("LookupPatient")
            .WithSummary("Encontra o patientRef pelo CPF, sem o CPF trafegar na URL.");

        return app;
    }
}
