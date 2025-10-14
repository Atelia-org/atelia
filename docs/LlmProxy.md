# The proxy server exposes the following endpoints:

## LLM服务聚合Proxy
url: `http://localhost:4000`

## OpenAI Compatible API
Chat Completions: `POST /openai/v1/chat/completions` (supports streaming via the stream parameter)
```bash
curl -X POST http://localhost:4000/openai/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello!"}],
    "stream": true
  }'
```

List Models: `GET /openai/v1/models`
```bash
curl http://localhost:4000/openai/v1/models
```

Retrieve Model: `GET /openai/v1/models/{model}`
```bash
curl http://localhost:4000/openai/v1/models/gpt-4.1
```

## Anthropic Compatible API
Messages: `POST /anthropic/v1/messages` (supports streaming via the stream parameter)
```bash
curl -X POST http://localhost:4000/anthropic/v1/messages \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello!"}],
    "stream": true
  }'
```

Count Tokens: `POST /anthropic/v1/messages/count_tokens` (counts the number of tokens in a message)
```bash
curl -X POST http://localhost:4000/anthropic/v1/messages/count_tokens \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello, Claude"}]
  }'
```

List Models: `GET /anthropic/v1/models`
Retrieve Model: `GET /anthropic/v1/models/{model}`

## Claude Code Compatible API
Messages: `POST /anthropic/claude/v1/messages` (supports streaming via the stream parameter)
```bash
curl -X POST http://localhost:4000/anthropic/claude/v1/messages \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "vscode-lm-proxy",
    "messages": [{"role":"user","content":"Hello!"}],
    "stream": true
  }'
```

Count Tokens: `POST /anthropic/claude/v1/messages/count_tokens`
List Models: `GET /anthropic/claude/v1/models`
Retrieve Model: `GET /anthropic/claude/v1/models/{model}`
