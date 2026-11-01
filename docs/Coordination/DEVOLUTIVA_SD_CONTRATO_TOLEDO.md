# Devolutiva de pesagem — fluxo SD (venda)

Contrato do POST que o Guardian deve enviar ao FugaTES quando a pesagem de uma **venda** é
concluída. É **diferente do ACM**: rota própria e dois campos adicionais obrigatórios.

---

## Endereço

```
POST http://192.168.17.40/api/guardian/set26/callback/venda
Content-Type: application/json
```

A rota do ACM (`.../callback/recebimento`) continua como está e **não deve ser usada para venda**.

---

## Exemplo completo

```json
{
  "SistemaBalanca": "Toledo",
  "DataPesagem": "11/01/2026 14:32:07 PM",
  "TipoPesagem": "S",
  "IdPesagem": "0020401",
  "UsuarioPesagem": "andreia.strapas",
  "Centro": "3011",
  "PesoBruto": "32500.0",
  "PesoTara": "12400.0",
  "PesoAntesDesconto": "20100.0",
  "PesoAposDesconto": "20100.0",
  "NomeMotorista": "MARCIO FERENZ",
  "CpfMotorista": "03064148990",
  "Placa": "COF7595",
  "Observacao": "",
  "camposAdicionais": [
    { "numero": 1003, "valor": "0000001815" },
    { "numero": 1009, "valor": "000010" }
  ],
  "to_QualidPes": [
    { "Tipo": "IMPUREZA", "ValorAferido": "0.0" },
    { "Tipo": "UMIDADE",  "ValorAferido": "0.0" }
  ],
  "to_Pesagem": [
    {
      "Indice": 1,
      "NrIndiceTotal": 1,
      "IdParceiroEntrega": "20000453",
      "PercentualDesdobro": "100",
      "PesoAntesDesconto": "20100.0",
      "PesoAposDesconto": "20100.0"
    }
  ]
}
```

---

## O que é obrigatório

| Campo | Observação |
|---|---|
| `IdPesagem` | identifica o evento; é por ele que um reenvio é reconhecido |
| `Centro` | `3011` ou `3008` |
| `TipoPesagem` | **`S`** (saída). `E` é recusado: pertence ao fluxo de recebimento |
| `DataPesagem` | ver formatos aceitos abaixo |
| `PesoBruto`, `PesoTara`, `PesoAposDesconto` | números em texto, ponto como separador decimal |
| `camposAdicionais` 1003 e 1009 | ver abaixo |

Opcionais e aproveitados: `Placa`, `NomeMotorista`.
Os demais campos podem vir — são guardados no documento original e ignorados na leitura.

---

## Os dois campos adicionais — a parte que difere do ACM

São eles que dizem **a qual ordem de venda e a qual item** a pesagem pertence. O ACM não
precisava disso (usava o contrato), por isso o JSON daquele fluxo não os traz.

| Número | Nome no Guardian | Conteúdo |
|---|---|---|
| `1003` | `OrdemVendas` | número da ordem de venda |
| `1009` | `SD_ItDocV` | item da ordem (10, 20, 30...) |

Forma preferida — `camposAdicionais` na raiz, com `numero` e `valor`:

```json
"camposAdicionais": [
  { "numero": 1003, "valor": "0000001815" },
  { "numero": 1009, "valor": "000010" }
]
```

**Zeros à esquerda não importam:** `0000001815` e `1815` são tratados como o mesmo valor.

Também são aceitas, para não travar a configuração: `nome` em vez de `numero`
(`"nome": "OrdemVendas"`), a lista dentro do item de `to_Pesagem`, e os valores soltos na raiz
(`"OrdemVendas": "1815"`).

**Se esses campos não vierem**, o POST **não é recusado**: a pesagem é gravada e marcada como
`SEM_CORRELACAO`, visível na tela de acompanhamento. Mas ela não segue para o SAP, porque não há
como saber de qual ordem é.

---

## Formatos de data aceitos

```
11/01/2026 14:32:07 PM     (MM/dd/yyyy, o mesmo já usado no recebimento)
01/11/2026 14:32:07        (dd/MM/yyyy)
2026-11-01T14:32:07        (ISO)
```

Não há fuso no documento; a hora é guardada como veio.

---

## Respostas

| HTTP | Corpo | Significado |
|---|---|---|
| 200 | `{"status":"RECEBIDA","persisted":true,"correlacao":"CORRELACIONADA","chave":"1815/10"}` | recebida e correlacionada |
| 200 | `{"status":"RECEBIDA","persisted":true,"correlacao":"SEM_CORRELACAO"}` | recebida, sem os campos 1003/1009 |
| 200 | `{"status":"JA_RECEBIDA","persisted":false}` | mesmo `IdPesagem` já recebido |
| 400 | `{"status":"RECUSADA","codigo":"...","detalhe":"..."}` | documento fora do contrato |
| 415 | `{"codigo":"INVALID_CONTENT_TYPE"}` | faltou `Content-Type: application/json` |
| 503 | `{"codigo":"SD_RECEPTOR_NAO_CONFIGURADO"}` | receptor desligado no servidor |

**Reenvio é seguro.** Se houver dúvida se o POST chegou, pode repetir: o mesmo `IdPesagem`
devolve `200 JA_RECEBIDA` e nada é gravado de novo. Não é preciso tratar como erro.

Códigos de recusa mais prováveis: `SD_DEVOLUTIVA_TIPO_NAO_E_SAIDA` (veio `E`),
`SD_DEVOLUTIVA_CAMPO_AUSENTE` (falta um obrigatório), `SD_DEVOLUTIVA_PESO_INVALIDO`.
O motivo vem sempre no corpo — uma recusa sem causa não ajudaria a corrigir.

---

## Antes de configurar

O receptor **ainda não está publicado** no servidor. Enquanto não estiver, o POST responde 404.
Combinar a publicação com o time do FugaTES antes de apontar o Guardian para esta rota.
