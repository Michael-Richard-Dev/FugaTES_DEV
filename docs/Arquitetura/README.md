# Arquitetura FugaTES

A baseline aprovada do Incremento 000 possui 9 projetos. A solução separa domínio, aplicação, persistência, integrações, infraestrutura, interface WinForms, host autônomo e testes. A direção das dependências protege regras internas contra detalhes de tecnologia e mantém o processamento automático fora da interface gráfica.

## BASELINE_TECNICA

SDK_VALIDADO=.NET SDK 10.0.301

PROJETOS_DE_BIBLIOTECA=net10.0

FugaTES.WinForms=net10.0-windows

UseWindowsForms=true

FugaTES.Testes=net10.0-windows

FugaTES.HostIntegracao=Microsoft.NET.Sdk.Worker

FugaTES.HostIntegracao_TARGET=net10.0

Nullable=enable

ImplicitUsings=enable

ARTEFATOS_BASE:

- Directory.Build.props
- .editorconfig
- .gitignore
- FugaTES.slnx

ENCODING=UTF-8 COM BOM

FIM_DE_LINHA=CRLF

## Projetos

- FugaTES.Dominio: modelos internos, enumeradores, regras puras e exceções de domínio.
- FugaTES.Aplicacao: camada destinada à coordenação futura dos contratos e casos de uso. No Incremento 000 existe somente sua fundação estrutural.
- FugaTES.Persistencia: fronteira para PostgreSQL futuro, sem implementação funcional nesta fundação.
- FugaTES.Integracao.SAP: fronteira para SAP futuro, sem cliente funcional nesta fundação.
- FugaTES.Integracao.Toledo: fronteira para Toledo/Guardian futuro, sem cliente funcional nesta fundação.
- FugaTES.Infraestrutura: configuração, segurança, logging e diagnóstico transversal futuros.
- FugaTES.WinForms: monitor, controle, consulta e solicitação operacional.
- FugaTES.HostIntegracao: motor autônomo futuro, composition root e lifetime do processamento.
- FugaTES.Testes: testes de arquitetura e construção estrutural.

CASOS_DE_USO_FUNCIONAIS_IMPLEMENTADOS=NAO

## Composition Roots

FugaTES.WinForms é o composition root da interface de monitoramento e controle.

FugaTES.HostIntegracao é o composition root do host autônomo de integração.

## Regras de dependência congeladas

- WinForms não referencia SAP/Toledo.
- HostIntegracao não referencia WinForms.
- Aplicacao não referencia adapters concretos.
- SAP não referencia Toledo.
- Toledo não referencia SAP.
- Persistencia não referencia SAP/Toledo.

## Persistência

COORDENACAO_PERSISTENTE=CONTRATO_CONCEITUAL_GAIA_REV2_APROVADO

IMPLEMENTACAO_PERSISTENCIA=NAO

DDL_EXECUTADO=NAO

BANCO_ALTERADO=NAO

## Fora de escopo da fundação

Não há integração funcional, SQL, DDL, banco criado, schema, role, Npgsql, token, SOAP, polling funcional ou retry funcional.
