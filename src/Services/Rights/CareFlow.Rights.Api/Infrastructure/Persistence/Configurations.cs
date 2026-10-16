using CareFlow.Rights.Domain.Requests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Rights.Api.Infrastructure.Persistence;

internal sealed class SubjectRequestConfiguration : IEntityTypeConfiguration<SubjectRequest>
{
    public const string TableName = "subject_requests";

    /// <summary>Um pedido em aberto por paciente e tipo: repetir o pedido não dispara outro código nem outra exportação.</summary>
    public const string OpenRequestIndex = "ux_subject_requests_open";

    public void Configure(EntityTypeBuilder<SubjectRequest> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint("ck_subject_requests_kind", "kind IN ('Access', 'Portability', 'Erasure')");
            table.HasCheckConstraint("ck_subject_requests_status", "status IN ('Received', 'InProgress', 'Completed', 'Denied')");
            table.HasCheckConstraint("ck_subject_requests_denial", "(status = 'Denied') = (denial_reason IS NOT NULL)");
            table.HasCheckConstraint("ck_subject_requests_code_attempts", "code_attempts BETWEEN 0 AND 5");
            table.HasCheckConstraint("ck_subject_requests_code_hash", "code_hash IS NULL OR octet_length(code_hash) = 32");
            table.HasCheckConstraint("ck_subject_requests_code_only_while_received", "code_hash IS NULL OR status = 'Received'");
            table.HasCheckConstraint("ck_subject_requests_key_only_while_open", "wrapped_recipient_key IS NULL OR status = 'InProgress'");
        });

        builder.HasKey(r => r.Id).HasName("pk_subject_requests");
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id");
        builder.Property(r => r.PatientRef).HasColumnName("patient_ref");
        builder.Property(r => r.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.DenialReason).HasColumnName("denial_reason").HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.ReceivedAt).HasColumnName("received_at");
        builder.Property(r => r.DueAt).HasColumnName("due_at");
        builder.Property(r => r.CodeHash).HasColumnName("code_hash");
        builder.Property(r => r.CodeExpiresAt).HasColumnName("code_expires_at");
        builder.Property(r => r.CodeAttempts).HasColumnName("code_attempts");
        builder.Property(r => r.CodeSentAt).HasColumnName("code_sent_at");
        builder.Property(r => r.CodeChannel).HasColumnName("code_channel").HasMaxLength(16);
        builder.Property(r => r.IdentityConfirmedAt).HasColumnName("identity_confirmed_at");
        builder.Property(r => r.CompletedAt).HasColumnName("completed_at");
        builder.Property(r => r.PolicyVersion).HasColumnName("policy_version").HasMaxLength(32);
        builder.Property(r => r.WrappedRecipientKey).HasColumnName("wrapped_recipient_key");
        builder.Ignore(r => r.IsExport);
        builder.Ignore(r => r.IsFinal);
        builder.Ignore(r => r.RemainingAttempts);

        // Concorrência otimista pelo xmin: duas tentativas de código simultâneas não somam uma só.
        builder.Property<uint>("RowVersion").IsRowVersion();

        builder.HasIndex(r => new { r.TenantId, r.PatientRef, r.Kind })
            .IsUnique()
            .HasFilter("status IN ('Received', 'InProgress')")
            .HasDatabaseName(OpenRequestIndex);
    }
}

internal sealed class SubjectRequestAnswerConfiguration : IEntityTypeConfiguration<SubjectRequestAnswer>
{
    public const string TableName = "subject_request_answers";

    public void Configure(EntityTypeBuilder<SubjectRequestAnswer> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(a => new { a.RequestId, a.Service }).HasName("pk_subject_request_answers");
        builder.Property(a => a.RequestId).HasColumnName("request_id");
        builder.Property(a => a.Service).HasColumnName("service").HasMaxLength(32);
        builder.Property(a => a.TenantId).HasColumnName("tenant_id");
        builder.Property(a => a.Outcome).HasColumnName("outcome").HasMaxLength(32);
        builder.Property(a => a.SealedSection).HasColumnName("sealed_section");
        builder.Property(a => a.Erasure).HasColumnName("erasure").HasColumnType("jsonb");
        builder.Property(a => a.AnsweredAt).HasColumnName("answered_at");

        builder.HasOne<SubjectRequest>().WithMany().HasForeignKey(a => a.RequestId)
            .HasConstraintName("fk_subject_request_answers_requests").OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SubjectRequestPackageConfiguration : IEntityTypeConfiguration<SubjectRequestPackage>
{
    public const string TableName = "subject_request_packages";

    public void Configure(EntityTypeBuilder<SubjectRequestPackage> builder)
    {
        builder.ToTable(TableName, table =>
        {
            table.HasCheckConstraint(
                "ck_subject_request_packages_destroyed",
                "(destroyed_at IS NULL) = (content IS NOT NULL AND wrapped_key IS NOT NULL)");
        });

        builder.HasKey(p => p.RequestId).HasName("pk_subject_request_packages");
        builder.Property(p => p.RequestId).HasColumnName("request_id").ValueGeneratedNever();
        builder.Property(p => p.TenantId).HasColumnName("tenant_id");
        builder.Property(p => p.Content).HasColumnName("content");
        builder.Property(p => p.WrappedKey).HasColumnName("wrapped_key");
        builder.Property(p => p.Size).HasColumnName("size");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.ExpiresAt).HasColumnName("expires_at");
        builder.Property(p => p.DestroyedAt).HasColumnName("destroyed_at");

        builder.HasOne<SubjectRequest>().WithOne().HasForeignKey<SubjectRequestPackage>(p => p.RequestId)
            .HasConstraintName("fk_subject_request_packages_requests").OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => p.ExpiresAt).HasFilter("destroyed_at IS NULL").HasDatabaseName("ix_subject_request_packages_expiring");
    }
}

internal sealed class DownloadLinkConfiguration : IEntityTypeConfiguration<DownloadLink>
{
    public const string TableName = "download_links";

    public void Configure(EntityTypeBuilder<DownloadLink> builder)
    {
        builder.ToTable(TableName, table =>
            table.HasCheckConstraint("ck_download_links_token_hash", "octet_length(token_hash) = 32"));

        builder.HasKey(l => l.Id).HasName("pk_download_links");
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(l => l.TenantId).HasColumnName("tenant_id");
        builder.Property(l => l.RequestId).HasColumnName("request_id");
        builder.Property(l => l.TokenHash).HasColumnName("token_hash");
        builder.Property(l => l.CreatedAt).HasColumnName("created_at");
        builder.Property(l => l.ExpiresAt).HasColumnName("expires_at");
        builder.Property(l => l.UsedAt).HasColumnName("used_at");

        // Dois downloads simultâneos com o mesmo link: só um grava used_at.
        builder.Property<uint>("RowVersion").IsRowVersion();

        builder.HasIndex(l => l.TokenHash).IsUnique().HasDatabaseName("ux_download_links_token_hash");
        builder.HasIndex(l => l.RequestId).HasDatabaseName("ix_download_links_request_id");
        builder.HasOne<SubjectRequest>().WithMany().HasForeignKey(l => l.RequestId)
            .HasConstraintName("fk_download_links_requests").OnDelete(DeleteBehavior.Cascade);
    }
}
