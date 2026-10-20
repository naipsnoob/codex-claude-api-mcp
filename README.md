# Codex + Claude API MCP

MCP local para o Codex delegar etapas de trabalho a uma API compatível com Anthropic Messages. O Codex continua sendo o orquestrador: define uma etapa pequena, Claude executa localmente, o MCP devolve ações e relatório, e o Codex confere o resultado antes de decidir a próxima etapa.

## Instalação rápida no Windows

1. Instale Node.js LTS.
2. Baixe `CodexClaudeApiMcpSetup.exe` em [Releases](https://github.com/naipsnoob/codex-claude-api-mcp/releases/latest).
3. Execute-o em um terminal. Informe a URL base ou o endpoint `/v1/messages` e cole a API key quando solicitado; a entrada da chave fica oculta.
4. O instalador faz uma chamada de validação, instala o servidor MCP, protege a chave com DPAPI para o usuário atual e atualiza a seção `claude_external` em `%USERPROFILE%\.codex\config.toml`.
5. Quando aparecer **Instalação OK**, reinicie o Codex.

O instalador aceita URLs HTTPS como `https://claude-ss.ia.br`, `https://proxy.exemplo/v1` ou `https://proxy.exemplo/v1/messages`. A chamada de validação não segue redirecionamentos nem repete automaticamente um HTTP 429.

O modelo padrão é `claude-opus-5`, esforço `high`, `max_tokens: 128000`. O endpoint precisa oferecer a API Messages compatível com Anthropic e acesso a esse modelo.

## Fluxo de trabalho

```text
Usuário → Codex entende e divide o trabalho
       → Codex envia uma etapa delimitada ao MCP
       → Claude usa ferramentas locais e retorna ações, evidências e relatório
       → Codex verifica alterações e resultados
       → Codex envia a próxima etapa ou entrega a conclusão
```

Use `ask_claude_opus_5` para orientação sem ações locais e `execute_with_claude_opus_5` para ler/gravar arquivos e executar PowerShell ou CMD. Tarefas longas podem usar `start_claude_task`, `wait_claude_task`, `get_claude_task_result`, `continue_claude_task` e `cancel_claude_task`. `verify_claude_task` oferece uma inspeção somente de leitura.

O MCP executa com as permissões do usuário Windows que abriu o Codex. Ele não concede elevação UAC nem privilégios de administrador. Claude recebe somente o resumo e o contexto que o Codex enviar; não envie chaves, credenciais ou arquivos privados nos prompts.

## Segurança e privacidade

- A API key é digitada sem eco e validada por HTTPS.
- A chave é gravada somente como blob criptografado com Windows DPAPI, vinculado ao usuário atual. Ela não é colocada no `config.toml`, no repositório ou no log do instalador.
- O launcher descriptografa a chave em memória e passa-a ao processo Node por variável de ambiente.
- O instalador usa `npm ci` com lockfile e scripts de dependência desativados.
- O MCP guarda status, relatórios e contagens locais de tokens; não persiste os prompts ou resultados completos das ferramentas.
- A Messages API informa tokens de entrada/saída por chamada; cota restante da conta não é estimada se o Gateway não fornecer uma fonte autenticada e confiável.
- O executor local pode alterar ou apagar arquivos e executar comandos dentro das permissões da conta atual. Revise cada etapa delegada.

## Desenvolvimento e verificações

```powershell
cd mcp
npm ci --ignore-scripts
npm run selfcheck
cd ..
.\installer\build.ps1
```

O selfcheck do instalador pode ser executado com `installer\CodexClaudeApiMcpSetup.exe --selfcheck`. Ele verifica normalização de URL, preservação de outras seções TOML, remoção da configuração antiga da chave e criptografia DPAPI. O teste real do endpoint acontece quando a pessoa executa o instalador e fornece sua própria chave.

## Configuração para outros clientes MCP stdio

O instalador atualiza a configuração global do Codex. Outros clientes stdio podem usar o mesmo `launch.ps1` e a pasta instalada, se permitirem configurar um comando local com argumentos. O cliente deve executar o launcher sob a mesma conta Windows que criptografou a chave. O formato exato da configuração depende de cada cliente.

## Licença

O código deste repositório está sob MIT. As dependências de terceiros mantêm suas próprias licenças; consulte os metadados no `mcp/package-lock.json` e os avisos publicados por cada pacote.
