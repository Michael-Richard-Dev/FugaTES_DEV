# R167 — Reaproveitar o endpoint SAP existente para o FugaTES unificado

Objetivo: o próximo contrato SAP novo chegar ao DEV unificado **sem reconfiguração do lado SAP**,
mantendo `http://192.168.17.40/api/v1/integracoes/sap/contratos`.

---

## Etapa 1 — O ingresso real, lido na VM

`/etc/nginx/sites-enabled/fugates-hml-http.conf`, location
`= /api/v1/integracoes/sap/contratos`:

```nginx
listen 192.168.17.40:80;          # sem TLS
allow 10.200.3.77; allow 10.200.2.17; allow 192.168.3.173;
allow 10.30.3.2;   allow 192.168.254.101/32;
limit_except GET { allow <os 4 primeiros>; deny all; }
deny all;
proxy_pass http://127.0.0.1:8080; # HML
proxy_set_header X-Forwarded-Proto http;
proxy_set_header Authorization "";   # remove qualquer credencial
access_log off;
```

O comentário do próprio arquivo diz: *"No external Authorization contract in internal HML."*

**A postura de segurança atual deste endpoint é: allowlist de IP de origem + backend em loopback.
Não há TLS e não há autenticação do emissor.** Confirmado também que **não existe nenhum servidor
TLS configurado**: só há um `listen 443` comentado no site `default` e um snippet snakeoil. Os
diretórios `fugates-tls`, `fugates-secrets` e `fugates-staging` são de root e não foram abertos.

### O menor ponto de adaptação

O `location` já existe, já tem a allowlist certa e já entrega em loopback. A única coisa que muda
é o **destino interno**:

```
proxy_pass http://127.0.0.1:8080;    (HML)
→ proxy_pass http://127.0.0.1:18764; (receptor DEV unificado)
```

A URL, a allowlist, os métodos e as demais locations ficam idênticos. Sem `mirror` — duplicar o
tráfego criaria o contrato duas vezes.

---

## Etapa 2 — As duas lacunas de admissão, e o que é honesto fazer

O receptor DEV (`HandleDurableAsync`) exige, nesta ordem: **peer loopback** → **Basic** →
**`X-Forwarded-Proto: https`** (apenas no modo `LoopbackBasicHttps`).

| Exigência | O ingresso entrega? | Solução honesta |
|---|---|---|
| Peer loopback | **sim** — `proxy_pass` para `127.0.0.1` | nada a fazer |
| `X-Forwarded-Proto: https` | **não** — envia `http` | usar `SecurityMode=LoopbackOnly` |
| `Authorization: Basic` | **não** — o header é removido | decisão do operador (abaixo) |

### Lacuna A — HTTPS: resolvida sem falsificar nada

`SapContractInboundProductiveComposition` aceita exatamente dois modos: `LoopbackBasicHttps` e
**`LoopbackOnly`**. O segundo dispensa **apenas** a exigência de `https`, preservando Basic e peer
loopback. Não é um modo inventado: já existe no código e é validado por allowlist.

Usar `LoopbackOnly` é o caminho correto justamente porque o tráfego **não** é TLS. A alternativa —
injetar `X-Forwarded-Proto: https` no nginx — seria declarar uma proteção que não existe, e está
descartada.

Fica registrado o que isso significa: entre SAP/CPI e o nginx o tráfego segue **em claro na rede
interna**, exatamente como já está hoje para o HML. Não é uma piora; é o estado atual, assumido
explicitamente em vez de mascarado.

### Lacuna B — Basic: exige decisão, não tem solução puramente técnica

O receptor exige Basic nos **dois** modos. O ingresso remove o header. Logo, sem alteração, o corte
produziria **401 garantido**.

Só há três caminhos, e nenhum é neutro:

1. **Injetar a credencial no nginx** (`proxy_set_header Authorization "Basic …"`), lida do arquivo
   600 da VM. Funciona e não falsifica TLS — mas é preciso dizer o que ela é: **autentica o
   ingresso, não o emissor SAP**. A autenticação do remetente continua sendo a allowlist de IP,
   como hoje. Ganha-se um controle real no salto loopback (impede outro processo local de postar no
   receptor), que o caminho HML sequer possui.
2. **Pedir ao SAP que envie Basic** — é exatamente a reconfiguração que esta missão quer evitar.
3. **Remover a exigência de Basic do receptor** — enfraquece o produto para todos os ambientes.
   **Descartado.**

A opção 1 é a única compatível com "não mexer no SAP", e **não é minha decisão**: ela muda o que
"Basic" significa nesse caminho.

---

## Etapa 3 — Receptor DEV

Pronto e não instalado. `Ativar-ReceptorDev.ps1` ganhou o parâmetro `-SecurityMode`, com
`LoopbackBasicHttps` como padrão — nada muda em silêncio. Para este cenário será preciso
`-SecurityMode LoopbackOnly`, com a justificativa acima.

Preservados: porta 18764 isolada, role `fugates_sap_inbound_app`, destino exclusivo
`fugates_dev:5433`, H4 e persistência transacional, idempotência por contrato/versão, ACK durável,
sem worker Guardian, sem SOAP/REST externo, janela R57 limitada.

Continuam faltando, e não serão fabricados: `WindowId`, `AuthorizationReference`, e o centro,
parceiro e material do contrato de homologação.

---

## Etapa 4 — Plano de corte

`Cortar-IngressoSapParaDev.ps1`, preparado e **não executado**:

| Requisito | Como é atendido |
|---|---|
| Backup | `fugates-hml-http.conf.backup.r167.<timestamp>` antes de qualquer escrita |
| Validação | `nginx -t` antes do `reload`; configuração inválida nunca entra |
| Rota HML preservada | só o `location` do contrato SAP é tocado; as demais ficam idênticas |
| Origem e autenticação | allowlist inalterada; Basic de canal exigido via `-InjetarBasic` |
| Destino verificado | recusa cortar se nada escutar em `127.0.0.1:18764` |
| Sem duplicação | troca de destino, nunca `mirror` |
| Rollback | `-Reverter -Confirmo` restaura o backup, revalida e recarrega |
| Janela limitada | o script avisa e cobra a reversão logo após o contrato |

Exige `-Confirmo` explícito. Em `-Conferir` apenas lê o estado:

```
ESTADO  PASS  destino atual=127.0.0.1:8080 | HML 8080 no ar=True | receptor DEV 18764 no ar=False
```

### O impacto que precisa ser aceito

Enquanto o corte estiver ativo, **todo** contrato SAP que chegar nessa URL vai para o DEV e **não**
para o HML. O nginx não consegue distinguir o contrato de homologação dos demais antes de ler o
corpo JSON, e rotear por corpo não é confiável. Por isso a janela tem de ser curta e combinada com
quem opera o SAP.

---

## Etapa 5 — Prova real

Depois do corte e com a janela R57 aberta, um único contrato **novo** (não o 1160, não os
históricos 1139/1140/1144/1147/1155) comprova a cadeia: identidade do contrato, endpoint e destino
efetivos, resposta do receptor, `COMMIT` em 5433, versão e IntegrationId, H4, consulta pela API
18762, visualização no DEV110 e JSON contratual persistido. Guardian sem execução empresarial.
