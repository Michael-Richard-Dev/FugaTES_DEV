# Emissores SAP read Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Exibir clientes/fornecedores e propriedades representadas por esses BPs em Cadastros -> Emissor, com paginas, filtros e JSON SAP.
**Architecture:** Modelo neutro de consulta na Aplicacao, adapter GET-only na Integracao.SAP, Form/Designer e composition com provider existente no WinForms. Sem cadastro mestre ou armazenamento dos BPs no FugaTES e sem Guardian nesta etapa.
**Tech Stack:** .NET 10, WinForms, OData V2 API_BUSINESS_PARTNER, xUnit.
**Spec:** docs/superpowers/specs/2026-11-03-emissores-sap-read-design.md (aprovado em chat).

## Global Constraints

- Trabalhar somente no repositorio oficial; preservar alteracoes anteriores/Claude e nao fazer commit/push.
- SAP origem, Guardian destino futuro, FugaTES integracao/monitoramento; zero POST e zero DDL/DML.
- GET HTTPS origem aprovada, client 110, provider protegido existente, timeout 30 segundos e sem redirects.
- Sem worker, nova tabela ou persistencia de cadastro; nenhuma dependencia da VM para esta consulta direta.
- Nao inferir propriedade por grupo Z2; BP20000170 tem ambos os papeis e aparece uma vez.

## Review Focus

- Continuacao externa ou mandante alterado nunca recebe credencial.
- Filtro com apostrofo nao altera expressao OData.
- Erro ou pagina incompleta nao vira ausencia nem sucesso total.
- Fechar tela ou trocar filtro nao permite resposta antiga sobrescrever consulta atual.
- BP com dois papeis e BPs sem cliente/fornecedor sao tratados sem duplicacao ou inclusao indevida.

## Task 1: contrato de leitura e adapter SAP

Files: novos Aplicacao/Consultas/EmissoresSap/EmissoresSapConsulta.cs, Integracao.SAP/BusinessPartner/EmissoresSapConsultaAdapter.cs e tests/R158.SapInboundReadiness.Tests/EmissoresSapConsultaTests.cs.
Interfaces: IEmissoresSapConsulta.ConsultarAsync(EmissoresSapFiltro filtro,string? continuacao,CancellationToken ct) -> Task<EmissoresSapPagina>; ObterJsonAsync(string bp,CancellationToken ct) -> Task<string>. Pagina contem registros, continuacao e horario. Erros tecnicos tipados e mensagens estaveis, sem corpo remoto nos logs.
- [ ] Confirmar GET de lista limitado e propriedades tecnicas, usando provider existente, sem registrar payload.
- [ ] Escrever testes RED: cliente, fornecedor, BP com ambos, sem papeis; PF/PJ; bloqueio; nome com apostrofo; pagina seguinte; host/rota/client/token repetido invalidos; HTTP401/403/500; body invalido; JSON do BP errado; cancelamento; metodos so GET.
- [ ] Implementar consulta paginada com ordem estavel e filtros por BP/nome, limites de body/tempo e validacao de continuidade.
- [ ] Executar filtro FullyQualifiedName~EmissoresSapConsultaTests e registrar GREEN.

## Task 2: tela e composicao

Files: novos WinForms/Cadastros/FrmEmissoresSap.cs, FrmEmissoresSap.Designer.cs, EmissoresSapComposition.cs; ajustes somente na abertura Emissor em FrmCadastros.cs e FrmIntegracoes.cs; novo EmissoresSapScreenTests.cs.
Interfaces: FrmEmissoresSap(IEmissoresSapConsulta? consulta=null), composition cria session disposable usando perfil/provider SAP existente e HttpClient GET-only.
- [ ] Escrever testes RED de filtros/colunas/JSON/paginacao, status erro/zero, bloqueio de acoes de escrita, descarte/cancelamento, navegação legada e demo sem chamadas reais.
- [ ] Implementar consulta assincrona manual, pagina anterior/proxima com mesmos filtros, JSON em dialogo de leitura e estado conectado/erro/ultima leitura; sem botao de cadastro Guardian.
- [ ] Rodar testes focais de consulta/tela e regressao Cadastros/MM/SD pertinente.

## Task 3: verificacao e candidato

- [ ] Revisar delta focal e seguranca; executar build Release e testes necessarios somente.
- [ ] Validar GET real limitado da lista e BP20000170; guardar somente classificacao/status/contagens.
- [ ] Gerar layout QA e candidato local sem perfis/segredos; fonte/hashes e procedimento de rollback.
- [ ] Publicar DEV110 somente com reserva de publicacao confirmada e aplicativo fechado. Se houver concorrencia Claude, deixar candidato pronto sem sobrescrever instalacao.

Sem commits: regra do operador prevalece sobre commits sugeridos por skills.
