# Resumable Transfer Lab

**Versão de desenvolvimento 0.1.0 — Lofrano Tecnologia.** Utilitário .NET original, com licença MIT, e laboratório HTTP local real. Interrompe em um checkpoint ordenado, reabre o estado persistido, retoma usando ETag forte e publica um arquivo novo somente após conferir o SHA-256 informado pelo chamador.

[English](README.md) · [Contrato preciso, em inglês](docs/contract.md) · [Projeto](docs/design.md) · [Site oficial](https://lofranotecnologia.com.br)

Não é cliente de produção, prova de autenticidade da origem, verificador de malware, sandbox de caminhos ou recuperação garantida após queda de energia. Implementa apenas o subconjunto local de intervalo único documentado, sem alegação de conformidade HTTP ampla.

## Instalação, preparação, testes e demonstração

Pré-requisitos: Bash e **SDK .NET 10.0.403** instalado, com os frameworks compartilhados .NET e ASP.NET Core. `global.json` desativa roll-forward. Não há pacotes NuGet de execução ou testes. Obtenha o SDK separadamente pelas instruções oficiais do .NET; os scripts não o instalam.

Na pasta do projeto:

```sh
bash scripts/prepare.sh
bash scripts/test.sh
bash scripts/demo.sh --text
bash scripts/demo.sh --json
```

A preparação faz restore explícito dos três projetos com fontes de pacotes vazias. Testes e demo compilam com `--no-restore` e executam as DLLs diretamente: não fazem restore implícito. Sem os arquivos preparados, falham em vez de baixar dependências. Diretórios do SDK, pacotes e saídas ficam em `build/`, ignorado, sem usar o HOME do host. Os testes são um executável de console sem dependências, não `dotnet test`. Precisam de portas efêmeras de loopback e diretórios temporários próprios. Após preparar o SDK, não exigem rede externa; a CI separa as etapas, mas não constitui um isolamento de rede.

Exemplo de saída determinística:

```text
Resumable Transfer Lab 0.1.0 (development)
Local synthetic HTTP; expected-byte integrity, not origin authenticity.
Checkpoint: 10/32 bytes (0123456789)
Resume: Range bytes=10-; If-Range "lab-artifact-32b-v1"
Response: 206; Content-Range bytes 10-31/32
Final: 0123456789abcdefghijklmnopqrstuv
SHA-256: 73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77
Published: True
Changed ETag: HTTP 200; validator_changed_or_full_response_refused
Preserved: 10 bytes (0123456789); destination created: False
```

O relatório JSON contém `version`, `resumeAndVerifyScenario` e `changedETagRefusalScenario`. Por exemplo: `resumeAndVerifyScenario.interruptedAtBytes` vale `10`, `resumeRangeHeader` vale `bytes=10-` e `changedETagRefusalScenario.destinationCreated` vale `false`. `--json` escreve somente JSON em stdout; mensagens de compilação vão para stderr. `--both` mistura texto e JSON, não sendo um documento JSON.

## Uso pela CLI

Após compilar:

```sh
rtl() { dotnet build/bin/ResumableTransferLab.Cli/Release/net10.0/ResumableTransferLab.Cli.dll "$@"; }
rtl --help
rtl fixture
```

`fixture` informa a URL efêmera real, identificador, tamanho e hash em JSON. Deixe esse terminal aberto; Enter ou EOF encerra o servidor. Em outro terminal, defina `URL` com o valor impresso e escolha caminhos **ainda inexistentes** em um diretório confiável:

```sh
rtl transfer --url "$URL" --artifact-id synthetic-32b --workspace ./lab-workspace --destination ./lab-output.bin --expected-length 32 --expected-sha256 73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77 --stop-after-bytes 10 --json
rtl inspect-state --workspace ./lab-workspace --json
rtl transfer --url "$URL" --artifact-id synthetic-32b --workspace ./lab-workspace --destination ./lab-output.bin --expected-length 32 --expected-sha256 73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77 --json
```

Códigos de saída: `0` para conclusão, interrupção ordenada, inspeção consistente ou demo bem-sucedida; `1` para sintaxe inválida; `2` para recusa, inspeção inconsistente ou falha operacional/de saída. A CLI não remove workspaces do chamador nem substitui destinos. Demo/testes removem apenas suas próprias pastas temporárias.

## Exemplo da biblioteca

Referencie `src/ResumableTransferLab/ResumableTransferLab.csproj` em um projeto .NET 10. Dentro de um método assíncrono:

```csharp
await using var server = await ResumableTransferLab.Fixture.LoopbackArtifactServer.StartAsync();
var request = new ResumableTransferLab.TransferRequest
{
    ArtifactId = "synthetic-32b",
    SourceUrl = server.ArtifactUrl,
    WorkspaceDirectory = "workspace-novo-proprio",
    DestinationPath = "saida-nova.bin",
    ExpectedLength = 32,
    ExpectedSha256 = "73337f479fe170d73e53e247f3052e4243cc9c2a0ffa621853d9385c619efb77",
    StopAfterCommittedBytes = 10
};
var first = await new ResumableTransferLab.ResumableTransferClient().ExecuteAsync(request);
var resumed = await new ResumableTransferLab.ResumableTransferClient().ExecuteAsync(
    request with { StopAfterCommittedBytes = null });
```

Confira `Status` e `Error.ContractCode` de cada resultado. Request nulo lança `ArgumentNullException`; falhas ordinárias de entrada, transporte e I/O retornam resultados estruturados. A inspeção não conserta arquivos.

## Decisões e limitações

Cabeçalhos são verificados antes do corpo. Na retomada, todo o sufixo é validado em arquivo temporário antes de anexar qualquer byte. O estado registra offset e hash do prefixo. Um arquivo marcador aberto com exclusividade impede processos cooperantes de usar simultaneamente o mesmo workspace. A publicação escreve um estágio exclusivo na pasta final, confere o estágio e o move sem sobrescrever.

Limites: artefato 64 MiB; estado 64 KiB; bloco 64 KiB; cabeçalhos de resposta 16 KiB; prazo padrão 15 segundos, máximo 120. Uma transferência por processo e um proprietário cooperante por workspace. O pico temporário pode se aproximar de três arquivos do tamanho do artefato. Não há tentativas ilimitadas, redirecionamentos, proxies, nomes DNS, credenciais, uploads, descompressão ou multipart.

A interrupção inicial valida somente o prefixo recebido. Falhas locais durante append/checkpoint podem deixar evidências ambíguas, que não são apagadas automaticamente. Workspaces completos mantêm estado/parcial e permitem publicar outro destino novo sem HTTP. I/O local não oferece prazo rígido em tempo real. A verificação de ancestrais rejeita symlinks/reparse points visíveis, mas depende de arquivos regulares e filesystem confiável, sem troca maliciosa de caminhos, aliases por hard link ou dispositivos. Não é fronteira de segurança multiusuário.

Estado e relatórios podem reter identificadores, validadores e URLs fornecidos. Escape não anonimiza. Não há telemetria, rastreamento, modelos ou APIs externas em execução. Contato geral pelo [site oficial](https://lofranotecnologia.com.br); relatos de segurança seguem [SECURITY.md](SECURITY.md).
