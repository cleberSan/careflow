using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using CareFlow.BuildingBlocks.Cryptography;
using CareFlow.BuildingBlocks.Messaging.Inbox;
using CareFlow.Contracts.Rights;
using CareFlow.PatientVault.IntegrationTests.Infrastructure;
using CareFlow.Testing;

using static CareFlow.PatientVault.IntegrationTests.Infrastructure.VaultClient;

namespace CareFlow.PatientVault.IntegrationTests.Rights;

/// <summary>
/// O cofre respondendo a pedidos do titular: exportação cifrada só para o pedido, eliminação por destruição da
/// chave do paciente, reaplicação depois de restaurar um backup e isolamento entre clínicas (ADR-0012).
/// </summary>
public sealed class SubjectRequestTests(VaultApiFixture api)
{
    private readonly Guid _clinic = Guid.NewGuid();

    private HttpClient Client => api.CreateClient(_clinic, Read, Write);

    private static SubjectRequestOpened Erasure(Guid patientRef, Guid? requestId = null) =>
        new(requestId ?? Guid.NewGuid(), patientRef, SubjectRequestKinds.Erasure, null, RetentionPolicy.Version, DateTimeOffset.UtcNow);

    private async Task<SubjectRequestAnswered> AnswerAsync(Guid clinic, Guid requestId)
    {
        var rows = await api.OutboxAsync(clinic);
        var row = Assert.Single(rows, r => r.Type == SubjectRequestAnswered.EventType && r.Payload.GetProperty("requestId").GetGuid() == requestId);
        return row.Payload.Deserialize<SubjectRequestAnswered>(Json)!;
    }

    [Fact]
    public async Task Export_SealsTheSectionForTheRequestOnly()
    {
        // Arrange
        var request = NewPatient();
        var (patientRef, _) = await Client.RegisterOkAsync(request);
        using var requestKey = SealedBox.CreateKeyPair();
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), patientRef, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(requestKey), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        var outcome = await api.DeliverAsync(_clinic, opened);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var answer = await AnswerAsync(_clinic, opened.RequestId);
        Assert.Equal(SubjectRequestOutcomes.Exported, answer.Outcome);
        Assert.Equal(SubjectRequestServices.Vault, answer.Service);
        Assert.DoesNotContain(request.FullName!, answer.SealedSection!, StringComparison.Ordinal);

        var json = SealedBox.Open(requestKey, answer.SealedSection!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Vault));
        using var section = JsonDocument.Parse(json);
        Assert.Equal(request.FullName, section.RootElement.GetProperty("fullName").GetString());
        Assert.Equal(request.Cpf, section.RootElement.GetProperty("cpf").GetString());

        // Outro pedido ou outro serviço não abrem a mesma seção.
        Assert.ThrowsAny<CryptographicException>(() =>
            SealedBox.Open(requestKey, answer.SealedSection!, SubjectRequestSealing.SectionContext(Guid.NewGuid(), SubjectRequestServices.Vault)));
        using var otherKey = SealedBox.CreateKeyPair();
        Assert.ThrowsAny<CryptographicException>(() =>
            SealedBox.Open(otherKey, answer.SealedSection!, SubjectRequestSealing.SectionContext(opened.RequestId, SubjectRequestServices.Vault)));

        var outbox = await api.OutboxAsync(_clinic);
        Assert.Contains(outbox, row => row.AuditAction == "patient.exported");
    }

    [Fact]
    public async Task Export_WithoutRequestKey_IsRejectedInsteadOfSentInTheClear()
    {
        // Arrange
        var (patientRef, _) = await Client.RegisterOkAsync(NewPatient());
        var opened = new SubjectRequestOpened(
            Guid.NewGuid(), patientRef, SubjectRequestKinds.Portability, null, RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        var outcome = await api.DeliverAsync(_clinic, opened);

        // Assert: vai para a fila de mensagens mortas; nada entra no Outbox.
        Assert.Equal(ProcessOutcome.Invalid, outcome);
        var outbox = await api.OutboxAsync(_clinic);
        Assert.DoesNotContain(outbox, row => row.Type == SubjectRequestAnswered.EventType);
    }

    [Fact]
    public async Task Erasure_DestroysThePatientKey_AndTheDataBecomesUnreadable()
    {
        // Arrange
        var request = NewPatient();
        var (patientRef, _) = await Client.RegisterOkAsync(request);
        var ciphertextBefore = await api.AdminScalarAsync<byte[]>("SELECT full_name_enc FROM patients WHERE patient_ref = @p", ("p", patientRef));
        var opened = Erasure(patientRef);

        // Act
        await api.DeliverAsync(_clinic, opened);

        // Assert: a chave sumiu; o texto cifrado continua lá, sem nada que o abra.
        Assert.Equal(0, await api.PatientKeyCountAsync(patientRef));
        Assert.Equal(ciphertextBefore, await api.AdminScalarAsync<byte[]>("SELECT full_name_enc FROM patients WHERE patient_ref = @p", ("p", patientRef)));
        Assert.Null(await api.AdminScalarAsync<byte[]>("SELECT cpf_index FROM patients WHERE patient_ref = @p", ("p", patientRef)));

        var read = await Client.GetAsync($"/v1/patients/{patientRef}", Ct);
        Assert.Equal(HttpStatusCode.Gone, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.LookupAsync(request.Cpf)).StatusCode);
        var internalClient = api.CreateServiceClient(api.InternalTokens.Issue(_clinic, "vault.contact.read"));
        Assert.Equal(HttpStatusCode.NotFound, (await internalClient.GetAsync($"/v1/patients/{patientRef}/contact", Ct)).StatusCode);

        var answer = await AnswerAsync(_clinic, opened.RequestId);
        Assert.Equal(SubjectRequestOutcomes.Erased, answer.Outcome);
        Assert.Null(answer.SealedSection);
        Assert.Contains(answer.Erasure, line => line is { Item: RetentionPolicy.Items.VaultPersonalData, Action: RetentionActions.Destroyed, Records: 1 });
        Assert.Contains(answer.Erasure, line => line is { Item: RetentionPolicy.Items.VaultPseudonym, Action: RetentionActions.Retained });
        Assert.Contains(await api.OutboxAsync(_clinic), row => row.AuditAction == "patient.erased");

        // O mesmo CPF pode ser cadastrado de novo: é outro pseudônimo, sem relação com o eliminado.
        var (again, _) = await Client.RegisterOkAsync(NewPatient(request.Cpf));
        Assert.NotEqual(patientRef, again);
    }

    [Fact]
    public async Task Erasure_OfLegacyRecord_OverwritesFieldsThatTheClinicKeyCouldStillOpen()
    {
        // Arrange
        var patientRef = await api.SeedLegacyPatientAsync(_clinic, SyntheticPatients.Cpf());
        var before = await api.AdminScalarAsync<byte[]>("SELECT cpf_enc FROM patients WHERE patient_ref = @p", ("p", patientRef));

        // Act
        await api.DeliverAsync(_clinic, Erasure(patientRef));

        // Assert
        var after = await api.AdminScalarAsync<byte[]>("SELECT cpf_enc FROM patients WHERE patient_ref = @p", ("p", patientRef));
        Assert.Equal(before!.Length, after!.Length);
        Assert.NotEqual(before, after);
        Assert.Equal(HttpStatusCode.Gone, (await Client.GetAsync($"/v1/patients/{patientRef}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Erasure_IsIdempotent_AndAnUpdateAfterwardsIsRefused()
    {
        // Arrange
        var (patientRef, etag) = await Client.RegisterOkAsync(NewPatient());
        await api.DeliverAsync(_clinic, Erasure(patientRef));
        var second = Erasure(patientRef);

        // Act
        var outcome = await api.DeliverAsync(_clinic, second);
        var update = await Client.UpdateAsync(
            patientRef, new Api.Features.Patients.UpdatePatientRequest(SyntheticPatients.FullName(), new DateOnly(1990, 1, 1), null, null), etag);

        // Assert
        Assert.Equal(ProcessOutcome.Processed, outcome);
        var answer = await AnswerAsync(_clinic, second.RequestId);
        Assert.Equal(SubjectRequestOutcomes.Erased, answer.Outcome);
        Assert.Contains(answer.Erasure, line => line is { Item: RetentionPolicy.Items.VaultPersonalData, Records: 0 });
        Assert.NotEqual(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(0, await api.PatientKeyCountAsync(patientRef));
    }

    [Fact]
    public async Task BackupRestore_BringsTheKeyBack_AndReplayingTheErasureDestroysItAgain()
    {
        // Arrange: "backup" das duas linhas antes da eliminação.
        var (patientRef, _) = await Client.RegisterOkAsync(NewPatient());
        var wrapped = await api.AdminScalarAsync<byte[]>("SELECT wrapped_key FROM patient_keys WHERE patient_ref = @p", ("p", patientRef));
        var version = await api.AdminScalarAsync<int>("SELECT tenant_key_version FROM patient_keys WHERE patient_ref = @p", ("p", patientRef));
        var opened = Erasure(patientRef);
        await api.DeliverAsync(_clinic, opened);

        // "Restauração": a chave e o cadastro voltam ao estado anterior.
        await api.AdminExecuteAsync(
            "INSERT INTO patient_keys (tenant_id, patient_ref, tenant_key_version, wrapped_key, created_at, updated_at) VALUES (@t, @p, @v, @k, now(), now())",
            ("t", _clinic), ("p", patientRef), ("v", version), ("k", wrapped!));
        await api.AdminExecuteAsync("UPDATE patients SET erased_at = NULL WHERE patient_ref = @p", ("p", patientRef));
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/v1/patients/{patientRef}", Ct)).StatusCode);

        // Act
        await api.DeliverAsync(_clinic, new ErasureReapplied(opened.RequestId, patientRef, RetentionPolicy.Version, DateTimeOffset.UtcNow));

        // Assert
        Assert.Equal(0, await api.PatientKeyCountAsync(patientRef));
        Assert.Equal(HttpStatusCode.Gone, (await Client.GetAsync($"/v1/patients/{patientRef}", Ct)).StatusCode);
        Assert.Contains(await api.OutboxAsync(_clinic), row => row.AuditAction == "patient.erasure-reapplied");
    }

    [Fact]
    public async Task RequestFromAnotherClinic_NeverTouchesThePatient()
    {
        // Arrange: o paciente é da clínica B; o pedido chega com a clínica A.
        var clinicB = Guid.NewGuid();
        var (patientOfB, _) = await api.CreateClient(clinicB, Read, Write).RegisterOkAsync(NewPatient());
        var erasure = Erasure(patientOfB);
        using var requestKey = SealedBox.CreateKeyPair();
        var export = new SubjectRequestOpened(
            Guid.NewGuid(), patientOfB, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(requestKey), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        await api.DeliverAsync(_clinic, erasure);
        await api.DeliverAsync(_clinic, export);

        // Assert
        Assert.Equal(SubjectRequestOutcomes.SubjectNotFound, (await AnswerAsync(_clinic, erasure.RequestId)).Outcome);
        Assert.Equal(SubjectRequestOutcomes.SubjectNotFound, (await AnswerAsync(_clinic, export.RequestId)).Outcome);
        Assert.Equal(1, await api.PatientKeyCountAsync(patientOfB));
        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient(clinicB, Read).GetAsync($"/v1/patients/{patientOfB}", Ct)).StatusCode);
        Assert.DoesNotContain(await api.OutboxAsync(clinicB), row => row.Type == SubjectRequestAnswered.EventType);
    }

    [Fact]
    public async Task Export_OfErasedPatient_AnswersSubjectNotFound()
    {
        // Arrange
        var (patientRef, _) = await Client.RegisterOkAsync(NewPatient());
        await api.DeliverAsync(_clinic, Erasure(patientRef));
        using var requestKey = SealedBox.CreateKeyPair();
        var export = new SubjectRequestOpened(
            Guid.NewGuid(), patientRef, SubjectRequestKinds.Access, SealedBox.ExportPublicKey(requestKey), RetentionPolicy.Version, DateTimeOffset.UtcNow);

        // Act
        await api.DeliverAsync(_clinic, export);

        // Assert
        Assert.Equal(SubjectRequestOutcomes.SubjectNotFound, (await AnswerAsync(_clinic, export.RequestId)).Outcome);
    }
}
