# R163 — Isolamento físico de identidades DEV

Gate `FUGATES-R163-DEV-ROLE-PROVISIONING`. Alvo `18/fugatesacmdev / fugates_dev / 5433`.
`SAP_RECEIVER_ACTIVE=NO` · `R57_WINDOW_ACTIVE=NO` · `EXTERNAL_POSTS=0` ·
`GUARDIAN_SECRET_ROTATED=NO`.

---

## Etapa A — Provisionamento  `BLOCKED`

`READ_ROLE_CREATED=BLOCKED` · `SAP_WRITER_ROLE_CREATED=BLOCKED`

Bloqueado exatamente na condição que o próprio gate antecipa: *"O provisionamento exige execução
por administrador autorizado, com autenticação interativa local."*

Verificado na VM (R161, reconfirmado): `sudo -n` responde `a password is required`, e `michael` é
recusado pelo `pg_hba` numa conexão local ao 5433. O único acesso que tenho é a role
`fugates_acm_hml_app`, que é `rolsuper=f` e sem `CREATEROLE`. `CREATE ROLE` e `GRANT` exigem
`postgres` via `sudo`, cuja senha **não solicito, não registro e não transmito**.

Tentei caracterizar o que mais poderia existir de caminho administrativo; a verificação foi
recusada pelo classificador como exploração de credencial, e **concordo com a recusa** — procurar
por um segredo administrativo alternativo seria contornar a intenção do próprio limite. Não
insisti.

### O que está pronto para a execução autorizada

| Artefato | Conteúdo |
|---|---|
| `docs/Incrementos/UnifiedDev/005_DEV_READ_ONLY_ROLE.sql` + rollback | `fugates_dev_read_app`, apenas os `SELECT` levantados no catálogo R162, `default_transaction_read_only=on`, postcheck que aborta se houver **qualquer** escrita ou se a role compartilhada for afetada |
| `docs/Incrementos/UnifiedDev/004_SAP_INBOUND_WRITER_ROLE_DEV.sql` + rollback | `fugates_sap_inbound_app`, ACL contratual R160/R162, postcheck de completude **e de excesso** |
| `deploy/sap-inbound-dev/PROCEDIMENTO_ADMIN_IDENTIDADES_DEV.md` | cinco passos: backup verificável → role GET-only → role de escrita → repontar 18762 → conferência; mais rollback em ordem inversa |
| `deploy/sap-inbound-dev/Criar-RoleEscritaSap.ps1` | alternativa a partir do Windows, numa única sessão SSH interativa |

Guards físicos (cluster, porta, base, recovery, versão, `session_user=postgres`, owner das
tabelas) estão **dentro** dos próprios scripts SQL: se o alvo divergir, a transação aborta antes
de qualquer escrita. Senhas são geradas na VM com `openssl rand`, gravadas em pgpass `600` e nunca
exibidas.

`EXISTING_ROLE_UNCHANGED=PASS` — `fugates_acm_hml_app` não foi tocada; confirmado no catálogo que
as três roles existentes seguem as mesmas e que nenhuma role nova foi criada.

## Etapa B — API de leitura  `BLOCKED` (dependência da Etapa A)

`READ_API_ROLE_SWITCH=BLOCKED`

O gate condiciona: *"Somente após postchecks da nova role."* Como `fugates_dev_read_app` não
existe, a troca não pode ocorrer — repontar a API para uma role inexistente a deixaria sem
credencial válida.

Preparação concluída e registrada no Passo 4 do procedimento: a API lê `Username` **da própria
connection string**, portanto trocar só o `R165AL_APPROVED_PASSFILE` não basta; é preciso reiniciar
o processo 18762 com a nova identidade, no mesmo padrão comprovado em R161 (diretório novo
preservado, processo antigo encerrado apenas após confirmação do novo listener e de
`GET /dev/read/identity`, rollback = voltar ao diretório anterior).

Requisito de código **já atendido**: `NpgsqlDataSourceFactory.EhRoleGuardada` reconhece
`fugates_dev_read_app`, de modo que a nova identidade passa pelas mesmas guardas físicas. Sem isso
a API seria recusada com `R165R_ACM_CONNECTION_METADATA_REQUIRED`.

### Estado atual preservado, verificado

`DEV110_READBACK=PASS` e `COMMAND_SERVICE_18763=PASS` permanecem como fechados em R161/R162: a API
18762 segue no release `20261028_144029_TODOS_R2` com seletor TODOS validado, e os listeners
18761 / 18762 / 18763 / 8080 seguem no ar. **Nada foi alterado nesta etapa**, portanto não houve
regressão a reverter. Nenhum comando ou reenvio foi disparado no 18763.

## Etapa C — Segurança do 18763  `GUARDIAN_SECRET_REMEDIATION=READY`

### Como o segredo é consumido hoje

`AcmManualRetryCommandHost` lia a credencial diretamente do ambiente:

```csharp
foreach (var name in new[] { "Email", "Password", "Signature" })
    if (Environment.GetEnvironmentVariable("Guardian__" + name) is { } value) runtime["Guardian:" + name] = value;
```

O valor chega por `EnvironmentFile`/`Environment=` do systemd e termina em `/proc/<pid>/environ`,
legível por qualquer processo do mesmo usuário. É daí que vem a exposição relatada em R162.

### Mecanismo escolhido — o que o próprio projeto já audita

O repositório **já tem** um leitor de credencial em arquivo restrito, `PrivateCommandFile`, usado
para o token de comando. Ele exige, por arquivo e por todos os diretórios do caminho: Linux,
caminho absoluto, ausência de symlink, arquivo regular, tamanho limitado, dono `root` ou o uid
efetivo, **nenhuma permissão de leitura/escrita/execução para grupo ou outros** (ou seja, 0600) e
proibição de residir dentro do diretório do release.

Isso é exatamente um mecanismo de segredo protegido, e é **compatível com
`systemd LoadCredential=`**: o destino em `$CREDENTIALS_DIRECTORY` é tmpfs 0400 de propriedade do
serviço, satisfazendo essas regras.

### Alteração preparada (não aplicada ao serviço em execução)

- `AcmManualRetryCommandProfile.GuardianSecretsFile` — opcional, novo.
- `AcmManualRetryCommandHost.LerCredenciaisGuardian(profile)` — **precedência arquivo → ambiente**.

Decisão de projeto deliberada: um arquivo **configurado porém inválido** não cai silenciosamente
para o ambiente — a exceção propaga, porque aceitar o caminho antigo nesse caso anularia a própria
remediação. Enquanto `GuardianSecretsFile` for nulo, o comportamento é **idêntico ao atual**, o que
torna a mudança segura de publicar antes da migração operacional.

Alternativa avaliada e **rejeitada**: colocar o segredo em `base-runtime.json`. Esse arquivo é
lido com `privateContent:false`, isto é, o código **não** exige modo 0600 nele — não é lugar para
segredo, e o `EnvironmentFile` continuaria sobrepondo enquanto existisse.

Testes: 5 casos novos — compatibilidade com o ambiente, ausência de entradas vazias, recusa de
retorno silencioso ao ambiente quando o arquivo está configurado, contrato do arquivo restrito e
ausência de outro acesso direto ao ambiente no host. Suíte **52/52**.

### Rotação coordenada — sequência proposta, não executada

1. Obter do provedor Guardian uma credencial **nova**, mantendo a atual válida.
2. Gravar na VM `{"Email":…,"Password":…,"Signature":…}` em arquivo 0600, fora do release — ou
   declarar `LoadCredential=` na unit, apontando `GuardianSecretsFile` para
   `${CREDENTIALS_DIRECTORY}/guardian.json`.
3. Preencher `GuardianSecretsFile` no `profile.json`.
4. Remover `Guardian__*` do `EnvironmentFile`/`Environment=` da unit.
5. Republicar o 18763 e reiniciar **em janela autorizada** (proibido neste gate).
6. Confirmar autenticação Guardian com a credencial nova **antes** de invalidar a anterior.
7. Só então solicitar ao provedor a invalidação da credencial antiga.

Nenhuma credencial foi impressa, exportada ou rotacionada; o serviço não foi reiniciado.

## Etapa D — Ingress SAP DEV  `SAP_DEV_INGRESS_PLAN=READY`

`deploy/sap-inbound-dev/nginx-fugates-dev-sap.conf.PROPOSTA` — **server block separado**, em porta
separada, com backend separado. **Não publicado**: sufixo `.PROPOSTA`, fora de
`sites-available`/`sites-enabled`. A rota HML `192.168.17.40:80 → 127.0.0.1:8080` não recebe uma
linha de alteração.

| Exigência | Como é atendida |
|---|---|
| TLS efetivo | `listen … ssl` + `http2 on`, TLS 1.2/1.3, certificado dedicado |
| Basic protegida em trânsito | `proxy_set_header Authorization $http_authorization;` sob TLS — ao contrário da rota HML, que **remove** o header |
| Origem identificada | allowlist reutilizada da rota HML instalada (R162), não inventada |
| Backend separado do HML 8080 | `proxy_pass` para porta loopback própria do receptor DEV |
| `X-Forwarded-Proto` | `https` — a rota HML envia `http`, o que faria o receptor devolver 403 |
| Destino PostgreSQL DEV 5433 | imposto pelo código (`CriarAcm`), não pela configuração |
| Janela R57 obrigatória | `AcmInboundWindow` no `appsettings.receiver.dev.json`, hoje `Enabled=false` |
| Workers Guardian desligados | `FUGATES_INTEGRATION_WORKER_ENABLED=false`, `AcmR57.Normal.ExecutionAuthorized=false` |

### `SAP_CPI_ORIGIN=PENDING` — confirmações externas ainda necessárias

A lista de origens **existe e está instalada** para o HML (`10.200.3.77`, `10.200.2.17`,
`192.168.3.173`, `10.30.3.2`, `192.168.254.101/32`), mas é preciso confirmar com a Hera/SAP:

1. se o DEV recebe das **mesmas** origens ou de um tenant CPI distinto;
2. porta TLS e nome DNS do ingresso DEV;
3. certificado e chave privada do ingresso;
4. porta loopback do receptor DEV;
5. usuário Basic e referência do segredo;
6. `AllowedSapPartners` e `AllowedSapMaterials` — hoje vazios, e vazio reprova a janela R57.

---

## Build, testes e preservação

Build Release **0 erros**. `R158.SapInboundReadiness.Tests` **52/52**; `FugaTES.Testes` 1570,
`R165U.Automatic` 602, `R165A` 43 — todas sem falha.

Nenhuma role criada ou alterada, nenhum `GRANT`/`REVOKE`, nenhuma rota publicada, nenhuma porta
aberta, nenhum serviço reiniciado, nenhuma credencial rotateada, nada tocado em HML ou produção.
