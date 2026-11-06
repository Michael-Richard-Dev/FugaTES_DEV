# Publicação do desktop — `C:\FugaTES_DEV110`

O `FugaTES.exe` e os binários que ele carrega. A pasta instalada guarda **configuração ao lado dos
binários** — `FugaTES.carrier.json` (que diz a qual SAP este desktop fala), `FugaTES.sd.json`, os do
MM, e as pastas `profiles` e `runtimes`. Por isso aqui não se copia pasta por cima de pasta: só
`*.dll`, `*.exe` e `*.pdb` são trocados, e nenhum `.json` é tocado.

## Por que isto passou a importar em 06/11/2026

O desktop ficou **três publicações atrás** do motor. As ações manuais da tela passaram a fazer
coisa diferente — e pior — do que o fluxo automático:

| botão da tela | com o desktop atrasado |
|---|---|
| **Compor texto** | procura lacre nos campos 1006/1010–1013, que o Guardian não manda mais → texto sem lacre |
| **Compor texto**, com Enter cru na observação | o parse falha e o complemento volta vazio → nota sem lacre, sem placa e sem motorista, em silêncio |
| **Ajustar** | PATCH por item, sem reconhecer a trava → foi o que deixou a remessa 80003934 pela metade |

## Conferir, sem alterar nada

```powershell
.\Publicar-Desktop.ps1 -Conferir
```

Diz quais correções estão — e quais não estão — **dentro dos binários instalados**.

## Publicar

Feche o `FugaTES.exe` antes. O script recusa publicar com ele aberto: o aplicativo segura os DLLs, e
copiar por cima falharia no meio, deixando a pasta com metade dos arquivos novos.

```powershell
.\Publicar-Desktop.ps1
```

O que ele faz, nesta ordem, parando em qualquer falha:

1. recusa se `FugaTES.exe` ou `FugaTES.HostIntegracao.exe` estiverem rodando;
2. compila a publicação numa pasta temporária;
3. **confere os marcadores no que foi compilado** — publicar binário velho por cima de velho seria
   uma operação sem efeito com relatório de sucesso, e isso já aconteceu hoje;
4. recusa se a publicação trouxer binário que não existe na pasta instalada: isso indicaria
   dependência nova, e aí o `deps.json` também precisaria mudar, o que este script não faz;
5. faz cópia de segurança da pasta inteira, com data e hora no nome;
6. troca os binários;
7. **confere os marcadores no que ficou instalado**. Se faltar algum, restaura a cópia sozinho.

Para voltar à mão, o script imprime o comando com o caminho da cópia.

## Sobre os marcadores

São nomes de código e literais das correções, procurados **byte a byte** dentro dos DLLs. Duas
armadilhas, as duas descobertas testando este script:

- **nome de variável local não fica no assembly**, fica no PDB. O marcador da guarda de rota é a
  rota em si (`.../API_OUTBOUND_DELIVERY_SRV;v=2/$batch`), não o nome da variável que a usa;
- **decodificar o arquivo inteiro como UTF-16 depende de alinhamento**: um literal que comece em
  offset ímpar não é encontrado. A busca é por bytes, com ISO-8859-1, para a posição não importar.

Foi o segundo verificador furado do dia — antes, o `strings` não existia na máquina e devolvia
vazio em silêncio, dando "0 ocorrências" para um pacote que estava correto. Quem confere também
precisa ser conferido.
