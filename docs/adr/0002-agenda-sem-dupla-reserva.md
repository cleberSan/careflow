# ADR-0002: Serviço de agenda sem dupla reserva

- Status: aceito
- Data: 2026-10-14

## Contexto

O primeiro serviço do CareFlow é a agenda multi-clínica. A regra crítica é que um profissional nunca tenha duas consultas ativas sobrepostas, mesmo com várias réplicas da API recebendo reservas simultâneas para o mesmo horário. Clientes móveis e integrações também repetem requisições após timeouts, o que não pode gerar consultas duplicadas.

## Decisão

- **Garantia no banco, não na aplicação.** Uma *exclusion constraint* GiST no PostgreSQL (`btree_gist`) recusa intervalos `[início, fim)` sobrepostos para o mesmo `(tenant_id, professional_id)` enquanto a consulta está `Booked`. A API não faz checagem prévia, que estaria sujeita a TOCTOU: ela tenta inserir e traduz a violação `23P01` em `409 Agenda.HorarioIndisponivel`.
- **Idempotência obrigatória na criação.** `POST /v1/appointments` exige `Idempotency-Key`. A chave e o hash SHA-256 do conteúdo ficam na própria consulta, com índice único por clínica. Repetir a chave com o mesmo conteúdo devolve a consulta original (`Idempotent-Replayed: true`); com outro conteúdo, `409 Idempotencia.ChaveReutilizada`. Retentativas simultâneas com a mesma chave podem disparar a exclusion constraint antes do índice único, e esse caso também é tratado como replay.
- **Sem retry automático no `SaveChanges`.** Um commit ambíguo repetido pelo servidor poderia duplicar efeitos; a retentativa segura é do cliente, protegida pela chave.
- **Multi-tenancy.** A clínica vem da claim `tenant_id` do token. Um filtro global do EF Core restringe toda leitura à clínica atual, então acessar o id de outra clínica devolve 404 (sem IDOR). O contexto de tenant falha fechado: sem claim válida, a requisição é recusada pela política de autorização. Row-Level Security no banco entra com o Patient Vault (fase 3).
- **Autorização por escopo.** `scheduling.write` para reservar e cancelar; `scheduling.read` (ou `write`) para consultar. Tokens JWT validados pelo emissor configurado em `Authentication:Schemes:Bearer`.
- **Minimização de dados.** O paciente é só um pseudônimo (`patientRef`); dados pessoais ficam no Patient Vault. A listagem da agenda devolve apenas horários ocupados, sem o paciente. Logs e métricas não carregam `patientRef`.
- **Estrutura.** `CareFlow.Scheduling.Domain` (regras puras, sem dependência de infraestrutura) e `CareFlow.Scheduling.Api` (Minimal APIs organizadas em vertical slices, EF Core, OpenTelemetry). Erros de negócio viram ProblemDetails (RFC 9457) com o código estável em `code`.
- **Precisão de tempo.** Início e fim devem cair em minutos exatos; carimbos de criação e cancelamento são truncados para microssegundos (precisão do `timestamptz`), para que criação, leitura e replay devolvam exatamente os mesmos valores.

## Alternativas

- **Grade fixa de horários com índice único por slot:** mais simples, mas só funciona com duração fixa e não detecta sobreposição parcial.
- **Lock pessimista (`SELECT ... FOR UPDATE`) ou advisory lock por profissional:** funciona, mas serializa reservas e depende de todo caminho de escrita lembrar de pegar o lock. A constraint protege inclusive escritas fora da API.
- **Tabela separada de idempotência com respostas armazenadas:** genérica, porém desnecessária enquanto só a criação de consultas precisa de idempotência.
- **FluentValidation na borda:** duplicaria regras que já vivem no domínio e retornam `Result`. A borda valida só formato (cabeçalho e período da listagem).

## Consequências

- A regra de não sobreposição vale para qualquer cliente do banco, não só para esta API.
- Os testes de integração rodam contra PostgreSQL real (Testcontainers), incluindo 20 reservas simultâneas do mesmo horário com exatamente uma vencedora.
- O cadastro de profissionais ainda não existe: qualquer `professionalId` é aceito dentro da clínica. A validação entra quando houver o catálogo de profissionais.
- Migrations são aplicadas na subida apenas em desenvolvimento e testes. Em produção, rodam como etapa do pipeline de deploy.
