# HERA V2 — Publicação isolada R16

GATE=FUGATES-MASTERDATA-MANUAL-READONLY-STAGING-R16
EXECUTOR=CODEX
MODO=WINDOWS_LOCAL / ISOLATED_STAGING / INTEGRITY_VALIDATION

R16_STAGING=READY
RELEASE_IDENTITY=FUGATES_MASTERDATA_R15_STAGE_R16_20261011_181320_036_98c00995
STAGING_PATH=C:\Users\TI\Downloads\FUGATES_MASTERDATA_R15_STAGE_R16_20261011_181320_036_98c00995
RELEASE_BUILD=PASS
PUBLISH_EXIT_CODE=0
R15_CODE_INCLUDED=SIM
R13_PATCH_INCLUDED=SIM
ASSEMBLY_MANIFEST=PASS
R3_2_STAGING_UNCHANGED=SIM
OFFICIAL_DESKTOP_UNCHANGED=SIM
R15_TEST_EVIDENCE_PRESERVED=SIM
STAGING_RUNTIME_READY=NAO
FIRST_BLOCKER=STAGING_HML_RUNTIME_PROFILE_NOT_PROVIDED_OR_VALIDATED
NEXT_HANDOFF=HERA V2

## Publicação real
Projeto: src\FugaTES.WinForms\FugaTES.WinForms.csproj
Target: net10.0-windows
Configuração: Release
RID: win-x64
SelfContained: true
UseAppHost: true
PublishSingleFile: false
PublishTrimmed: false

Executável do projeto publicado: FugaTES.WinForms.exe.
Não foi renomeado para o executável oficial nem copiado para C:\FugaTES_DEV110.
Formato verificado pela leitura do header: Windows PE AMD64.
Runtimeconfig possui includedFrameworks Microsoft.NETCore.App, Microsoft.WindowsDesktop.App e Microsoft.AspNetCore.App 10.0.9.
coreclr.dll, hostfxr.dll, hostpolicy.dll e System.Windows.Forms.dll estão publicados.

Comando executado:
```text
dotnet publish src\FugaTES.WinForms\FugaTES.WinForms.csproj --configuration Release --runtime win-x64 --self-contained true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false --artifacts-path .artifacts\manual-readonly-r16 --output C:\Users\TI\Downloads\FUGATES_MASTERDATA_R15_STAGE_R16_20261011_181320_036_98c00995 --verbosity minimal "-flp:logfile=.artifacts\manual-readonly-r16\evidence\publish.log;verbosity=normal"
EXIT_CODE=0
```
publish.log não contém diagnostics de warning/error. Não foram repetidos os 71 testes R15. Nenhuma alteração de projeto ou fonte foi necessária.

## Manifestos e prova de inclusão
No staging:
- ASSEMBLY_MANIFEST.json: SHA256 e tamanho de 368 executáveis/DLLs, incluindo apphost, WinForms, Guardian, SAP, Host, Aplicação, Persistência e dependências.
- SOURCE_MANIFEST_R13_R15.json: SHA256 dos dez arquivos R15 e dois fontes R13.1.
- INTEGRITY_VALIDATION.json: prova de inclusão, formato e contagens.
- PACKAGE_MANIFEST.json: SHA256 de 387 arquivos do payload; exclui o próprio manifesto para evitar autorreferência.
- PREFLIGHT_PENDING.md: campos pendentes, cinco bloqueios e limites de utilização.
- RELEASE_R16.md: identidade exclusiva da publicação.

ASSEMBLY_MANIFEST_SHA256=6B6FB2D9FFBA7185C8EDE0BE204128A7B9493CC993D4682A8548E46AFC4607A8
EXECUTABLE_SHA256=952B00DB398E8EC4D4C8616E373FD046B798F18139E4D0DADFA8CFF6DD10BB53

Leitura binária sem carregar/iniciar assemblies confirmou os símbolos compilados do entrypoint, composição, caso de uso, parser e adapters R15. No Guardian foi confirmado ResolveApprovedCollectionUpdateRoute; Guardian e Host contêm o literal collection aprovado /api/v0/Guardian/Transportadora. As nove DLLs FugaTES publicadas coincidem em SHA256 com os outputs do build desta publicação.

A leitura de símbolos e hashes comprova inclusão no artefato. Não representa nova execução comportamental, chamada real ou QA visual. A validação comportamental permanece a evidência R15 aprovada.

## Preservação
Baseline R16 registrada antes do publish:
- 239 arquivos em src: SHA256 idênticos após publicação.
- 9 arquivos de evidências R15: SHA256 idênticos.
- 50 arquivos protegidos R15: SHA256 idênticos; incluem staging R3.2, desktop oficial, core automático, SAP SD e patch R13.1.
- Dez fontes R15 conferidos contra source-manifest.json aprovado antes do publish.

Evidências nesta pasta:
baseline.json, preservation.json, publish-command.txt, publish.log, publish-exit-code.txt,
published-build-output-match.json, assembly-manifest.json, source-manifest-r13-r15.json,
package-manifest.json, integrity-validation.json e validate-r16.py.

O caminho R15 informado no pedido tinha ausente a barra antes de .artifacts.
Foi utilizado o arquivo existente correto:
C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV\.artifacts\manual-readonly-r15\evidence\HANDOFF_HERA_V2_R15.md

## Configuração pendente
Nenhum FugaTES.runtime.json ou FugaTES.carrier.json privado foi transportado.
O único appsettings publicado é appsettings.Development.json contendo somente Logging.
Os demais JSON do publish são deps/runtimeconfig gerados pelo SDK.
Não há chave SSH, arquivo de segredo, conexão com senha ou flag de aprovação copiada por este gate.

CONFIGURATION_PENDING:
- FugaTES.runtime.json: identidade HML, PostgreSQL/role/schema/target Credential Manager e perfil SSH comprovado.
- FugaTES.carrier.json: identidade SAP HML client 110/Guardian HML; referências autorizadas aos segredos existentes; aprovação manual de leitura BP/SID/prazo/timeout; contratos GET e vínculo BP/código Guardian/idAssinatura.
- ProfileVerified, SignatureVerified e DesktopProfileApproved devem ser comprovados pelo owner; não foram alterados/promovidos.
- Cinco flags obrigatoriamente FALSE no futuro perfil validado:
  FUGATES_INTEGRATION_WORKER_ENABLED
  INC005_WORKER_ENABLED
  Carrier:IntegrationWorkerEnabled
  Carrier:DesktopGuardianWritesEnabled
  Carrier:Authorization:IncrementalReleased
- Comparação completa para NO_ACTION real permanece não comprovada; igualdade parcial continua INDETERMINATE.
- Não criar contrato delta fictício. O caso de uso manual R15 continua independente do delta.

Ausência dos perfis mantém startup/consulta fail-closed. Não há declaração de ambiente efetivo validado, funcionalidade real HML ou QA visual aprovado.

SOURCE_CHANGE=0
SAP_HTTP_REAL=0
GUARDIAN_HTTP_REAL=0
SAP_WRITE=0
GUARDIAN_BUSINESS_WRITE=0
DATABASE_DDL=0
DATABASE_DML=0
DATABASE_CONNECTIONS=0
WINFORMS_STARTED=0
WORKERS_STARTED=0
DESKTOP_OFFICIAL_WRITE=0
COMMIT=0
PUSH=0
SECRET_OUTPUT=0
EXTERNAL_WRITES=0
