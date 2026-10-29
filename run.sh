#!/usr/bin/env bash
set -euo pipefail

MODEL="${MODEL:-phi3}"

if [[ $# -eq 0 ]]; then
  set -- studio
fi

# Build slaw if needed
if [[ ! -f ./out/slaw ]]; then
  echo "[setup] building slaw..."
  dotnet publish src/Slaw/Slaw.csproj -c Release -o out
fi

# Start ollama in background if not already running
if ! curl -sf http://localhost:11434 > /dev/null 2>&1; then
  echo "[setup] starting ollama..."
  ollama serve &
  OLLAMA_PID=$!
  # Wait for ollama to be ready
  for i in $(seq 1 20); do
    sleep 0.5
    if curl -sf http://localhost:11434 > /dev/null 2>&1; then break; fi
    if [[ $i -eq 20 ]]; then echo "[error] ollama did not start in time"; exit 1; fi
  done
fi

# Pull model if not present
if ! ollama list | grep -q "^${MODEL}"; then
  echo "[setup] pulling model ${MODEL}..."
  ollama pull "$MODEL"
fi

echo "[run] slaw $* with model $MODEL"
exec ./out/slaw --model "$MODEL" "$@"
