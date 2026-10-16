using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CareFlow.Rights.Api.Infrastructure.Persistence;

/// <summary>
/// Índice de expiração dos pacotes, a única tabela sem RLS: a limpeza precisa saber quais clínicas têm pacote
/// vencido para, então, entrar em cada uma e apagar pelo caminho normal (com RLS). Só tem identificadores e a data;
/// nenhum conteúdo, chave ou pseudônimo.
/// </summary>
internal sealed class PackageExpiration
{
    public Guid RequestId { get; init; }

    public Guid TenantId { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class PackageExpirationConfiguration : IEntityTypeConfiguration<PackageExpiration>
{
    public const string TableName = "package_expirations";

    public void Configure(EntityTypeBuilder<PackageExpiration> builder)
    {
        builder.ToTable(TableName);
        builder.HasKey(e => e.RequestId).HasName("pk_package_expirations");
        builder.Property(e => e.RequestId).HasColumnName("request_id").ValueGeneratedNever();
        builder.Property(e => e.TenantId).HasColumnName("tenant_id");
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_package_expirations_expires_at");
    }
}
