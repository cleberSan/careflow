using System.Text.Json;

using CareFlow.Triage.Domain.Cases;
using CareFlow.Triage.Domain.Consents;
using CareFlow.Triage.Domain.Engine;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Triage.Api.Infrastructure.Persistence;

internal sealed class TriageCaseConfiguration : IEntityTypeConfiguration<TriageCase>
{
    public const string TableName = "triage_cases";

    public void Configure(EntityTypeBuilder<TriageCase> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint(
                "ck_triage_cases_complaint",
                $"(complaint IS NULL AND erased_at IS NOT NULL) OR char_length(complaint) BETWEEN {TriageCase.MinComplaintLength} AND {TriageEngine.MaxComplaintLength}");
            table.HasCheckConstraint("ck_triage_cases_model_status", $"model_status IN ({InList<ModelStatus>()})");
            table.HasCheckConstraint("ck_triage_cases_source", $"source IN ({InList<SuggestionSource>()})");
            table.HasCheckConstraint("ck_triage_cases_decision", $"decision IN ({InList<ReviewDecision>()})");
            table.HasCheckConstraint("ck_triage_cases_priorities", "suggested_priority BETWEEN 1 AND 4 AND model_priority BETWEEN 1 AND 4 AND final_priority BETWEEN 1 AND 4");
            // O que foi enviado tem sempre o digest, e o digest só existe se algo foi enviado.
            table.HasCheckConstraint("ck_triage_cases_sent", "(sent_to_model IS NULL) = (prompt_digest IS NULL) OR erased_at IS NOT NULL");
            // Revisada tem prioridade final, revisor e horário; pendente não tem nenhum dos três.
            table.HasCheckConstraint("ck_triage_cases_review", "(decision = 'Pending') = (final_priority IS NULL AND reviewed_by IS NULL AND reviewed_at IS NULL)");
        });

        builder.HasKey(c => c.Id).HasName("pk_triage_cases");
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id");
        builder.Property(c => c.PatientRef).HasColumnName("patient_ref");
        builder.Property(c => c.Complaint).HasColumnName("complaint").HasMaxLength(TriageEngine.MaxComplaintLength);
        builder.Property(c => c.AgeBand).HasColumnName("age_band").HasMaxLength(8);
        builder.Property(c => c.SentToModel).HasColumnName("sent_to_model").HasMaxLength(4 * TriageEngine.MaxComplaintLength);
        builder.Property(c => c.PromptDigest).HasColumnName("prompt_digest").HasMaxLength(64).IsFixedLength();
        builder.Property(c => c.PromptVersion).HasColumnName("prompt_version").HasMaxLength(32);
        builder.Property(c => c.ModelName).HasColumnName("model_name").HasMaxLength(64);
        builder.Property(c => c.ModelStatus).HasColumnName("model_status").HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.ModelPriority).HasColumnName("model_priority");
        builder.Property(c => c.SuggestedPriority).HasColumnName("suggested_priority");
        builder.Property(c => c.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.Rationale).HasColumnName("rationale").HasMaxLength(512);
        builder.Property(c => c.Guardrails).HasColumnName("guardrails").HasColumnType("text[]");
        builder.Property(c => c.AlarmSigns).HasColumnName("alarm_signs").HasColumnType("text[]");
        builder.Property(c => c.Masks).HasColumnName("masks").HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonSerializerOptions.Default) ?? new Dictionary<string, int>(),
                new ValueComparer<Dictionary<string, int>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    value => value.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    value => new Dictionary<string, int>(value)));
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
        builder.Property(c => c.ErasedAt).HasColumnName("erased_at");
        builder.Property(c => c.Decision).HasColumnName("decision").HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.FinalPriority).HasColumnName("final_priority");
        builder.Property(c => c.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(TriageCase.MaxReviewerLength);
        builder.Property(c => c.ReviewedAt).HasColumnName("reviewed_at");

        // Coluna de sistema xmin: duas revisões simultâneas não sobrescrevem uma à outra.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(c => new { c.TenantId, c.PatientRef, c.CreatedAt }).HasDatabaseName("ix_triage_cases_tenant_patient_created");
        builder.HasIndex(c => new { c.TenantId, c.Decision, c.CreatedAt }).HasDatabaseName("ix_triage_cases_tenant_decision_created");
    }

    private static string InList<TEnum>()
        where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"'{name}'"));
}

internal sealed class TriageConsentConfiguration : IEntityTypeConfiguration<TriageConsent>
{
    public const string TableName = "triage_consents";

    public void Configure(EntityTypeBuilder<TriageConsent> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_triage_consents_state", "state IN ('Unknown', 'Granted', 'Revoked')");
            table.HasCheckConstraint("ck_triage_consents_version", "version >= 0");
        });
        builder.HasKey(c => new { c.TenantId, c.PatientRef }).HasName("pk_triage_consents");
        builder.Property(c => c.TenantId).HasColumnName("tenant_id");
        builder.Property(c => c.PatientRef).HasColumnName("patient_ref");
        builder.Property(c => c.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Version).HasColumnName("version");
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        builder.Ignore(c => c.AllowsAi);
    }
}
