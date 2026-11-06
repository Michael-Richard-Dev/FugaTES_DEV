# Cadastros Material e Transportadora Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Native execution in current session authorized by operator request aplicar mesmo padrao. No additional worktree/commit; official source only.

**Goal:** Leitura SAP paginada Material/Transportadora no menu Cadastros, preservando Emissor e demais fluxos.
**Architecture:** Contrato neutro de consulta, dois adaptersGET e helper de leitura/paginacao/JSON, Form/Designer parametrizado por categoria. FonteSAP, dados temporarios somente em memoria, sem cadastro mestre local.
**Tech Stack:** .NET10, WinForms, ODataV2, providerSAPexistente, xUnit.
**Spec:** docs/superpowers/specs/2026-11-03-cadastros-sap-material-transportadora-design.md

## Global Constraints
- SomenteGET, origemSAPaprovada e client110; sem cookies/redirects/segredos emlogs.
- ZeroGuardiancalls,DBwrites,VMchanges,workers e commits/push.
- Preservar codigoEmissor1.2.16,Transportadora legado,ACM/MM/SD.
- Motorista/Veiculo aguardam contrato; nao inferir.

## Review Focus
- Falha em pagina posterior nao promove lista parcial.
- BP/material alfanumerico, apostrofo e zeros preservados com escape correto.
- NomePT ausente nao e preenchido com idioma arbitrario.
- CRM010 expirado ou incompleto fica visivel com estado correto, sem falsa elegibilidade.
- Tela cancelada/erro/refresh invalida memoria/cursor anterior.

## Task1 Leitores
Files:Aplicacao/Consultas/CadastrosSap/CadastrosSapConsulta.cs;Integracao.SAP/Cadastros/SapCadastroLeitura.cs,MateriaisSapConsultaAdapter.cs,TransportadorasSapConsultaAdapter.cs;tests/R158.SapInboundReadiness.Tests/CadastrosSapConsultaTests.cs.
Interfaces:ICadastroSapConsulta.Categoria; ConsultarAsync(CadastrosSapFiltro,string?,CancellationToken)->Task<CadastrosSapPagina>; ObterDetalheAsync(string,CancellationToken)->Task<CadastroSapDetalhe>.
- [ ] Contratos/stubs/testesRED com conteudo sintetico.
- [ ] Implementar leitura segura e temporaria completa com pagina/filtro; detalhe restrito porcategoria e identidade.
- [ ] GREEN e prova fisica somenteGET com contagens/estados sem payloads.

## Task2 Tela
Files:WinForms/Cadastros/FrmCadastroSap.cs,.Designer.cs,CadastrosSapComposition.cs;FrmCadastros.cs,FrmIntegracoes.cs;tests/CadastrosSapScreenTests.cs.
- [ ] TestesRED, implementar duas categorias e navegacao mantendo demo/legados; JSON/detalhes, status/filtros/paginas e descarte.
- [ ] GREEN+regressao Emissor/Cadastros/MM/SD pertinente, QAvisual.

## Task3 Candidato
- [ ] Revisao independente focal, buildRelease e testes binarios exatos.
- [ ] Confirmar estabilidade fonte, backupprivado e publicacaoDEV110 com perfis intactos, abrirEXEoficial.
- [ ] Fechamento sanitizado, caminhos e pendencia explicita Motorista/Veiculo.
