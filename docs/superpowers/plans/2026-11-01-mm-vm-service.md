# MM VM Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rodar o MM na VM sem sessão Windows, com corte fixo 01/11/2026 e intervalo de 3 minutos, mantendo o WinForms como observador.
**Architecture:** Reutilizar mapper, adapters e reservas PostgreSQL MM. Extrair coletor/snapshot portáveis; compor um modo exclusivo do host com providers systemd e lock de sessão; publicar observação na API privada GET-only existente.
**Tech Stack:** .NET10, systemd, Npgsql/PostgreSQL18, ODataV4, WinForms.
**Spec:** ../specs/2026-11-01-mm-vm-service-design.md (R2 aprovada pelo operador).

## Global Constraints
- Repositório oficial somente; preservar alterações Claude e histórico MM. Sem worktree, commit, push ou merge.
- SAP110/empresa3000/centros3011,3008/todos os tipos ativos aprovados; CreationDate Edm.Date confirmado em SAP_METADATA_SCHEMA.json de31/10.
- Corte fixo inclusivo2026-11-01; ciclo180s; registros sem data elegível não são enviados.
- VM127.0.0.1:5433/fugates_dev/fugates_mm_app, cluster18/fugatesacmdev, sem fallback de identidade.
- Não fazer instalação, restart, secret provisioning, DDL/GRANT ou POST empresarial nesta implementação.
- Preservar5protocolos/161recusas e todos os resultados adicionais; nenhum replay.

## Review Focus
- Paginação não pode introduzir pedido anterior/sem CreationDate; validação local além do filtro.
- Intervalo180s não pode expirar admissão em90s; falha de coleta revoga novas reservas.
- Perda do lock ou shutdown não pode permitir nova admissão nem perder resultado externo.
- Snapshot antigo/malicioso não pode aparecer como consulta física atual ou conter segredo.
- Abrir WinForms no modo VM não pode iniciar worker local ou fazer fallback silencioso.

### Task 1: Recorte SAP e coordenação portável
**Files:** Modelos/Mm/PedidosCompraMm.cs, novo Modelos/Mm/MmPurchaseOrderSnapshot.cs; Servicos/Mm/MmPurchaseOrderCollector.cs; Infraestrutura/Mm/MmFileSnapshotStore.cs; WinForms/Mm wrappers; SAP/BusinessDocuments/PedidosCompraMmConsultaAdapter.cs; testes MmVmAdmissionTests/MmVmCollectorTests e MM existentes.
**Interfaces:** PedidoCompraMmLeitura.DataCriacao DateOnly? (opcional para compatibilidade de projeção); MmCollectionPolicy.Interval=180s,CreationCutoff=2026-11-01; IMmSnapshotStore.Read/Write/Update; MmPurchaseOrderCollector(source,store,guardian?,policy?,admission?) com CycleAsync/RunAsync.
- [ ] Escrever e observar falha dos testes de corte inclusivo, data inválida/ausente, filtro V4 e atualidade180s.
- [ ] Implementar filtro CreationDate e revalidação, estado/coordenação compartilhados e wrappers Windows sem duplicar motor.
- [ ] Executar testes de admissão/coletor e regressão MM.

### Task 2: Host Linux exclusivo e credenciais
**Files:** HostIntegracao/Mm/MmLinuxOptions.cs,MmLinuxComposition.cs,MmLinuxWorker.cs; CarrierSystemdCredentials.cs; HostIntegracaoComposition.cs/Program.cs; Persistencia/Mm/MmWorkerLease.cs; testes MmLinuxHostTests/MmLinuxCredentialTests.
**Interfaces:** Mode=MmPurchaseOrders, Enabled=false padrão; options.Validate(); IMmWorkerLease.TryAcquireAsync/CheckAsync/DisposeAsync; runtime coordena cancelamento/drain, sessão advisory lock exclusiva e admissão.
- [ ] Escrever e observar falha dos testes de configuração, isolamento, lock perdido, escopo systemd e ausência de fallback.
- [ ] Implementar providers existentes, datasource5433 com identidade conferida, serviços exclusivos e uma lease de sessão não poolada.
- [ ] Verificar shutdown/gravação final sem reenvio; startup inerte não resolve segredos nem abre conexões.

### Task 3: Observabilidade privada MM
**Files:** Callback.Http/MmSnapshotReadSource.cs,DevUnifiedReadApi.cs; testes MmPrivateReadApiTests.
**Interfaces:** IMmSnapshotReadSource.ReadAsync(ct) -> MmPurchaseOrderSnapshot; Map(...,IMmSnapshotReadSource? mm=null), somente GET/dev/read/mm com DevReadEnvelope e identidade READ ONLY.
- [ ] Escrever e observar falha para sem chave, arquivo ausente/antigo/escopo errado/links/permissões e POST rejeitado.
- [ ] Implementar leitura limitada e proveniência separada de cache; preservar demais rotas.
- [ ] Executar testes GET-only e MM.

### Task 4: WinForms observador remoto
**Files:** Monitor/Physical/PhysicalMonitorClient.cs,Monitor/UnifiedDesktopSession.cs,FrmIntegracoes.cs,PainelInicialForm.cs,FrmPedidosCompraMm.cs,WinForms/Mm/MmPurchaseOrderComposition.cs e testes tela/binding.
**Interfaces:** ReadMmAsync(ct),CreateMm(); injeção assíncrona na tela, modoVM sem EnsureStarted/RunCollector local; perfil FugaTES.mm-host.json Mode=VmObserver.
- [ ] Escrever e observar falha para leitura assíncrona, desconexão/stale, JSON e nenhum início local no modo VM.
- [ ] Implementar reutilizando túnel/chave existentes; cancelamento e proteção de consultas concorrentes.
- [ ] Executar testes de tela/JSON e conexão privada.

### Task 5: Release e promoção/rollback candidatos
**Files:** deploy/mm-linux/*.service,*.json.example,README.md,preflight.sh; manifesto/hashes/evidências sob evidence/MM_VM_SERVICE_20261101.
- [ ] Gerar perfis inertes sem segredos, unidade com hardening e referências de LoadCredential e plano de corte único Windows→VM.
- [ ] Executar build Release Linux/WinForms e regressões focais; revisão independente final e corrigir achados materiais com testes.
- [ ] Empacotar runtime host/API/Desktop e manifestos SHA256; verificar pacote e preservar rollback instalado1.2.13.
- [ ] Apresentar autorização operacional única com pacote concreto; não instalar nem ativar neste gate.
