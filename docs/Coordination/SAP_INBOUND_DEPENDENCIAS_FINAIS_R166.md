# R166 — Recebimento SAP no DEV unificado: o que falta

Continuação direta do R164. Sem novo diagnóstico. `GUARDIAN_WORKERS=DISABLED` ·
`NEW_SAP_CONTRACT_RECEIVED=NO` · nenhuma alteração em 18762, 18763, HML 8080, 5432, 5434 ou
MasterData HML.

---

## Dependências humanas — objetivamente

### D1 · Administrador com `sudo` na VM  → destrava a **Etapa 1 inteira**

| | |
|---|---|
| **Ação** | Executar `deploy/sap-inbound-dev/Executar-IdentidadesDev.ps1 -Pacote <...ROLE_OPTIN.zip>` num terminal interativo e digitar a senha do sudo quando o script pedir |
| **Quem** | Operador/administrador com `sudo` em `vm-fugatesbio` (não tenho, e não procuro alternativa) |
| **Destrava** | backup → `fugates_dev_read_app` → `fugates_sap_inbound_app` → postchecks de ACL → autenticação da role de leitura → promoção da API 18762 → revalidação do DEV110 |
| **Estado hoje** | alvo conferido, SQLs íntegros (`005=C7F26568…`, `004=395012AC…`), nenhuma role criada, `fugates_acm_hml_app` intacta |

Enquanto isso não ocorre, `fugates_sap_inbound_app` não existe e **o receptor não tem como
autenticar no 5433** — por isso a Etapa 4 permanece `NOT_EXECUTED`.

### D2 · Rota SAP/CPI dedicada ao DEV  → destrava a **Etapa 4**

Já levantado em R162 e **reaproveitado**, não redescoberto. O ingress HML existente
(`192.168.17.40:80 → 127.0.0.1:8080`) **não serve** ao receptor DEV por três motivos concretos:
envia `X-Forwarded-Proto: http` (o receptor exige `https` → 403), **remove** o header
`Authorization` (Basic → 401) e aponta para o serviço **HML**, que não pode ser alterado.

Falta decidir e fornecer, sem inventar:

| # | Parâmetro | Quem confirma |
|---|---|---|
| 1 | Rota DEV autorizada (host/porta TLS, nome DNS, certificado) | Infraestrutura + Hera |
| 2 | Identidade da origem SAP/CPI para o DEV — as mesmas da allowlist HML (`10.200.3.77`, `10.200.2.17`, `192.168.3.173`, `10.30.3.2`, `192.168.254.101/32`) ou outro tenant | SAP/CPI |
| 3 | Usuário e segredo da autenticação Basic | Hera |
| 4 | Porta loopback do receptor DEV | Hera |
| 5 | Centro, parceiro e material do contrato novo | SAP/negócio |
| 6 | Janela R57: `WindowId`, `AuthorizationReference`, `NotBeforeUtc`/`UntilUtc` (≤ 1 dia) | Hera |
| 7 | `Passfile` da role de escrita e as credenciais Guardian de partida | Operador |

Proposta de ingress pronta e **não publicada**:
`deploy/sap-inbound-dev/nginx-fugates-dev-sap.conf.PROPOSTA` — server block separado, TLS real,
`X-Forwarded-Proto: https` e `Authorization` preservado, backend próprio, HML intocado.

---

## O que avançou neste gate, sem depender de ninguém

### Receptor empacotado e pronto para instalação em um passo

`FUGATES_SAP_INBOUND_RECEIVER_DEV.zip`
SHA-256 `A0DDB47C83F39930CE6E8D04DB13E185CFF1EBBCB8FB45E8867AD617C6EACA50`, 19 arquivos,
**zero credenciais**, `installed=false`. Reutiliza integralmente `FugaTES.Callback.Http`,
`SapContractInboundEndpoint.MapDurable`, `AceitarContratoSapToledoDuravelCasoDeUso` e
`ContratoSapToledoVersaoStorePostgreSql` — nada foi reconstruído.

### Duas provas executadas de verdade

**1. O receptor recusa subir sem janela autorizada.** Publiquei os binários num diretório isolado
no Windows, apliquei a configuração entregue e iniciei o host:

```
Unhandled exception. System.InvalidOperationException: SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED
listener aberto: nenhum
```

Fail-closed comprovado em execução real, não por inspeção.

**2. Com escopo R57 completo, a composição é aceita.** Em teste offline (sem host, sem porta, sem
banco): `ConfigurarServicos` devolve `true`, registra
`AceitarContratoSapToledoDuravelCasoDeUso` e **nenhum** `IHostedService` — worker desligado.
Seis variações de lacuna no escopo (janela desabilitada, sem `WindowId`, sem autorização, parceiro
vazio, material vazio, centro fora do aprovado) voltam a reprovar com o mesmo código.

### Dois defeitos de configuração encontrados e corrigidos

Ambos só apareceriam no momento da ativação, com a janela empresarial já aberta:

1. **Seção `TimeoutProcessamentoIntegracao` ausente** na minha fixture — exigida por
   `HostIntegracaoComposition.GetRequiredSection`. O arquivo entregue já a tinha.
2. **Seção `Guardian` ausente no `appsettings.receiver.dev.json`.** Este é o relevante: o guard
   SAP exige `HostIntegracao:ExternalMode=Guardian`, e `AddGuardianRuntime` valida
   `BaseUrl`/`Timeout` e **exige** `Email`/`Password`/`Signature` **na partida**. Sem a seção, o
   receptor não subiria mesmo com janela R57 autorizada. Acrescentada com `BaseUrl`, endpoints e
   timeout vindos da referência atestada `configuration/appsettings.inert.json`, e as três
   credenciais como `<<PENDENTE_REFERENCIA_CREDENCIAL_GUARDIAN>>`.

   Observação: exigir credencial Guardian só para **receber** do SAP é um acoplamento do desenho
   atual, não uma escolha minha — `ExternalMode=Guardian` é condição do próprio guard. A execução
   continua desligada por `AcmR57.Normal.ExecutionAuthorized=false` e
   `FUGATES_INTEGRATION_WORKER_ENABLED=false`, de modo que nenhum SOAP/REST Guardian ocorre no
   primeiro recebimento.

### Transação, H4, IntegrationId e idempotência

Já comprovados e mantidos verdes: identidade → H4 (`RegistrarCandidatoNaTransacaoAsync`, falha
dura se não criar) → versão com payload → `COMMIT`, tudo em uma transação, com `ROLLBACK` em
qualquer falha; reentrega com mesmo fingerprint não grava; payload alterado gera nova versão.

---

## Sequência para o recebimento real, quando D1 e D2 estiverem resolvidos

1. Operador executa `Executar-IdentidadesDev.ps1` (D1).
2. Hera preenche os sete parâmetros de D2 no `appsettings.receiver.dev.json`.
3. `Validar-ReceptorDev.ps1` precisa sair de `BLOCKED` para `READY_FOR_HERA_AUTHORIZED_INSTALL`.
4. Instalar o pacote do receptor na porta loopback aprovada, separado de 18762/18763/8080.
5. Publicar a rota DEV a partir da proposta nginx, preservando a HML.
6. Abrir a janela R57 pelo tempo da homologação.
7. SAP/CPI envia **um** contrato novo.
8. Validar: POST → admissão → `COMMIT` 5433 → H4 e IntegrationId → API 18762 → monitor DEV110 →
   JSON do contrato. Sem Guardian SOAP/REST, sem reenvio de histórico.

---

## Regressão

`dotnet build FugaTES.slnx -c Release` → **0 erros**.

| Suíte | Aprovados | Falhas |
|---|---|---|
| R158.SapInboundReadiness.Tests | 71 | 0 |
| FugaTES.Testes | 1598 | 1 |
| R165U.Automatic.Tests | 615 | 0 |
| R165R.Provider.Tests | 115 | 0 |
| FugaTES.WinForms.Monitor.R165M.Tests | 255 | 0 |
| R165A.Tests | 43 | 0 |
| **TOTAL** | **2697** | **1** |

A minha suíte subiu de 62 para **71**: os nove casos novos de composição do receptor.
`FugaTES.Testes` subiu de 1570 para 1599 casos — são testes novos do Codex, do trabalho Toledo em
curso.

### Correção de uma leitura minha

A primeira varredura reportou **6 falhas** em `ToledoUnifiedHttpTests`, todas com 503 onde se
esperava 403/401. Eu tentei atribuir isso à linha 42 de `ToledoCallbackAdmission.cs`
(`options.Configured == false`) — e uma sonda com os mesmos valores do teste devolveu
`CONFIGURED=True`, derrubando a hipótese.

A causa real era **binário obsoleto**: `FugaTES.Testes.dll` foi compilado às 14:13:23 e o Codex
reescreveu `ToledoUnifiedHttpTests.cs` às 14:16:38, três minutos depois; minhas reexecuções com
`--no-build` rodavam o DLL antigo. Recompilando, as seis falhas **desaparecem**. Nenhuma delas era
real. A sonda temporária foi removida.

### A falha remanescente é do Codex, não uma regressão

`ToledoMasterdataCoexistenceTests.One_existing_route_serves_full_Toledo_and_six_field_R41_...`,
linha 41: o payload legado Guardian de seis campos recebe **400** onde o teste espera 200.

Pertence ao recurso dual-format em construção: `GuardianCallbackEndpoint.MapDual`, em
`GuardianCallbackEndpoint.DualFormat.cs` — arquivo **não rastreado**, criado às 14:08:29, enquanto
o teste que o exercita é de 14:16:38. Implementação e teste estão em edição ativa.

Fora do meu caminho de dependências: minhas alterações deste gate são
`deploy/sap-inbound-dev/appsettings.receiver.dev.json`, o meu projeto de testes e documentação —
nada que o endpoint Guardian alcance. **Não modifiquei nenhum arquivo do Codex**, nem alterei
expectativas de teste para obter PASS.
