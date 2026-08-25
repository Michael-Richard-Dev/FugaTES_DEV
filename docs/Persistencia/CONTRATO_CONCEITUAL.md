# Contrato conceitual de persistência

POSTGRESQL_MAJOR=18

BANCO_CRIADO=NAO
DDL_EXECUTADO=NAO
TABELAS_IMPLEMENTADAS=NAO
NPGSQL_IMPLEMENTADO=NAO

Contrato Gaia=CONCEITUAL

Nenhuma tabela existe fisicamente nesta fundação. Este documento não contém DDL, SQL executável, connection string ou implementação de persistência.

## Entidades conceituais

- integracao: representa uma operação de integração e seu estado conceitual.
- tentativa_integracao: representa uma tentativa de processamento vinculada a uma integração.
- auditoria_integracao: registra eventos relevantes para rastreabilidade e auditoria.
- pausa_processamento: representa bloqueio persistente futuro para novos claims.

## Conceitos obrigatórios

claim: obtenção de posse temporária para processar uma integração.

lease: janela de validade da posse de processamento.

heartbeat: sinal periódico futuro de que a instância proprietária continua ativa.

fencing: proteção contra escrita ou conclusão por instância que perdeu a posse.

recovery: recuperação futura de integrações abandonadas, expiradas ou indeterminadas.

idempotencia local: proteção contra duplicidade dentro do próprio FugaTES.

idempotencia externa: proteção conceitual contra duplicidade no sistema externo quando houver suporte ou chave apropriada.

resultado indeterminado: estado conservador quando não é possível afirmar se o efeito externo ocorreu.

pausa persistente: bloqueio armazenado de novos claims para um alvo de processamento.

## Nomenclatura Hera

Os termos claim, lease e fencing podem ser usados na explicação técnica. Os futuros identificadores físicos próprios de banco deverão seguir português sem acento.

Mapeamento conceitual recomendado:

- correlation_id -> id_correlacao
- claim_token -> id_posse_processamento
- claim_owner -> instancia_proprietaria
- claim_started_at -> posse_iniciada_em
- lease_expires_at -> posse_expira_em

O nome definitivo será fechado quando Gaia produzir o DDL executável autorizado.

## Marco de possível envio externo

MARCO_DE_POSSIVEL_ENVIO_EXTERNO é conceito oficial.

Nome físico futuro recomendado: marco_envio_externo_em.

Semântica: após esse marco o FugaTES deverá assumir conservadoramente que o efeito externo pode ter ocorrido. Isso não comprova transmissão efetiva.

## Estados conceituais

- PENDENTE
- EM_PROCESSAMENTO
- AGUARDANDO_REPROCESSAMENTO
- AGUARDANDO_ACAO_MANUAL
- RESULTADO_INDETERMINADO
- CONCLUIDA
- FALHA_DEFINITIVA
- CANCELADA

AGUARDANDO_REPROCESSAMENTO representa nova tentativa futura de integração com histórico. AGUARDANDO_ACAO_MANUAL representa bloqueio por decisão humana pendente. RESULTADO_INDETERMINADO representa incerteza conservadora sobre possível efeito externo.

## Pausa

Pausa bloqueia novos claims e não aborta automaticamente chamada externa em andamento. A pausa pode existir conceitualmente para SAP, TOLEDO ou OPERACAO.

## Segurança

Segredos ficam fora do Git. Credenciais hardcoded são proibidas. A configuração futura será por ambiente. Credenciais do legado não podem ser reutilizadas. Endpoints operacionais não pertencem à fundação.

FugaPET é referência conceitual/visual. IntegracaoSAP_CSharp é evidência histórica, não baseline.
