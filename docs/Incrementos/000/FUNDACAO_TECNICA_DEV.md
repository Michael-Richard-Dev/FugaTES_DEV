# Incremento 000 - Fundação técnica DEV

## OBJETIVO

Criar a estrutura física inicial da solução FugaTES no ambiente DEV, com projetos, referências, shell WinForms, host autônomo de integração, documentação de baseline e testes de arquitetura.

## GATES

GATE_000_A=APROVADO_ORIGINALMENTE

GATE_000_B=APROVADO_PARA_FUNDACAO_REV1

REV3=TOPOLOGIA_AUTONOMA_APROVADA

GATE_000_C=EM_AUDITORIA_FINAL

INCREMENTO_000_ENCERRADO=NAO

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

## ARQUITETURA

A solução segue separação em camadas: Domínio, Aplicação, Persistência, Integração SAP, Integração Toledo, Infraestrutura, WinForms e HostIntegracao. Domínio não conhece infraestrutura, banco, integrações, host ou interface gráfica.

FugaTES.Aplicacao é a camada destinada à coordenação futura dos contratos e casos de uso. No Incremento 000 existe somente sua fundação estrutural.

CASOS_DE_USO_FUNCIONAIS_IMPLEMENTADOS=NAO

## PROJETOS

- FugaTES.Dominio
- FugaTES.Aplicacao
- FugaTES.Persistencia
- FugaTES.Integracao.SAP
- FugaTES.Integracao.Toledo
- FugaTES.Infraestrutura
- FugaTES.WinForms
- FugaTES.HostIntegracao
- FugaTES.Testes

## DEPENDENCIAS

Aplicação referencia Domínio. Persistência, Integração SAP e Integração Toledo referenciam Aplicação e Domínio. Infraestrutura referencia Aplicação. WinForms referencia Aplicação, Persistência e Infraestrutura para monitoramento, controle e composição da interface, sem depender diretamente das integrações SAP ou Toledo. HostIntegracao referencia Aplicação, Persistência, Integração SAP, Integração Toledo e Infraestrutura para atuar futuramente como composition root autônomo.

## PADRAO_VISUAL

O shell inicial possui menu lateral, cabeçalho, área de conteúdo e rodapé. O rodapé exibe Ambiente, Versão, SAP, Toledo e Banco, todos sem validação operacional real. WinForms representa monitor, controle, consulta e solicitação, não motor de processamento.

## HOST_INTEGRACAO

FugaTES.HostIntegracao é o host autônomo responsável por lifetime e composição do processamento automático futuro. Nesta fundação há somente um BackgroundService estrutural com espera cancelável neutra, sem polling funcional, retry, banco, SAP, Toledo, URL ou SQL.

## COMPOSITION_ROOTS

A solução possui dois pontos de composição: FugaTES.WinForms para interface de monitoramento e controle, e FugaTES.HostIntegracao para execução autônoma futura. Nenhum caso de uso real foi implementado nesta revisão.

## TESTES

DOTNET_VERSION=10.0.301

RESTORE_EXIT_CODE=0

BUILD_EXIT_CODE=0

TEST_EXIT_CODE=0

TESTES_DEFINIDOS=12

TESTES_EXECUTADOS=12

TESTES_APROVADOS=12

TESTES_FALHARAM=0

WARNINGS=0

ERRORS=0

Os testes atuais validam a fundação arquitetural: isolamento do Domínio; restrições da Aplicação; ausência de referência WinForms para SAP e Toledo; referência do HostIntegracao para Aplicação; ausência de referência HostIntegracao para WinForms; independência entre SAP e Toledo; Persistência sem referência a integrações; ausência de ciclo interno; construção do PainelInicialForm sem acesso externo; construção estrutural do HostIntegracao sem banco, SAP ou Toledo real.

Os testes não validam integração funcional SAP/Toledo.

## SEGURANCA

Não foram adicionadas credenciais, tokens, connection strings autenticadas, endpoints operacionais ou chamadas externas reais. Configurações sensíveis devem permanecer fora do repositório.

Credenciais do legado não podem ser reutilizadas. FugaPET é referência conceitual/visual. IntegracaoSAP_CSharp é evidência histórica, não baseline.

## BANCO

POSTGRESQL_MAJOR=18

BANCO_CRIADO=NAO

DDL_EXECUTADO=NAO

TABELAS_IMPLEMENTADAS=NAO

NPGSQL_IMPLEMENTADO=NAO

Contrato Gaia=CONCEITUAL

Nenhuma tabela existe fisicamente nesta fundação.

COORDENACAO_PERSISTENTE=CONTRATO_CONCEITUAL_GAIA_REV2_APROVADO

IMPLEMENTACAO_PERSISTENCIA=NAO

BANCO_ALTERADO=NAO

## FORA_DE_ESCOPO

Não há login, banco de dados, SQL, integração SAP, integração Toledo, autenticação, repository funcional, gateway funcional, polling funcional, retry ou fluxo de cliente, fornecedor e transportadora.

## VALIDACAO

A validação prevista inclui restore, build, testes de arquitetura, inspeção estrutural do Designer, verificação de encoding UTF-8 com BOM, CRLF e busca por textos corrompidos.
