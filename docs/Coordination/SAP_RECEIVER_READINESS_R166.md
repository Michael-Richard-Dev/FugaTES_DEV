# R166 — Prontidão do receptor SAP DEV

Gate `CONTINUAR_SAP_INBOUND_SEM_AGUARDAR_DESKTOP`. Nenhum DDL, GRANT, POST SAP/Guardian, ativação de
receptor ou abertura de janela R57. 18761, 18762, 18763 e o ingress HML 8080 intocados.

---

## 1 · Autenticação da identidade de escrita — PASS

Verificado na VM, somente leitura, com `psql -w` (que nunca pede senha):

```
pgpass mode=600 owner=michael
fugates_sap_inbound_app;fugates_dev;5433;18/fugatesacmdev
```

A role autentica e conecta no destino correto. O `CONNECT` concedido pelo `006` está efetivo.

## 2 · Guardas de conexão — PASS

`NpgsqlDataSourceFactory` submete a role de escrita às mesmas guardas da compartilhada: porta 5433,
base `fugates_dev`, schema `fugates`, host loopback, `Passfile` absoluto, sem `PGPASSWORD`, e sonda
de identidade física por conexão. Coberto por teste: as três identidades aprovadas são guardadas;
`postgres`, `fugates_dev_app`, `fugates_dev_owner`, nulo e vazio **não** são; e destino divergente é
recusado **antes de qualquer conexão**, para as três.

## 3 · Pacote consolidado

`.artifacts/R166_DEV_IDENTITIES/FUGATES_SAP_INBOUND_RECEIVER_DEV.zip`
SHA-256 `030A2A2C8FB8D62D81B4BB30AF9D655CFD998B9356BA879D66864B14B1ABA0F9` · 23 arquivos ·
1 484 790 bytes · `installed=false` · **zero credenciais** (varredura por `password`, `senha`,
`signature`, `secret`, `apikey`, `token` nos arquivos de texto: nenhuma literal).

### Sobre os arquivos Toledo do Codex

Eles **não podem** ser retirados do assembly sem alterar o projeto em que o Codex está trabalhando,
o que quebraria o build dele. O que foi feito é mais forte do que removê-los: provar que o receptor
**não os compõe**. `Program.cs` tem **zero** referências a Toledo, e o único caminho que levaria a
`ToledoCallbackComposition.Configure` é `GuardianCallbackHttpComposition`, invocado apenas quando
`Callback:Enabled=true` — e a configuração entregue declara `false`.

Três testes novos asseguram isso **sobre o arquivo real entregue**, não sobre uma fixture
equivalente:

| Teste | O que fixa |
|---|---|
| `O_arquivo_entregue_mantem_a_admissao_r57_obrigatoria_e_fechada` | como entregue, a composição recusa com `SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED` |
| `O_arquivo_entregue_compoe_apenas_o_caminho_sap_duravel` | `SapProductive=true`, `SapOffline=false`, `Callback=null`, caso de uso durável registrado, **nenhum** `IHostedService` |
| `Nenhum_servico_toledo_do_codex_e_registrado_pelo_receptor` | nenhum tipo do caminho callback/pesagem Toledo entra no contêiner |

O terceiro distingue o que importa: `ContratosSapToledo` é o **contrato SAP**, caminho aprovado deste
receptor; o que não pode aparecer é `IToledoCallbackAdmission`, `ToledoPesagemCallbackDto`,
`RegistrarPesagemToledoCasoDeUso`, `RecebimentosToledo` e afins.

## 4 e 5 · Worker Guardian desligado, admissão R57 obrigatória e fechada

`FUGATES_INTEGRATION_WORKER_ENABLED=false`, `AcmR57:Normal:ExecutionAuthorized=false`,
`AcmProtocolCommand` desabilitado, `AcmInboundWindow:Enabled=false`. Comprovado em execução real num
diretório isolado: o host morreu com o código do guard e **não abriu listener**.

### A dependência de credencial Guardian na partida foi resolvida

O guard SAP exige `HostIntegracao:ExternalMode=Guardian`, e `AddGuardianRuntime` exigia
`Email`/`Password`/`Signature` **na partida** — obrigando a provisionar um segredo que este receptor
nunca usaria. Resolvido **sem reutilizar nenhum segredo exposto**, e sem afrouxar controle:

- o diferimento exige opt-in explícito (`Guardian:CredentialsDeferredUntilUse=true`) **e**,
  cumulativamente, que nenhuma execução Guardian esteja autorizada no host
  (`AcmR57:Normal:ExecutionAuthorized`, `AcmProtocolCommand:ExecutionAuthorized`,
  `FUGATES_INTEGRATION_WORKER_ENABLED`). Qualquer uma delas em `true` recoloca a exigência no boot;
- o controle que importa não se moveu: `GuardianRuntimeCredenciaisProvider` recusa fechado no ponto
  de uso, com `Guardian runtime credentials are absent.` — ou seja, nenhuma chamada Guardian pode
  ocorrer sem credencial, com ou sem diferimento;
- a configuração entregue **não carrega credencial Guardian alguma**.

Sete testes fixam esse comportamento, incluindo o de que um valor não booleano não vale como opt-in.

## 6 · Instalação isolada e rollback

`deploy/sap-inbound-dev/Instalar-ReceptorDev.ps1`, preparado e **não executado**. Recusa portas de
serviços existentes (8080, 18761, 18762, 18763, 5433, 5432, 5434), confere o SHA do pacote, exige
configuração **sem pendências**, valida a autenticação da role de escrita antes de instalar, faz
backup `tar.gz` da instalação anterior e, na fase de subida, exige `/health` 200 **e** que as rotas
de callback Guardian e Toledo respondam **404**. Em qualquer falha encerra o processo novo, marca o
diretório como `_FALHOU`, preserva o anterior e devolve `BLOCKED`.

Ensaio real em modo `-Conferir`:

```
ALVO          PASS   vm-fugatesbio;michael | destino 18764 livre
                     preservados: EMUSO:8080,EMUSO:18761,EMUSO:18762,EMUSO:18763
PACOTE        PASS   030A2A2C8FB8D62D
CONFIGURACAO  BLOCKED  5 definicoes empresariais pendentes
```

Dois defeitos encontrados e corrigidos no próprio ensaio: `[IO.Compression.ZipFile]` não existe no
Windows PowerShell 5.1 sem `Add-Type` (trocado por `Expand-Archive`, nativo), e `Remoto` recebeu
`-n` para nunca encaminhar a entrada padrão local — o mesmo defeito que havia feito uma senha ser
ecoada em claro no roteiro de identidades.

## 7 · As cinco definições empresariais que faltam

Reduzidas de sete para cinco: o `Passfile` foi preenchido com o caminho verificado fisicamente
(`/home/michael/.fugates-sap-inbound/pgpass`, modo 600, autenticando), e a credencial Guardian deixou
de ser necessária.

| # | Pendência | Quem decide |
|---|---|---|
| 1 | `<<PENDENTE_PORTA_LOOPBACK_APROVADA_HERA>>` — porta loopback do receptor | Hera |
| 2 | `<<PENDENTE_USUARIO_BASIC_APROVADO_HERA>>` — usuário Basic | Hera |
| 3 | `<<PENDENTE_REFERENCIA_SEGREDO_APROVADA_HERA>>` — referência do segredo Basic | Hera |
| 4 | `<<PENDENTE_WINDOWID_GUID_N_APROVADO_HERA>>` — `WindowId` da janela R57 | Hera |
| 5 | `<<PENDENTE_REFERENCIA_AUTORIZACAO_HERA>>` — referência de autorização R57 | Hera |

Além dos cinco marcadores, a janela R57 ainda precisa de `NotBeforeUtc`/`UntilUtc` (≤ 1 dia) e das
listas `AllowedSapPartners` e `AllowedSapMaterials`, hoje vazias — que dependem do centro, parceiro e
material do contrato de homologação, definidos pelo SAP/negócio. E a rota DEV continua dependendo da
origem SAP/CPI, host/porta TLS e certificado, com a proposta nginx pronta e **não publicada**.

Nada disso foi inventado: os valores entram no `appsettings.receiver.dev.json` e, enquanto houver
qualquer marcador, o instalador bloqueia e o receptor recusa subir.

## Regressão

`dotnet build FugaTES.slnx -c Release` → **0 erros**.

| Suíte | Aprovados | Falhas |
|---|---|---|
| FugaTES.Testes | 1626 | 0 |
| R158.SapInboundReadiness.Tests | 82 | 0 |
| FugaTES.WinForms.Monitor.R165M.Tests | 277 | 0 |
| R165A.Tests | 43 | 0 |
| R165U.Automatic.Tests | 607 | 8 |
| R165R.Provider.Tests | 113 | 2 |

As 10 falhas são **dependências de ambiente**, não regressão, e dizem isso explicitamente:

- R165R: `Set R165R_PACKAGE_ROOT to the isolated, disabled release candidate.` Tentei apontar a
  variável para o staging do receptor; o teste espera outro layout (`runtime/host/appsettings.json`,
  do candidato de release R165R), então não forcei.
- R165U: `Frozen approved evidence not found.` As asserções são de existência de arquivos sob
  `evidence/R165Y_AUTO_1144/`, diretório **inteiramente não rastreado** pelo git e hoje ausente da
  árvore. Nenhuma alteração de código faz um arquivo desaparecer, e não cabe fabricar evidência
  congelada.

## Estado preservado

| | |
|---|---|
| Listeners | `18761`, `18762`, `18763`, `8080` no ar |
| API 18762 | HTTP 200 · `fugates_acm_hml_app` · release `…_TODOS_R2` — **não promovida** |
| 18763 | `pid=651989`, o mesmo da linha de base |
| Porta 18764 | ausente — **nenhum receptor instalado** |

### Nota: o DEV110 foi republicado pelo Codex, e isso destrava a janela conjunta

O `FugaTES.exe` instalado passou de `C128EFCA…` (1.2.4) para `76F70EA4…`
(**1.2.5+toledo.receipts.monitor**, 28/10 16:32:52). Não fui eu: não publiquei o Desktop neste gate.

O relevante é o conteúdo. O `FugaTES.Persistencia.dll` instalado agora contém `fugates_dev_read_app`
**e** `current_user=@role` — isto é, a publicação do Codex **carregou a correção de identidade** que
eu havia feito. Com isso, o impedimento que eu reportei no gate anterior deixou de existir: o Desktop
instalado passou a aceitar a identidade GET-only.

Não promovi a API, porque este gate determina esperar a janela conjunta. Fica registrado que a
condição que a Hera pediu para aguardar **já se materializou**, e a suíte R165M está verde (277/0).
