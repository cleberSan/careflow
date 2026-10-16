# ADR-0004: Patient Vault com cifragem por campo e chave por clínica

- Status: aceito
- Data: 2026-10-14

## Contexto

Os outros serviços do CareFlow (agenda, triagem, notificações) não precisam saber quem é o paciente, só precisam de uma referência estável a ele. Dados pessoais concentrados num único serviço reduzem a superfície de vazamento (minimização, LGPD art. 6º, III). Dentro desse serviço, o dado precisa continuar ilegível para quem tiver acesso ao banco ou a um backup, e a exclusão futura por clínica deve poder ser feita apagando uma chave (crypto-shredding).

## Decisão

- **Serviço próprio, `PatientVault`,** com banco próprio (`careflow_vault`). Os outros serviços recebem só o `patient_ref`: um UUID v4 aleatório, sem relação com os dados nem com a data de cadastro (por isso não UUID v7). O mesmo CPF em duas clínicas gera dois `patient_ref` e dois blind indexes diferentes, então não há como ligar os cadastros entre clínicas.
- **Envelope encryption com uma chave por clínica.** Uma KEK no KMS cifra a DEK de cada clínica (AES-256-GCM, com clínica e versão da chave como dado autenticado). A DEK cifrada fica em `tenant_keys`; a decifrada só existe em memória, em cache por 5 minutos (configurável) para não chamar o KMS a cada requisição.
- **Cifragem por campo.** Nome, CPF, nascimento, e-mail e telefone são cifrados um a um com AES-256-GCM (nonce aleatório de 96 bits, byte de versão de formato). O dado autenticado de cada campo inclui clínica, `patient_ref` e nome do campo: copiar um valor cifrado para outro paciente, outra coluna ou outra clínica faz a leitura falhar em vez de devolver o dado errado.
- **Subchaves por finalidade.** A DEK não é usada direto: HKDF-SHA256 deriva uma subchave para cifrar campos e outra para o blind index.
- **Blind index para o CPF.** HMAC-SHA256 do CPF normalizado com a subchave da clínica, com índice único `(tenant_id, cpf_index)`. Permite recusar CPF duplicado na mesma clínica (409) e buscar o `patient_ref` pelo CPF sem guardar o CPF em claro. A busca é `POST /v1/patients/lookup` para o CPF não aparecer em URL, logs de acesso ou proxies.
- **Gestão de chaves atrás de `IKeyManagementService`** (wrap/unwrap com contexto, mesmo contrato do AWS KMS `Encrypt`/`Decrypt` com `EncryptionContext` e do Azure Key Vault `wrapKey`/`unwrapKey`). A implementação local lê KEKs em Base64 de user-secrets ou variáveis de ambiente, aceita várias KEKs (a ativa cifra, as demais decifram) e é recusada na subida em ambiente `Production`. O provedor de nuvem entra quando a nuvem for escolhida.
- **API mínima e menor privilégio.** `vault.write` cadastra e altera; `vault.read` lê e busca. Escrita não implica leitura: o cadastro devolve só o `patient_ref` e a alteração responde 204, então quem só escreve não recebe de volta dados que não enviou. Alterações exigem `If-Match` com o ETag (versão da linha), recusando `*`; o CPF é imutável. Respostas com `Cache-Control: no-store`.
- **Sem dado pessoal em logs.** Records de dados pessoais têm `ToString` redigido, erros nunca repetem o valor recebido, o EF nunca liga `EnableSensitiveDataLogging` e o Npgsql não registra parâmetros. Os logs do CareFlow também não carregam o `patient_ref`.

## Alternativas

- **Cifragem transparente do disco ou do banco (TDE):** protege o disco, mas qualquer um com acesso SQL lê tudo em claro, e não permite apagar uma clínica por chave.
- **`pgcrypto` no banco:** a chave teria de trafegar até o PostgreSQL em cada consulta e apareceria em logs de statement.
- **Um único blob JSON cifrado por paciente:** menos colunas, mas cada alteração recifra tudo e perde a possibilidade de revelar campos de forma seletiva (por exemplo, só o telefone para notificações).
- **DEK por paciente (três níveis de chave):** permite crypto-shredding de um único paciente. Ficou para a fase 8 e entrou lá (ADR-0012), sem migração destrutiva: o cofre lê os dois esquemas e migra em lotes.
- **Hash simples (SHA-256) do CPF no lugar do HMAC:** o espaço de CPFs é pequeno (cerca de 10^9), então um hash sem chave é revertido por força bruta em minutos.

## Consequências

- Os testes leem o banco direto com o papel de admin e provam que nenhum dado pessoal aparece em claro (UTF-8, UTF-16 e Latin-1), que ciphertexts trocados entre pacientes são detectados e que a DEK só existe cifrada. Um teste percorre todos os fluxos com logs em nível Trace e falha se qualquer dado pessoal aparecer em qualquer log.
- O tamanho do valor cifrado revela o tamanho do valor original (por exemplo, o comprimento do nome). Aceito nesta fase; padding por faixas é a mitigação se virar requisito.
- Busca só por igualdade exata de CPF. Busca por nome ou parcial exigiria outra técnica e não faz parte do escopo.
- Rotação da KEK é só configuração (nova chave ativa; as antigas continuam decifrando). Rotação da DEK e a reescrita dos registros ficam para a fase 8, junto do crypto-shredding.
  - **Atualização (fase 8, ADR-0012):** cada paciente tem a própria DEK, embrulhada pela chave da clínica. A chave da clínica rotaciona por rota própria (`vault.keys`), a manutenção reembrulha e recifra em lotes com o cofre no ar, e versões antigas são destruídas quando nada depende delas.
- O `patient_ref` pode aparecer no caminho da URL nos logs de requisição do ASP.NET Core e nos traces. Esses logs ficam desligados em produção (nível Warning) e o pseudônimo, sozinho, não identifica ninguém.
- A agenda continua aceitando qualquer `patientRef` da clínica. Validar a existência do paciente entra na fase 4, por eventos, para não acoplar a disponibilidade da agenda à do cofre.
