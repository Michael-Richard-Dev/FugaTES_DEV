# MM Purchase Orders Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans; implementação inline, revisão independente final.
**Goal:** consultar todos os pedidos ativos/liberados de empresa 3000, centros 3011/3008, a cada 30s mesmo com WinForms fechado.
**Architecture:** adaptador SAP V4 GET-only paginado; coletor headless no EXE existente com provider Windows; snapshot local atômico e tela MM observadora.
**Tech Stack:** .NET 10 / WinForms / System.Text.Json / HttpClient / tarefa Windows do usuário.
**Spec:** docs/superpowers/specs/2026-10-31-mm-purchase-orders-design.md, adendo confirmado de 31/10.
## Global Constraints
Somente repositório oficial. Nenhum POST/DB/VM/GRANT/migration. Preservar SD/ACM/Toledo e alterações não reconciliadas. Sem commit/push ou novo workspace. Provider existente; segredos nunca em pacote/log. Apenas TI/SYSTEM no snapshot. Estação ligada e sessão TI necessária; sem falsa promessa de processo com Windows desligado.
## Review Focus
1. Próxima página externa/cliente diferente: rejeitar antes da autenticação.
2. Falha em página posterior: não apresentar snapshot parcial nem apagar último bom.
3. Coletores/ciclos concorrentes: trava independente da UI e sem sobreposição.
4. API ignora filtro/status ausente: revalidar localmente; nunca classificar como aprovado.
5. Fechamento/reabertura e cache antigo: não parar coletor; mostrar desatualização explicitamente.
## Task 1: consulta SAP MM
Files: new Aplicacao/Modelos/Mm/PedidosCompraMm.cs (DTO/porta), Integracao.SAP/BusinessDocuments/PedidosCompraMmConsultaAdapter.cs; tests/R158.SapInboundReadiness.Tests/PedidosCompraMmTests.cs.
Interface: IPedidosCompraMmConsulta.ConsultarAsync(CancellationToken) -> Task<MmConsultaResultado>. Scope fixo 3000, 3011/3008, status05 e liberação/incompleto false, deleções vazias. JSON por cabeçalho/item.
- [ ] RED testes do adapter ausente via porta/reflection, transporte simulado no limite HTTP.
- [ ] GREEN paginação cabeçalho/itens, segurança nextLink, decimal, validações, status/erros sanitizados; sem alterar executor compartilhado.
- [ ] Testes MM filtrados PASS; GET físico completo e evidência sanitizada de contagens.
## Task 2: coletor e snapshot
Files: new WinForms/Mm/MmSnapshotStore.cs, MmPurchaseOrderCollector.cs, MmPurchaseOrderComposition.cs; new tests/MmPurchaseOrderCollectorTests.cs. Modify Program.cs somente flag --mm-purchase-orders antes do mutex da UI.
Interfaces: MmSnapshotStore.Read/Write; MmPurchaseOrderCollector.CycleAsync/RunAsync; factory cria adapter com provider aprovado. Trava de arquivo exclusiva do processo. Ciclo imediato e PeriodicTimer30s, timeout25s, fila zero. Snapshot schema1, origem/client/empresa/centros, linhas, timestamps/código/contagens; gravação temporária + rename atômico. Falha preserva últimas linhas. Headless loga somente código sanitizado em status. Fechar tela não dispõe coletor.
- [ ] RED testes snapshot preservado em falha, concorrência, cancelamento, identidade inválida e cache ilegível.
- [ ] GREEN mínimo e execução background no EXE existente.
- [ ] Testes MM collector PASS; execução real GET sem POST.
## Task 3: tela, integração e entrega
Files: new WinForms/FrmPedidosCompraMm.cs + Designer.cs; modify FrmIntegracoes.cs + Designer.cs (somente menu MM); project version1.2.12 se publicação exclusiva; tests/MmPurchaseOrderScreenTests.cs.
Tela: observadora de snapshot a cada2s, status/origem/empresa/centros/último sucesso/próxima consulta, contagem pedidos/itens, grid campos solicitados e JSON, sem botão empresarial. EnsureStarted cria headless sem dependência da vida da tela. Instalação registra tarefa por logon TI; sem admin ou senha.
- [ ] RED testes menu/grade/JSON/desatualização, preservar SD e demais botões.
- [ ] GREEN implementação e build Release.
- [ ] Rodar projeto de testes pertinente; registrar falhas prévias sem esconder. Revisão independente final; corrigir defeitos comprovados.
- [ ] Coordenar EXE com Claude, backup, pacote/hashes e publicação DEV110 sem copiar segredos. Registrar tarefa startup de usuário, iniciar coletor, abrir oficial e validar visualmente MM+SD; fechar UI e provar novo ciclo; reabrir e comprovar snapshot físico. Backup/rollback tarefa+arquivos.
Commits deliberadamente omitidos por regra do operador. Nenhum commit/push.
