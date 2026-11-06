# Publicação do receptor compartilhado — 06/11/2026

Serviço `fugates-toledo-callback` (127.0.0.1:18086), que atende as duas rotas do Guardian:

- `/api/guardian/set26/callback/recebimento` — ACM
- `/api/guardian/set26/callback/venda` — devolutiva SD

Commit `ae26df2`. Nada do ACM muda nesta publicação; as três correções são do fluxo SD.

## O que esta publicação corrige

**1. São dois tipos de lacre, e agora cada um vai no seu campo.** Testemunha também é lacre — muda
a nomenclatura, não a natureza —, e a NF-e tem um espaço próprio para cada um. O Guardian não tem
campo separado para os dois, então a quebra de linha dentro da `Observacao` é o que diz qual é
qual: **primeira linha é lacre de carga, da segunda em diante é testemunha**. A leitura saiu dos
campos adicionais 1006 e 1010 a 1013, que não são mais lacre.

O rótulo `N dos envelopes de seguranca das amostras testemunhas:` já existia no código, mas só como
delimitador — marcava onde terminava o espaço dos lacres e nunca era preenchido. Agora é campo.

**2. Enter cru na observação deixa de derrubar a pesagem.** O JSON exige que quebra de linha dentro
de string venha escapada. Crua, o documento é inválido e era recusado antes de olhar campo nenhum —
foi o 400 da pesagem 0030384, duas vezes em 05/11/2026, com `'0x0D' is invalid within a JSON
string`. Como a quebra é parte do processo (é ela que separa os dois tipos de lacre), ela vai
acontecer sempre, e depender do modelo do Guardian estar escapado seria depender do que quem opera
não controla.

**Nada além de caractere de controle é tolerado**: vírgula faltando, aspas sem par e chave sem
fechamento continuam sendo recusados.

**3. Pesagem de entrada deixa de ser recusa.** O Guardian manda a mesma finalização para as duas
rotas, então todo recebimento passa pela rota de venda. O documento está certo — só pertence ao
outro fluxo. Respondia 400, e com `Valor em Caso de Sucesso = OK` configurado no Guardian isso o
fazia marcar falha e **reenviar para sempre** uma pesagem que está certa. Agora responde 200 com
`mensagem: OK` e `status: NAO_E_DO_SD`, sem gravar nada.

Documento de fato errado continua respondendo 400 com a causa — a distinção está travada em teste.

## Como publicar

No Windows, nesta pasta:

```powershell
.\Enviar-CallbackSd.ps1
```

Na VM, nesta ordem. Os dois primeiros **não alteram nada**:

```bash
chmod +x ~/publicar-callback-sd.sh && sudo ~/publicar-callback-sd.sh conferir
```

```bash
sudo ~/publicar-callback-sd.sh ensaio
```

```bash
sudo ~/publicar-callback-sd.sh instalar
```

Qualquer falha na conferência das rotas volta sozinha para o release anterior. Para voltar à mão:

```bash
sudo ~/publicar-callback-sd.sh reverter
```

## Depois de publicar

A rota responde ao GET sem gravar nada, então dá para confirmar que o processo novo está servindo:

```bash
curl -s http://127.0.0.1:18086/api/guardian/set26/callback/venda
```

E o journal mostra as duas situações que deixaram de ser 400:

```bash
sudo journalctl -u fugates-toledo-callback --since '-1h' --no-pager | grep -E 'SD_DEVOLUTIVA|SD_DEVOLUTIVA_E_DE_ENTRADA'
```

Pesagem de recebimento agora aparece como `SD_DEVOLUTIVA_E_DE_ENTRADA ... nada gravado`, e não mais
como `SD_DEVOLUTIVA_RECUSADA`.

## O campo Observação no Guardian

Duas linhas. A primeira é o lacre da carga, a segunda é o da amostra testemunha:

```
134955,134956,134957,134958
65593,65592,65591
```

Isso sai na NF-e como:

```
Lacres: 134955,134956,134957,134958
N dos envelopes de seguranca das amostras testemunhas: 65593,65592,65591
```

Dentro de uma linha nada é interpretado: vírgula, ponto e vírgula, barra ou espaço passam como
vieram. Só a quebra de linha separa os dois tipos.

Uma linha só significa que há apenas lacre de carga. Linha vazia ou zerada some sem deslocar os
tipos. Da terceira linha em diante tudo vai junto com a testemunha — não é o formato previsto, mas
descartar perderia lacre em silêncio.

**`Observacao` é campo de texto livre**: o que for digitado nela vai para a NF-e como lacre. Não há
como o código distinguir um lacre de um recado.
